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
        return ToResponse(session, "Samtalen er startet.");
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
                // Et skrevet "ja"/"nej" svarer på kladden, ligesom knapperne.
                var answer = ParseYesNo(message);
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

        // Svar fra en tidligere bekræftelse, som ikke nåede at blive udført,
        // ryddes først, så de ikke tæller med som "allerede pending" nedenfor.
        if (session.AnswerLabelsToExecute.Count > 0)
        {
            await ExecuteQueuedAnswerLabelsAsync(session, simulationId, cancellationToken);
            await RefreshStateAsync(session, cancellationToken);
        }

        var pendingLabelsBefore = GetPendingLabels(session.CurrentGraphState).Select(e => e.Id).ToHashSet();
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
            await sessionStore.SaveSessionAsync(session, cancellationToken);
            throw;
        }

        // Choice-eventet er nu udført i DCR. Gem det med det samme, så en
        // senere fejl ikke efterlader kladden som "Executing" for evigt.
        draft.ExecutionStatus = "Executed";
        session.PendingAnswersQueue.Remove(draft);
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        await RefreshStateAsync(session, cancellationToken);

        // FAQ-svaret er de label-events, som udførslen gjorde pending via
        // response-relationen. Det permanente anker var pending i forvejen
        // og kommer derfor ikke med.
        var answers = GetPendingLabels(session.CurrentGraphState)
            .Where(e => !pendingLabelsBefore.Contains(e.Id))
            .ToList();
        var reply = answers.Count == 0
            ? "Jeg har desværre ikke et svar på det spørgsmål endnu."
            : string.Join("\n\n", answers.Select(GetAnswerText));
        session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });

        // Svarene udføres, så samme spørgsmål kan stilles igen og svaret
        // vises på ny. De sættes i kø og gemmes først: fejler udførslen,
        // har borgeren stadig fået svaret, og køen prøves igen ved næste
        // bekræftelse.
        session.AnswerLabelsToExecute.AddRange(answers.Select(e => e.Id));
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

        if (answers.Count > 0)
        {
            await RefreshStateAsync(session, cancellationToken);
        }

        return ToResponse(session, reply);
    }

    // DCR.Repo kræver en tom streng som værdi for labels; null giver en serverfejl.
    private async Task ExecuteQueuedAnswerLabelsAsync(
        ChatSession session, string simulationId, CancellationToken cancellationToken)
    {
        foreach (var labelId in session.AnswerLabelsToExecute.ToList())
        {
            await dcrRepository.ExecuteEventAsync(
                session.GraphId, simulationId, labelId, string.Empty, cancellationToken);
            session.AnswerLabelsToExecute.Remove(labelId);
        }
    }

    private async Task<ChatResponse> CreateDraftAsync(
        ChatSession session, string message, CancellationToken cancellationToken)
    {
        var candidates = GetCandidateEvents(session.CurrentGraphState);
        if (candidates.Count == 0)
        {
            return ToResponse(session, "Der er ingen tilgængelige spørgsmål lige nu.");
        }

        var (match, _) = await llmService.ExtractIntentAsync(message, candidates, llmOptions, cancellationToken);

        if (string.IsNullOrWhiteSpace(match.MatchedEventId))
        {
            return ToResponse(session, "Jeg kunne ikke finde et spørgsmål der matcher det. Prøv at omformulere.");
        }

        var matchedEvent = candidates.FirstOrDefault(e =>
            string.Equals(e.Id, match.MatchedEventId, StringComparison.OrdinalIgnoreCase));

        // FAQ-flow: LLM'en finder kun spørgsmålet. Borgerens bekræftelse er
        // selve "ja"-svaret, så værdien tages fra grafen, ikke fra LLM'en.
        var value = matchedEvent is null ? null : GetYesValue(matchedEvent);
        var validation = EventValidator.Validate(matchedEvent, value);
        if (!validation.IsValid)
        {
            return ToResponse(session, ToCitizenMessage(validation.Error));
        }

        var draft = new PendingAnswer
        {
            EventId = matchedEvent!.Id,
            Question = matchedEvent.Label,
            ProposedValue = value!,
            Explanation = match.UserIntentExplanation,
            IsAutoInferred = true
        };
        session.PendingAnswersQueue.Add(draft);

        // Selve spørgsmålet vises på kladdekortet sammen med Ja/Nej-knapperne.
        return ToResponse(session, "Jeg tror, jeg har fundet dit spørgsmål. Er det det her?");
    }

    // Grafens "ja"-værdi for et choice-event, ellers den første gyldige værdi.
    private static string? GetYesValue(DcrEvent dcrEvent)
    {
        var values = EventValidator.GetChoiceValues(dcrEvent);
        return values.FirstOrDefault(v => YesWords.Contains(v)) ?? values.FirstOrDefault();
    }

    private static readonly HashSet<string> YesWords =
        new(["ja", "yes", "jo", "jep", "ja tak", "ok", "okay", "korrekt", "rigtigt"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NoWords =
        new(["nej", "no", "nej tak", "forkert"], StringComparer.OrdinalIgnoreCase);

    private static bool? ParseYesNo(string message)
    {
        var normalized = message.Trim().TrimEnd('.', '!', '?').Trim();
        return YesWords.Contains(normalized) ? true
            : NoWords.Contains(normalized) ? false
            : null;
    }

    private static IEnumerable<DcrEvent> GetPendingLabels(GraphState? state) =>
        state?.EnabledEvents.Where(e =>
            e.IsPending && string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)) ?? [];

    // I FAQ-graferne er label-eventets label selve svarteksten. Description
    // er DCR's dokumentationsfelt og kan indeholde interne noter.
    private static string GetAnswerText(DcrEvent dcrEvent) => dcrEvent.Label;

    private static string ToCitizenMessage(EventValidationError error) => error switch
    {
        EventValidationError.EventNotEnabled => "Det spørgsmål kan ikke besvares lige nu.",
        _ => "Jeg kunne ikke finde et spørgsmål der matcher det. Prøv at omformulere."
    };

    // FR-DCR-4: kun choice-events er kandidater. Label-events er aldrig
    // kandidater (de er informationsbeskeder, ikke spørgsmål), og
    // allerede udførte choice-events forbliver kandidater, så borgeren
    // kan spørge om samme emne igen, så længe det ikke er excluded.
    private static List<DcrEvent> GetCandidateEvents(GraphState? state) =>
        state?.EnabledEvents
            .Where(e => string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

    private async Task RefreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (session.SimulationId is not null)
        {
            session.CurrentGraphState = await dcrRepository.GetGraphStateAsync(
                session.GraphId, session.SimulationId, cancellationToken);
        }
    }

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