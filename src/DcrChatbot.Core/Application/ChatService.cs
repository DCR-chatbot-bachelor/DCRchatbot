using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Drafts;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Modes;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static DcrChatbot.Core.Application.ChatResponses;

namespace DcrChatbot.Core.Application;

/// <summary>
/// Styrer samtalen: borgerens besked tolkes, bliver til en kladde, som
/// borgeren bekræfter (FR-HITL-1), og først derefter udføres den i DCR.
/// Opslag i grafen, kladdekøen og de tre tilstande ligger i egne klasser.
/// </summary>
public sealed class ChatService(
    IDcrRepository dcrRepository,
    ILlmService llmService,
    ISessionStore sessionStore,
    IOptions<LlmOptions> llmOptions,
    TurnTrace trace,
    ILogger<ChatService> logger) : IChatService
{
    private readonly LlmOptions llmOptions = llmOptions.Value;
    private readonly SimulationSync simulation = new(dcrRepository, sessionStore);
    private readonly LlmOnlyConversation llmOnly = new(llmService, llmOptions.Value, trace);
    private readonly IDcrReplyStrategy strictReplies = new StrictReplies();
    private readonly IDcrReplyStrategy guidedReplies = new GuidedReplies(llmService, llmOptions.Value, logger);

    public async Task<ChatResponse> StartAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var graphId = RequireValue(request.GraphId, "GraphId er påkrævet ved start af en samtale.");

        // FR-TEST-1: LlmOnly har ingen DCR-kobling, kun grafens viden som
        // tekst. Den læses fra grafens definition, så intet udføres for at
        // bygge den, og der oprettes ingen simulation.
        if (request.Mode == ExecutionMode.LlmOnly)
        {
            var knowledge = GraphKnowledgeBuilder.Build(
                await dcrRepository.GetGraphDefinitionAsync(graphId, cancellationToken));
            var llmOnlySession = new ChatSession { GraphId = graphId, Mode = request.Mode, GraphKnowledge = knowledge };
            await sessionStore.SaveSessionAsync(llmOnlySession, cancellationToken);
            return ToResponse(llmOnlySession, LlmOnlyConversation.Greeting);
        }

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
        trace.Attach(session);
        trace.RecordOutcome("started");

        // En formular starter med sit første spørgsmål; en FAQ-graf har intet
        // spørgsmål at stille, så den viser i stedet de emner, den dækker.
        var pendingEvent = FaqGraph.GetNextAnswerablePendingEvent(state);
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
            trace.Attach(session);

            if (session.Mode == ExecutionMode.LlmOnly)
            {
                return await llmOnly.ReplyAsync(session, message, cancellationToken);
            }

            session.History.Add(new ChatMessage { MessageId = request.MessageId, Sender = "User", Content = message });
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

            return await CreateDraftAsync(session, message, request.MessageId, cancellationToken);
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
            using var scope = BeginSessionScope(session);
            trace.Attach(session);

            var existingDraft = DraftQueue.FindDraft(session, request.TargetPendingAnswerId);
            if (existingDraft.ExecutionStatus is "Executing" or "Executed")
            {
                throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke rettes.");
            }

            session.PendingAnswersQueue.Remove(existingDraft);
            session.History.Add(new ChatMessage { Sender = "User", Content = message });
            return await CreateDraftAsync(session, message, null, cancellationToken);
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
            using var scope = BeginSessionScope(session);
            trace.Attach(session);
            trace.RecordEvent(eventId, null);
            session.History.Add(new ChatMessage { Sender = "User", Content = message });

            // FR-HITL-2: spørg DCR, ikke sessionens kopi, om svaret stadig kan rettes.
            session.IsGraphStateStale = true;
            await simulation.EnsureFreshStateAsync(session, cancellationToken);
            var dcrEvent = session.CurrentGraphState?.GetEvent(eventId);
            var refusal = AnswerRevision.GetEditRefusal(dcrEvent);
            if (refusal is not null)
            {
                trace.RecordOutcome("edit_refused");
                session.History.Add(new ChatMessage { Sender = "Bot", Content = refusal });
                return ToResponse(session, refusal);
            }

            var (extracted, _) = await llmService.ExtractValueAsync(
                message, dcrEvent!, [], llmOptions, cancellationToken);
            var value = string.IsNullOrWhiteSpace(extracted.ExtractedValue)
                ? null
                : AnswerValues.ToCanonicalValue(dcrEvent!, extracted.ExtractedValue);
            trace.RecordEvent(dcrEvent!.Id, value);
            var validation = value is null ? null : EventValidator.Validate(dcrEvent, value);
            if (validation is not null)
            {
                trace.RecordValidation(dcrEvent.Id, value!, validation, isInferred: false);
            }

            if (validation?.IsValid != true)
            {
                trace.RecordOutcome(value is null ? "no_value" : "validation_failed");
                return ToResponse(
                    session,
                    $"Jeg kunne ikke forstå det som et svar på '{dcrEvent.Label}'. Prøv at skrive det igen.");
            }

            var previousValue = AnswerValues.GetAnswerValue(dcrEvent);
            if (string.Equals(value, previousValue, StringComparison.OrdinalIgnoreCase))
            {
                trace.RecordOutcome("edit_unchanged");
                return ToResponse(session, $"Dit svar på '{dcrEvent.Label}' er allerede '{previousValue}'.");
            }

            AnswerRevision.QueueRevision(session, dcrEvent, value!, previousValue);
            trace.RecordOutcome("draft_created");
            return ToResponse(
                session,
                $"Vil du ændre dit svar på '{dcrEvent.Label}' fra '{previousValue}' til '{value}'?");
        }, cancellationToken);
    }

    public async Task<ChatResponse> ReturnToMessageAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var messageId = RequireValue(request.ReturnToMessageId, "MessageId er påkrævet for at gå tilbage.");
        return await sessionStore.ExecuteAsync(sessionId, async session =>
        {
            using var scope = BeginSessionScope(session);
            trace.Attach(session);
            var checkpoint = session.Checkpoints.FirstOrDefault(c => c.MessageId == messageId)
                ?? throw new ChatConflictException("Denne besked kan ikke længere bruges som tilbagegangspunkt.");

            await simulation.ReplayAsync(session, checkpoint.ExecutedEvents, cancellationToken);
            session.PendingAnswersQueue.Clear();
            session.ParkedAnswers.Clear();
            session.AnswerLabelsToExecute.Clear();
            session.PendingLabelsBeforeExecution = null;
            var checkpointIndex = session.Checkpoints.IndexOf(checkpoint);
            session.Checkpoints = session.Checkpoints.Take(checkpointIndex).ToList();
            session.History = session.History
                .TakeWhile(history => history.MessageId != messageId)
                .Concat(session.History.Where(history => history.MessageId == messageId).Take(1))
                .ToList();

            var nextQuestion = FaqGraph.GetNextAnswerablePendingEvent(session.CurrentGraphState);
            var responseText = nextQuestion?.Label ?? "Okay, vi er gået tilbage hertil.";
            trace.RecordOutcome("returned_to_message");
            session.History.Add(new ChatMessage { Sender = "Bot", Content = responseText });
            return ToResponse(session, responseText);
        }, cancellationToken);
    }

    // ---- Ny besked: tolk den og lav en kladde ----

    // Tolkningen af en besked: hvilket event den gælder, med hvilken værdi,
    // om den matchede et emne, og de afledte svar på andre åbne spørgsmål.
    private sealed record Interpretation(
        DcrEvent Event, string? Value, bool IsEventMatch, List<PendingAnswer> InferredDrafts);

    private async Task<ChatResponse> CreateDraftAsync(
        ChatSession session, string message, string? messageId, CancellationToken cancellationToken)
    {
        await simulation.EnsureFreshStateAsync(session, cancellationToken);

        // Har botten stillet et spørgsmål, er beskeden et svar på det; ellers
        // matches den til et af grafens emner.
        var pendingEvent = FaqGraph.GetNextAnswerablePendingEvent(session.CurrentGraphState);
        var candidates = FaqGraph.GetTopicEvents(session.CurrentGraphState);
        if (pendingEvent is null && candidates.Count == 0)
        {
            trace.RecordOutcome("no_candidates");
            return ToResponse(session, "Der er ingen tilgængelige spørgsmål lige nu.");
        }

        Interpretation interpretation;
        if (pendingEvent is not null)
        {
            AddCheckpoint(session, messageId);
            interpretation = await InterpretAnswerAsync(session, pendingEvent, candidates, message, messageId, cancellationToken);
        }
        else
        {
            var topic = await MatchTopicAsync(message, candidates, cancellationToken);
            if (topic is null)
            {
                // Fx "hvad kan jeg få hjælp til?" eller noget, grafen ikke
                // dækker: vis grafens emner i stedet for blot at afvise. Også
                // i DcrGuided: et svar, DCR ikke har valgt, kunne føre
                // samtalen ud på et sidespor, som grafen ikke kender.
                trace.RecordOutcome("no_match");
                return HelpResponse(session);
            }

            // En event-match beskæftiger sig kun med spørgsmålet. For choice-
            // events bruges grafens svarværdi; der kaldes ingen value extractor.
            interpretation = new Interpretation(topic, FaqGraph.GetYesValue(topic), IsEventMatch: true, []);
        }

        var (matchedEvent, value, isEventMatch, inferredDrafts) = interpretation;
        trace.RecordEvent(matchedEvent.Id, value);

        // Svaret på det stillede spørgsmål kommer først i køen. Kan det ikke
        // bruges, men har borgeren svaret på andre åbne spørgsmål, bekræftes
        // de i stedet, og spørgsmålet stilles igen bagefter.
        if (string.IsNullOrWhiteSpace(value))
        {
            if (inferredDrafts.Count == 0)
            {
                // Botten har stillet et spørgsmål; den bedste hjælp er at
                // minde om det i stedet for blot at bede om en omformulering.
                trace.RecordOutcome("no_value");
                return ToResponse(session, $"Jeg mangler stadig et svar på: {matchedEvent.Label}");
            }
        }
        else
        {
            value = AnswerValues.ToCanonicalValue(matchedEvent, value);
            var validation = EventValidator.Validate(matchedEvent, value);
            trace.RecordEvent(matchedEvent.Id, value);
            trace.RecordValidation(matchedEvent.Id, value, validation, isInferred: false);
            if (validation.IsValid)
            {
                session.PendingAnswersQueue.Add(new PendingAnswer
                {
                    EventId = matchedEvent.Id,
                    Question = matchedEvent.Label,
                    ProposedValue = value,
                    SourceMessageId = messageId,
                    IsAutoInferred = false,
                    IsEventMatch = isEventMatch,
                    UserMessage = message
                });
            }
            else if (inferredDrafts.Count == 0)
            {
                trace.RecordOutcome("validation_failed");
                return ToResponse(
                    session,
                    isEventMatch
                        ? ToCitizenMessage(validation.Error)
                        : $"Det svar passer ikke til spørgsmålet: {matchedEvent.Label}");
            }
        }

        DraftQueue.QueueInferredDrafts(session, inferredDrafts);
        if (session.PendingAnswersQueue.Count == 0)
        {
            trace.RecordOutcome("answers_parked");
            return ToResponse(session, $"Jeg mangler stadig et svar på: {pendingEvent?.Label ?? matchedEvent.Label}");
        }

        trace.RecordOutcome("draft_created");

        // Kladderne vises ét ad gangen; beskeden beskriver den første.
        var first = session.PendingAnswersQueue[0];
        return ToResponse(
            session,
            $"Jeg har forstået '{first.Question}' som '{first.ProposedValue}'. Er det korrekt?");
    }

    // Beskeden er et svar på det spørgsmål, botten har stillet, og måske på
    // flere åbne spørgsmål på én gang (FR-DCR-2).
    private async Task<Interpretation> InterpretAnswerAsync(
        ChatSession session,
        DcrEvent pendingEvent,
        IReadOnlyList<DcrEvent> candidates,
        string message,
        string? messageId,
        CancellationToken cancellationToken)
    {
        // Et tryk på en svar-knap sender præcis et af grafens valg; det
        // kræver ingen tolkning, så LLM'en springes over.
        var chosenOption = AnswerValues.FindChoiceOptionByText(pendingEvent, message);
        if (chosenOption is not null)
        {
            return new Interpretation(pendingEvent, chosenOption, IsEventMatch: false, []);
        }

        // FR-DCR-2: de andre åbne spørgsmål sendes med, så borgeren kan
        // besvare flere af dem i samme besked; også dem, der kun venter
        // på et tidligere felt (fx adresse, mens navn spørges).
        var otherOpenEvents = FaqGraph.GetOpenQuestions(session.CurrentGraphState)
            .Where(e => !string.Equals(e.Id, pendingEvent.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var (extracted, _) = await llmService.ExtractValueAsync(
            message,
            pendingEvent,
            otherOpenEvents,
            llmOptions,
            cancellationToken);
        var value = extracted.ExtractedValue;

        // Med flere spørgsmål i prompten finder modellen af og til de
        // afledte svar, men ikke svaret på det stillede spørgsmål
        // ("jens, jeg bor lige her …"). Spurgt alene finder den det, så
        // i netop det tilfælde spørges der én gang til.
        if (string.IsNullOrWhiteSpace(value) && extracted.InferredReplies.Count > 0)
        {
            var (alone, _) = await llmService.ExtractValueAsync(
                message, pendingEvent, [], llmOptions, cancellationToken);
            value = alone.ExtractedValue;
        }

        var inferredDrafts = DraftQueue.BuildInferredDrafts(
            session, extracted.InferredReplies, otherOpenEvents, pendingEvent.Id, messageId, trace);

        // Er beskeden hverken et svar eller et afledt svar, kan den
        // handle om et af grafens emner, fx "jeg vil ikke ansøge
        // alligevel" midt i formularen.
        if (inferredDrafts.Count == 0 && !AnswerValues.IsUsableAnswer(pendingEvent, value))
        {
            var topic = await MatchTopicAsync(message, candidates, cancellationToken);
            if (topic is not null)
            {
                return new Interpretation(topic, FaqGraph.GetYesValue(topic), IsEventMatch: true, inferredDrafts);
            }
        }

        return new Interpretation(pendingEvent, value, IsEventMatch: false, inferredDrafts);
    }

    // Et tilbagegangspunkt før svaret på et formularspørgsmål, så borgeren
    // kan gå tilbage hertil og svare igen.
    private static void AddCheckpoint(ChatSession session, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId) || session.Checkpoints.Any(c => c.MessageId == messageId))
        {
            return;
        }

        session.Checkpoints.Add(new ConversationCheckpoint
        {
            MessageId = messageId,
            ExecutedEvents = session.ExecutedEvents
                .Select(e => new ExecutedEvent { EventId = e.EventId, Value = e.Value })
                .ToList()
        });
    }

    // Et emne matches på præcis tekst (fx fra en emne-knap) og ellers af
    // LLM'en; null, hvis beskeden ikke handler om et af grafens emner.
    private async Task<DcrEvent?> MatchTopicAsync(
        string message, IReadOnlyList<DcrEvent> topics, CancellationToken cancellationToken)
    {
        if (topics.Count == 0)
        {
            return null;
        }

        var matchedEventId = topics.FirstOrDefault(e => AnswerValues.IsSameText(e.Label, message))?.Id
            ?? (await llmService.MatchEventAsync(message, topics, llmOptions, cancellationToken)).MatchedEventId;
        return string.IsNullOrWhiteSpace(matchedEventId)
            ? null
            : topics.FirstOrDefault(e => string.Equals(e.Id, matchedEventId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Kladde bekræftet eller afvist ----

    // Grafens tilstand lige før kladden blev udført; svaret bagefter
    // beskrives ud fra, hvad udførslen ændrede.
    private sealed record StateBeforeExecution(
        List<string> PendingLabels,
        Dictionary<string, DcrEvent> AnswersInEffect,
        List<string> OpenQuestions,
        bool WasExecuted);

    private async Task<ChatResponse> ResolveDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        return await sessionStore.ExecuteAsync(
            sessionId,
            session =>
            {
                trace.Attach(session);
                return ResolveDraftCoreAsync(
                    session, DraftQueue.FindDraft(session, pendingAnswerId), confirm, cancellationToken);
            },
            cancellationToken);
    }

    private async Task<ChatResponse> ResolveDraftCoreAsync(
        ChatSession session,
        PendingAnswer draft,
        bool confirm,
        CancellationToken cancellationToken)
    {
        trace.RecordEvent(draft.EventId, draft.ProposedValue);
        return confirm
            ? await HandleConfirmedDraftAsync(session, draft, cancellationToken)
            : await HandleRejectedDraftAsync(session, draft, cancellationToken);
    }

    private async Task<ChatResponse> HandleRejectedDraftAsync(
        ChatSession session, PendingAnswer draft, CancellationToken cancellationToken)
    {
        trace.RecordOutcome("draft_rejected");
        session.PendingAnswersQueue.Remove(draft);
        await simulation.RefreshStateAsync(session, cancellationToken);

        // Afledte svar til felter, der ventede på det afviste svar, kan
        // ikke udføres nu (fx adresse, når navnet blev afvist).
        DraftQueue.DropQueuedDraftsThatNoLongerApply(session);

        // Et afvist svar i et formularforløb: stil det spørgsmål igen,
        // botten stadig mangler svar på, frem for at bede om en ny formulering.
        var openQuestion = FaqGraph.GetNextAnswerablePendingEvent(session.CurrentGraphState);
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

    private async Task<ChatResponse> HandleConfirmedDraftAsync(
        ChatSession session, PendingAnswer draft, CancellationToken cancellationToken)
    {
        if (draft.ExecutionStatus is "Executing" or "Executed")
        {
            throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke udføres igen.");
        }

        // FR-HITL-1: intet event når DCR uden at borgeren først har bekræftet.
        var simulationId = RequireValue(session.SimulationId, "Sessionen mangler en DCR-simulation.");
        var before = await ExecuteDraftAsync(session, draft, simulationId, cancellationToken);

        var answers = FaqGraph.FindNewAnswers(session.CurrentGraphState, before.PendingLabels);
        session.PendingLabelsBeforeExecution = null;
        var droppedDrafts = DraftQueue.DropQueuedDraftsThatNoLongerApply(session);

        // FR-HITL-2: en rettelse bekræftes med de konsekvenser, den fik.
        var revisionParts = draft.PreviousValue is null
            ? []
            : AnswerRevision.DescribeRevision(draft, droppedDrafts, before.AnswersInEffect, session.CurrentGraphState);

        // Er der flere kladder i køen (FR-DCR-2), vises den næste i stedet
        // for et nyt spørgsmål eller en konklusion.
        var hasQueuedDrafts = session.PendingAnswersQueue.Count > 0;
        var nextPendingEvent = hasQueuedDrafts ? null : FaqGraph.GetNextAnswerablePendingEvent(session.CurrentGraphState);
        var replyParts = answers.Select(FaqGraph.GetAnswerText).ToList();

        // DCR har valgt svaret; tilstanden afgør, hvordan det formuleres.
        if (draft.IsEventMatch && replyParts.Count > 0)
        {
            replyParts = [await RepliesFor(session).FormulateAnswerAsync(
                draft, string.Join("\n\n", replyParts), cancellationToken)];
        }

        if (nextPendingEvent is not null)
        {
            replyParts.Add(nextPendingEvent.Label);
        }

        if (draft.IsEventMatch && !hasQueuedDrafts && replyParts.Count == 0)
        {
            replyParts.Add(DescribeTopicWithoutAnswer(session, before));
        }

        // En formular er slut, når der hverken er kladder, spørgsmål eller svar
        // tilbage. Grafens resultat-labels (fx et DMN-svar) udføres så først,
        // så konklusionen kun bygger på udførte events (FR-DCR-5).
        var isFinished = !draft.IsEventMatch && !hasQueuedDrafts && nextPendingEvent is null && replyParts.Count == 0;
        var resultLabels = isFinished ? FaqGraph.FindResultLabels(session.CurrentGraphState) : [];
        await ExecuteAnswerLabelsAsync(session, simulationId, answers.Concat(resultLabels), cancellationToken);

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
                Value = draft.ProposedValue,
                IsAutoInferred = draft.IsAutoInferred,
                SourceMessageId = draft.SourceMessageId
            };
        return ToResponse(session, reply, confirmedAnswer);
    }

    // Udfører kladden i DCR og henter grafens nye tilstand. Returnerer
    // tilstanden fra lige før, så svaret kan beskrive, hvad der ændrede sig.
    private async Task<StateBeforeExecution> ExecuteDraftAsync(
        ChatSession session, PendingAnswer draft, string simulationId, CancellationToken cancellationToken)
    {
        // "Pending før" skal beregnes ud fra grafens faktiske tilstand. Svar
        // fra en tidligere bekræftelse, som ikke nåede at blive udført,
        // ryddes først, så de ikke tæller med som "allerede pending".
        await simulation.EnsureFreshStateAsync(session, cancellationToken);
        if (session.AnswerLabelsToExecute.Count > 0)
        {
            await simulation.ExecuteQueuedAnswerLabelsAsync(session, simulationId, cancellationToken);
            await simulation.EnsureFreshStateAsync(session, cancellationToken);
        }

        var state = session.CurrentGraphState;
        var before = new StateBeforeExecution(
            FaqGraph.GetPendingLabels(state).Select(e => e.Id).ToList(),
            FaqGraph.GetAnswersInEffect(state),
            FaqGraph.GetOpenQuestions(state).Select(e => e.Id).ToList(),
            state?.GetEvent(draft.EventId)?.IsExecuted == true);
        session.PendingLabelsBeforeExecution = before.PendingLabels;
        draft.ExecutionStatus = "Executing";
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        try
        {
            await simulation.ExecuteEventAsync(
                session, simulationId, draft.EventId, draft.ProposedValue, cancellationToken);
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
        trace.RecordOutcome("event_executed");
        session.PendingAnswersQueue.Remove(draft);
        session.IsGraphStateStale = true;
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        await simulation.RefreshStateAsync(session, cancellationToken);
        return before;
    }

    // Et emne, der ikke gør et svar pending, er ikke slutningen på et
    // forløb. Det kan have stoppet en formular (fx "jeg vil ikke ansøge
    // alligevel"), føre ind i en formular, der allerede er udfyldt, eller
    // også mangler grafen bare et svar.
    private static string DescribeTopicWithoutAnswer(ChatSession session, StateBeforeExecution before)
    {
        var openQuestionsAfter = FaqGraph.GetOpenQuestions(session.CurrentGraphState).Select(e => e.Id).ToList();
        var stoppedQuestions = before.OpenQuestions.Except(openQuestionsAfter).Any();
        return stoppedQuestions
            ? "Okay, så stopper vi der."
            : before.WasExecuted && FaqGraph.HasFormAnswers(session.CurrentGraphState)
                ? "Det har du allerede været igennem. Du kan rette dine svar med blyanten ✎ ved dem."
                : "Jeg har desværre ikke et svar på det spørgsmål endnu.";
    }

    // Svarene udføres, så samme spørgsmål kan stilles igen og svaret vises
    // på ny. De sættes i kø og gemmes først: fejler udførslen, har borgeren
    // stadig fået svaret, og køen prøves igen ved næste bekræftelse.
    private async Task ExecuteAnswerLabelsAsync(
        ChatSession session, string simulationId, IEnumerable<DcrEvent> labels, CancellationToken cancellationToken)
    {
        session.AnswerLabelsToExecute.AddRange(labels.Select(e => e.Id));
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        try
        {
            await simulation.ExecuteQueuedAnswerLabelsAsync(session, simulationId, cancellationToken);
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
            await simulation.EnsureFreshStateAsync(session, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Kunne ikke hente grafens tilstand i session {SessionId}; hentes ved næste handling.",
                session.SessionId);
        }
    }

    // ---- Fælles ----

    // FR-TEST-1: det eneste, DcrStrict og DcrGuided gør forskelligt.
    private IDcrReplyStrategy RepliesFor(ChatSession session) =>
        session.Mode == ExecutionMode.DcrGuided ? guidedReplies : strictReplies;

    private IDisposable? BeginSessionScope(ChatSession session) =>
        logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = session.SessionId,
            ["Mode"] = session.Mode.ToString()
        });

    private static string ToCitizenMessage(EventValidationError error) => error switch
    {
        EventValidationError.EventNotEnabled => "Det spørgsmål kan ikke besvares lige nu.",
        _ => "Jeg kunne ikke finde et spørgsmål der matcher det. Prøv at omformulere."
    };

    private static string RequireValue(string? value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();
}
