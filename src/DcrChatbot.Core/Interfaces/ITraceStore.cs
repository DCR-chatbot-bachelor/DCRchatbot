using DcrChatbot.Core.Application.Tracing;

namespace DcrChatbot.Core.Interfaces;

/// <summary>
/// Gemmer og læser trace-linjer for samtaler (FR-TEST-1).
/// </summary>
public interface ITraceStore
{
    Task AppendAsync(TraceRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraceRecord>> ReadSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
