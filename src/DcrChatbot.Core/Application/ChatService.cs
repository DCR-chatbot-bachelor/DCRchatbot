using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;
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

        // FR-TEST-1: LlmOnly og DcrGuided får grafens viden som tekst. Den
        // læses fra grafens definition, så intet udføres for at bygge den.
        var knowledge = request.Mode is ExecutionMode.LlmOnly or ExecutionMode.DcrGuided
            ? GraphKnowledgeBuilder.Build(await dcrRepository.GetGraphDefinitionAsync(graphId, cancellationToken))
            : null;

        // LlmOnly har ingen DCR-kobling: ingen simulation og ingen tilstand.
        if (request.Mode == ExecutionMode.LlmOnly)
        {
            var llmOnlySession = new ChatSession { GraphId = graphId, Mode = request.Mode, GraphKnowledge = knowledge };
            await sessionStore.SaveSessionAsync(llmOnlySession, cancellationToken);
            return ToResponse(llmOnlySession, "Hej! Hvad kan jeg hjælpe dig med?");
        }

        var simulationId = await dcrRepository.CreateSimulationAsync(graphId, cancellationToken);
        var state = await dcrRepository.GetGraphStateAsync(graphId, simulationId, cancellationToken);
        var session = new ChatSession
        {
            GraphId = graphId,
            Mode = request.Mode,
            SimulationId = simulationId,
            CurrentGraphState = state,
            GraphKnowledge = knowledge
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
            // NFR-4: alle LLM-kald herunder får SessionId og Mode på loglinjen.
            using var scope = BeginSessionScope(session);

            if (session.Mode == ExecutionMode.LlmOnly)
            {
                return await ReplyWithLlmOnlyAsync(session, message, cancellationToken);
            }

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

    // DcrGuided: svar-teksten fra grafen formuleres af LLM'en, kun ud fra den
    // tekst. Fejler kaldet, vises grafens tekst ordret som i DcrStrict.
    private async Task<string> RephraseAnswerAsync(
        PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken)
    {
        var question = draft.Question ?? draft.EventId;
        var prompt = ModePrompts.RephraseAnswer(draft.UserMessage ?? question, question, officialAnswer);
        try
        {
            var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();
            return reply.Length > 0 ? reply : officialAnswer;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Kunne ikke formulere svaret; grafens tekst vises ordret.");
            return officialAnswer;
        }
    }

    // DcrGuided: en besked uden emne må besvares ud fra grafens viden, men
    // kun hvis svaret står der. Ellers, og i DcrStrict, vises grafens emner.
    private async Task<ChatResponse> HelpOrGroundedAnswerAsync(
        ChatSession session, string message, CancellationToken cancellationToken)
    {
        if (session.Mode != ExecutionMode.DcrGuided || string.IsNullOrWhiteSpace(session.GraphKnowledge))
        {
            return HelpResponse(session);
        }

        try
        {
            var prompt = ModePrompts.GroundedAnswer(session.GraphKnowledge, message);
            var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();
            if (reply.Length > 0 && !reply.Contains(ModePrompts.UnknownAnswer, StringComparison.OrdinalIgnoreCase))
            {
                session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });
                return ToResponse(session, reply);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Kunne ikke svare ud fra grafens viden; viser grafens emner.");
        }

        return HelpResponse(session);
    }

    private const int LlmOnlyHistoryLength = 20;

    // LlmOnly (baseline): LLM'en svarer frit ud fra grafens viden og
    // samtalen. Intet valideres, bekræftes eller udføres i DCR.
    private async Task<ChatResponse> ReplyWithLlmOnlyAsync(
        ChatSession session, string message, CancellationToken cancellationToken)
    {
        var history = session.History
            .TakeLast(LlmOnlyHistoryLength)
            .Select(m => (FromCitizen: m.Sender == "User", Text: m.Content));
        var prompt = ModePrompts.LlmOnly(session.GraphKnowledge ?? string.Empty, history, message);
        var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();

        session.History.Add(new ChatMessage { Sender = "User", Content = message });
        session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });
        return ToResponse(session, reply);
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
            using var scope = BeginSessionScope(session);

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
    private IDisposable? BeginSessionScope(ChatSession session) =>
        logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = session.SessionId,
            ["Mode"] = session.Mode.ToString()
        });

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

            // Afledte svar til felter, der ventede på det afviste svar, kan
            // ikke udføres nu (fx adresse, når navnet blev afvist).
            DropQueuedDraftsThatNoLongerApply(session);

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
        var openQuestionsBefore = GetOpenQuestions(session.CurrentGraphState).Select(e => e.Id).ToList();
        var wasExecutedBefore = session.CurrentGraphState?.GetEvent(draft.EventId)?.IsExecuted == true;
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

        // DcrGuided: DCR har valgt svaret; LLM'en formulerer det ud fra
        // grafens tekst og borgerens spørgsmål.
        if (session.Mode == ExecutionMode.DcrGuided && draft.IsEventMatch && replyParts.Count > 0)
        {
            replyParts = [await RephraseAnswerAsync(draft, string.Join("\n\n", replyParts), cancellationToken)];
        }

        if (nextPendingEvent is not null)
        {
            replyParts.Add(nextPendingEvent.Label);
        }

        // Et emne, der ikke gør et svar pending, er ikke slutningen på et
        // forløb. Det kan have stoppet en formular (fx "jeg vil ikke ansøge
        // alligevel"), føre ind i en formular, der allerede er udfyldt, eller
        // også mangler grafen bare et svar.
        var hasNoFaqAnswer = draft.IsEventMatch && !hasQueuedDrafts && replyParts.Count == 0;
        if (hasNoFaqAnswer)
        {
            var openQuestionsAfter = GetOpenQuestions(session.CurrentGraphState).Select(e => e.Id).ToList();
            var stoppedQuestions = openQuestionsBefore.Except(openQuestionsAfter).Any();
            replyParts.Add(
                stoppedQuestions
                    ? "Okay, så stopper vi der."
                    : wasExecutedBefore && HasFormAnswers(session.CurrentGraphState)
                        ? "Det har du allerede været igennem. Du kan rette dine svar med blyanten ✎ ved dem."
                        : "Jeg har desværre ikke et svar på det spørgsmål endnu.");
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

        // Har botten stillet et spørgsmål, er beskeden et svar på det; ellers
        // matches den til et af grafens emner.
        var pendingEvent = GetNextAnswerablePendingEvent(session.CurrentGraphState);
        var candidates = GetTopicEvents(session.CurrentGraphState);
        if (pendingEvent is null && candidates.Count == 0)
        {
            return ToResponse(session, "Der er ingen tilgængelige spørgsmål lige nu.");
        }

        DcrEvent? matchedEvent;
        string? value;
        var isEventMatch = false;
        List<PendingAnswer> inferredDrafts = [];
        if (pendingEvent is not null)
        {
            matchedEvent = pendingEvent;

            // Et tryk på en svar-knap sender præcis et af grafens valg; det
            // kræver ingen tolkning, så LLM'en springes over.
            var chosenOption = FindChoiceOptionByText(pendingEvent, message);
            if (chosenOption is not null)
            {
                value = chosenOption;
            }
            else
            {
                // FR-DCR-2: de andre åbne spørgsmål sendes med, så borgeren kan
                // besvare flere af dem i samme besked; også dem, der kun venter
                // på et tidligere felt (fx adresse, mens navn spørges).
                var otherOpenEvents = GetOpenQuestions(session.CurrentGraphState)
                    .Where(e => !string.Equals(e.Id, pendingEvent.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var (extracted, _) = await llmService.ExtractValueAsync(
                    message,
                    matchedEvent,
                    otherOpenEvents,
                    llmOptions,
                    cancellationToken);
                value = extracted.ExtractedValue;

                // Med flere spørgsmål i prompten finder modellen af og til de
                // afledte svar, men ikke svaret på det stillede spørgsmål
                // ("jens, jeg bor lige her …"). Spurgt alene finder den det, så
                // i netop det tilfælde spørges der én gang til.
                if (string.IsNullOrWhiteSpace(value) && extracted.InferredReplies.Count > 0)
                {
                    var (alone, _) = await llmService.ExtractValueAsync(
                        message, matchedEvent, [], llmOptions, cancellationToken);
                    value = alone.ExtractedValue;
                }

                inferredDrafts = BuildInferredDrafts(
                    session, extracted.InferredReplies, otherOpenEvents, pendingEvent.Id);

                // Er beskeden hverken et svar eller et afledt svar, kan den
                // handle om et af grafens emner, fx "jeg vil ikke ansøge
                // alligevel" midt i formularen.
                if (inferredDrafts.Count == 0 && !IsUsableAnswer(pendingEvent, value))
                {
                    var topic = await MatchTopicAsync(message, candidates, cancellationToken);
                    if (topic is not null)
                    {
                        matchedEvent = topic;
                        value = GetYesValue(topic);
                        isEventMatch = true;
                    }
                }
            }
        }
        else
        {
            matchedEvent = await MatchTopicAsync(message, candidates, cancellationToken);
            if (matchedEvent is null)
            {
                // Fx "hvad kan jeg få hjælp til?" eller noget, grafen ikke
                // dækker: vis grafens emner i stedet for blot at afvise.
                return await HelpOrGroundedAnswerAsync(session, message, cancellationToken);
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
                    IsEventMatch = isEventMatch,
                    UserMessage = message
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

        // Nye svar erstatter parkerede svar på de samme felter.
        session.ParkedAnswers.RemoveAll(parked =>
            inferredDrafts.Concat(session.PendingAnswersQueue).Any(d =>
                string.Equals(d.EventId, parked.EventId, StringComparison.OrdinalIgnoreCase)));
        session.PendingAnswersQueue.AddRange(inferredDrafts);

        // Et afledt svar på et felt, der stadig venter på et tidligere felt,
        // kan DCR ikke udføre endnu; det parkeres, til feltet bliver klar.
        ParkBlockedDrafts(session);
        DropBlockedDraftsAtFront(session);
        if (session.PendingAnswersQueue.Count == 0)
        {
            return ToResponse(session, $"Jeg mangler stadig et svar på: {pendingEvent?.Label ?? matchedEvent.Label}");
        }

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
            if (!IsValidValue(dcrEvent, value))
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
    // være blevet udelukket, allerede besvaret eller ugyldigt. Det fjernes fra
    // køen i stedet for at blive vist for borgeren. Et felt, der kun venter
    // på et tidligere felt i køen, beholdes; det bliver enabled undervejs.
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
                !dcrEvent.Included ||
                (draft.PreviousValue is null &&
                 dcrEvent.IsExecuted == true &&
                 !string.Equals(dcrEvent.DataType, "choice", StringComparison.OrdinalIgnoreCase)) ||
                !IsValidValue(dcrEvent, draft.ProposedValue);
        }).ToList();

        session.PendingAnswersQueue.RemoveAll(dropped.Contains);
        ParkBlockedDrafts(session);
        dropped.AddRange(DropBlockedDraftsAtFront(session));
        UnparkReadyDrafts(session);
        return dropped;
    }

    // Afledte svar til felter, der ikke er enabled endnu, flyttes fra køen
    // til parkering; de kan ikke udføres nu, men skal ikke glemmes.
    private static void ParkBlockedDrafts(ChatSession session)
    {
        var blocked = session.PendingAnswersQueue
            .Where(draft => draft.ExecutionStatus == "Pending" &&
                            draft.IsAutoInferred &&
                            session.CurrentGraphState?.GetEvent(draft.EventId)?.IsEnabled != true)
            .ToList();
        foreach (var draft in blocked)
        {
            session.PendingAnswersQueue.Remove(draft);
            session.ParkedAnswers.RemoveAll(p => string.Equals(p.EventId, draft.EventId, StringComparison.OrdinalIgnoreCase));
            session.ParkedAnswers.Add(draft);
        }
    }

    // Parkerede svar, hvis felt nu er klar, sættes i køen i grafens
    // rækkefølge. Felter, der er udelukket eller allerede besvaret, glemmes.
    private static void UnparkReadyDrafts(ChatSession session)
    {
        var state = session.CurrentGraphState;
        session.ParkedAnswers.RemoveAll(draft =>
        {
            var dcrEvent = state?.GetEvent(draft.EventId);
            return dcrEvent is null || !dcrEvent.Included || dcrEvent.IsExecuted == true ||
                   !IsValidValue(dcrEvent, draft.ProposedValue);
        });

        var ready = session.ParkedAnswers
            .Where(draft => state?.GetEvent(draft.EventId)?.IsEnabled == true)
            .OrderBy(draft => state?.GetEvent(draft.EventId)?.Sequence ?? 0)
            .ToList();
        foreach (var draft in ready)
        {
            session.ParkedAnswers.Remove(draft);
            if (!session.PendingAnswersQueue.Any(d =>
                    string.Equals(d.EventId, draft.EventId, StringComparison.OrdinalIgnoreCase)))
            {
                session.PendingAnswersQueue.Add(draft);
            }
        }
    }

    // Den forreste kladde skal kunne udføres nu. Venter dens felt stadig på et
    // andet felt (fx fordi borgeren afviste det), kan den ikke bruges.
    private static List<PendingAnswer> DropBlockedDraftsAtFront(ChatSession session)
    {
        var dropped = new List<PendingAnswer>();
        while (session.PendingAnswersQueue.Count > 0)
        {
            var front = session.PendingAnswersQueue[0];
            var dcrEvent = session.CurrentGraphState?.GetEvent(front.EventId);
            if (front.ExecutionStatus != "Pending" || dcrEvent?.IsEnabled == true)
            {
                break;
            }

            session.PendingAnswersQueue.RemoveAt(0);
            dropped.Add(front);
        }

        return dropped;
    }

    // Værdien passer til feltet. Om feltet er enabled endnu, afgøres først,
    // når kladden står forrest i køen (se DropBlockedDraftsAtFront).
    private static bool IsValidValue(DcrEvent dcrEvent, string value) =>
        EventValidator.Validate(
            dcrEvent.IsEnabled
                ? dcrEvent
                : new DcrEvent
                {
                    Id = dcrEvent.Id,
                    DataType = dcrEvent.DataType,
                    ChoiceValues = dcrEvent.ChoiceValues,
                    IsEnabled = true
                },
            value).IsValid;

    private static bool IsUsableAnswer(DcrEvent question, string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        EventValidator.Validate(question, ToCanonicalValue(question, value)).IsValid;

    // Et emne matches på præcis tekst (fx fra en emne-knap) og ellers af
    // LLM'en; null, hvis beskeden ikke handler om et af grafens emner.
    private async Task<DcrEvent?> MatchTopicAsync(
        string message, IReadOnlyList<DcrEvent> topics, CancellationToken cancellationToken)
    {
        if (topics.Count == 0)
        {
            return null;
        }

        var matchedEventId = topics.FirstOrDefault(e => IsSameText(e.Label, message))?.Id
            ?? (await llmService.MatchEventAsync(message, topics, llmOptions, cancellationToken)).MatchedEventId;
        return string.IsNullOrWhiteSpace(matchedEventId)
            ? null
            : topics.FirstOrDefault(e => string.Equals(e.Id, matchedEventId, StringComparison.OrdinalIgnoreCase));
    }

    // Borgeren har udfyldt formularfelter: udførte spørgsmål, der ikke er emner.
    private static bool HasFormAnswers(GraphState? state)
    {
        var topics = GetTopicEvents(state).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state?.ExecutedEvents.Any(e => e.Included && IsCitizenQuestion(e) && !topics.Contains(e.Id)) == true;
    }

    // Alle spørgsmål, grafen venter på svar på, også dem der først bliver
    // enabled, når et tidligere felt er udfyldt (condition-relationer).
    private static List<DcrEvent> GetOpenQuestions(GraphState? state) =>
        state?.Events
            .Where(e => e.Included && e.IsPending && e.IsExecuted != true && IsCitizenQuestion(e))
            .OrderBy(e => e.Sequence)
            .ToList() ?? [];

    // Grafens egen stavemåde af en choice-værdi (fx "Denmark" frem for "denmark").
    private static string ToCanonicalValue(DcrEvent dcrEvent, string value)
    {
        var trimmed = value.Trim();
        return EventValidator.GetChoiceValues(dcrEvent)
            .FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? trimmed;
    }

    // Værdien af det valg, hvis tekst eller værdi beskeden er præcis lig med.
    private static string? FindChoiceOptionByText(DcrEvent dcrEvent, string message) =>
        EventValidator.GetChoiceOptions(dcrEvent)
            .Where(option => IsSameText(option.Label, message) || IsSameText(option.Value, message))
            .Select(option => option.Value)
            .FirstOrDefault();

    private static bool IsSameText(string? text, string message) =>
        !string.IsNullOrWhiteSpace(text) &&
        string.Equals(text.Trim(), message.Trim(), StringComparison.OrdinalIgnoreCase);

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
    // Grafens emner: choice-spørgsmål med et ja-valg, som borgeren bekræfter
    // med ja (FAQ-konventionen). Et formularfelt som "uddannelse" er et
    // choice uden ja-valg; det er et svar, ikke et emne, og må aldrig
    // udføres igen blot fordi borgeren nævner det. Udførte emner forbliver
    // emner, så de kan stilles igen.
    private static List<DcrEvent> GetTopicEvents(GraphState? state) =>
        state?.EnabledEvents
            .Where(e =>
                IsCitizenQuestion(e) &&
                string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase) &&
                EventValidator.GetChoiceValues(e).Any(ChoiceYesWords.Contains))
            .OrderBy(e => e.Sequence)
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

    // Emnerne er grafens FAQ-spørgsmål (se GetTopicEvents). Labels er svar og
    // formularspørgsmål stilles af botten selv, så de foreslås ikke.
    private static List<Suggestion> GetHelpSuggestions(GraphState? state) =>
        GetTopicEvents(state)
            .Where(e => !string.IsNullOrWhiteSpace(e.Label))
            .Take(MaxSuggestions)
            .Select(e => new Suggestion { EventId = e.Id, Label = e.Label })
            .ToList();

    // Venter botten på svar på et choice-spørgsmål, er grafens gyldige værdier
    // svar-knapper. Ikke mens en kladde er åben: der svares med Ja/Nej til den.
    private static List<AnswerOption> GetAnswerOptions(ChatSession session)
    {
        if (session.PendingAnswersQueue.Count > 0)
        {
            return [];
        }

        var question = GetNextAnswerablePendingEvent(session.CurrentGraphState);
        return question is null ||
               !string.Equals(question.DataType, "choice", StringComparison.OrdinalIgnoreCase)
            ? []
            : EventValidator.GetChoiceOptions(question)
                .Select(option => new AnswerOption { Label = option.Label, Value = option.Value })
                .ToList();
    }

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
        AnswerOptions = GetAnswerOptions(session),
        AvailableEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? [],
        ExecutedEvents = session.CurrentGraphState?.ExecutedEvents.ToList() ?? [],
        IsEnded = session.CurrentGraphState?.IsAccepting ?? false
    };

    private static string RequireValue(string? value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();
}
