using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Core.Application.Dtos;

public class ChatRequest
{
    public string? GraphId { get; set; }
    public string? SessionId { get; set; }
    public string Message { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; } = ExecutionMode.NeuroSymbolic;

    /// <summary>
    /// Håndterer to-faset bekræftelse / rettelse jf. FR-HITL-1 og FR-HITL-2.
    /// Værdier: null, "confirm", "reject", "revise".
    /// </summary>
    public string? Action { get; set; }
    public string? TargetPendingAnswerId { get; set; }
}