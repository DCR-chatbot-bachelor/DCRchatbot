using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Options;

namespace DcrChatbot.Core.Application;

public sealed class ChatService(
    IDcrRepository dcrRepository,
    ILlmService llmService,
    ISessionStore sessionStore,
    IOptions<LlmOptions> llmOptions) : IChatService
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
                throw new ChatConflictException(
                    "Kladde skal bekræftes, afvises eller rettes, før næste besked kan behandles.");
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
        return await sessionStore.ExecuteAsync(sessionId, async session =>
        {
            var draft = FindDraft(session, pendingAnswerId);
            if (confirm)
            {
                if (draft.ExecutionStatus is "Executing" or "Executed")
                {
                    throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke udføres igen.");
                }

                // FR-HITL-1: intet event når DCR uden at borgeren først har bekræftet.
                var simulationId = RequireValue(session.SimulationId, "Sessionen mangler en DCR-simulation.");
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

                draft.ExecutionStatus = "Executed";
                session.History.Add(new ChatMessage { Sender = "Bot", Content = "Kladde bekræftet og event udført." });
            }
            else
            {
                session.History.Add(new ChatMessage { Sender = "Bot", Content = "Kladde afvist." });
            }

            session.PendingAnswersQueue.Remove(draft);
            await RefreshStateAsync(session, cancellationToken);
            return ToResponse(session, confirm ? "Kladde bekræftet." : "Kladde afvist.");
        }, cancellationToken);
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
        var validation = EventValidator.Validate(matchedEvent, match.ExtractedValue);

        if (!validation.IsValid)
        {
            return ToResponse(session, $"Det gav ikke mening: {validation.Message}");
        }

        var draft = new PendingAnswer
        {
            EventId = matchedEvent!.Id,
            ProposedValue = match.ExtractedValue!,
            Explanation = match.UserIntentExplanation,
            IsAutoInferred = true
        };
        session.PendingAnswersQueue.Add(draft);

        return ToResponse(
            session, $"Jeg har forstået '{matchedEvent.Label}' som '{draft.ProposedValue}'. Er det korrekt?");
    }

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