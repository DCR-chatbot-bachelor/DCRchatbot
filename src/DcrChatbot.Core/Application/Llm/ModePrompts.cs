namespace DcrChatbot.Core.Application.Llm;

/// <summary>
/// Prompts til de tilstande, hvor LLM'en selv formulerer tekst (FR-TEST-1).
/// Samlet ét sted, så de senere kan flyttes til versionerede filer (FR-LLM-1).
/// </summary>
public static class ModePrompts
{
    /// <summary>Svaret, en grounded-prompt giver, når grafens viden ikke dækker spørgsmålet.</summary>
    public const string UnknownAnswer = "UKENDT";

    // LlmOnly: frie tøjler. Grafens viden er udgangspunkt, ikke en grænse,
    // og der er ingen DCR-kobling eller validering.
    public static string LlmOnly(string knowledge, IEnumerable<(bool FromCitizen, string Text)> history, string message) =>
        $"""
        You are a helpful assistant for citizens who contact a public authority.
        Use the knowledge below as your starting point. Answer in Danish, in a
        friendly and short way. If the citizen wants help with a form described
        in the knowledge, ask its questions one at a time and keep track of the
        answers in the conversation.

        Knowledge:
        {knowledge}

        Conversation so far:
        {FormatHistory(history)}
        Borger: {message}

        Write only your next reply to the citizen.
        """;

    // DcrGuided: DCR har valgt svaret; LLM'en må kun formulere det, ikke
    // tilføje noget. Det gør det muligt at måle, om den alligevel gør det.
    public static string RephraseAnswer(string userMessage, string question, string officialAnswer) =>
        $"""
        A citizen wrote: "{userMessage}"
        It was matched to the question: "{question}"

        The official answer is:
        ""\"
        {officialAnswer}
        ""\"

        Write the reply to the citizen in Danish. Use only information from the
        official answer: do not add facts, numbers, links or advice that are not
        in it. Focus on what the citizen asked about and keep it short. If the
        official answer does not cover exactly what they asked, give its most
        relevant part.
        """;

    // DcrGuided: en besked, der ikke matcher et emne, må besvares ud fra
    // grafens viden, men kun hvis svaret står der.
    public static string GroundedAnswer(string knowledge, string message) =>
        $"""
        Knowledge:
        {knowledge}

        Citizen message: "{message}"

        If the knowledge answers the message, reply to the citizen in Danish
        using only information from the knowledge, and keep it short. If the
        knowledge does not answer it, reply with exactly: {UnknownAnswer}
        """;

    private static string FormatHistory(IEnumerable<(bool FromCitizen, string Text)> history)
    {
        var lines = history.Select(h => $"{(h.FromCitizen ? "Borger" : "Assistent")}: {h.Text}").ToList();
        return lines.Count == 0 ? "(ingen endnu)" : string.Join("\n", lines);
    }
}
