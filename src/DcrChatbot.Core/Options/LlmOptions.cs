namespace DcrChatbot.Core.Options;

using System.ComponentModel.DataAnnotations;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    [Required]
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public double Temperature { get; set; } = 0.0;
    public int MaxOutputTokens { get; set; } = 2048;
    public string PromptVersion { get; set; } = "v1";
    public string SystemPrompt { get; set; } = string.Empty;
}