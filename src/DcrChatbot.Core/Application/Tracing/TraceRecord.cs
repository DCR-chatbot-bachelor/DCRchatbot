using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Core.Application.Tracing;

/// <summary>
/// Én linje i trace-loggen: alt hvad der skete for én besked/handling i en
/// samtale (FR-TEST-1). Linjerne for en session kan samles til samtalen igen.
/// </summary>
public sealed class TraceRecord
{
    public DateTimeOffset Timestamp { get; set; }
    public string? SessionId { get; set; }
    public string? GraphId { get; set; }
    public ExecutionMode? Mode { get; set; }

    /// <summary>start, message, confirm, reject, revise, edit eller return.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Borgerens tekst. Tom ved start/confirm/reject via knap.</summary>
    public string? Input { get; set; }

    /// <summary>Det event, som beskeden blev koblet til, og den foreslåede værdi.</summary>
    public string? EventId { get; set; }
    public string? ProposedValue { get; set; }

    /// <summary>Resultatet af EventValidator for hvert forslag fra LLM'en.</summary>
    public List<ValidationTrace> Validations { get; set; } = [];

    /// <summary>Udfaldet af turen, fx draft_created, event_executed, no_match.</summary>
    public string? Outcome { get; set; }

    public string? Reply { get; set; }
    public DraftTrace? PendingDraft { get; set; }
    public bool IsEnded { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public List<LlmCallTrace> LlmCalls { get; set; } = [];

    public long DurationMs { get; set; }

    /// <summary>Sat, hvis turen fejlede (exception-type og besked).</summary>
    public string? Error { get; set; }
}

public sealed class ValidationTrace
{
    public string EventId { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public bool IsInferred { get; set; }
}

public sealed class LlmCallTrace
{
    public string Operation { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public string? EventId { get; set; }
    public List<string> InferredEventIds { get; set; } = [];
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public long DurationMs { get; set; }
    public bool Failed { get; set; }
}

public sealed class DraftTrace
{
    public string EventId { get; set; } = string.Empty;
    public string? Question { get; set; }
    public string ProposedValue { get; set; } = string.Empty;
    public bool IsAutoInferred { get; set; }
}
