using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcrChatbot.Core.Application.DcrEngine;
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

    // Modellen svarer af og til med true eller 42 i stedet for "true"/"42";
    // det skal ikke vælte hele forespørgslen med en 500.
    private static readonly JsonSerializerOptions MatchResultJsonOptions = new(JsonOptions)
    {
        Converters = { new LenientStringConverter() }
    };

    private readonly HttpClient httpClient;

    public GeminiLlmService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<(string? MatchedEventId, TokenUsageResult TokenUsage)> MatchEventAsync(
        string userMessage,
        IEnumerable<DcrEvent> availableEvents,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        var events = availableEvents.Select(dcrEvent => new
        {
            id = dcrEvent.Id,
            label = dcrEvent.Label
        }).ToArray();
        var prompt = $"""
            User message:
            {userMessage}

            Available events:
            {JsonSerializer.Serialize(events)}

            Identify which available event the user is talking about.
            Do not extract or infer any value from the message.
            Return only JSON with exactly one field:
            MatchedEventId (string or null).
            """;

        var (text, usage) = await SendPromptAsync(prompt, options, cancellationToken);
        using var document = JsonDocument.Parse(text);
        var matchedEventId = document.RootElement.TryGetProperty("MatchedEventId", out var value)
            ? value.GetString()
            : document.RootElement.TryGetProperty("matchedEventId", out value)
                ? value.GetString()
                : null;
        return (matchedEventId, usage);
    }

    public async Task<(string? ExtractedValue, string Explanation, TokenUsageResult TokenUsage)> ExtractValueAsync(
        string userMessage,
        DcrEvent matchedEvent,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            User message:
            {userMessage}

            Event:
            {JsonSerializer.Serialize(new
            {
                id = matchedEvent.Id,
                label = matchedEvent.Label,
                dataType = matchedEvent.DataType,
                allowedValues = EventValidator.GetChoiceValues(matchedEvent)
            })}

            Extract only the value that answers this event. For example,
            "Jeg er 18 år" for an integer event returns "18".
            Return only JSON with exactly these fields:
            ExtractedValue (string or null), Explanation (short string).
            """;

        var (text, usage) = await SendPromptAsync(prompt, options, cancellationToken);
        var result = JsonSerializer.Deserialize<LlmValueResult>(text, MatchResultJsonOptions)
            ?? throw new LlmProviderException("Gemini returned an empty value result.");
        return (result.ExtractedValue, result.Explanation, usage);
    }

    public async Task<(bool? IsConfirmed, TokenUsageResult TokenUsage)> ClassifyConfirmationAsync(
        string userMessage,
        PendingAnswer draft,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            User message:
            {userMessage}

            Pending confirmation:
            {JsonSerializer.Serialize(new
            {
                question = draft.Question,
                proposedValue = draft.ProposedValue
            })}

            Classify whether the user confirms or rejects this pending draft.
            Understand natural language in the user's language, including
            affirmative or negative explanations. Do not execute anything and
            do not extract a new value.
            Return only JSON with exactly one field:
            IsConfirmed (boolean or null).
            Return null when the message is unrelated, ambiguous, or asks for a revision.
            """;

        var (text, usage) = await SendPromptAsync(prompt, options, cancellationToken);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var property = root.TryGetProperty("IsConfirmed", out var value)
            ? value
            : root.TryGetProperty("isConfirmed", out value)
                ? value
                : default;
        bool? result = property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
        return (result, usage);
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

        // allowedValues kommer fra samme parsing som EventValidator bruger,
        // så modellen kun kan foreslå værdier, guardrailen også accepterer.
        // Description sendes ikke: det er DCR's dokumentationsfelt og kan
        // indeholde interne noter, som ikke skal ud til en ekstern udbyder.
        var events = availableEvents
            .Select(dcrEvent => new
            {
                id = dcrEvent.Id,
                label = dcrEvent.Label,
                dataType = dcrEvent.DataType,
                allowedValues = EventValidator.GetChoiceValues(dcrEvent)
            })
            .ToArray();
        var prompt = $"""
            User message:
            {userMessage}

            Available events:
            {JsonSerializer.Serialize(events)}

            Match the user's message to the best available event and extract
            only the value for that event. For example, if the user says
            "Jeg er 18 år" for an integer event, return "18", not the full
            sentence. For a date, return the date value. For free text, return
            the user's relevant answer. Return null values when the message
            does not answer an available event.

            Return only a JSON object with exactly these fields:
            MatchedEventId (string or null), ExtractedValue (string or null),
            InferredReplies (object mapping event IDs to string values),
            IsFaqQuestion (boolean), UserIntentExplanation (short string).
            MatchedEventId must be null when no available event matches.
            ExtractedValue must be exactly one of the matched event's allowedValues,
            or null when the user did not clearly give one of them.
            All values in ExtractedValue and InferredReplies must be JSON strings.
            """;

        var (text, usage) = await SendPromptAsync(
            prompt,
            options,
            cancellationToken);
        var result = DeserializeMatchResult(text);
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
                Parts = [new GeminiPart { Text = BuildSystemPrompt(options) }]
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

    private static LlmMatchResult DeserializeMatchResult(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
            {
                json = json[(firstLineEnd + 1)..lastFence].Trim();
            }
        }

        try
        {
            var result = JsonSerializer.Deserialize<LlmMatchResult>(json, MatchResultJsonOptions)
                ?? throw new LlmProviderException("Gemini returned an empty JSON result.");
            if (result.InferredReplies is null)
            {
                result.InferredReplies = new();
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                "Gemini returned invalid structured JSON.",
                exception);
        }
    }

    private static string BuildSystemPrompt(LlmOptions options) =>
        string.IsNullOrWhiteSpace(options.SystemPrompt)
            ? "You map user messages to available DCR events. Never invent event IDs."
            : options.SystemPrompt;

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

    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }

            using var element = JsonDocument.ParseValue(ref reader);
            return element.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
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

    private sealed class LlmValueResult
    {
        public string? ExtractedValue { get; init; }
        public string Explanation { get; init; } = string.Empty;
    }
}

public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message)
        : base(message)
    {
    }

    public LlmProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}