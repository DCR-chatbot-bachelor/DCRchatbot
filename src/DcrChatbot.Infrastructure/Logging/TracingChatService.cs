using System.Diagnostics;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DcrChatbot.Infrastructure.Logging;

/// <summary>
/// Decorator om IChatService: skriver én trace-linje pr. besked/handling med
/// input, valgt event, validering, svar, mode og tokens (FR-TEST-1).
/// Fejl ved skrivning af loggen må aldrig vælte selve samtalen.
/// </summary>
public sealed class TracingChatService(
    IChatService inner,
    TurnTrace turn,
    ITraceStore traceStore,
    ILogger<TracingChatService> logger) : IChatService
{
    public Task<ChatResponse> StartAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        TraceAsync("start", request.SessionId, null, () => inner.StartAsync(request, cancellationToken));

    public Task<ChatResponse> SendMessageAsync(
        string sessionId, ChatRequest request, CancellationToken cancellationToken = default) =>
        TraceAsync("message", sessionId, request.Message,
            () => inner.SendMessageAsync(sessionId, request, cancellationToken));

    public Task<ChatResponse> ConfirmDraftAsync(
        string sessionId, string? pendingAnswerId, CancellationToken cancellationToken = default) =>
        TraceAsync("confirm", sessionId, null,
            () => inner.ConfirmDraftAsync(sessionId, pendingAnswerId, cancellationToken));

    public Task<ChatResponse> RejectDraftAsync(
        string sessionId, string? pendingAnswerId, CancellationToken cancellationToken = default) =>
        TraceAsync("reject", sessionId, null,
            () => inner.RejectDraftAsync(sessionId, pendingAnswerId, cancellationToken));

    public Task<ChatResponse> ReviseDraftAsync(
        string sessionId, ChatRequest request, CancellationToken cancellationToken = default) =>
        TraceAsync("revise", sessionId, request.Message,
            () => inner.ReviseDraftAsync(sessionId, request, cancellationToken));

    public Task<ChatResponse> EditAnswerAsync(
        string sessionId, ChatRequest request, CancellationToken cancellationToken = default) =>
        TraceAsync("edit", sessionId, request.Message,
            () => inner.EditAnswerAsync(sessionId, request, cancellationToken));

    private async Task<ChatResponse> TraceAsync(
        string action, string? sessionId, string? input, Func<Task<ChatResponse>> call)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        ChatResponse? response = null;
        Exception? error = null;
        try
        {
            response = await call();
            return response;
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            await WriteAsync(BuildRecord(action, sessionId, input, timestamp, stopwatch.ElapsedMilliseconds, response, error));
        }
    }

    private TraceRecord BuildRecord(
        string action,
        string? sessionId,
        string? input,
        DateTimeOffset timestamp,
        long durationMs,
        ChatResponse? response,
        Exception? error)
    {
        var draft = response?.PendingDraft;
        return new TraceRecord
        {
            Timestamp = timestamp,
            SessionId = response?.SessionId ?? turn.SessionId ?? sessionId,
            GraphId = turn.GraphId,
            Mode = response?.Mode ?? turn.Mode,
            Action = action,
            Input = input,
            EventId = turn.EventId,
            ProposedValue = turn.ProposedValue,
            Validations = turn.Validations.ToList(),
            Outcome = error is null ? turn.Outcome : "error",
            Reply = response?.Message,
            PendingDraft = draft is null ? null : new DraftTrace
            {
                EventId = draft.EventId,
                Question = draft.Question,
                ProposedValue = draft.ProposedValue,
                IsAutoInferred = draft.IsAutoInferred
            },
            IsEnded = response?.IsEnded ?? false,
            PromptTokens = turn.LlmCalls.Sum(c => c.PromptTokens),
            CompletionTokens = turn.LlmCalls.Sum(c => c.CompletionTokens),
            LlmCalls = turn.LlmCalls.ToList(),
            DurationMs = durationMs,
            Error = error is null ? null : $"{error.GetType().Name}: {error.Message}"
        };
    }

    private async Task WriteAsync(TraceRecord record)
    {
        try
        {
            // CancellationToken.None: turen skal logges, også hvis klienten afbrød.
            await traceStore.AppendAsync(record, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kunne ikke skrive trace-linje for session {SessionId}.", record.SessionId);
        }
    }
}
