using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Interfaces;

public interface IDcrRepository
{
    /// <summary>
    /// Opretter en ny simulation mod DCR.Repo REST API (/sims) (FR-DCR-3).
    /// </summary>
    Task<string> CreateSimulationAsync(string graphId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter metadata for en DCR-graf (FR-DCR-2).
    /// </summary>
    Task<DcrGraph> GetGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter de grafer, som den konfigurerede DCR.Repo-bruger har adgang til.
    /// </summary>
    Task<IReadOnlyList<DcrGraph>> GetGraphsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter den aktuelle tilstand for simulationen.
    /// </summary>
    Task<GraphState> GetGraphStateAsync(
        string graphId,
        string simulationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Eksekverer et godkendt event på DCR.Repo (/event) (FR-DCR-1, FR-HITL-1).
    /// </summary>
    Task<bool> ExecuteEventAsync(
        string graphId,
        string simulationId,
        string eventId,
        string? value,
        CancellationToken cancellationToken = default);
}