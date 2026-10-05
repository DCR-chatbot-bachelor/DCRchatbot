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
    /// Finder det næste event der skal stilles til borgeren (FR-DCR-4).
    /// Et event er pending hvis det er enabled og markeret pending,
    /// eller enabled, productive og endnu ikke udført.
    /// </summary>
    public DcrEvent? GetPendingEvent() =>
        Events.FirstOrDefault(IsPendingEvent);

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
        (dcrEvent.IsPending || (dcrEvent.IsProductive && dcrEvent.IsExecuted != true));
}