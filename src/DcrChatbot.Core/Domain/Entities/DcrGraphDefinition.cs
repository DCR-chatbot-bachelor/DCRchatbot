namespace DcrChatbot.Core.Domain.Entities;

/// <summary>
/// Grafens struktur, som den er modelleret i DCR (events og relationer),
/// uafhængigt af en simulation. Bruges til at give en LLM grafens viden som
/// tekst uden at udføre noget (FR-TEST-1).
/// </summary>
public sealed class DcrGraphDefinition
{
    public List<GraphEventDefinition> Events { get; set; } = new();
    public List<GraphRelation> Relations { get; set; } = new();
}

public sealed class GraphEventDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Roles { get; set; } = string.Empty;
    public int Sequence { get; set; }

    /// <summary>Den nesting (fx en formular), eventet ligger i, eller null.</summary>
    public string? ParentId { get; set; }

    /// <summary>True for en nesting/gruppe, der selv indeholder events.</summary>
    public bool IsNesting { get; set; }

    public List<GraphChoiceOption> Options { get; set; } = new();
}

public sealed class GraphChoiceOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class GraphRelation
{
    /// <summary>condition, response, include, exclude, milestone …</summary>
    public string Type { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
}
