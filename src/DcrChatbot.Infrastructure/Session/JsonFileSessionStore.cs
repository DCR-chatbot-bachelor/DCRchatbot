using System.Text.Json;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Infrastructure.Session;

public sealed class JsonFileSessionStore : ISessionStore
{
    private readonly string directory;
    private readonly SessionLockManager lockManager = new();
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

    ChatSession? session;
    await using (var stream = File.OpenRead(path))
    {
        session = await JsonSerializer.DeserializeAsync<ChatSession>(stream, jsonOptions, cancellationToken);
    }

    if (session is not null && SessionExpiry.HasExpired(session))
    {
        File.Delete(path);
        return null;
    }

    return session;
}

    public async Task<TResult> ExecuteAsync<TResult>(
        string sessionId,
        Func<ChatSession, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        using (await lockManager.AcquireAsync(sessionId, cancellationToken))
        {
            var session = await GetSessionAsync(sessionId, cancellationToken)
                ?? throw new KeyNotFoundException("Samtalen blev ikke fundet.");
            var result = await operation(session);
            await SaveSessionAsync(session, cancellationToken);
            return result;
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

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using (await lockManager.AcquireAsync(sessionId, cancellationToken))
        {
            var path = GetPath(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string GetPath(string sessionId) =>
        Path.Combine(directory, Uri.EscapeDataString(sessionId) + ".json");
}