using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Core.Application;

public sealed class ChatService(
    IDcrRepository dcrRepository,
    ISessionStore sessionStore) : IChatService
{
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
        return await sessionStore.ExecuteAsync(sessionId, session =>
        {
            session.History.Add(new ChatMessage { Sender = "User", Content = message });
            if (session.PendingAnswersQueue.Count > 0)
            {
                throw new ChatConflictException("Kladde skal bekræftes, afvises eller rettes, før næste besked kan behandles.");
            }

            return Task.FromResult(CreateDraft(session, message));
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
        return await sessionStore.ExecuteAsync(sessionId, session =>
        {
            RemoveDraft(session, request.TargetPendingAnswerId);
            session.History.Add(new ChatMessage { Sender = "User", Content = message });
            return Task.FromResult(CreateDraft(session, message));
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
                if (draft.ExecutionStatus == "Executing" || draft.ExecutionStatus == "Executed")
                {
                    throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke udføres igen.");
                }

                var simulationId = RequireValue(session.SimulationId, "Sessionen mangler en DCR-simulation.");
                draft.ExecutionStatus = "Executing";
                await sessionStore.SaveSessionAsync(session, cancellationToken);
                await dcrRepository.ExecuteEventAsync(session.GraphId, simulationId, draft.EventId, draft.ProposedValue, cancellationToken);
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

    private ChatResponse CreateDraft(ChatSession session, string message)
    {
        var eventToDraft = session.CurrentGraphState?.EnabledEvents.FirstOrDefault();
        if (eventToDraft is null)
        {
            return ToResponse(session, "Der er ingen tilgængelige events at foreslå.");
        }

        var pending = new PendingAnswer
        {
            EventId = eventToDraft.Id,
            ProposedValue = message,
            Explanation = eventToDraft.Explanation ?? eventToDraft.Description,
            IsAutoInferred = true
        };
        session.PendingAnswersQueue.Add(pending);
        return ToResponse(session, $"Jeg foreslår: {eventToDraft.Label} = {message}.");
    }

    private async Task RefreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (session.SimulationId is not null)
        {
            session.CurrentGraphState = await dcrRepository.GetGraphStateAsync(
                session.GraphId,
                session.SimulationId,
                cancellationToken);
        }
    }

    private static PendingAnswer FindDraft(ChatSession session, string? pendingAnswerId) =>
        session.PendingAnswersQueue.FirstOrDefault(draft =>
            string.IsNullOrWhiteSpace(pendingAnswerId) || draft.Id == pendingAnswerId)
        ?? throw new KeyNotFoundException("Den ønskede kladde blev ikke fundet.");

    private static void RemoveDraft(ChatSession session, string? pendingAnswerId)
    {
        var draft = FindDraft(session, pendingAnswerId);
        session.PendingAnswersQueue.Remove(draft);
    }

    private static ChatResponse ToResponse(ChatSession session, string message) => new()
    {
        SessionId = session.SessionId,
        Message = message,
        Mode = session.Mode,
        PendingDraft = session.PendingAnswersQueue.FirstOrDefault(),
        AvailableEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? new(),
        ExecutedEvents = session.CurrentGraphState?.ExecutedEvents.ToList() ?? new(),
        IsEnded = session.CurrentGraphState?.IsAccepting ?? false
    };

    private static string RequireValue(string? value, string error) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(error) : value.Trim();
}