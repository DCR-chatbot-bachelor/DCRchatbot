using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Drafts;

/// <summary>
/// Sessionens kø af kladder, der venter på borgerens bekræftelse
/// (FR-HITL-1), og de parkerede afledte svar (FR-DCR-2). Kladderne vises ét
/// ad gangen; den forreste skal altid kunne udføres i DCR.
/// </summary>
public static class DraftQueue
{
    public static PendingAnswer FindDraft(ChatSession session, string? pendingAnswerId) =>
        session.PendingAnswersQueue.FirstOrDefault(draft =>
            string.IsNullOrWhiteSpace(pendingAnswerId) || draft.Id == pendingAnswerId)
        ?? throw new KeyNotFoundException("Den ønskede kladde blev ikke fundet.");

    // FR-DCR-2: afledte svar fra LLM'en er kun forslag. De bliver til
    // kladder, når de hører til et åbent spørgsmål, ikke allerede er i køen
    // og har en gyldig værdi; rækkefølgen følger grafen.
    public static List<PendingAnswer> BuildInferredDrafts(
        ChatSession session,
        IReadOnlyDictionary<string, string> inferredReplies,
        IReadOnlyList<DcrEvent> openEvents,
        string primaryEventId,
        string? sourceMessageId,
        TurnTrace trace)
    {
        var replies = new Dictionary<string, string>(inferredReplies, StringComparer.OrdinalIgnoreCase);
        var drafts = new List<PendingAnswer>();
        foreach (var dcrEvent in openEvents)
        {
            if (!replies.TryGetValue(dcrEvent.Id, out var rawValue) ||
                string.IsNullOrWhiteSpace(rawValue) ||
                dcrEvent.IsExecuted == true ||
                string.Equals(dcrEvent.Id, primaryEventId, StringComparison.OrdinalIgnoreCase) ||
                session.PendingAnswersQueue.Any(d =>
                    string.Equals(d.EventId, dcrEvent.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = AnswerValues.ToCanonicalValue(dcrEvent, rawValue);
            var validation = AnswerValues.ValidateValue(dcrEvent, value);
            trace.RecordValidation(dcrEvent.Id, value, validation, isInferred: true);
            if (!validation.IsValid)
            {
                continue;
            }

            drafts.Add(new PendingAnswer
            {
                EventId = dcrEvent.Id,
                Question = dcrEvent.Label,
                ProposedValue = value,
                SourceMessageId = sourceMessageId,
                IsAutoInferred = true
            });
        }

        return drafts;
    }

    // Nye afledte svar sættes i køen og erstatter parkerede svar på de samme
    // felter. Et svar på et felt, der stadig venter på et tidligere felt, kan
    // DCR ikke udføre endnu; det parkeres, til feltet bliver klar.
    public static void QueueInferredDrafts(ChatSession session, IReadOnlyList<PendingAnswer> inferredDrafts)
    {
        session.ParkedAnswers.RemoveAll(parked =>
            inferredDrafts.Concat(session.PendingAnswersQueue).Any(d =>
                string.Equals(d.EventId, parked.EventId, StringComparison.OrdinalIgnoreCase)));
        session.PendingAnswersQueue.AddRange(inferredDrafts);

        ParkBlockedDrafts(session);
        DropBlockedDraftsAtFront(session);
    }

    // Når et svar er udført, kan grafen have ændret sig: et afledt svar kan
    // være blevet udelukket, allerede besvaret eller ugyldigt. Det fjernes fra
    // køen i stedet for at blive vist for borgeren. Et felt, der kun venter
    // på et tidligere felt i køen, beholdes; det bliver enabled undervejs.
    public static List<PendingAnswer> DropQueuedDraftsThatNoLongerApply(ChatSession session)
    {
        var dropped = session.PendingAnswersQueue.Where(draft =>
        {
            // En rettelse gælder et svar, der allerede er udført, så "udført"
            // gør den ikke ugyldig; ellers er reglerne de samme.
            if (draft.ExecutionStatus != "Pending")
            {
                return false;
            }

            var dcrEvent = session.CurrentGraphState?.GetEvent(draft.EventId);
            return dcrEvent is null ||
                !dcrEvent.Included ||
                (draft.PreviousValue is null &&
                 dcrEvent.IsExecuted == true &&
                 !string.Equals(dcrEvent.DataType, "choice", StringComparison.OrdinalIgnoreCase)) ||
                !AnswerValues.IsValidValue(dcrEvent, draft.ProposedValue);
        }).ToList();

        session.PendingAnswersQueue.RemoveAll(dropped.Contains);
        ParkBlockedDrafts(session);
        dropped.AddRange(DropBlockedDraftsAtFront(session));
        UnparkReadyDrafts(session);
        return dropped;
    }

    // Afledte svar til felter, der ikke er enabled endnu, flyttes fra køen
    // til parkering; de kan ikke udføres nu, men skal ikke glemmes.
    private static void ParkBlockedDrafts(ChatSession session)
    {
        var blocked = session.PendingAnswersQueue
            .Where(draft => draft.ExecutionStatus == "Pending" &&
                            draft.IsAutoInferred &&
                            session.CurrentGraphState?.GetEvent(draft.EventId)?.IsEnabled != true)
            .ToList();
        foreach (var draft in blocked)
        {
            session.PendingAnswersQueue.Remove(draft);
            session.ParkedAnswers.RemoveAll(p => string.Equals(p.EventId, draft.EventId, StringComparison.OrdinalIgnoreCase));
            session.ParkedAnswers.Add(draft);
        }
    }

    // Parkerede svar, hvis felt nu er klar, sættes i køen i grafens
    // rækkefølge. Felter, der er udelukket eller allerede besvaret, glemmes.
    private static void UnparkReadyDrafts(ChatSession session)
    {
        var state = session.CurrentGraphState;
        session.ParkedAnswers.RemoveAll(draft =>
        {
            var dcrEvent = state?.GetEvent(draft.EventId);
            return dcrEvent is null || !dcrEvent.Included || dcrEvent.IsExecuted == true ||
                   !AnswerValues.IsValidValue(dcrEvent, draft.ProposedValue);
        });

        var ready = session.ParkedAnswers
            .Where(draft => state?.GetEvent(draft.EventId)?.IsEnabled == true)
            .OrderBy(draft => state?.GetEvent(draft.EventId)?.Sequence ?? 0)
            .ToList();
        foreach (var draft in ready)
        {
            session.ParkedAnswers.Remove(draft);
            if (!session.PendingAnswersQueue.Any(d =>
                    string.Equals(d.EventId, draft.EventId, StringComparison.OrdinalIgnoreCase)))
            {
                session.PendingAnswersQueue.Add(draft);
            }
        }
    }

    // Den forreste kladde skal kunne udføres nu. Venter dens felt stadig på et
    // andet felt (fx fordi borgeren afviste det), kan den ikke bruges.
    private static List<PendingAnswer> DropBlockedDraftsAtFront(ChatSession session)
    {
        var dropped = new List<PendingAnswer>();
        while (session.PendingAnswersQueue.Count > 0)
        {
            var front = session.PendingAnswersQueue[0];
            var dcrEvent = session.CurrentGraphState?.GetEvent(front.EventId);
            if (front.ExecutionStatus != "Pending" || dcrEvent?.IsEnabled == true)
            {
                break;
            }

            session.PendingAnswersQueue.RemoveAt(0);
            dropped.Add(front);
        }

        return dropped;
    }
}
