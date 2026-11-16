using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Core.Application.Routing;

/// <summary>
/// Gør en graf til samtalens aktuelle graf. Den hidtidige graf lægges til
/// side med sin simulation, så borgeren kan skifte frem og tilbage mellem
/// emner og fortsætte, hvor de slap. En graf, samtalen ikke har været i før,
/// får en ny simulation (eller, i LlmOnly, sin viden som tekst).
/// </summary>
internal sealed class GraphActivation(IDcrRepository dcrRepository, ISessionStore sessionStore)
{
    public async Task ActivateAsync(ChatSession session, string graphId, CancellationToken cancellationToken)
    {
        if (string.Equals(session.GraphId, graphId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (session.Mode == ExecutionMode.LlmOnly)
        {
            // LlmOnly har ingen simulation at genoptage; den får blot den
            // nye grafs viden, så den altid kun har én grafs viden ad gangen.
            session.GraphKnowledge = GraphKnowledgeBuilder.Build(
                await dcrRepository.GetGraphDefinitionAsync(graphId, cancellationToken));
            session.GraphId = graphId;
            await sessionStore.SaveSessionAsync(session, cancellationToken);
            return;
        }

        var resumed = session.SuspendedGraphs.FirstOrDefault(
            g => string.Equals(g.GraphId, graphId, StringComparison.OrdinalIgnoreCase));
        var simulationId = resumed?.SimulationId
            ?? await dcrRepository.CreateSimulationAsync(graphId, cancellationToken);
        var state = await dcrRepository.GetGraphStateAsync(graphId, simulationId, cancellationToken);

        // Først når den nye graf er klar, lægges den gamle til side; fejler
        // opslaget ovenfor, er samtalen uændret.
        Suspend(session);
        if (resumed is not null)
        {
            session.SuspendedGraphs.Remove(resumed);
        }

        session.GraphId = graphId;
        session.SimulationId = simulationId;
        session.CurrentGraphState = state;
        session.IsGraphStateStale = false;
        session.RecommendedTopicIds = resumed?.RecommendedTopicIds ?? [];
        session.ParkedAnswers = resumed?.ParkedAnswers ?? [];
        session.AnswerLabelsToExecute = resumed?.AnswerLabelsToExecute ?? [];
        session.ExecutedEvents = resumed?.ExecutedEvents ?? [];
        session.PendingLabelsBeforeExecution = null;
        session.AvailableTopicsBeforeExecution = null;

        // Gemmes med det samme, så en ny simulation ikke går tabt, hvis
        // resten af turen fejler.
        await sessionStore.SaveSessionAsync(session, cancellationToken);
    }

    private static void Suspend(ChatSession session)
    {
        if (string.IsNullOrWhiteSpace(session.GraphId))
        {
            return;
        }

        session.SuspendedGraphs.RemoveAll(
            g => string.Equals(g.GraphId, session.GraphId, StringComparison.OrdinalIgnoreCase));
        session.SuspendedGraphs.Add(new SuspendedGraph
        {
            GraphId = session.GraphId,
            SimulationId = session.SimulationId,
            RecommendedTopicIds = session.RecommendedTopicIds,
            ParkedAnswers = session.ParkedAnswers,
            AnswerLabelsToExecute = session.AnswerLabelsToExecute,
            ExecutedEvents = session.ExecutedEvents
        });
    }
}
