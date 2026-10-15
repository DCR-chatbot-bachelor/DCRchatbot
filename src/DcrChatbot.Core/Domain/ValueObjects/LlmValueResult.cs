namespace DcrChatbot.Core.Domain.ValueObjects;

public class LlmValueResult
{
    /// <summary>
    /// Svaret på det spørgsmål, botten har stillet (FR-LLM-2).
    /// </summary>
    public string? ExtractedValue { get; set; }

    /// <summary>
    /// Kort forklaring på modellens tolkning.
    /// </summary>
    public string Explanation { get; set; } = string.Empty;

    /// <summary>
    /// Afledte svar på andre åbne spørgsmål i samme besked (FR-DCR-2).
    /// Key = EventId, Value = værdien. Er kun forslag: ChatService validerer
    /// dem og lader borgeren bekræfte hvert enkelt.
    /// </summary>
    public Dictionary<string, string> InferredReplies { get; set; } = new();
}
