using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Core.Application.Dtos;

public class ChatResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; }

    /// <summary>
    /// Aktuelt udkast i Draft-tilstand, der kræver borgerens bekræftelse jf. FR-HITL-1.
    /// </summary>
    public PendingAnswer? PendingDraft { get; set; }

    public bool RequiresConfirmation => PendingDraft != null;

    /// <summary>
    /// Svaret, der blev udført i denne forespørgsel, så det kan rettes senere (FR-HITL-2).
    /// </summary>
    public AnsweredQuestion? ConfirmedAnswer { get; set; }

    /// <summary>
    /// Emner fra grafen, som borgeren kan vælge imellem, når beskeden ikke
    /// matchede et event (FR-LLM-3). Tom i alle andre svar.
    /// </summary>
    public List<Suggestion> Suggestions { get; set; } = new();
    public List<DcrEvent> AvailableEvents { get; set; } = new();
    public List<DcrEvent> ExecutedEvents { get; set; } = new();
    public bool IsEnded { get; set; }
}