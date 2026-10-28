namespace DcrChatbot.Core.Application.Dtos;

/// <summary>
/// Et svar, der netop er udført i DCR. Frontenden viser det som et svar-kort
/// med en Ret-knap (FR-HITL-2).
/// </summary>
public class AnsweredQuestion
{
    public string EventId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsAutoInferred { get; set; }
    public string? SourceMessageId { get; set; }
}
