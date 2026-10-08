using System.Text.Json;
using System.Text.Json.Serialization;
using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Options;

namespace DcrChatbot.Infrastructure.LlmProviders;

/// <summary>
/// Prompt og svarformat, der er fælles for alle LLM-udbydere (NFR-2), så
/// et skift af udbyder ikke ændrer, hvad modellen bliver bedt om.
/// </summary>
internal static class LlmIntentPrompt
{
    // Modellen svarer af og til med true eller 42 i stedet for "true"/"42";
    // det skal ikke vælte hele forespørgslen med en 500.
    private static readonly JsonSerializerOptions MatchResultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new LenientStringConverter() }
    };

    public static string BuildSystemPrompt(LlmOptions options) =>
        string.IsNullOrWhiteSpace(options.SystemPrompt)
            ? "You map user messages to available DCR events. Never invent event IDs."
            : options.SystemPrompt;

    public static string BuildIntentPrompt(string userMessage, IEnumerable<DcrEvent> availableEvents)
    {
        // allowedValues kommer fra samme parsing som EventValidator bruger,
        // så modellen kun kan foreslå værdier, guardrailen også accepterer.
        // Description sendes ikke: det er DCR's dokumentationsfelt og kan
        // indeholde interne noter, som ikke skal ud til en ekstern udbyder.
        var events = availableEvents
            .Select(dcrEvent => new
            {
                id = dcrEvent.Id,
                label = dcrEvent.Label,
                allowedValues = EventValidator.GetChoiceValues(dcrEvent)
            })
            .ToArray();

        return $"""
            User message:
            {userMessage}

            Available events:
            {JsonSerializer.Serialize(events)}

            Return only a JSON object with exactly these fields:
            MatchedEventId (string or null), ExtractedValue (string or null),
            InferredReplies (object mapping event IDs to string values),
            IsFaqQuestion (boolean), UserIntentExplanation (short string).
            MatchedEventId must be null when no available event matches.
            ExtractedValue must be exactly one of the matched event's allowedValues,
            or null when the user did not clearly give one of them.
            All values in ExtractedValue and InferredReplies must be JSON strings.
            """;
    }

    public static LlmMatchResult DeserializeMatchResult(string text, string providerName)
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
                ?? throw new LlmProviderException($"{providerName} returned an empty JSON result.");
            if (result.InferredReplies is null)
            {
                result.InferredReplies = new();
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                $"{providerName} returned invalid structured JSON.",
                exception);
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
}
