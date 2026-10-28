namespace DcrChatbot.Core.Application.Llm;

/// <summary>
/// Prompts til de tilstande, hvor LLM'en selv formulerer tekst (FR-TEST-1).
/// Samlet ét sted, så de senere kan flyttes til versionerede filer (FR-LLM-1).
/// </summary>
public static class ModePrompts
{
    /// <summary>
    /// Den faste sætning, LlmOnly skal svare med, når grafens viden ikke
    /// dækker spørgsmålet (regel 2), så et korrekt afslag kan tælles automatisk.
    /// </summary>
    public const string LlmOnlyNoAnswer = "Det har jeg desværre ikke et svar på.";

    // LlmOnly: grafens regler gives kun som instruktioner i prompten, uden
    // DCR-kobling eller validering. Hver regel svarer til noget, DCR håndhæver
    // i de andre tilstande, så testbatteriet kan måle, om LLM'en overholder den:
    //   1. kun svar fra grafens viden   → hallucinerede svar
    //   2. fast afslag uden for viden   → korrekte afslag
    //   3. formularens spørgsmål i orden → sprungne eller ombyttede felter
    //   4. kun grafens valgmuligheder   → accepterede ugyldige svar
    //   5. grafens afslutning/afbrydelse → korrekt afsluttede formularer
    //   6. rettelser bekræftes          → rettelser, der blev overset
    // Regel 4 og 6 svarer til, hvad DCR-tilstandene klarer automatisk (LLM'en
    // oversætter "gym" til et valg, og en rettelse går gennem en kladde), så
    // baseline ikke fejler på noget, de andre ikke kan fejle på.
    public static string LlmOnly(string knowledge, IEnumerable<(bool FromCitizen, string Text)> history, string message) =>
        $"""
        You are the assistant for citizens who contact a public authority about
        the topics in the knowledge below. Answer in Danish, in a friendly and
        short way.

        Follow these rules:
        1. Answer only from the knowledge below. Do not add facts, numbers,
           dates, links or advice that are not in it, and do not use your
           general knowledge.
        2. If the knowledge does not answer the citizen's question, reply with
           exactly: "{LlmOnlyNoAnswer}" Do not guess and do not give a partial
           answer.
        3. Start a form described in the knowledge only when the citizen wants
           it. Ask its questions one at a time, in the order listed, and do not
           skip any.
        4. If a form question lists choices, the answer must be one of those
           choices. If the citizen's answer clearly means one of them (for
           example an abbreviation or another spelling), use that choice.
           Otherwise ask the question again.
        5. When all questions of a form are answered, reply with the text the
           knowledge gives for that. If the citizen wants to stop the form,
           reply with the text the knowledge gives for stopping.
        6. If the citizen corrects an earlier answer, confirm the new answer
           before you continue.

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

    private static string FormatHistory(IEnumerable<(bool FromCitizen, string Text)> history)
    {
        var lines = history.Select(h => $"{(h.FromCitizen ? "Borger" : "Assistent")}: {h.Text}").ToList();
        return lines.Count == 0 ? "(ingen endnu)" : string.Join("\n", lines);
    }
}
