using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;

namespace DcrChatbot.Infrastructure.LlmProviders;

/// <summary>
/// OpenAI som alternativ LLM-udbyder bag ILlmService (NFR-2). Bruger samme
/// prompt og svarformat som Gemini via LlmIntentPrompt.
/// </summary>
public sealed class OpenAiLlmService : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient;

    public OpenAiLlmService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<(LlmMatchResult MatchResult, TokenUsageResult TokenUsage)> ExtractIntentAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(availableEvents);
        ValidateOptions(options);

        var prompt = LlmIntentPrompt.BuildIntentPrompt(userMessage, availableEvents);
        var (text, usage) = await SendPromptAsync(prompt, options, jsonResponse: true, cancellationToken);
        var result = LlmIntentPrompt.DeserializeMatchResult(text, "OpenAI");
        return (result, usage);
    }

    public async Task<string> GenerateTextAsync(
        string prompt,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateOptions(options);

        var (text, _) = await SendPromptAsync(prompt, options, jsonResponse: false, cancellationToken);
        return text;
    }

    private async Task<(string Text, TokenUsageResult Usage)> SendPromptAsync(
        string prompt,
        LlmOptions options,
        bool jsonResponse,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelId = options.OpenAi.ModelId;
        var request = new ChatCompletionRequest
        {
            Model = modelId,
            Messages =
            [
                new ChatMessage { Role = "system", Content = LlmIntentPrompt.BuildSystemPrompt(options) },
                new ChatMessage { Role = "user", Content = prompt }
            ],
            MaxCompletionTokens = options.MaxOutputTokens,
            Temperature = SupportsTemperature(modelId) ? options.Temperature : null,
            ResponseFormat = jsonResponse ? new ResponseFormat { Type = "json_object" } : null
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAi.ApiKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new LlmProviderException(
                $"OpenAI request failed with status {(int)response.StatusCode}: {details}");
        }

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
            JsonOptions,
            cancellationToken);
        var text = completion?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmProviderException("OpenAI returned no text content.");
        }

        var usage = new TokenUsageResult
        {
            PromptTokens = completion?.Usage?.PromptTokens ?? 0,
            CompletionTokens = completion?.Usage?.CompletionTokens ?? 0,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds
        };

        return (text, usage);
    }

    // Reasoning-modellerne (o-serien og gpt-5) afviser en temperatur
    // forskellig fra standardværdien, så den sendes kun til de øvrige.
    private static bool SupportsTemperature(string modelId) =>
        !(modelId.StartsWith("o", StringComparison.OrdinalIgnoreCase) ||
          modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase));

    private static void ValidateOptions(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.OpenAi);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OpenAi.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OpenAi.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PromptVersion);
        if (options.MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxOutputTokens));
        }
    }

    private sealed class ChatCompletionRequest
    {
        public required string Model { get; init; }
        public required ChatMessage[] Messages { get; init; }
        public int MaxCompletionTokens { get; init; }
        public double? Temperature { get; init; }
        public ResponseFormat? ResponseFormat { get; init; }
    }

    private sealed class ChatMessage
    {
        public required string Role { get; init; }
        public string? Content { get; init; }
    }

    private sealed class ResponseFormat
    {
        public required string Type { get; init; }
    }

    private sealed class ChatCompletionResponse
    {
        public ChatChoice[]? Choices { get; init; }
        public ChatUsage? Usage { get; init; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage? Message { get; init; }
    }

    private sealed class ChatUsage
    {
        public int PromptTokens { get; init; }
        public int CompletionTokens { get; init; }
    }
}
