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
        var pendingEvent = GetNextAnswerablePendingEvent(state);
        return ToResponse(
            session,
            pendingEvent is null ? "Samtalen er startet." : pendingEvent.Label);
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
            const string rejected = "Okay. Prøv at omformulere dit spørgsmål.";
            session.History.Add(new ChatMessage { Sender = "Bot", Content = rejected });
            await RefreshStateAsync(session, cancellationToken);
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
        var nextPendingEvent = GetNextAnswerablePendingEvent(session.CurrentGraphState);
        var replyParts = answers.Select(GetAnswerText).ToList();

        // Hvis alle spørgsmål er besvaret, find eventuelle aktive konklusioner (f.eks. DMN resultater)
        List<DcrEvent> conclusionLabels = [];
        if (nextPendingEvent is null && replyParts.Count == 0)
        {
            conclusionLabels = session.CurrentGraphState?.EnabledEvents
                .Where(e => string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)
                         && (!string.IsNullOrWhiteSpace(e.Value) || !string.IsNullOrWhiteSpace(e.DisplayValue)))
                .ToList() ?? [];

            if (conclusionLabels.Count > 0)
            {
                replyParts.AddRange(conclusionLabels.Select(GetAnswerText));
            }
        }

        if (nextPendingEvent is not null)
        {
            replyParts.Add(nextPendingEvent.Label);
        }

        var reply = replyParts.Count == 0
            ? "Tak for dine svar. Forløbet er nu afsluttet."
            : string.Join("\n\n", replyParts);

        session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });

        // Svarene udføres, så samme spørgsmål kan stilles igen og svaret
        // vises på ny. De sættes i kø og gemmes først: fejler udførslen,
        // har borgeren stadig fået svaret, og køen prøves igen ved næste
        // bekræftelse.
        session.AnswerLabelsToExecute.AddRange(answers.Concat(conclusionLabels).Select(e => e.Id));
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

        return ToResponse(session, reply);
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
        string explanation;
        var isEventMatch = false;
        if (pendingEvent is not null)
        {
            matchedEvent = pendingEvent;
            var extracted = await llmService.ExtractValueAsync(
                message,
                matchedEvent,
                llmOptions,
                cancellationToken);
            value = extracted.ExtractedValue;
            explanation = extracted.Explanation;
        }
        else
        {
            var (matchedEventId, _) = await llmService.MatchEventAsync(
                message,
                candidates,
                llmOptions,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(matchedEventId))
            {
                return ToResponse(session, "Jeg kunne ikke finde et spørgsmål der matcher det.");
            }

            matchedEvent = candidates.FirstOrDefault(e =>
                string.Equals(e.Id, matchedEventId, StringComparison.OrdinalIgnoreCase));
            if (matchedEvent is null)
            {
                return ToResponse(session, "Jeg kunne ikke finde et spørgsmål der matcher det.");
            }

            // En event-match beskæftiger sig kun med spørgsmålet. For choice-
            // events bruges grafens svarværdi; der kaldes ingen value extractor.
            value = GetYesValue(matchedEvent);
            isEventMatch = true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return ToResponse(session, "Jeg kunne ikke udtrække en værdi fra dit svar. Prøv venligst igen.");
        }
        var validation = EventValidator.Validate(matchedEvent, value);
        if (!validation.IsValid)
        {
            return ToResponse(session, ToCitizenMessage(validation.Error));
        }

        var draft = new PendingAnswer
        {
            EventId = matchedEvent!.Id,
            Question = matchedEvent.Label,
            ProposedValue = value,
            IsAutoInferred = true,
            IsEventMatch = isEventMatch
        };
        session.PendingAnswersQueue.Add(draft);

        return ToResponse(
            session,
            $"Jeg har forstået '{matchedEvent.Label}' som '{value}'. Er det korrekt?");
    }

    private static string? GetYesValue(DcrEvent dcrEvent)
    {
        var values = EventValidator.GetChoiceValues(dcrEvent);
        return values.FirstOrDefault(v => ChoiceYesWords.Contains(v)) ?? values.FirstOrDefault();
    }

    private static readonly HashSet<string> ChoiceYesWords =
        new(["ja", "yes", "jo", "jep", "ja tak", "ok", "okay", "korrekt", "rigtigt"], StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<DcrEvent> GetPendingLabels(GraphState? state) =>
        state?.EnabledEvents.Where(e =>
            e.IsPending && string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)) ?? [];

    private static DcrEvent? GetNextAnswerablePendingEvent(GraphState? state) =>
        state?.GetPendingEvents().FirstOrDefault(e =>
            !string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase));

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

    // Label-events er informationsbeskeder, ikke svarbare kandidater.
    private static List<DcrEvent> GetCandidateEvents(GraphState? state) =>
        state?.EnabledEvents
            .Where(e =>
                !string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase) &&
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

    private static ChatResponse ToResponse(ChatSession session, string message) => new()
    {
        SessionId = session.SessionId,
        Message = message,
        Mode = session.Mode,
        PendingDraft = session.PendingAnswersQueue.FirstOrDefault(),
        AvailableEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? [],
        ExecutedEvents = session.CurrentGraphState?.ExecutedEvents.ToList() ?? [],
        IsEnded = session.CurrentGraphState?.IsAccepting ?? false
    };

    private static string RequireValue(string? value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();
}