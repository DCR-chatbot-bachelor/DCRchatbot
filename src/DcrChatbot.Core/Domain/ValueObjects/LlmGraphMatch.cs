namespace DcrChatbot.Core.Domain.ValueObjects;

/// <summary>
/// LLM'ens valg af graf for borgerens besked. GraphId er null, når ingen
/// graf passer, eller når flere passer lige godt; i det sidste tilfælde
/// står de i CandidateGraphIds, så borgeren kan blive spurgt.
/// </summary>
public sealed class LlmGraphMatch
{
    public string? GraphId { get; set; }
    public List<string> CandidateGraphIds { get; set; } = new();
    public string Explanation { get; set; } = string.Empty;
}
