using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// DcrStrict: grafens svartekst vises ordret. LLM'en tolker kun borgeren.
/// </summary>
internal sealed class StrictReplies : IDcrReplyStrategy
{
    public Task<string> FormulateAnswerAsync(
        PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken) =>
        Task.FromResult(officialAnswer);
}
