namespace DcrChatbot.Core.Domain.ValueObjects;

/// <summary>
/// En graf i kataloget, som borgerens første besked routes imod: titel og
/// grafens FAQ-emner (labels på choice-events med et ja-valg).
/// </summary>
public sealed class GraphCatalogEntry
{
    public string GraphId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> Topics { get; init; } = [];
}
