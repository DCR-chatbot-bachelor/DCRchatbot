using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Core.Application.Routing;

/// <summary>
/// Alle grafer med titel og FAQ-emner, som borgerens første besked routes
/// imod. Kataloget hentes fra DCR.Repo én gang og caches, så der ikke
/// laves et opslag pr. graf ved hver besked. Registreres som singleton;
/// repository'et gives med ved hvert kald, så singletonen ikke holder på
/// en scoped HttpClient.
/// </summary>
public sealed class GraphCatalog(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<GraphCatalogEntry>? entries;
    private DateTimeOffset loadedAt;

    public async Task<IReadOnlyList<GraphCatalogEntry>> GetEntriesAsync(
        IDcrRepository dcrRepository, CancellationToken cancellationToken = default)
    {
        if (IsFresh())
        {
            return entries!;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsFresh())
            {
                entries = await LoadAsync(dcrRepository, cancellationToken);
                loadedAt = time.GetUtcNow();
            }

            return entries!;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsFresh() => entries is not null && time.GetUtcNow() - loadedAt < CacheDuration;

    // Kan én grafs definition ikke hentes, kommer den med på titlen alene,
    // så én defekt graf ikke lukker for routing til de andre.
    private static async Task<IReadOnlyList<GraphCatalogEntry>> LoadAsync(
        IDcrRepository dcrRepository, CancellationToken cancellationToken)
    {
        var graphs = await dcrRepository.GetGraphsAsync(cancellationToken);
        var result = new List<GraphCatalogEntry>(graphs.Count);
        foreach (var graph in graphs)
        {
            IReadOnlyList<string> topics;
            try
            {
                topics = GraphKnowledgeBuilder.GetTopicLabels(
                    await dcrRepository.GetGraphDefinitionAsync(graph.GraphId, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                topics = [];
            }

            result.Add(new GraphCatalogEntry
            {
                GraphId = graph.GraphId,
                Title = string.IsNullOrWhiteSpace(graph.Title) ? graph.GraphId : graph.Title.Trim(),
                Topics = topics
            });
        }

        return result;
    }
}
