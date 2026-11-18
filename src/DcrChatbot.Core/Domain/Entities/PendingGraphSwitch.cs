namespace DcrChatbot.Core.Domain.Entities;

/// <summary>
/// Et skift til en anden graf, der venter på borgerens svar, fordi de er midt
/// i en formular ("Vil du stoppe med den?"). Beskeden, der fik routing til at
/// pege på den anden graf, gemmes, så den besvares, hvis borgeren siger ja.
/// </summary>
public class PendingGraphSwitch
{
    public string GraphId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public string Explanation { get; set; } = string.Empty;
}
