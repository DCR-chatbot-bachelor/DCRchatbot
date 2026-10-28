namespace DcrChatbot.Core.Application.Llm;

/// <summary>
/// Versionen af alle prompts: fortolkning (LlmIntentPrompt) og dialog
/// (ModePrompts) (FR-LLM-1). Versionen logges på hvert LLM-kald, så et
/// testresultat altid kan knyttes til de prompts, der lavede det.
/// Ændres en prompt, skal versionen hæves; PromptSnapshotTests fejler ellers.
/// </summary>
public static class PromptCatalog
{
    // v2: LlmOnly fik faste regler i stedet for frie tøjler.
    // v3: LlmOnly oversætter tydelige svar til et valg og bekræfter rettelser;
    //     DcrGuided har ikke længere en prompt til beskeder uden emne; æøå
    //     i events skrives som de er i stedet for som escape-koder.
    public const string Version = "v3";
}
