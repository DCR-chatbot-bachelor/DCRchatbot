using System.Collections.Concurrent;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Infrastructure.Session;

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, ChatSession> sessions = new();

    public Task<ChatSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task SaveSessionAsync(
        ChatSession session,
        CancellationToken cancellationToken = default)
    {
        session.LastUpdatedAt = DateTime.UtcNow;
        sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}