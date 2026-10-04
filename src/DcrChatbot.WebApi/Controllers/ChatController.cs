using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DcrChatbot.WebApi.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(IChatService chatService) : ControllerBase
{
    public const string SessionHeader = "X-Session-Id";

    [HttpPost("start")]
    public async Task<ActionResult<ChatResponse>> Start(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken) =>
        Ok(await chatService.StartAsync(request, cancellationToken));

    [HttpPost("message")]
    public async Task<ActionResult<ChatResponse>> SendMessage(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken) =>
        Ok(await chatService.SendMessageAsync(RequireSessionId(), request, cancellationToken));

    [HttpPost("confirm")]
    public async Task<ActionResult<ChatResponse>> Confirm(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ChatRequest? request,
        CancellationToken cancellationToken) =>
        Ok(await chatService.ConfirmDraftAsync(
            RequireSessionId(), request?.TargetPendingAnswerId, cancellationToken));

    [HttpPost("reject")]
    public async Task<ActionResult<ChatResponse>> Reject(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ChatRequest? request,
        CancellationToken cancellationToken) =>
        Ok(await chatService.RejectDraftAsync(
            RequireSessionId(), request?.TargetPendingAnswerId, cancellationToken));

    [HttpPost("revise")]
    public async Task<ActionResult<ChatResponse>> Revise(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken) =>
        Ok(await chatService.ReviseDraftAsync(RequireSessionId(), request, cancellationToken));

    private string RequireSessionId() =>
        Request.Headers.TryGetValue(SessionHeader, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : throw new ArgumentException($"Headeren {SessionHeader} er påkrævet.");
}