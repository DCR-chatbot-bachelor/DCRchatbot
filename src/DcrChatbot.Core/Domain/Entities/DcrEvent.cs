namespace DcrChatbot.Core.Domain.Entities;

public class DcrEvent
{
    public string Id { get; set; } = string.Empty;
    public bool Included { get; set; }
    public bool IsProductive { get; set; }
    public int Sequence { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? DisplayValue { get; set; }
    public string? Description { get; set; }
    public string? Explanation { get; set; }
    public string DataType { get; set; } = "string";
    public string? Roles { get; set; }
    public string? Type { get; set; }
    public string? Tags { get; set; }
    public string? Deadline { get; set; }
    public string? Phases { get; set; }
    public string? EventType { get; set; }
    public int? EngineDataType { get; set; }
    public string ChoiceValues { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool? IsExecuted { get; set; }
    public bool IsPending { get; set; }
    public List<string> AllowedValues { get; set; } = new();
}