namespace DcrChatbot.Core.Application.Dtos;

/// <summary>
/// Et emne, borgeren kan spørge om, taget direkte fra grafens events.
/// Vises som en knap, når botten ikke kan matche en besked (FR-LLM-3).
/// </summary>
public class Suggestion
{
    public string EventId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
