using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.DcrEngine;

/// <summary>
/// Borgerens svarværdier: grafens stavemåde, svar-knapper og om en værdi
/// passer til et felt.
/// </summary>
public static class AnswerValues
{
    // Grafens egen stavemåde af en choice-værdi (fx "Denmark" frem for "denmark").
    public static string ToCanonicalValue(DcrEvent dcrEvent, string value)
    {
        var trimmed = value.Trim();
        return EventValidator.GetChoiceValues(dcrEvent)
            .FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? trimmed;
    }

    // Værdien af det valg, hvis tekst eller værdi beskeden er præcis lig med.
    public static string? FindChoiceOptionByText(DcrEvent dcrEvent, string message) =>
        EventValidator.GetChoiceOptions(dcrEvent)
            .Where(option => IsSameText(option.Label, message) || IsSameText(option.Value, message))
            .Select(option => option.Value)
            .FirstOrDefault();

    public static bool IsSameText(string? text, string message) =>
        !string.IsNullOrWhiteSpace(text) &&
        string.Equals(text.Trim(), message.Trim(), StringComparison.OrdinalIgnoreCase);

    // Det svar, borgeren har givet på et udført spørgsmål.
    public static string GetAnswerValue(DcrEvent dcrEvent) =>
        !string.IsNullOrWhiteSpace(dcrEvent.Value) ? dcrEvent.Value
        : !string.IsNullOrWhiteSpace(dcrEvent.DisplayValue) ? dcrEvent.DisplayValue
        : string.Empty;

    public static bool IsUsableAnswer(DcrEvent question, string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        EventValidator.Validate(question, ToCanonicalValue(question, value)).IsValid;

    // Værdien passer til feltet. Om feltet er enabled endnu, afgøres først,
    // når kladden står forrest i køen (se DraftQueue.DropBlockedDraftsAtFront).
    public static bool IsValidValue(DcrEvent dcrEvent, string value) =>
        ValidateValue(dcrEvent, value).IsValid;

    public static EventValidationResult ValidateValue(DcrEvent dcrEvent, string value) =>
        EventValidator.Validate(
            dcrEvent.IsEnabled
                ? dcrEvent
                : new DcrEvent
                {
                    Id = dcrEvent.Id,
                    DataType = dcrEvent.DataType,
                    ChoiceValues = dcrEvent.ChoiceValues,
                    IsEnabled = true
                },
            value);
}
