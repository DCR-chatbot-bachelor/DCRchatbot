using System.Collections.Concurrent;
using System.Text.Json;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Infrastructure.Session;

public sealed class JsonFileSessionStore : ISessionStore
{
    private readonly string directory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public JsonFileSessionStore()
    {
        directory = Path.Combine(AppContext.BaseDirectory, "App_Data", "sessions");
        Directory.CreateDirectory(directory);
    }

    public async Task<ChatSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ChatSession>(stream, jsonOptions, cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        string sessionId,
        Func<ChatSession, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        var sessionLock = locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var session = await GetSessionAsync(sessionId, cancellationToken)
                ?? throw new KeyNotFoundException("Samtalen blev ikke fundet.");
            var result = await operation(session);
            await SaveSessionAsync(session, cancellationToken);
            return result;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task SaveSessionAsync(
        ChatSession session,
        CancellationToken cancellationToken = default)
    {
        session.LastUpdatedAt = DateTime.UtcNow;
        var path = GetPath(session.SessionId);
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, session, jsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string sessionId) =>
        Path.Combine(directory, Uri.EscapeDataString(sessionId) + ".json");
}