using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// DcrStrict: grafens svartekst vises ordret, og en besked uden emne giver
/// grafens emner. LLM'en tolker kun borgeren.
/// </summary>
internal sealed class StrictReplies : IDcrReplyStrategy
{
    public Task<string> FormulateAnswerAsync(
        PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken) =>
        Task.FromResult(officialAnswer);

    public Task<string?> AnswerUnmatchedAsync(
        ChatSession session, string message, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
