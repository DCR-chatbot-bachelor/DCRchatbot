using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Core.Application.Tracing;

/// <summary>
/// Samler data om én tur (ét HTTP-kald), mens den kører. Registreres scoped,
/// så ChatService og LLM-decoratoren skriver til samme objekt, og
/// TracingChatService gemmer det som én linje bagefter.
/// </summary>
public sealed class TurnTrace
{
    private readonly object gate = new();

    public string? SessionId { get; private set; }
    public string? GraphId { get; private set; }
    public ExecutionMode? Mode { get; private set; }
    public string? EventId { get; private set; }
    public string? ProposedValue { get; private set; }
    public string? Outcome { get; private set; }
    public List<ValidationTrace> Validations { get; } = [];
    public List<LlmCallTrace> LlmCalls { get; } = [];

    public void Attach(ChatSession session)
    {
        SessionId = session.SessionId;
        GraphId = session.GraphId;
        Mode = session.Mode;
    }

    public void RecordEvent(string eventId, string? proposedValue)
    {
        EventId = eventId;
        ProposedValue = proposedValue;
    }

    public void RecordOutcome(string outcome) => Outcome = outcome;

    public void RecordValidation(string eventId, string? value, EventValidationResult result, bool isInferred)
    {
        lock (gate)
        {
            Validations.Add(new ValidationTrace
            {
                EventId = eventId,
                Value = value,
                IsValid = result.IsValid,
                Error = result.IsValid ? null : result.Error.ToString(),
                IsInferred = isInferred
            });
        }
    }

    public void RecordLlmCall(LlmCallTrace call)
    {
        lock (gate)
        {
            LlmCalls.Add(call);
        }
    }
}
