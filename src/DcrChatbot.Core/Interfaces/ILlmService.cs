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