namespace DcrChatbot.Core.Domain.Entities;

/// <summary>
/// En graf, samtalen har forladt for at tale om et andet emne. Det, der
/// hører til grafen, gemmes her, så samtalen kan fortsætte, hvor borgeren
/// slap, hvis de vender tilbage til emnet.
/// </summary>
public class SuspendedGraph
{
    public string GraphId { get; set; } = string.Empty;
    public string? SimulationId { get; set; }
    public List<string> RecommendedTopicIds { get; set; } = new();
    public List<PendingAnswer> ParkedAnswers { get; set; } = new();
    public List<string> AnswerLabelsToExecute { get; set; } = new();
    public List<ExecutedEvent> ExecutedEvents { get; set; } = new();
}
