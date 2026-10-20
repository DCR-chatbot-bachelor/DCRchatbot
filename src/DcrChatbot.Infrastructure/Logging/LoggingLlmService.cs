using System.Diagnostics;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Logging;

namespace DcrChatbot.Infrastructure.Logging;

public sealed class LoggingLlmService(
    ILlmService inner,
    ILogger<LoggingLlmService> logger) : ILlmService
{
    public Task<(string? MatchedEventId, TokenUsageResult TokenUsage)> MatchEventAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default) =>
        TrackAsync(
            "MatchEvent", options,
            () => inner.MatchEventAsync(userMessage, availableEvents, options, cancellationToken),
            r => r.TokenUsage,
            r => r.MatchedEventId);

    public Task<(LlmValueResult ValueResult, TokenUsageResult TokenUsage)> ExtractValueAsync(
        string userMessage,
        DcrEvent matchedEvent,
        IEnumerable<DcrEvent> otherOpenEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default) =>
        TrackAsync(
            "ExtractValue", options,
            () => inner.ExtractValueAsync(userMessage, matchedEvent, otherOpenEvents, options, cancellationToken),
            r => r.TokenUsage,
            _ => matchedEvent.Id);

    public Task<(bool? IsConfirmed, TokenUsageResult TokenUsage)> ClassifyConfirmationAsync(
        string userMessage,
        PendingAnswer draft,
        LlmOptions options,
        CancellationToken cancellationToken = default) =>
        TrackAsync(
            "ClassifyConfirmation", options,
            () => inner.ClassifyConfirmationAsync(userMessage, draft, options, cancellationToken),
            r => r.TokenUsage,
            _ => draft.EventId);

    public Task<(LlmMatchResult MatchResult, TokenUsageResult TokenUsage)> ExtractIntentAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default) =>
        TrackAsync(
            "ExtractIntent", options,
            () => inner.ExtractIntentAsync(userMessage, availableEvents, options, cancellationToken),
            r => r.TokenUsage,
            r => r.MatchResult.MatchedEventId);

    public async Task<string> GenerateTextAsync(
        string prompt,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var text = await inner.GenerateTextAsync(prompt, options, cancellationToken);
            logger.LogInformation(
                "LLM {Operation} | {Provider} {ModelId} | tokens: ukendt | total: {TotalMs} ms",
                "GenerateText", options.Provider, options.ActiveModelId, stopwatch.ElapsedMilliseconds);
            return text;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(exception, "GenerateText", options, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    // Fælles for de fire metoder, der returnerer TokenUsageResult.
    private async Task<T> TrackAsync<T>(
        string operation,
        LlmOptions options,
        Func<Task<T>> call,
        Func<T, TokenUsageResult> usageOf,
        Func<T, string?> targetOf)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await call();
            var usage = usageOf(result);
            logger.LogInformation(
                "LLM {Operation} | {Provider} {ModelId} | tokens: {PromptTokens} ind / {CompletionTokens} ud | " +
                "llm: {LlmMs} ms, total: {TotalMs} ms | event: {EventId} | prompt {PromptVersion}",
                operation, options.Provider, options.ActiveModelId,
                usage.PromptTokens, usage.CompletionTokens,
                usage.DurationMilliseconds, stopwatch.ElapsedMilliseconds,
                targetOf(result), options.PromptVersion);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(exception, operation, options, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private void LogFailure(Exception exception, string operation, LlmOptions options, long totalMs) =>
        logger.LogError(
            exception,
            "LLM {Operation} | {Provider} {ModelId} | FEJLEDE efter {TotalMs} ms",
            operation, options.Provider, options.ActiveModelId, totalMs);
}