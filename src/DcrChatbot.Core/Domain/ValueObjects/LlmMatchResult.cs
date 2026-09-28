namespace DcrChatbot.Core.Domain.ValueObjects;

public class LlmMatchResult
{
    /// <summary>
    /// ID på den identificerede event (MatchedEventId) jf. FR-LLM-2.
    /// </summary>
    public string? MatchedEventId { get; set; }

    /// <summary>
    /// Den udtrukne værdi (ExtractedValue) jf. FR-LLM-2.
    /// </summary>
    public string? ExtractedValue { get; set; }

    /// <summary>
    /// Afledte svar på andre events ved multi-intent opsamling (InferredReplies) jf. FR-LLM-2 & FR-DCR-2.
    /// Key = EventId, Value = ExtractedValue.
    /// </summary>
    public Dictionary<string, string> InferredReplies { get; set; } = new();

    /// <summary>
    /// Markering af om borgerens henvendelse er et oplysende spørgsmål (IsFaqQuestion) jf. FR-LLM-2 & FR-LLM-3.
    /// </summary>
    public bool IsFaqQuestion { get; set; }

    /// <summary>
    /// Kort forklaring på modellens tolkning (UserIntentExplanation) jf. FR-LLM-2.
    /// </summary>
    public string UserIntentExplanation { get; set; } = string.Empty;
}