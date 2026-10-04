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
        var session = await GetSessionAsync(sessionId, cancellationToken);
        var message = RequireValue(request.Message, "Beskeden må ikke være tom.");
        session.History.Add(new ChatMessage { Sender = "User", Content = message });

        if (session.PendingAnswersQueue.Count > 0)
        {
            throw new InvalidOperationException("Kladde skal bekræftes, afvises eller rettes, før næste besked kan behandles.");
        }

        var response = CreateDraft(session, message);
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        return response;
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
        var session = await GetSessionAsync(sessionId, cancellationToken);
        RemoveDraft(session, request.TargetPendingAnswerId);
        session.History.Add(new ChatMessage { Sender = "User", Content = message });
        var response = CreateDraft(session, message);
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        return response;
    }

    private async Task<ChatResponse> ResolveDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        bool confirm,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        var draft = FindDraft(session, pendingAnswerId);

        if (confirm)
        {
            var simulationId = RequireValue(session.SimulationId, "Sessionen mangler en DCR-simulation.");
            await dcrRepository.ExecuteEventAsync(
                session.GraphId,
                simulationId,
                draft.EventId,
                draft.ProposedValue,
                cancellationToken);
            session.History.Add(new ChatMessage { Sender = "Bot", Content = "Kladde bekræftet og event udført." });
        }
        else
        {
            session.History.Add(new ChatMessage { Sender = "Bot", Content = "Kladde afvist." });
        }

        session.PendingAnswersQueue.Remove(draft);
        await RefreshStateAsync(session, cancellationToken);
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        return ToResponse(session, confirm ? "Kladde bekræftet." : "Kladde afvist.");
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

    private async Task<ChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
        return session ?? throw new KeyNotFoundException("Samtalen blev ikke fundet.");
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