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
            A message that names, quotes or rephrases an event's topic matches
            that event, even if it contains words such as "help" or "hjælp".
            Return null only when the message is not about any of the events,
            for example a greeting, a general question such as "what can you
            help me with?", or a topic none of the events cover.
            Return only JSON with exactly one field:
            MatchedEventId (string or null).
            """;
    }

    public static string? ParseMatchedEventId(string text, string providerName) =>
        Deserialize<MatchEventResult>(text, providerName).MatchedEventId;

    // Udtrækker værdien til det spørgsmål botten har stillet, og eventuelle
    // svar på de andre åbne spørgsmål, som borgeren nævner i samme besked.
    public static string BuildValuePrompt(
        string userMessage, DcrEvent matchedEvent, IEnumerable<DcrEvent> otherOpenEvents) =>
        $"""
            User message:
            {userMessage}

            Event:
            {JsonSerializer.Serialize(DescribeEvent(matchedEvent))}

            Other open events:
            {JsonSerializer.Serialize(otherOpenEvents.Select(DescribeEvent))}

            Extract only the value that answers this event. For example,
            "Jeg er 18 år" for an integer event returns "18".
            Do not judge whether the answer is complete, realistic or correct:
            the citizen confirms it afterwards. For a text event, return what
            the user wrote as their answer, e.g. "jens" for a full name or
            "lige her" for an address. Return null only when the message does
            not try to answer the event at all.
            A message often answers several events at once. Take each part
            for its own event: for "jens, og jeg bor lige her og går på
            gymnasiet" asked about the name, ExtractedValue is "jens", and
            the address and education go into InferredReplies. A short or
            vague part still counts as the answer: for "derhjemme, og jeg
            læser til lærer" asked about the address, ExtractedValue is
            "derhjemme".
            If the message also clearly answers some of the other open events,
            put those answers in InferredReplies, keyed by their event id.
            Never include the main event in InferredReplies, and never guess:
            leave InferredReplies empty when nothing else is clearly answered.
            For choice events, every value must be one of their allowedValues.
            Use the choices' labels to understand the user: pick the value whose
            label best matches what they wrote, even if it is shortened or
            misspelled (for example "erhverv" for the label "Erhvervsuddannelse").
            Return only JSON with exactly these fields:
            ExtractedValue (string or null), Explanation (short string),
            InferredReplies (object mapping event IDs to string values).
            """;

    public static LlmValueResult ParseValueResult(string text, string providerName)
    {
        var result = Deserialize<LlmValueResult>(text, providerName);
        if (result.InferredReplies is null)
        {
            result.InferredReplies = new();
        }

        if (result.Explanation is null)
        {
            result.Explanation = string.Empty;
        }

        return result;
    }

    // allowedValues er det, der må svares; choices giver teksten til hver
    // værdi, så modellen kan koble "erhverv" til værdien for "Erhvervsuddannelse".
    private static object DescribeEvent(DcrEvent dcrEvent) => new
    {
        id = dcrEvent.Id,
        label = dcrEvent.Label,
        dataType = dcrEvent.DataType,
        allowedValues = EventValidator.GetChoiceValues(dcrEvent),
        choices = EventValidator.GetChoiceOptions(dcrEvent)
            .Select(option => new { label = option.Label, value = option.Value })
    };

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
