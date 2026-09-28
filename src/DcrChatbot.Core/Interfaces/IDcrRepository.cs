using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Interfaces;

public interface IDcrRepository
{
    /// <summary>
    /// Opretter en ny simulation mod DCR.Repo REST API (/sims) (FR-DCR-3).
    /// </summary>
    Task<string> CreateSimulationAsync(string graphId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter den aktuelle tilstand for simulationen.
    /// </summary>
    Task<GraphState> GetGraphStateAsync(string simulationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Eksekverer et godkendt event på DCR.Repo (/event) (FR-DCR-1, FR-HITL-1).
    /// </summary>
    Task<bool> ExecuteEventAsync(string simulationId, string eventId, string? value, CancellationToken cancellationToken = default);
}