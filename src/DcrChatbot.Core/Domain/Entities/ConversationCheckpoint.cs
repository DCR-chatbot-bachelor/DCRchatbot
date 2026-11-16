namespace DcrChatbot.Core.Domain.Entities;

public class ConversationCheckpoint
{
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Grafen, tilbagegangspunktet hører til. Samtalen kan have skiftet
    /// emne siden; så skiftes der tilbage, før svarene udføres igen.
    /// </summary>
    public string GraphId { get; set; } = string.Empty;
    public List<ExecutedEvent> ExecutedEvents { get; set; } = new();
}

public class ExecutedEvent
{
    public string EventId { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}