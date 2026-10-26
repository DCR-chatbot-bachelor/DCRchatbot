namespace DcrChatbot.Core.Domain.Entities;

public class PendingAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EventId { get; set; } = string.Empty;

    /// <summary>Spørgsmålet (eventets label), som borgeren bekræfter.</summary>
    public string? Question { get; set; }
    public string ProposedValue { get; set; } = string.Empty;

    /// <summary>
    /// Det nuværende svar, når kladden er en rettelse af et tidligere svar
    /// (FR-HITL-2). Null for almindelige svar.
    /// </summary>
    public string? PreviousValue { get; set; }
    public string? Explanation { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsAutoInferred { get; set; }
    public bool IsEventMatch { get; set; }
    public string ExecutionStatus { get; set; } = "Pending";
}