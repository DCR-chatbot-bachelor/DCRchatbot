using System.ComponentModel.DataAnnotations;

namespace DcrChatbot.WebApi.Configuration;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    [Required]
    public string ModelId { get; init; } = string.Empty;

    [Range(0, 2)]
    public double Temperature { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxOutputTokens { get; init; }

    [Required]
    public string PromptVersion { get; init; } = string.Empty;
}