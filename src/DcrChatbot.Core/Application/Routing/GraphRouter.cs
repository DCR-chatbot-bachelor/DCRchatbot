using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;

namespace DcrChatbot.Core.Application.Routing;

/// <summary>Udfaldet af routing: én graf, flere mulige eller ingen.</summary>
public enum GraphRoutingKind
{
    Routed,
    Ambiguous,
    NotFound
}

/// <summary>
/// Beslutningen for én besked. Graph er kun sat ved Routed; Candidates er
/// de grafer, borgeren skal vælge imellem ved Ambiguous.
/// </summary>
public sealed record GraphRoutingDecision(
    GraphRoutingKind Kind,
    GraphCatalogEntry? Graph,
    IReadOnlyList<GraphCatalogEntry> Candidates,
    string Explanation);

/// <summary>
/// Finder den graf, borgerens besked handler om. Samme princip som ved
/// event-match: LLM'en må kun vælge blandt katalogets id'er, og et id, der
/// ikke findes i kataloget, behandles som "ingen graf" frem for et gæt.
/// </summary>
public sealed class GraphRouter(ILlmService llmService, LlmOptions llmOptions)
{
    public async Task<GraphRoutingDecision> RouteAsync(
        string message, IReadOnlyList<GraphCatalogEntry> catalog, CancellationToken cancellationToken)
    {
        if (catalog.Count == 0)
        {
            return new GraphRoutingDecision(GraphRoutingKind.NotFound, null, [], "Kataloget er tomt.");
        }

        // Et tryk på en emne-knap sender grafens titel; det kræver ingen LLM.
        var byTitle = catalog.FirstOrDefault(g => AnswerValues.IsSameText(g.Title, message));
        if (byTitle is not null)
        {
            return new GraphRoutingDecision(GraphRoutingKind.Routed, byTitle, [byTitle], "Grafens titel.");
        }

        var (match, _) = await llmService.MatchGraphAsync(message, catalog, llmOptions, cancellationToken);
        var explanation = match.Explanation ?? string.Empty;
        var chosen = Find(catalog, match.GraphId);
        if (chosen is not null)
        {
            return new GraphRoutingDecision(GraphRoutingKind.Routed, chosen, [chosen], explanation);
        }

        var candidates = (match.CandidateGraphIds ?? [])
            .Select(id => Find(catalog, id))
            .OfType<GraphCatalogEntry>()
            .DistinctBy(g => g.GraphId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count switch
        {
            0 => new GraphRoutingDecision(GraphRoutingKind.NotFound, null, [], explanation),
            1 => new GraphRoutingDecision(GraphRoutingKind.Routed, candidates[0], candidates, explanation),
            _ => new GraphRoutingDecision(GraphRoutingKind.Ambiguous, null, candidates, explanation)
        };
    }

    private static GraphCatalogEntry? Find(IReadOnlyList<GraphCatalogEntry> catalog, string? graphId) =>
        string.IsNullOrWhiteSpace(graphId)
            ? null
            : catalog.FirstOrDefault(g => string.Equals(g.GraphId, graphId.Trim(), StringComparison.OrdinalIgnoreCase));
}
