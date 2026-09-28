namespace DcrChatbot.Core.Options;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string ModelId { get; set; } = "gemini-3.6-flash";
    public double Temperature { get; set; } = 0.0;
    public int MaxOutputTokens { get; set; } = 500;
    public string PromptVersion { get; set; } = "v1";
    public string SystemPrompt { get; set; } = string.Empty;
}