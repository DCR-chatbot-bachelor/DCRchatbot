namespace DcrChatbot.Core.Domain.Entities;

public class GraphState
{
    public string SimulationId { get; set; } = string.Empty;
    public string GraphId { get; set; } = string.Empty;
    public bool IsAccepting { get; set; }
    public DateTimeOffset? CurrentTime { get; set; }
    public string? NextDelay { get; set; }
    public string? NextDeadline { get; set; }
    public string? CurrentPhase { get; set; }
    public string? CurrentPhaseTitle { get; set; }
    public List<DcrEvent> Events { get; set; } = new();

    public IEnumerable<DcrEvent> EnabledEvents => Events.Where(e => e.IsEnabled);
    public IEnumerable<DcrEvent> ExecutedEvents => Events.Where(e => e.IsExecuted == true);

    /// <summary>
    /// Finder alle events, der stadig skal besvares (FR-DCR-4).
    /// Rækkefølgen fra grafen bevares, så formularspørgsmål kan stilles
    /// sekventielt. Et allerede udført event er aldrig pending igen.
    /// </summary>
    public IEnumerable<DcrEvent> GetPendingEvents() =>
        Events.Where(IsPendingEvent).OrderBy(e => e.Sequence);

    public DcrEvent? GetPendingEvent() =>
        GetPendingEvents().FirstOrDefault();

    /// <summary>
    /// Finder et event ved id. Falder tilbage til case-insensitive match,
    /// så små forskelle i store/små bogstaver ikke fejler unødigt.
    /// </summary>
    public DcrEvent? GetEvent(string id)
    {
        var exact = Events.FirstOrDefault(e => e.Id == id);
        return exact ?? Events.FirstOrDefault(
            e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPendingEvent(DcrEvent dcrEvent) =>
        dcrEvent.IsEnabled &&
        dcrEvent.IsExecuted != true &&
        (dcrEvent.IsPending || dcrEvent.IsProductive);
}