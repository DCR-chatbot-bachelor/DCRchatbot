namespace DcrChatbot.Core.Domain.Entities;

public class DcrEvent
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Explanation { get; set; }
    public string DataType { get; set; } = "string"; // f.eks. string, integer, date, boolean
    public bool IsEnabled { get; set; }
    public bool IsExecuted { get; set; }
    public bool IsPending { get; set; }
    public List<string> AllowedValues { get; set; } = new();
}