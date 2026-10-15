using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Core.Domain.ValueObjects;

namespace DcrChatbot.Core.Interfaces;

public class TokenUsageResult
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public long DurationMilliseconds { get; set; }
}

public interface ILlmService
{
    Task<(string? MatchedEventId, TokenUsageResult TokenUsage)> MatchEventAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Udtrækker svaret på det stillede spørgsmål og eventuelle afledte svar
    /// på de andre åbne spørgsmål i samme besked (FR-LLM-2, FR-DCR-2).
    /// </summary>
    Task<(LlmValueResult ValueResult, TokenUsageResult TokenUsage)> ExtractValueAsync(
        string userMessage,
        DcrEvent matchedEvent,
        IEnumerable<DcrEvent> otherOpenEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    Task<(bool? IsConfirmed, TokenUsageResult TokenUsage)> ClassifyConfirmationAsync(
        string userMessage,
        PendingAnswer draft,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Trækker intention og værdier ud af borgerens fritekst som et struktureret objekt (FR-LLM-2, NFR-4).
    /// </summary>
    Task<(LlmMatchResult MatchResult, TokenUsageResult TokenUsage)> ExtractIntentAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Genererer pædagogisk fritekstsvar (f.eks. til FAQ eller opsummering).
    /// </summary>
    Task<string> GenerateTextAsync(
        string prompt,
        LlmOptions options,
        CancellationToken cancellationToken = default);
}