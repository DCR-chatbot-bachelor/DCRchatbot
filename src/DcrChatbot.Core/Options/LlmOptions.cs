namespace DcrChatbot.Core.Options;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string ActiveModelId => Provider switch
    {
        LlmProvider.OpenAi => OpenAi.ModelId,
        _ => ModelId
    };

    public LlmProvider Provider { get; set; } = LlmProvider.Gemini;

    // ApiKey og ModelId gælder Gemini; OpenAI har sine egne under Llm:OpenAi.
    // Hvilken nøgle der er påkrævet, afhænger af Provider og valideres ved opstart.
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public OpenAiOptions OpenAi { get; set; } = new();

    public double Temperature { get; set; } = 0.0;
    public int MaxOutputTokens { get; set; } = 2048;
    // Skal matche PromptCatalog.Version; det tjekkes ved opstart.
    public string PromptVersion { get; set; } = Application.Llm.PromptCatalog.Version;
    public string SystemPrompt { get; set; } = string.Empty;

    public bool HasApiKeyForProvider() => Provider switch
    {
        LlmProvider.OpenAi => !string.IsNullOrWhiteSpace(OpenAi.ApiKey),
        _ => !string.IsNullOrWhiteSpace(ApiKey)
    };
}

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4.1-mini";
}
