using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DcrChatbot.WebApi.Controllers;

/// <summary>
/// Genskaber en samtale ud fra trace-loggen (FR-TEST-1). Loggen indeholder
/// borgerens beskeder, så endpointet er kun tilgængeligt i Development.
/// </summary>
[ApiController]
[Route("api/traces")]
public sealed class TracesController(ITraceStore traceStore, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionTraceResponse>> GetSession(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        var turns = await traceStore.ReadSessionAsync(sessionId, cancellationToken);
        if (turns.Count == 0)
        {
            return NotFound($"Ingen trace fundet for session {sessionId}.");
        }

        return Ok(new SessionTraceResponse(
            sessionId,
            turns.Sum(t => t.TotalTokens),
            ConversationReplay.Rebuild(turns),
            turns));
    }
}

public sealed record SessionTraceResponse(
    string SessionId,
    int TotalTokens,
    IReadOnlyList<ReplayMessage> Transcript,
    IReadOnlyList<TraceRecord> Turns);
