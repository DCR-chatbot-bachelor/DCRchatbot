namespace DcrChatbot.Core.Domain.Entities;

public class GraphState
{
    public string SimulationId { get; set; } = string.Empty;
    public string GraphId { get; set; } = string.Empty;
    public List<DcrEvent> Events { get; set; } = new();

    public IEnumerable<DcrEvent> EnabledEvents => Events.Where(e => e.IsEnabled);
    public IEnumerable<DcrEvent> ExecutedEvents => Events.Where(e => e.IsExecuted);
}