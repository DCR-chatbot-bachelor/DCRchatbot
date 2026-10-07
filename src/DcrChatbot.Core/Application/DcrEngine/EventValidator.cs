using System.Globalization;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.DcrEngine;

public enum EventValidationError
{
    None,
    EventNotFound,
    EventNotEnabled,
    LabelCannotReceiveValue,
    ValueRequired,
    ValueTypeMismatch
}

public sealed record EventValidationResult(
    bool IsValid,
    EventValidationError Error = EventValidationError.None,
    string? Message = null)
{
    public static EventValidationResult Success() => new(true);

    public static EventValidationResult Failure(EventValidationError error, string message) =>
        new(false, error, message);
}

/// <summary>
/// Symbolsk guardrail: afgør om et event reelt må udføres (FR-DCR-1).
/// Dette er sidste stop før et forslag fra LLM'en bliver sendt til DCR.Repo.
/// </summary>
public static class EventValidator
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd-MM-yyyy"];

    public static EventValidationResult Validate(DcrEvent? dcrEvent, string? value)
    {
        if (dcrEvent is null)
        {
            return EventValidationResult.Failure(
                EventValidationError.EventNotFound,
                "Event not found in the current graph state.");
        }

        if (!dcrEvent.IsEnabled)
        {
            return EventValidationResult.Failure(
                EventValidationError.EventNotEnabled,
                $"Event '{dcrEvent.Id}' is not enabled and cannot be executed.");
        }

        if (IsLabel(dcrEvent))
        {
            // Et label er en informationsbesked, ikke et spørgsmål.
            // Dette var den konkrete bug i Python-versionen: et label blev
            // udført med en tilfældig fritekst-værdi ("Velkommen"=minsu).
            return value is null
                ? EventValidationResult.Success()
                : EventValidationResult.Failure(
                    EventValidationError.LabelCannotReceiveValue,
                    $"Event '{dcrEvent.Id}' is a label and cannot receive a value.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return EventValidationResult.Failure(
                EventValidationError.ValueRequired,
                $"Event '{dcrEvent.Id}' requires a value.");
        }

        return MatchesDataType(dcrEvent, value)
            ? EventValidationResult.Success()
            : EventValidationResult.Failure(
                EventValidationError.ValueTypeMismatch,
                $"Value '{value}' does not match data type '{dcrEvent.DataType}' " +
                $"for event '{dcrEvent.Id}'.");
    }

    private static bool IsLabel(DcrEvent dcrEvent) =>
        string.Equals(dcrEvent.DataType, "label", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDataType(DcrEvent dcrEvent, string value) =>
        dcrEvent.DataType.ToLowerInvariant() switch
        {
            "choice" => GetChoiceValues(dcrEvent).Contains(value, StringComparer.OrdinalIgnoreCase),
            "integer" or "int" => int.TryParse(value, out _),
            "date" => TryParseDate(value),
            "text" or "longtext" => true,
            _ => true
        };

    private static bool TryParseDate(string value) =>
        DateTime.TryParseExact(
            value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // Midlertidig: udtrækker gyldige værdier direkte fra ChoiceValues
    // ("ja (ja), nej (nej)"), fordi DcrEvent.AllowedValues endnu ikke er
    // rettet til at skille label og værdi ad (se issuet om ReadStringArray).
    // Når den rettes, kan denne metode fjernes og erstattes med
    // dcrEvent.AllowedValues.Select(o => o.Value).
    // Offentlig, så LLM-prompten og ChatService bruger præcis de samme gyldige værdier.
    public static IReadOnlyList<string> GetChoiceValues(DcrEvent dcrEvent)
    {
        if (string.IsNullOrWhiteSpace(dcrEvent.ChoiceValues))
        {
            return [];
        }

        return dcrEvent.ChoiceValues
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ExtractValue)
            .ToList();
    }

    private static string ExtractValue(string raw)
    {
        var openParen = raw.IndexOf('(');
        var closeParen = raw.IndexOf(')');
        return openParen >= 0 && closeParen > openParen
            ? raw[(openParen + 1)..closeParen].Trim()
            : raw.Trim();
    }
}