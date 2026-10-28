using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Drafts;

/// <summary>
/// Rettelse af et tidligere svar (FR-HITL-2): om det kan rettes, og hvad
/// rettelsen betød for borgerens andre svar.
/// </summary>
public static class AnswerRevision
{
    // Forklarer borgeren, hvorfor et svar ikke kan rettes, eller null hvis det kan.
    public static string? GetEditRefusal(DcrEvent? dcrEvent)
    {
        if (dcrEvent is null)
        {
            return "Det spørgsmål findes ikke længere i forløbet, så svaret kan ikke rettes.";
        }

        if (string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase) ||
            dcrEvent.IsExecuted != true)
        {
            return $"Du har ikke svaret på '{dcrEvent.Label}' endnu, så der er ikke noget at rette.";
        }

        if (!dcrEvent.Included)
        {
            return $"Spørgsmålet '{dcrEvent.Label}' er ikke længere en del af forløbet efter dine " +
                "andre svar, så det kan ikke rettes.";
        }

        return dcrEvent.IsEnabled
            ? null
            : $"Svaret på '{dcrEvent.Label}' kan ikke rettes lige nu, fordi forløbet ikke længere " +
              "tillader det. Det sker typisk, når et senere trin bygger videre på svaret.";
    }

    // Rettelsen bekræftes som enhver anden kladde (FR-HITL-1) og kommer
    // først i køen, foran eventuelle afledte svar.
    public static PendingAnswer QueueRevision(ChatSession session, DcrEvent dcrEvent, string value, string previousValue)
    {
        session.PendingAnswersQueue.RemoveAll(d =>
            d.PreviousValue is not null &&
            string.Equals(d.EventId, dcrEvent.Id, StringComparison.OrdinalIgnoreCase));
        var revision = new PendingAnswer
        {
            EventId = dcrEvent.Id,
            Question = dcrEvent.Label,
            ProposedValue = value,
            PreviousValue = previousValue
        };
        session.PendingAnswersQueue.Insert(0, revision);
        return revision;
    }

    // Ændringen og dens konsekvenser: svar i køen, der er ryddet, og
    // tidligere svar, som grafen har udelukket efter rettelsen.
    public static List<string> DescribeRevision(
        PendingAnswer draft,
        IReadOnlyList<PendingAnswer> droppedDrafts,
        IReadOnlyDictionary<string, DcrEvent> answersInEffectBefore,
        GraphState? stateAfter)
    {
        var parts = new List<string>
        {
            $"Dit svar på '{draft.Question}' er ændret fra '{draft.PreviousValue}' til '{draft.ProposedValue}'."
        };

        var answersInEffectAfter = FaqGraph.GetAnswersInEffect(stateAfter);
        var noLongerInEffect = answersInEffectBefore.Values
            .Where(e => !string.Equals(e.Id, draft.EventId, StringComparison.OrdinalIgnoreCase) &&
                        !answersInEffectAfter.ContainsKey(e.Id))
            .OrderBy(e => e.Sequence)
            .Select(e => e.Label)
            .Concat(droppedDrafts.Select(d => d.Question ?? d.EventId))
            .ToList();

        if (noLongerInEffect.Count > 0)
        {
            parts.Add("Det betyder, at disse svar ikke længere gælder:\n" +
                string.Join("\n", noLongerInEffect.Select(label => $"- {label}")));
        }

        return parts;
    }
}
