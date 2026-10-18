using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.DcrEngine;

/// <summary>Ét besvaret spørgsmål i opsummeringen.</summary>
public sealed record ConclusionAnswer(string Question, string Answer);

/// <summary>
/// Konklusionen på et forløb: grafens resultater (fx et DMN-svar) og en
/// opsummering af borgerens svar.
/// </summary>
public sealed record Conclusion(IReadOnlyList<string> Results, IReadOnlyList<ConclusionAnswer> Answers)
{
    public bool IsEmpty => Results.Count == 0 && Answers.Count == 0;

    // Borgeren får kun selve resultatet at se; Answers er opsummeringen af
    // de udførte svar til brug andre steder (fx en kvittering eller log).
    public string ToText() => string.Join("\n\n", Results);
}

/// <summary>
/// Bygger konklusionen ud fra grafens tilstand (FR-DCR-5). Kun events, der
/// faktisk er udført i DCR, kommer med. Python-versionen tog alle enabled
/// labels med, så et ubesvaret eller blot synligt event kunne optræde i
/// konklusionen som om det var sket.
/// </summary>
public static class ConclusionBuilder
{
    public static Conclusion Build(GraphState? state)
    {
        var executed = state?.ExecutedEvents.OrderBy(e => e.Sequence).ToList() ?? [];

        // Udførte labels er resultater og svar, botten har vist; teksten er
        // grafens beregnede værdi, hvis den har én, ellers selve labelen.
        var results = executed
            .Where(IsLabel)
            .Select(e => FirstNonEmpty(e.DisplayValue, e.Value, e.Label))
            .OfType<string>()
            .ToList();

        // Udførte spørgsmål med en værdi er borgerens svar. Events med rollen
        // Robot udfører DCR selv (fx en DMN-beslutning); deres udfald står
        // allerede i resultat-labels og er ikke noget, borgeren har svaret.
        var answers = executed
            .Where(e => !IsLabel(e) && !IsRobotEvent(e))
            .Select(e => (Event: e, Answer: FirstNonEmpty(e.DisplayValue, e.Value)))
            .Where(x => x.Answer is not null && !string.IsNullOrWhiteSpace(x.Event.Label))
            .Select(x => new ConclusionAnswer(x.Event.Label, x.Answer!))
            .ToList();

        return new Conclusion(results, answers);
    }

    private static bool IsLabel(DcrEvent dcrEvent) =>
        string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase);

    private static bool IsRobotEvent(DcrEvent dcrEvent) =>
        dcrEvent.Roles?.Split(',', StringSplitOptions.TrimEntries)
            .Contains("Robot", StringComparer.OrdinalIgnoreCase) == true;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
