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

    /// <summary>
    /// Sat når et event er udført i DCR, men grafens nye tilstand endnu ikke
    /// er hentet. Næste handling skal hente frisk tilstand, før den fortsætter.
    /// </summary>
    public bool IsGraphStateStale { get; set; }

    /// <summary>
    /// De labels der var pending, før det seneste choice-event blev udført.
    /// Gemmes, så svar-labels kan findes igen, hvis den nye tilstand ikke
    /// kunne hentes lige efter udførslen. Null når intet er i gang.
    /// </summary>
    public List<string>? PendingLabelsBeforeExecution { get; set; }
    public List<ChatMessage> History { get; set; } = new();
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatMessage
{
    public string Sender { get; set; } = string.Empty; // "User" eller "Bot"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}