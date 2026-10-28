using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// Det, DcrStrict og DcrGuided gør forskelligt (FR-TEST-1). Resten af
/// samtalen er den samme: DCR vælger svaret og styrer formularen i begge.
/// </summary>
internal interface IDcrReplyStrategy
{
    /// <summary>Teksten, borgeren får, når DCR har valgt et svar.</summary>
    Task<string> FormulateAnswerAsync(PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken);

    /// <summary>
    /// Et svar på en besked, der ikke matcher et af grafens emner, eller null,
    /// så borgeren får grafens emner i stedet.
    /// </summary>
    Task<string?> AnswerUnmatchedAsync(ChatSession session, string message, CancellationToken cancellationToken);
}
