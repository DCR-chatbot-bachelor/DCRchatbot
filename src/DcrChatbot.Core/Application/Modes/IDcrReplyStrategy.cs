using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// Det eneste, DcrStrict og DcrGuided gør forskelligt (FR-TEST-1): hvordan
/// det svar, DCR har valgt, formuleres. Resten er det samme i begge: DCR
/// vælger svaret, styrer formularen, og en besked uden emne giver grafens emner.
/// </summary>
internal interface IDcrReplyStrategy
{
    /// <summary>Teksten, borgeren får, når DCR har valgt et svar.</summary>
    Task<string> FormulateAnswerAsync(PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken);
}
