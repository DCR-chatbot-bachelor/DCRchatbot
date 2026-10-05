using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Infrastructure.Session;

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, ChatSession> sessions = new();
    private readonly object syncRoot = new();
    private readonly SessionLockManager lockManager = new();

    public Task<ChatSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        string sessionId,
        Func<ChatSession, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        using var sessionLock = await lockManager.AcquireAsync(sessionId, cancellationToken);
        var session = await GetSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException("Samtalen blev ikke fundet.");
        var result = await operation(session);
        await SaveSessionAsync(session, cancellationToken);
        return result;
    }

    public Task SaveSessionAsync(
        ChatSession session,
        CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            session.LastUpdatedAt = DateTime.UtcNow;
            sessions[session.SessionId] = Clone(session);
        }
        return Task.CompletedTask;
    }

    public async Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var sessionLock = await lockManager.AcquireAsync(sessionId, cancellationToken);
        lock (syncRoot)
        {
            sessions.Remove(sessionId);
        }
    }

    private static ChatSession Clone(ChatSession session) =>
        System.Text.Json.JsonSerializer.Deserialize<ChatSession>(
            System.Text.Json.JsonSerializer.Serialize(session))
        ?? throw new InvalidOperationException("Sessionen kunne ikke kopieres.");
}