using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;

namespace DcrChatbot.Infrastructure.LlmProviders;

public sealed class GeminiLlmService : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient httpClient;

    public GeminiLlmService(HttpClient httpClient)
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
        var (text, usage) = await SendPromptAsync(
            prompt,
            options,
            cancellationToken);
        var result = LlmIntentPrompt.DeserializeMatchResult(text, "Gemini");
        return (result, usage);
    }

    public async Task<string> GenerateTextAsync(
        string prompt,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateOptions(options);

        var (text, _) = await SendPromptAsync(prompt, options, cancellationToken);
        return text;
    }

    private async Task<(string Text, TokenUsageResult Usage)> SendPromptAsync(
        string prompt,
        LlmOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestUri = $"v1beta/models/{Uri.EscapeDataString(options.ModelId)}:generateContent";
        var request = new GeminiRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = [new GeminiPart { Text = LlmIntentPrompt.BuildSystemPrompt(options) }]
            },
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts = [new GeminiPart { Text = prompt }]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                ResponseMimeType = "application/json"
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        httpRequest.Headers.Add("x-goog-api-key", options.ApiKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new LlmProviderException(
                $"Gemini request failed with status {(int)response.StatusCode}: {details}");
        }

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(
            JsonOptions,
            cancellationToken);
        var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?
            .FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmProviderException("Gemini returned no text content.");
        }

        var usage = new TokenUsageResult
        {
            PromptTokens = geminiResponse?.UsageMetadata?.PromptTokenCount ?? 0,
            CompletionTokens = geminiResponse?.UsageMetadata?.CandidatesTokenCount ?? 0,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds
        };

        return (text, usage);
    }

    private static void ValidateOptions(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PromptVersion);
        if (options.MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxOutputTokens));
        }
    }

    private sealed class GeminiRequest
    {
        public GeminiContent? SystemInstruction { get; init; }
        public required GeminiContent[] Contents { get; init; }
        public required GeminiGenerationConfig GenerationConfig { get; init; }
    }

    private sealed class GeminiGenerationConfig
    {
        public double Temperature { get; init; }
        public int MaxOutputTokens { get; init; }
        public string ResponseMimeType { get; init; } = "application/json";
    }

    private sealed class GeminiContent
    {
        public string? Role { get; init; }
        public required GeminiPart[] Parts { get; init; }
    }

    private sealed class GeminiPart
    {
        public required string Text { get; init; }
    }

    private sealed class GeminiResponse
    {
        public GeminiCandidate[]? Candidates { get; init; }
        public GeminiUsageMetadata? UsageMetadata { get; init; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; init; }
    }

    private sealed class GeminiUsageMetadata
    {
        public int PromptTokenCount { get; init; }
        public int CandidatesTokenCount { get; init; }
    }
}