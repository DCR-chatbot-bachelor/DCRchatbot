using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Infrastructure.Session;

namespace DcrChatbot.UnitTests;

public sealed class SessionExpiryTests
{
    [Fact]
    public void HasExpired_ReturnsTrue_WhenLastUpdatedIsOlderThanTimeout()
    {
        var session = new ChatSession
        {
            GraphId = "graph-1",
            LastUpdatedAt = DateTime.UtcNow - SessionExpiry.Timeout - TimeSpan.FromMinutes(1)
        };

        Assert.True(SessionExpiry.HasExpired(session));
    }

    [Fact]
    public void HasExpired_ReturnsFalse_WhenRecentlyUpdated()
    {
        var session = new ChatSession { GraphId = "graph-1", LastUpdatedAt = DateTime.UtcNow };

        Assert.False(SessionExpiry.HasExpired(session));
    }

    [Fact]
    public async Task ConcurrentSessions_DoNotBlendData()
    {
        var store = new InMemorySessionStore();
        var sessionA = new ChatSession { GraphId = "graph-a" };
        var sessionB = new ChatSession { GraphId = "graph-b" };
        await store.SaveSessionAsync(sessionA);
        await store.SaveSessionAsync(sessionB);

        await Task.WhenAll(
            store.ExecuteAsync(sessionA.SessionId, s => { s.History.Add(new ChatMessage { Content = "A" }); return Task.FromResult(true); }),
            store.ExecuteAsync(sessionB.SessionId, s => { s.History.Add(new ChatMessage { Content = "B" }); return Task.FromResult(true); }));

        var resultA = await store.GetSessionAsync(sessionA.SessionId);
        var resultB = await store.GetSessionAsync(sessionB.SessionId);

        Assert.Single(resultA!.History);
        Assert.Equal("A", resultA.History[0].Content);
        Assert.Single(resultB!.History);
        Assert.Equal("B", resultB.History[0].Content);
    }
}