using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application;

/// <summary>
/// Bygger svaret til klienten: beskeden, den åbne kladde, grafens tilstand
/// og de knapper, borgeren kan trykke på.
/// </summary>
public static class ChatResponses
{
    private const int MaxSuggestions = 8;

    public static ChatResponse ToResponse(
        ChatSession session,
        string message,
        AnsweredQuestion? confirmedAnswer = null,
        List<Suggestion>? suggestions = null) => new()
    {
        SessionId = session.SessionId,
        Message = message,
        Mode = session.Mode,
        PendingDraft = session.PendingAnswersQueue.FirstOrDefault(),
        ConfirmedAnswer = confirmedAnswer,
        Suggestions = suggestions ?? [],
        AnswerOptions = GetAnswerOptions(session),
        AvailableEvents = session.CurrentGraphState?.EnabledEvents.ToList() ?? [],
        ExecutedEvents = session.CurrentGraphState?.ExecutedEvents.ToList() ?? [],
        IsEnded = session.CurrentGraphState?.IsAccepting ?? false
    };

    // FR-LLM-3: hjælpen kommer fra grafen, ikke fra LLM'en. Borgeren får de
    // emner, grafen tilbyder lige nu, og intet udføres i DCR.
    public static ChatResponse HelpResponse(ChatSession session)
    {
        var suggestions = GetHelpSuggestions(session.CurrentGraphState);
        return suggestions.Count == 0
            ? ToResponse(session, "Det kan jeg desværre ikke hjælpe med. Prøv at omformulere dit spørgsmål.")
            : ToResponse(
                session,
                "Det kan jeg desværre ikke svare på, men jeg kan hjælpe dig med disse emner:",
                suggestions: suggestions);
    }

    // Emnerne er grafens FAQ-spørgsmål (se FaqGraph.GetTopicEvents). Labels
    // er svar og formularspørgsmål stilles af botten selv, så de foreslås ikke.
    public static List<Suggestion> GetHelpSuggestions(GraphState? state) =>
        FaqGraph.GetTopicEvents(state)
            .Where(e => !string.IsNullOrWhiteSpace(e.Label))
            .Take(MaxSuggestions)
            .Select(e => new Suggestion { EventId = e.Id, Label = e.Label })
            .ToList();

    // Venter botten på svar på et choice-spørgsmål, er grafens gyldige værdier
    // svar-knapper. Ikke mens en kladde er åben: der svares med Ja/Nej til den.
    private static List<AnswerOption> GetAnswerOptions(ChatSession session)
    {
        if (session.PendingAnswersQueue.Count > 0)
        {
            return [];
        }

        var question = FaqGraph.GetNextAnswerablePendingEvent(session.CurrentGraphState);
        return question is null ||
               !string.Equals(question.DataType, "choice", StringComparison.OrdinalIgnoreCase)
            ? []
            : EventValidator.GetChoiceOptions(question)
                .Select(option => new AnswerOption { Label = option.Label, Value = option.Value })
                .ToList();
    }
}
