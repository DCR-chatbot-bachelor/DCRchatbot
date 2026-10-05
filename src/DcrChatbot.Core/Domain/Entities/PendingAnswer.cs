namespace DcrChatbot.Core.Domain.Entities;

public class PendingAnswer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EventId { get; set; } = string.Empty;
    public string ProposedValue { get; set; } = string.Empty;
    public string? Explanation { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsAutoInferred { get; set; }
    public string ExecutionStatus { get; set; } = "Pending";
}