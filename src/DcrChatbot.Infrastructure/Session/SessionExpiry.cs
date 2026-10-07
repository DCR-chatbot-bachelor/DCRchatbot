using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Infrastructure.Session;

public static class SessionExpiry
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static bool HasExpired(ChatSession session) =>
        DateTime.UtcNow - session.LastUpdatedAt > Timeout;
}