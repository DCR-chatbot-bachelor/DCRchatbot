using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using System.Globalization;

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
                try
                {
                    await dcrRepository.ExecuteEventAsync(session.GraphId, simulationId, draft.EventId, draft.ProposedValue, cancellationToken);
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

    private ChatResponse CreateDraft(ChatSession session, string message)
    {
        var enabledEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? [];
        if (enabledEvents.Count == 0)
        {
            return ToResponse(session, "Der er ingen tilgængelige events at foreslå.");
        }

        if (!TryResolveDraftInput(message, enabledEvents, out var eventToDraft, out var proposedValue, out var validationError))
        {
            return ToResponse(session, validationError ?? "Beskeden kunne ikke tolkes som en gyldig event-besvarelse.");
        }

        var pending = new PendingAnswer
        {
            EventId = eventToDraft.Id,
            ProposedValue = proposedValue,
            Explanation = eventToDraft.Explanation ?? eventToDraft.Description,
            IsAutoInferred = true
        };
        session.PendingAnswersQueue.Add(pending);
        return ToResponse(session, $"Jeg foreslår: {eventToDraft.Label} = {proposedValue}.");
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
        if (draft.ExecutionStatus == "Executing" || draft.ExecutionStatus == "Executed")
        {
            throw new ChatConflictException("Denne kladde er allerede sendt til DCR og kan ikke fjernes.");
        }

        session.PendingAnswersQueue.Remove(draft);
    }

    private static bool TryResolveDraftInput(
        string message,
        IReadOnlyList<DcrEvent> enabledEvents,
        out DcrEvent eventToDraft,
        out string proposedValue,
        out string? validationError)
    {
        eventToDraft = null!;
        proposedValue = string.Empty;
        validationError = null;

        var trimmedMessage = message.Trim();
        if (trimmedMessage.Length == 0)
        {
            validationError = "Beskeden må ikke være tom.";
            return false;
        }

        if (TryResolveByPrefix(trimmedMessage, enabledEvents, out eventToDraft, out proposedValue))
        {
            return ValidateValue(eventToDraft, proposedValue, out validationError);
        }

        if (enabledEvents.Count == 1)
        {
            eventToDraft = enabledEvents[0];
            proposedValue = trimmedMessage;
            return ValidateValue(eventToDraft, proposedValue, out validationError);
        }

        validationError = "Vælg et event med formatet '<event-id eller navn>: værdi'.";
        return false;
    }

    private static bool TryResolveByPrefix(
        string message,
        IReadOnlyList<DcrEvent> enabledEvents,
        out DcrEvent eventToDraft,
        out string proposedValue)
    {
        eventToDraft = null!;
        proposedValue = string.Empty;

        foreach (var separator in new[] { ':', '=' })
        {
            var separatorIndex = message.IndexOf(separator);
            if (separatorIndex <= 0)
            {
                continue;
            }

            var eventKey = message[..separatorIndex].Trim();
            var value = message[(separatorIndex + 1)..].Trim();
            if (eventKey.Length == 0 || value.Length == 0)
            {
                continue;
            }

            var match = enabledEvents.FirstOrDefault(@event =>
                string.Equals(@event.Id, eventKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(@event.Label, eventKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            eventToDraft = match;
            proposedValue = value;
            return true;
        }

        return false;
    }

    private static bool ValidateValue(DcrEvent @event, string value, out string? validationError)
    {
        validationError = null;
        if (@event.AllowedValues.Count > 0)
        {
            var allowedValue = @event.AllowedValues.FirstOrDefault(allowed =>
                string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
            if (allowedValue is null)
            {
                validationError = $"Ugyldig værdi for '{@event.Label}'. Tilladte værdier: {string.Join(", ", @event.AllowedValues)}.";
                return false;
            }
        }

        return @event.DataType.ToLowerInvariant() switch
        {
            "boolean" or "bool" =>
                ValidateTypedValue(bool.TryParse(value, out _), @event, "boolean", out validationError),
            "int" or "integer" =>
                ValidateTypedValue(int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _), @event, "integer", out validationError),
            "number" or "decimal" or "double" or "float" =>
                ValidateTypedValue(decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _), @event, "number", out validationError),
            "date" =>
                ValidateTypedValue(DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), @event, "date", out validationError),
            "datetime" =>
                ValidateTypedValue(DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _), @event, "datetime", out validationError),
            _ => true
        };
    }

    private static bool ValidateTypedValue(bool isValid, DcrEvent @event, string expectedType, out string? validationError)
    {
        validationError = null;
        if (isValid)
        {
            return true;
        }

        validationError = $"Ugyldig værdi for '{@event.Label}'. Forventet datatype: {expectedType}.";
        return false;
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