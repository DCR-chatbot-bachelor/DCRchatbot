using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Core.Application;

/// <summary>
/// Holder sessionens kopi af grafen i takt med simulationen i DCR.Repo:
/// udfører events, henter ny tilstand og rydder op efter en udførsel, der
/// ikke nåede at blive færdig.
/// </summary>
internal sealed class SimulationSync(IDcrRepository dcrRepository, ISessionStore sessionStore)
{
    public async Task RefreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (session.SimulationId is not null)
        {
            session.CurrentGraphState = await dcrRepository.GetGraphStateAsync(
                session.GraphId, session.SimulationId, cancellationToken);
            session.IsGraphStateStale = false;
        }
    }

    // Kaster videre, hvis tilstanden ikke kan hentes: hellere en fejl end
    // at fortsætte på en graf, der ikke længere svarer til DCR.
    public async Task EnsureFreshStateAsync(ChatSession session, CancellationToken cancellationToken)
    {
        if (!session.IsGraphStateStale)
        {
            return;
        }

        await RefreshStateAsync(session, cancellationToken);

        // Kunne tilstanden ikke hentes lige efter en udførsel, blev svaret
        // aldrig vist og står stadig som pending. Det sættes i kø og udføres
        // ved næste bekræftelse, så spørgsmålet kan stilles og besvares igen.
        if (session.PendingLabelsBeforeExecution is { } before)
        {
            session.AnswerLabelsToExecute.AddRange(FaqGraph.FindNewAnswers(session.CurrentGraphState, before)
                .Select(e => e.Id)
                .Except(session.AnswerLabelsToExecute));
            session.PendingLabelsBeforeExecution = null;
        }
    }

    public async Task ExecuteEventAsync(
        ChatSession session, string simulationId, string eventId, string value, CancellationToken cancellationToken)
    {
        await dcrRepository.ExecuteEventAsync(session.GraphId, simulationId, eventId, value, cancellationToken);
        session.ExecutedEvents.RemoveAll(e => string.Equals(e.EventId, eventId, StringComparison.OrdinalIgnoreCase));
        session.ExecutedEvents.Add(new ExecutedEvent { EventId = eventId, Value = value });
    }

    // DCR.Repo kræver en tom streng som værdi for labels; null giver en serverfejl.
    // Køen gemmes efter hver udført label, så en fejl på en senere label
    // ikke får en allerede udført label til at blive udført igen.
    public async Task ExecuteQueuedAnswerLabelsAsync(
        ChatSession session, string simulationId, CancellationToken cancellationToken)
    {
        if (session.AnswerLabelsToExecute.Count == 0)
        {
            return;
        }

        session.IsGraphStateStale = true;
        await sessionStore.SaveSessionAsync(session, cancellationToken);
        foreach (var labelId in session.AnswerLabelsToExecute.ToList())
        {
            await ExecuteEventAsync(session, simulationId, labelId, string.Empty, cancellationToken);
            session.AnswerLabelsToExecute.Remove(labelId);
            await sessionStore.SaveSessionAsync(session, cancellationToken);
        }
    }

    // En ny simulation, hvor de givne events udføres igen i samme rækkefølge.
    // Bruges til at gå tilbage i samtalen: DCR kan ikke fortryde et event.
    public async Task ReplayAsync(
        ChatSession session, IReadOnlyList<ExecutedEvent> executedEvents, CancellationToken cancellationToken)
    {
        var simulationId = await dcrRepository.CreateSimulationAsync(session.GraphId, cancellationToken);
        foreach (var executedEvent in executedEvents)
        {
            await dcrRepository.ExecuteEventAsync(
                session.GraphId, simulationId, executedEvent.EventId, executedEvent.Value, cancellationToken);
        }

        session.SimulationId = simulationId;
        session.CurrentGraphState = await dcrRepository.GetGraphStateAsync(
            session.GraphId, simulationId, cancellationToken);
        session.ExecutedEvents = executedEvents
            .Select(e => new ExecutedEvent { EventId = e.EventId, Value = e.Value })
            .ToList();
        session.IsGraphStateStale = false;
    }
}
