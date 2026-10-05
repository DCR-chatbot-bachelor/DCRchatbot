using DcrChatbot.Core.Application.Dtos;

namespace DcrChatbot.Core.Interfaces;

public interface IChatService
{
    Task<ChatResponse> StartAsync(ChatRequest request, CancellationToken cancellationToken = default);

    Task<ChatResponse> SendMessageAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default);

    Task<ChatResponse> ConfirmDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        CancellationToken cancellationToken = default);

    Task<ChatResponse> RejectDraftAsync(
        string sessionId,
        string? pendingAnswerId,
        CancellationToken cancellationToken = default);

    Task<ChatResponse> ReviseDraftAsync(
        string sessionId,
        ChatRequest request,
        CancellationToken cancellationToken = default);
}