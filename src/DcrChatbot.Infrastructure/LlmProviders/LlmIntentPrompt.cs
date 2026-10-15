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
                dataType = dcrEvent.DataType,
                allowedValues = EventValidator.GetChoiceValues(dcrEvent)
            })
            .ToArray();

        return $"""
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
    }

    // Finder kun spørgsmålet; borgerens bekræftelse er selve svaret.
    public static string BuildMatchEventPrompt(string userMessage, IEnumerable<DcrEvent> availableEvents)
    {
        var events = availableEvents
            .Select(dcrEvent => new { id = dcrEvent.Id, label = dcrEvent.Label })
            .ToArray();

        return $"""
            User message:
            {userMessage}

            Available events:
            {JsonSerializer.Serialize(events)}

            Identify which available event the user is talking about.
            Do not extract or infer any value from the message.
            Return only JSON with exactly one field:
            MatchedEventId (string or null).
            """;
    }

    public static string? ParseMatchedEventId(string text, string providerName) =>
        Deserialize<MatchEventResult>(text, providerName).MatchedEventId;

    // Udtrækker værdien til ét bestemt event (det spørgsmål botten har stillet).
    public static string BuildValuePrompt(string userMessage, DcrEvent matchedEvent) =>
        $"""
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

    public static (string? ExtractedValue, string Explanation) ParseValueResult(string text, string providerName)
    {
        var result = Deserialize<ValueResult>(text, providerName);
        return (result.ExtractedValue, result.Explanation ?? string.Empty);
    }

    // Afgør om borgerens fritekst bekræfter eller afviser kladden.
    public static string BuildConfirmationPrompt(string userMessage, PendingAnswer draft) =>
        $"""
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

    // Alt andet end true/false (fx null eller "måske") tolkes som uafklaret.
    public static bool? ParseConfirmation(string text, string providerName) =>
        Deserialize<ConfirmationResult>(text, providerName).IsConfirmed.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    public static LlmMatchResult DeserializeMatchResult(string text, string providerName)
    {
        var json = StripCodeFence(text);
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

    private static T Deserialize<T>(string text, string providerName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StripCodeFence(text), MatchResultJsonOptions)
                ?? throw new LlmProviderException($"{providerName} returned an empty JSON result.");
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException(
                $"{providerName} returned invalid structured JSON.",
                exception);
        }
    }

    private static string StripCodeFence(string text)
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

        return json;
    }

    private sealed class MatchEventResult
    {
        public string? MatchedEventId { get; init; }
    }

    private sealed class ValueResult
    {
        public string? ExtractedValue { get; init; }
        public string? Explanation { get; init; }
    }

    private sealed class ConfirmationResult
    {
        public JsonElement IsConfirmed { get; init; }
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
