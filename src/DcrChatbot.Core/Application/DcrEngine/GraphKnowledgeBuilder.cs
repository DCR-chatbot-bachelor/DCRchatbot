using System.Text;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.DcrEngine;

/// <summary>
/// Gør grafens viden til almindelig tekst: FAQ-spørgsmål med deres svar og de
/// formularer, et emne åbner (FR-TEST-1). Teksten bygges fra den samme graf,
/// som DCR-tilstandene bruger, så alle tilstande har præcis samme viden, og
/// forskelle i en sammenligning kun skyldes arkitekturen.
/// </summary>
public static class GraphKnowledgeBuilder
{
    private static readonly HashSet<string> YesValues = new(["ja", "yes"], StringComparer.OrdinalIgnoreCase);

    public static string Build(DcrGraphDefinition definition)
    {
        var events = definition.Events.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();

        // FAQ: et emne (choice med ja) og de labels, det gør pending (response).
        var faq = Topics(definition)
            .Select(topic => (Topic: topic, Answers: Targets(definition, topic.Id, "response").Where(IsLabel).ToList()))
            .Where(entry => entry.Answers.Count > 0)
            .ToList();
        if (faq.Count > 0)
        {
            text.AppendLine("Ofte stillede spørgsmål:");
            foreach (var (topic, answers) in faq)
            {
                text.AppendLine();
                text.AppendLine($"Spørgsmål: {topic.Label}");
                text.AppendLine($"Svar: {string.Join(" ", answers.Select(a => a.Label))}");
            }
        }

        // Formularer: et emne, der inkluderer en nesting med felter.
        foreach (var topic in Topics(definition))
        {
            foreach (var form in Targets(definition, topic.Id, "include").Where(e => e.IsNesting))
            {
                AppendForm(text, definition, topic, form);
            }
        }

        return text.ToString().Trim();

        IEnumerable<GraphEventDefinition> Targets(DcrGraphDefinition graph, string sourceId, string type) =>
            graph.Relations
                .Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(r.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                .Select(r => events.GetValueOrDefault(r.TargetId))
                .OfType<GraphEventDefinition>()
                .OrderBy(e => e.Sequence);
    }

    private static void AppendForm(
        StringBuilder text, DcrGraphDefinition definition, GraphEventDefinition topic, GraphEventDefinition form)
    {
        var children = definition.Events.Where(e => string.Equals(e.ParentId, form.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var fields = children.Where(e => !IsLabel(e) && !IsTopic(e) && !string.IsNullOrWhiteSpace(e.DataType))
            .OrderBy(e => e.Sequence)
            .ToList();
        if (fields.Count == 0)
        {
            return;
        }

        var fieldIds = fields.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var closing = children
            .Where(e => IsLabel(e) && definition.Relations.Any(r =>
                string.Equals(r.Type, "response", StringComparison.OrdinalIgnoreCase) &&
                fieldIds.Contains(r.SourceId) &&
                string.Equals(r.TargetId, e.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var cancel = children
            .Where(e => IsTopic(e) && definition.Relations.Any(r =>
                string.Equals(r.Type, "exclude", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.SourceId, e.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.TargetId, form.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        text.AppendLine();
        text.AppendLine();
        text.AppendLine($"Formular: \"{topic.Label}\"");
        text.AppendLine("Hvis borgeren ønsker det, stilles disse spørgsmål ét ad gangen:");
        for (var i = 0; i < fields.Count; i++)
        {
            var options = fields[i].Options.Count > 0
                ? $" (vælg mellem: {string.Join(", ", fields[i].Options.Select(o => o.Label))})"
                : string.Empty;
            text.AppendLine($"{i + 1}. {fields[i].Label}{options}");
        }

        foreach (var label in closing)
        {
            text.AppendLine($"Når alle spørgsmål er besvaret: {label.Label}");
        }

        foreach (var choice in cancel)
        {
            text.AppendLine($"Borgeren kan afbryde formularen: \"{choice.Label}\"");
        }
    }

    // Et emne er et choice-spørgsmål med et ja-valg (FAQ-konventionen).
    private static IEnumerable<GraphEventDefinition> Topics(DcrGraphDefinition definition) =>
        definition.Events.Where(IsTopic).OrderBy(e => e.Sequence);

    private static bool IsTopic(GraphEventDefinition e) =>
        string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase) &&
        e.Options.Any(o => YesValues.Contains(o.Value));

    private static bool IsLabel(GraphEventDefinition e) =>
        string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase);
}
