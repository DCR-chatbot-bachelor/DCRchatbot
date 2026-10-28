namespace DcrChatbot.Core.Application.Llm;

/// <summary>
/// Versionen af alle prompts: fortolkning (LlmIntentPrompt) og dialog
/// (ModePrompts) (FR-LLM-1). Versionen logges på hvert LLM-kald, så et
/// testresultat altid kan knyttes til de prompts, der lavede det.
/// Ændres en prompt, skal versionen hæves; PromptSnapshotTests fejler ellers.
/// </summary>
public static class PromptCatalog
{
    public const string Version = "v1";
}
