using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Interfaces;

public interface ISessionStore
{
    /// <summary>
    /// Henter en eksisterende samtale fra ekstern session store jf. NFR-6.
    /// </summary>
    Task<ChatSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gemmer eller opdaterer samtale-tilstanden.
    /// </summary>
    Task SaveSessionAsync(ChatSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sletter en session ved afslutning.
    /// </summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}