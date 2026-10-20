using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DcrChatbot.Core.Application;

public sealed class ChatService(
    IDcrRepository dcrRepository,
    ILlmService llmService,
    ISessionStore sessionStore,
    IOptions<LlmOptions> llmOptions,
    ILogger<ChatService> logger) : IChatService
{
    private readonly LlmOptions llmOptions = llmOptions.Value;

    public async Task<ChatResponse> StartAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var graphId = RequireValue(request.GraphId, "GraphId er påkrævet ved start af en samtale.");
        var simulationId = await dcrRepository.CreateSimulationAsync(graphId, cancellationToken);
        var state = await dcrRepository.GetGraphStateAsync(graphId, simulationId, cancellationToken);
        var session = new ChatSession
        {
            GraphId = graphId,
            Mode = request.Mode,
            SimulationId = simulationId,
            CurrentGraphState = state
        };

        await sessionStore.SaveSessionAsync(session, cancellationToken);

        // En formular starter med sit første spørgsmål; en FAQ-graf har intet
        // spørgsmål at stille, så den viser i stedet de emner, den dækker.
        var pendingEvent = GetNextAnswerablePendingEvent(state);
        if (pendingEvent is not null)
        {
            return ToResponse(session, pendingEvent.Label);
        }

        var suggestions = GetHelpSuggestions(state);
        return suggestions.Count == 0
            ? ToResponse(session, "Samtalen er startet.")
            : ToResponse(session, "Hej! Hvad vil du vide noget om?", suggestions: suggestions);
    }

    public async Task<ChatResponse> SendMessageAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var message = RequireValue(request.Message, "Beskeden må ikke være tom.");
        return await sessionStore.ExecuteAsync(sessionId, async session =>
        {
            session.History.Add(new ChatMessage { Sender = "User", Content = message });
            if (session.PendingAnswersQueue.Count > 0)
            {
                var answerResult = await llmService.ClassifyConfirmationAsync(
                    message,
                    session.PendingAnswersQueue[0],
                    llmOptions,
                    cancellationToken);
                var answer = answerResult.IsConfirmed;
                if (answer is null)
                {
                    throw new ChatConflictException(
                        "Kladde skal bekræftes, afvises eller rettes, før næste besked kan behandles.");
                }

                return await ResolveDraftCoreAsync(
                    session, session.PendingAnswersQueue[0], answer.Value, cancellationToken);
            }

            return await CreateDraftAsync(session, message, cancellationToken);
        }, cancellationToken);
    }

    public Task<ChatResponse> ConfirmDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        CancellationToken cancellationToken = default) =>
        ResolveDraftAsync(sessionId, pendingAnswerId, true, cancellationToken);

    public Task<ChatResponse> RejectDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        CancellationToken cancellationToken = default) =>
        ResolveDraftAsync(sessionId, pendingAnswerId, false, cancellationToken);

    public async Task<ChatResponse> ReviseDraftAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var message = RequireValue(request.Message, "Den reviderede besked må ikke være tom.");
        return await sessionStore.ExecuteAsync(sessionId, async session =>
        {
            var existingDraft = FindDraft(session, request.TargetPendingAnswerId);
            if (existingDraft.ExecutionStatus is "Executing" or "Executed")
            {
                throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke rettes.");
            }

            session.PendingAnswersQueue.Remove(existingDraft);
            session.History.Add(new ChatMessage { Sender = "User", Content = message });
            return await CreateDraftAsync(session, message, cancellationToken);
        }, cancellationToken);
    }

    public async Task<ChatResponse> EditAnswerAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var message = RequireValue(request.Message, "Det nye svar må ikke være tomt.");
        var eventId = RequireValue(request.EventId, "EventId er påkrævet for at rette et svar.");
        return await sessionStore.ExecuteAsync(sessionId, async session =>
        {
            session.History.Add(new ChatMessage { Sender = "User", Content = message });

            // FR-HITL-2: spørg DCR, ikke sessionens kopi, om svaret stadig kan rettes.
            session.IsGraphStateStale = true;
            await EnsureFreshStateAsync(session, cancellationToken);
            var dcrEvent = session.CurrentGraphState?.GetEvent(eventId);
            var refusal = GetEditRefusal(dcrEvent);
            if (refusal is not null)
            {
                session.History.Add(new ChatMessage { Sender = "Bot", Content = refusal });
                return ToResponse(session, refusal);
            }

            var (extracted, _) = await llmService.ExtractValueAsync(
                message, dcrEvent!, [], llmOptions, cancellationToken);
            var value = string.IsNullOrWhiteSpace(extracted.ExtractedValue)
                ? null
                : ToCanonicalValue(dcrEvent!, extracted.ExtractedValue);
            if (value is null || !EventValidator.Validate(dcrEvent, value).IsValid)
            {
                return ToResponse(
                    session,
                    $"Jeg kunne ikke forstå det som et svar på '{dcrEvent!.Label}'. Prøv at skrive det igen.");
            }

            var previousValue = GetAnswerValue(dcrEvent!);
            if (string.Equals(value, previousValue, StringComparison.OrdinalIgnoreCase))
            {
                return ToResponse(session, $"Dit svar på '{dcrEvent!.Label}' er allerede '{previousValue}'.");
            }

            // Rettelsen bekræftes som enhver anden kladde (FR-HITL-1) og
            // kommer først i køen, foran eventuelle afledte svar.
            session.PendingAnswersQueue.RemoveAll(d =>
                d.PreviousValue is not null &&
                string.Equals(d.EventId, dcrEvent!.Id, StringComparison.OrdinalIgnoreCase));
            session.PendingAnswersQueue.Insert(0, new PendingAnswer
            {
                EventId = dcrEvent!.Id,
                Question = dcrEvent.Label,
                ProposedValue = value,
                PreviousValue = previousValue
            });

            return ToResponse(
                session,
                $"Vil du ændre dit svar på '{dcrEvent.Label}' fra '{previousValue}' til '{value}'?");
        }, cancellationToken);
    }

    // Forklarer borgeren, hvorfor et svar ikke kan rettes, eller null hvis det kan.
    private static string? GetEditRefusal(DcrEvent? dcrEvent)
    {
        if (dcrEvent is null)
        {
            return "Det spørgsmål findes ikke længere i forløbet, så svaret kan ikke rettes.";
        }

        if (string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase) ||
            dcrEvent.IsExecuted != true)
        {
            return $"Du har ikke svaret på '{dcrEvent.Label}' endnu, så der er ikke noget at rette.";
        }

        if (!dcrEvent.Included)
        {
            return $"Spørgsmålet '{dcrEvent.Label}' er ikke længere en del af forløbet efter dine " +
                "andre svar, så det kan ikke rettes.";
        }

        return dcrEvent.IsEnabled
            ? null
            : $"Svaret på '{dcrEvent.Label}' kan ikke rettes lige nu, fordi forløbet ikke længere " +
              "tillader det. Det sker typisk, når et senere trin bygger videre på svaret.";
    }

    private static string GetAnswerValue(DcrEvent dcrEvent) =>
        !string.IsNullOrWhiteSpace(dcrEvent.Value) ? dcrEvent.Value
        : !string.IsNullOrWhiteSpace(dcrEvent.DisplayValue) ? dcrEvent.DisplayValue
        : string.Empty;

    private async Task<ChatResponse> ResolveDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        return await sessionStore.ExecuteAsync(
            sessionId,
            session => ResolveDraftCoreAsync(session, FindDraft(session, pendingAnswerId), confirm, cancellationToken),
            cancellationToken);
    }

    private async Task<ChatResponse> ResolveDraftCoreAsync(
        ChatSession session,
        PendingAnswer draft,
        bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            session.PendingAnswersQueue.Remove(draft);
            await RefreshStateAsync(session, cancellationToken);

            // Et afvist svar i et formularforløb: stil det spørgsmål igen,
            // botten stadig mangler svar på, frem for at bede om en ny formulering.
            var openQuestion = GetNextAnswerablePendingEvent(session.CurrentGraphState);
            var rejected = session.PendingAnswersQueue.Count > 0
                ? "Okay, det svar springer jeg over."
                : draft.PreviousValue is not null
                    ? $"Okay, dit svar på '{draft.Question}' er stadig '{draft.PreviousValue}'."
                    : !draft.IsEventMatch && openQuestion is not null
                        ? $"Okay, så spørger jeg igen: {openQuestion.Label}"
                        : "Okay. Prøv at omformulere dit spørgsmål.";
            session.History.Add(new ChatMessage { Sender = "Bot", Content = rejected });
            return ToResponse(session, rejected);
        }

        if (draft.ExecutionStatus is "Executing" or "Executed")
        {
            throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke udføres igen.");
        }

        // FR-HITL-1: intet event når DCR uden at borgeren først har bekræftet.
        var simulationId = RequireValue(session.SimulationId, "Sessionen mangler en DCR-simulation.");

        // "Pending før" skal beregnes ud fra grafens faktiske tilstand. Svar
        // fra en tidligere bekræftelse, som ikke nåede at blive udført,
        // ryddes først, så de ikke tæller med som "allerede pending".
        await EnsureFreshStateAsync(session, cancellationToken);
        if (session.AnswerLabelsToExecute.Count > 0)
        {
            await ExecuteQueuedAnswerLabelsAsync(session, simulationId, cancellationToken);
            await EnsureFreshStateAsync(session, cancellationToken);
        }

        var pendingLabelsBefore = GetPendingLabels(session.CurrentGraphState).Select(e => e.Id).ToList();
        var answersInEffectBefore = GetAnswersInEffect(session.CurrentGraphState);
        session.PendingLabelsBeforeExecution = pendingLabelsBefore;
        draft.ExecutionStatus = "Executing";
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        try
        {
            await dcrRepository.ExecuteEventAsync(
                session.GraphId, simulationId, draft.EventId, draft.ProposedValue, cancellationToken);
        }
        catch
        {
            draft.ExecutionStatus = "Pending";
            session.PendingLabelsBeforeExecution = null;
            await sessionStore.SaveSessionAsync(session, cancellationToken);
            throw;
        }

        // Choice-eventet er nu udført i DCR. Gem det med det samme, så en
        // senere fejl ikke efterlader kladden som "Executing" for evigt.
        // Tilstanden markeres som forældet, indtil den nye er hentet.
        draft.ExecutionStatus = "Executed";
        session.PendingAnswersQueue.Remove(draft);
        session.IsGraphStateStale = true;
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        await RefreshStateAsync(session, cancellationToken);

        var answers = FindNewAnswers(session.CurrentGraphState, pendingLabelsBefore);
        session.PendingLabelsBeforeExecution = null;
        var droppedDrafts = DropQueuedDraftsThatNoLongerApply(session);

        // FR-HITL-2: en rettelse bekræftes med de konsekvenser, den fik.
        var revisionParts = draft.PreviousValue is null
            ? []
            : DescribeRevision(draft, droppedDrafts, answersInEffectBefore, session.CurrentGraphState);

        // Er der flere kladder i køen (FR-DCR-2), vises den næste i stedet
        // for et nyt spørgsmål eller en konklusion.
        var hasQueuedDrafts = session.PendingAnswersQueue.Count > 0;
        var nextPendingEvent = hasQueuedDrafts ? null : GetNextAnswerablePendingEvent(session.CurrentGraphState);
        var replyParts = answers.Select(GetAnswerText).ToList();
        if (nextPendingEvent is not null)
        {
            replyParts.Add(nextPendingEvent.Label);
        }

        // Et FAQ-spørgsmål, der ikke gør et svar pending, er ikke slutningen
        // på et forløb; det mangler bare et svar i grafen.
        var hasNoFaqAnswer = draft.IsEventMatch && !hasQueuedDrafts && replyParts.Count == 0;
        if (hasNoFaqAnswer)
        {
            replyParts.Add("Jeg har desværre ikke et svar på det spørgsmål endnu.");
        }

        // En formular er slut, når der hverken er kladder, spørgsmål eller svar
        // tilbage. Grafens resultat-labels (fx et DMN-svar) udføres så først,
        // så konklusionen kun bygger på udførte events (FR-DCR-5).
        var isFinished = !draft.IsEventMatch && !hasQueuedDrafts && nextPendingEvent is null && replyParts.Count == 0;
        var resultLabels = isFinished ? FindResultLabels(session.CurrentGraphState) : [];

        // Svarene udføres, så samme spørgsmål kan stilles igen og svaret
        // vises på ny. De sættes i kø og gemmes først: fejler udførslen,
        // har borgeren stadig fået svaret, og køen prøves igen ved næste
        // bekræftelse.
        session.AnswerLabelsToExecute.AddRange(answers.Concat(resultLabels).Select(e => e.Id));
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        try
        {
            await ExecuteQueuedAnswerLabelsAsync(session, simulationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Kunne ikke udføre svar-labels {LabelIds} i session {SessionId}; prøves igen ved næste bekræftelse.",
                session.AnswerLabelsToExecute,
                session.SessionId);
        }

        // Borgeren har fået svaret; kan den nye tilstand ikke hentes nu,
        // forbliver den markeret som forældet og hentes ved næste handling.
        try
        {
            await EnsureFreshStateAsync(session, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Kunne ikke hente grafens tilstand i session {SessionId}; hentes ved næste handling.",
                session.SessionId);
        }

        if (isFinished)
        {
            var conclusionText = ConclusionBuilder.Build(session.CurrentGraphState).ToText();
            replyParts.Add(conclusionText.Length > 0
                ? conclusionText
                : "Tak for dine svar. Forløbet er nu afsluttet.");
        }

        var reply = string.Join("\n\n", revisionParts.Concat(replyParts));
        if (reply.Length > 0)
        {
            session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });
        }

        // FAQ-match er ikke et svar, borgeren kan rette; det er alt andet.
        var confirmedAnswer = draft.IsEventMatch
            ? null
            : new AnsweredQuestion
            {
                EventId = draft.EventId,
                Question = draft.Question ?? draft.EventId,
                Value = draft.ProposedValue
            };
        return ToResponse(session, reply, confirmedAnswer);
    }

    // Borgerens svar, der gælder lige nu: udførte og stadig inkluderede
    // spørgsmål (ikke labels og ikke DCR's egne Robot-events).
    private static Dictionary<string, DcrEvent> GetAnswersInEffect(GraphState? state) =>
        state?.ExecutedEvents
            .Where(e => e.Included &&
                        !string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase) &&
                        e.Roles?.Contains("Robot", StringComparison.OrdinalIgnoreCase) != true)
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase)
        ?? new(StringComparer.OrdinalIgnoreCase);

    // Ændringen og dens konsekvenser: svar i køen, der er ryddet, og
    // tidligere svar, som grafen har udelukket efter rettelsen.
    private static List<string> DescribeRevision(
        PendingAnswer draft,
        IReadOnlyList<PendingAnswer> droppedDrafts,
        IReadOnlyDictionary<string, DcrEvent> answersInEffectBefore,
        GraphState? stateAfter)
    {
        var parts = new List<string>
        {
            $"Dit svar på '{draft.Question}' er ændret fra '{draft.PreviousValue}' til '{draft.ProposedValue}'."
        };

        var answersInEffectAfter = GetAnswersInEffect(stateAfter);
        var noLongerInEffect = answersInEffectBefore.Values
            .Where(e => !string.Equals(e.Id, draft.EventId, StringComparison.OrdinalIgnoreCase) &&
                        !answersInEffectAfter.ContainsKey(e.Id))
            .OrderBy(e => e.Sequence)
            .Select(e => e.Label)
            .Concat(droppedDrafts.Select(d => d.Question ?? d.EventId))
            .ToList();

        if (noLongerInEffect.Count > 0)
        {
            parts.Add("Det betyder, at disse svar ikke længere gælder:\n" +
                string.Join("\n", noLongerInEffect.Select(label => $"- {label}")));
        }

        return parts;
    }

    // DCR.Repo kræver en tom streng som værdi for labels; null giver en serverfejl.
    // Køen gemmes efter hver udført label, så en fejl på en senere label
    // ikke får en allerede udført label til at blive udført igen.
    private async Task ExecuteQueuedAnswerLabelsAsync(
        ChatSession session, string simulationId, CancellationToken cancellationToken)
    {
        if (session.AnswerLabelsToExecute.Count == 0)
        {
            return;
        }

        session.IsGraphStateStale = true;
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        foreach (var labelId in session.AnswerLabelsToExecute.ToList())
        {
            await dcrRepository.ExecuteEventAsync(
                session.GraphId, simulationId, labelId, string.Empty, cancellationToken);
            session.AnswerLabelsToExecute.Remove(labelId);
            await sessionStore.SaveSessionAsync(session, cancellationToken);
        }
    }

    private async Task<ChatResponse> CreateDraftAsync(
        ChatSession session, string message, CancellationToken cancellationToken)
    {
        await EnsureFreshStateAsync(session, cancellationToken);
        var candidates = GetCandidateEvents(session.CurrentGraphState);
        if (candidates.Count == 0)
        {
            return ToResponse(session, "Der er ingen tilgængelige spørgsmål lige nu.");
        }

        var pendingEvent = GetNextAnswerablePendingEvent(session.CurrentGraphState);

        if (pendingEvent is not null)
        {
            candidates = candidates
                .OrderByDescending(e => e.Id == pendingEvent.Id)
                .ThenBy(e => e.Sequence)
                .ToList();
        }

        DcrEvent? matchedEvent;
        string? value;
        var isEventMatch = false;
        List<PendingAnswer> inferredDrafts = [];
        if (pendingEvent is not null)
        {
            matchedEvent = pendingEvent;

            // FR-DCR-2: de andre åbne spørgsmål sendes med, så borgeren kan
            // besvare flere af dem i samme besked.
            var otherOpenEvents = GetAnswerablePendingEvents(session.CurrentGraphState)
                .Where(e => !string.Equals(e.Id, pendingEvent.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var (extracted, _) = await llmService.ExtractValueAsync(
                message,
                matchedEvent,
                otherOpenEvents,
                llmOptions,
                cancellationToken);
            value = extracted.ExtractedValue;
            inferredDrafts = BuildInferredDrafts(
                session, extracted.InferredReplies, otherOpenEvents, pendingEvent.Id);
        }
        else
        {
            var (matchedEventId, _) = await llmService.MatchEventAsync(
                message,
                candidates,
                llmOptions,
                cancellationToken);
            matchedEvent = string.IsNullOrWhiteSpace(matchedEventId)
                ? null
                : candidates.FirstOrDefault(e =>
                    string.Equals(e.Id, matchedEventId, StringComparison.OrdinalIgnoreCase));
            if (matchedEvent is null)
            {
                // Fx "hvad kan jeg få hjælp til?" eller noget, grafen ikke
                // dækker: vis grafens emner i stedet for blot at afvise.
                return HelpResponse(session);
            }

            // En event-match beskæftiger sig kun med spørgsmålet. For choice-
            // events bruges grafens svarværdi; der kaldes ingen value extractor.
            value = GetYesValue(matchedEvent);
            isEventMatch = true;
        }

        // Svaret på det stillede spørgsmål kommer først i køen. Kan det ikke
        // bruges, men har borgeren svaret på andre åbne spørgsmål, bekræftes
        // de i stedet, og spørgsmålet stilles igen bagefter.
        if (string.IsNullOrWhiteSpace(value))
        {
            if (inferredDrafts.Count == 0)
            {
                // Botten har stillet et spørgsmål; den bedste hjælp er at
                // minde om det i stedet for blot at bede om en omformulering.
                return ToResponse(session, $"Jeg mangler stadig et svar på: {matchedEvent.Label}");
            }
        }
        else
        {
            value = ToCanonicalValue(matchedEvent, value);
            var validation = EventValidator.Validate(matchedEvent, value);
            if (validation.IsValid)
            {
                session.PendingAnswersQueue.Add(new PendingAnswer
                {
                    EventId = matchedEvent.Id,
                    Question = matchedEvent.Label,
                    ProposedValue = value,
                    IsAutoInferred = false,
                    IsEventMatch = isEventMatch
                });
            }
            else if (inferredDrafts.Count == 0)
            {
                return ToResponse(
                    session,
                    isEventMatch
                        ? ToCitizenMessage(validation.Error)
                        : $"Det svar passer ikke til spørgsmålet: {matchedEvent.Label}");
            }
        }

        session.PendingAnswersQueue.AddRange(inferredDrafts);

        // Kladderne vises ét ad gangen; beskeden beskriver den første.
        var first = session.PendingAnswersQueue[0];
        return ToResponse(
            session,
            $"Jeg har forstået '{first.Question}' som '{first.ProposedValue}'. Er det korrekt?");
    }

    // FR-DCR-2: afledte svar fra LLM'en er kun forslag. De bliver til
    // kladder, når de hører til et åbent spørgsmål, ikke allerede er i køen
    // og har en gyldig værdi; rækkefølgen følger grafen.
    private static List<PendingAnswer> BuildInferredDrafts(
        ChatSession session,
        IReadOnlyDictionary<string, string> inferredReplies,
        IReadOnlyList<DcrEvent> openEvents,
        string primaryEventId)
    {
        var replies = new Dictionary<string, string>(inferredReplies, StringComparer.OrdinalIgnoreCase);
        var drafts = new List<PendingAnswer>();
        foreach (var dcrEvent in openEvents)
        {
            if (!replies.TryGetValue(dcrEvent.Id, out var rawValue) ||
                string.IsNullOrWhiteSpace(rawValue) ||
                dcrEvent.IsExecuted == true ||
                string.Equals(dcrEvent.Id, primaryEventId, StringComparison.OrdinalIgnoreCase) ||
                session.PendingAnswersQueue.Any(d =>
                    string.Equals(d.EventId, dcrEvent.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = ToCanonicalValue(dcrEvent, rawValue);
            if (!EventValidator.Validate(dcrEvent, value).IsValid)
            {
                continue;
            }

            drafts.Add(new PendingAnswer
            {
                EventId = dcrEvent.Id,
                Question = dcrEvent.Label,
                ProposedValue = value,
                IsAutoInferred = true
            });
        }

        return drafts;
    }

    // Når et svar er udført, kan grafen have ændret sig: et afledt svar kan
    // være blevet disabled eller allerede besvaret. Det fjernes fra køen i
    // stedet for at blive vist for borgeren.
    private static List<PendingAnswer> DropQueuedDraftsThatNoLongerApply(ChatSession session)
    {
        var dropped = session.PendingAnswersQueue.Where(draft =>
        {
            // En rettelse gælder et svar, der allerede er udført, så "udført"
            // gør den ikke ugyldig; ellers er reglerne de samme.
            if (draft.ExecutionStatus != "Pending")
            {
                return false;
            }

            var dcrEvent = session.CurrentGraphState?.GetEvent(draft.EventId);
            return dcrEvent is null ||
                (draft.PreviousValue is null &&
                 dcrEvent.IsExecuted == true &&
                 !string.Equals(dcrEvent.DataType, "choice", StringComparison.OrdinalIgnoreCase)) ||
                !EventValidator.Validate(dcrEvent, draft.ProposedValue).IsValid;
        }).ToList();

        session.PendingAnswersQueue.RemoveAll(dropped.Contains);
        return dropped;
    }

    // Grafens egen stavemåde af en choice-værdi (fx "Denmark" frem for "denmark").
    private static string ToCanonicalValue(DcrEvent dcrEvent, string value)
    {
        var trimmed = value.Trim();
        return EventValidator.GetChoiceValues(dcrEvent)
            .FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? trimmed;
    }

    private static string? GetYesValue(DcrEvent dcrEvent)
    {
        var values = EventValidator.GetChoiceValues(dcrEvent);
        return values.FirstOrDefault(v => ChoiceYesWords.Contains(v)) ?? values.FirstOrDefault();
    }

    private static readonly HashSet<string> ChoiceYesWords =
        new(["ja", "yes", "jo", "jep", "ja tak", "ok", "okay", "korrekt", "rigtigt"], StringComparer.OrdinalIgnoreCase);

    // Labels som DCR har beregnet en værdi til (fx et DMN-resultat), men som
    // endnu ikke er udført. De udføres ved forløbets afslutning, så de
    // kommer med i konklusionen. Pending labels er ikke resultater: det
    // permanente anker er pending og må aldrig udføres (FR-DCR-4).
    private static List<DcrEvent> FindResultLabels(GraphState? state) =>
        state?.EnabledEvents
            .Where(e => string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)
                     && e.IsExecuted != true
                     && !e.IsPending
                     && (!string.IsNullOrWhiteSpace(e.Value) || !string.IsNullOrWhiteSpace(e.DisplayValue)))
            .ToList() ?? [];

    private static IEnumerable<DcrEvent> GetPendingLabels(GraphState? state) =>
        state?.EnabledEvents.Where(e =>
            e.IsPending && string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)) ?? [];

    private static DcrEvent? GetNextAnswerablePendingEvent(GraphState? state) =>
        GetAnswerablePendingEvents(state).FirstOrDefault();

    private static IEnumerable<DcrEvent> GetAnswerablePendingEvents(GraphState? state) =>
        state?.GetPendingEvents().Where(IsCitizenQuestion) ?? [];

    // Et spørgsmål, borgeren kan svare på: det har en datatype (en værdi at
    // udfylde) og er ikke et label eller et event, DCR udfører selv (rollen
    // Robot, fx en DMN-beslutning). Et event uden datatype ville acceptere
    // hvad som helst som svar, så botten aldrig kom videre.
    private static bool IsCitizenQuestion(DcrEvent dcrEvent) =>
        !string.IsNullOrWhiteSpace(dcrEvent.DataType) &&
        !string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase) &&
        dcrEvent.Roles?.Contains("Robot", StringComparison.OrdinalIgnoreCase) != true;

    // I FAQ-graferne er label-eventets label selve svarteksten. Description
    // er DCR's dokumentationsfelt og kan indeholde interne noter.
    private static string GetAnswerText(DcrEvent dcrEvent)
    {
        if (!string.IsNullOrWhiteSpace(dcrEvent.DisplayValue))
        {
            return dcrEvent.DisplayValue;
        }
        if (!string.IsNullOrWhiteSpace(dcrEvent.Value))
        {
            return dcrEvent.Value;
        }
        return dcrEvent.Label;
    }

    private static string ToCitizenMessage(EventValidationError error) => error switch
    {
        EventValidationError.EventNotEnabled => "Det spørgsmål kan ikke besvares lige nu.",
        _ => "Jeg kunne ikke finde et spørgsmål der matcher det. Prøv at omformulere."
    };

    // Kun spørgsmål, borgeren kan svare på, er kandidater. Udførte
    // choice-events (FAQ) forbliver kandidater, så de kan stilles igen.
    private static List<DcrEvent> GetCandidateEvents(GraphState? state) =>
        state?.EnabledEvents
            .Where(e =>
                IsCitizenQuestion(e) &&
                (string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase) ||
                 e.IsExecuted != true))
            .ToList() ?? [];

    private async Task RefreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (session.SimulationId is not null)
        {
            session.CurrentGraphState = await dcrRepository.GetGraphStateAsync(
                session.GraphId, session.SimulationId, cancellationToken);
            session.IsGraphStateStale = false;
        }
    }

    // Kaster videre, hvis tilstanden ikke kan hentes: hellere en fejl end
    // at fortsætte på en graf, der ikke længere svarer til DCR.
    private async Task EnsureFreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (!session.IsGraphStateStale)
        {
            return;
        }

        await RefreshStateAsync(session, cancellationToken);

        // Kunne tilstanden ikke hentes lige efter en udførsel, blev svaret
        // aldrig vist og står stadig som pending. Det sættes i kø og udføres
        // ved næste bekræftelse, så spørgsmålet kan stilles og besvares igen.
        if (session.PendingLabelsBeforeExecution is { } before)
        {
            session.AnswerLabelsToExecute.AddRange(FindNewAnswers(session.CurrentGraphState, before)
                .Select(e => e.Id)
                .Except(session.AnswerLabelsToExecute));
            session.PendingLabelsBeforeExecution = null;
        }
    }

    // FAQ-svaret er de label-events, som udførslen gjorde pending via
    // response-relationen. Det permanente anker var pending i forvejen
    // og kommer derfor ikke med.
    private static List<DcrEvent> FindNewAnswers(GraphState? state, IReadOnlyCollection<string> pendingBefore) =>
        GetPendingLabels(state).Where(e => !pendingBefore.Contains(e.Id)).ToList();

    private static PendingAnswer FindDraft(ChatSession session, string? pendingAnswerId) =>
        session.PendingAnswersQueue.FirstOrDefault(draft =>
            string.IsNullOrWhiteSpace(pendingAnswerId) || draft.Id == pendingAnswerId)
        ?? throw new KeyNotFoundException("Den ønskede kladde blev ikke fundet.");

    // FR-LLM-3: hjælpen kommer fra grafen, ikke fra LLM'en. Borgeren får de
    // emner, grafen tilbyder lige nu, og intet udføres i DCR.
    private static ChatResponse HelpResponse(ChatSession session)
    {
        var suggestions = GetHelpSuggestions(session.CurrentGraphState);
        return suggestions.Count == 0
            ? ToResponse(session, "Det kan jeg desværre ikke hjælpe med. Prøv at omformulere dit spørgsmål.")
            : ToResponse(
                session,
                "Det kan jeg desværre ikke svare på, men jeg kan hjælpe dig med disse emner:",
                suggestions: suggestions);
    }

    private const int MaxSuggestions = 8;

    // Emnerne er grafens enabled choice-events (FAQ-spørgsmålene). Labels er
    // svar og formularspørgsmål stilles af botten selv, så de foreslås ikke.
    private static List<Suggestion> GetHelpSuggestions(GraphState? state) =>
        state?.EnabledEvents
            .Where(e => string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(e.Label))
            .OrderBy(e => e.Sequence)
            .Take(MaxSuggestions)
            .Select(e => new Suggestion { EventId = e.Id, Label = e.Label })
            .ToList() ?? [];

    private static ChatResponse ToResponse(
        ChatSession session,
        string message,
        AnsweredQuestion? confirmedAnswer = null,
        List<Suggestion>? suggestions = null) => new()
    {
        SessionId = session.SessionId,
        Message = message,
        Mode = session.Mode,
        PendingDraft = session.PendingAnswersQueue.FirstOrDefault(),
        ConfirmedAnswer = confirmedAnswer,
        Suggestions = suggestions ?? [],
        AvailableEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? [],
        ExecutedEvents = session.CurrentGraphState?.ExecutedEvents.ToList() ?? [],
        IsEnded = session.CurrentGraphState?.IsAccepting ?? false
    };

    private static string RequireValue(string? value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();
}
