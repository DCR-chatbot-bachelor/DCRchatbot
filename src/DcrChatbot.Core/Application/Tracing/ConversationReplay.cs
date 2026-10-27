namespace DcrChatbot.Core.Application.Tracing;

public sealed record ReplayMessage(DateTimeOffset Timestamp, string Sender, string Content);

/// <summary>
/// Genskaber samtalen (borger og bot på skift) ud fra trace-linjerne for én session.
/// </summary>
public static class ConversationReplay
{
    public static List<ReplayMessage> Rebuild(IEnumerable<TraceRecord> records)
    {
        var messages = new List<ReplayMessage>();
        foreach (var record in records.OrderBy(r => r.Timestamp))
        {
            var userText = !string.IsNullOrWhiteSpace(record.Input)
                ? record.Input
                : record.Action switch
                {
                    "confirm" => "[Bekræftede svaret]",
                    "reject" => "[Afviste svaret]",
                    _ => null
                };
            if (userText is not null)
            {
                messages.Add(new ReplayMessage(record.Timestamp, "User", userText));
            }

            var botText = record.Error is not null ? $"[Fejl: {record.Error}]" : record.Reply;
            if (!string.IsNullOrWhiteSpace(botText))
            {
                messages.Add(new ReplayMessage(record.Timestamp, "Bot", botText));
            }
        }

        return messages;
    }
}
