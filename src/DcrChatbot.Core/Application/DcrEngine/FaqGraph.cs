using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.DcrEngine;

/// <summary>
/// Opslag i grafens tilstand efter FAQ-konventionen: hvad er emner, hvilke
/// spørgsmål venter på svar, og hvilke labels er svar (FR-DCR-4). Ren
/// læsning; intet udføres i DCR.
/// </summary>
public static class FaqGraph
{
    private static readonly HashSet<string> ChoiceYesWords =
        new(["ja", "yes", "jo", "jep", "ja tak", "ok", "okay", "korrekt", "rigtigt"], StringComparer.OrdinalIgnoreCase);

    // Et spørgsmål, borgeren kan svare på: det har en datatype (en værdi at
    // udfylde) og er ikke et label eller et event, DCR udfører selv (rollen
    // Robot, fx en DMN-beslutning). Et event uden datatype ville acceptere
    // hvad som helst som svar, så botten aldrig kom videre.
    public static bool IsCitizenQuestion(DcrEvent dcrEvent) =>
        !string.IsNullOrWhiteSpace(dcrEvent.DataType) &&
        !string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase) &&
        dcrEvent.Roles?.Contains("Robot", StringComparison.OrdinalIgnoreCase) != true;

    // Grafens emner: choice-spørgsmål med et ja-valg, som borgeren bekræfter
    // med ja (FAQ-konventionen). Et formularfelt som "uddannelse" er et
    // choice uden ja-valg; det er et svar, ikke et emne, og må aldrig
    // udføres igen blot fordi borgeren nævner det. Udførte emner forbliver
    // emner, så de kan stilles igen.
    public static List<DcrEvent> GetTopicEvents(GraphState? state) =>
        state?.EnabledEvents
            .Where(e =>
                IsCitizenQuestion(e) &&
                string.Equals(e.DataType, "choice", StringComparison.OrdinalIgnoreCase) &&
                EventValidator.GetChoiceValues(e).Any(ChoiceYesWords.Contains))
            .OrderBy(e => e.Sequence)
            .ToList() ?? [];

    public static string? GetYesValue(DcrEvent dcrEvent)
    {
        var values = EventValidator.GetChoiceValues(dcrEvent);
        return values.FirstOrDefault(v => ChoiceYesWords.Contains(v)) ?? values.FirstOrDefault();
    }

    public static DcrEvent? GetNextAnswerablePendingEvent(GraphState? state) =>
        state?.GetPendingEvents().FirstOrDefault(IsCitizenQuestion);

    // Alle spørgsmål, grafen venter på svar på, også dem der først bliver
    // enabled, når et tidligere felt er udfyldt (condition-relationer).
    public static List<DcrEvent> GetOpenQuestions(GraphState? state) =>
        state?.Events
            .Where(e => e.Included && e.IsPending && e.IsExecuted != true && IsCitizenQuestion(e))
            .OrderBy(e => e.Sequence)
            .ToList() ?? [];

    // Borgeren har udfyldt formularfelter: udførte spørgsmål, der ikke er emner.
    public static bool HasFormAnswers(GraphState? state)
    {
        var topics = GetTopicEvents(state).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state?.ExecutedEvents.Any(e => e.Included && IsCitizenQuestion(e) && !topics.Contains(e.Id)) == true;
    }

    // Borgerens svar, der gælder lige nu: udførte og stadig inkluderede
    // spørgsmål (ikke labels og ikke DCR's egne Robot-events).
    public static Dictionary<string, DcrEvent> GetAnswersInEffect(GraphState? state) =>
        state?.ExecutedEvents
            .Where(e => e.Included &&
                        !string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase) &&
                        e.Roles?.Contains("Robot", StringComparison.OrdinalIgnoreCase) != true)
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase)
        ?? new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<DcrEvent> GetPendingLabels(GraphState? state) =>
        state?.EnabledEvents.Where(e =>
            e.IsPending && string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)) ?? [];

    // FAQ-svaret er de label-events, som udførslen gjorde pending via
    // response-relationen. Det permanente anker var pending i forvejen
    // og kommer derfor ikke med.
    public static List<DcrEvent> FindNewAnswers(GraphState? state, IReadOnlyCollection<string> pendingBefore) =>
        GetPendingLabels(state).Where(e => !pendingBefore.Contains(e.Id)).ToList();

    // Labels som DCR har beregnet en værdi til (fx et DMN-resultat), men som
    // endnu ikke er udført. De udføres ved forløbets afslutning, så de
    // kommer med i konklusionen. Pending labels er ikke resultater: det
    // permanente anker er pending og må aldrig udføres (FR-DCR-4).
    public static List<DcrEvent> FindResultLabels(GraphState? state) =>
        state?.EnabledEvents
            .Where(e => string.Equals(e.DataType, "label", StringComparison.OrdinalIgnoreCase)
                     && e.IsExecuted != true
                     && !e.IsPending
                     && (!string.IsNullOrWhiteSpace(e.Value) || !string.IsNullOrWhiteSpace(e.DisplayValue)))
            .ToList() ?? [];

    // I FAQ-graferne er label-eventets label selve svarteksten. Description
    // er DCR's dokumentationsfelt og kan indeholde interne noter.
    public static string GetAnswerText(DcrEvent dcrEvent)
    {
        if (!string.IsNullOrWhiteSpace(dcrEvent.DisplayValue))
        {
            return dcrEvent.DisplayValue;
        }
        if (!string.IsNullOrWhiteSpace(dcrEvent.Value))
        {
            return dcrEvent.Value;
        }
        return dcrEvent.Label;
    }
}
