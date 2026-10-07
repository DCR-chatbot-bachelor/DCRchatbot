using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Core.Domain.Entities;

public class ChatSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string GraphId { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; } = ExecutionMode.NeuroSymbolic;
    public string? SimulationId { get; set; }
    public GraphState? CurrentGraphState { get; set; }
    public List<PendingAnswer> PendingAnswersQueue { get; set; } = new();

    /// <summary>
    /// Svar-labels der er vist for borgeren, men endnu ikke udført i DCR.
    /// Prøves igen ved næste bekræftelse, hvis udførslen fejlede.
    /// </summary>
    public List<string> AnswerLabelsToExecute { get; set; } = new();
    public List<ChatMessage> History { get; set; } = new();
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatMessage
{
    public string Sender { get; set; } = string.Empty; // "User" eller "Bot"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}