using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DcrChatbot.WebApi.Controllers;

[ApiController]
[Route("api/graphs")]
public sealed class GraphsController(IDcrRepository dcrRepository) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DcrGraph>>> GetGraphs(
        CancellationToken cancellationToken) =>
        Ok(await dcrRepository.GetGraphsAsync(cancellationToken));

    [HttpGet("{graphId}")]
    public async Task<ActionResult<DcrGraph>> GetGraph(
        string graphId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphId))
        {
            return BadRequest("Graph-id må ikke være tomt.");
        }

        return Ok(await dcrRepository.GetGraphAsync(graphId, cancellationToken));
    }
}
