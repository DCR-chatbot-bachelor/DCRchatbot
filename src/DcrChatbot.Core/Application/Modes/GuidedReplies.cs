using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Logging;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// DcrGuided: DCR vælger svaret, og LLM'en formulerer det ud fra grafens
/// svartekst alene; den ser ikke resten af grafen. Fejler kaldet, vises
/// grafens tekst ordret som i DcrStrict.
/// </summary>
internal sealed class GuidedReplies(ILlmService llmService, LlmOptions llmOptions, ILogger logger) : IDcrReplyStrategy
{
    public async Task<string> FormulateAnswerAsync(
        PendingAnswer draft, string officialAnswer, CancellationToken cancellationToken)
    {
        var question = draft.Question ?? draft.EventId;
        var prompt = ModePrompts.RephraseAnswer(draft.UserMessage ?? question, question, officialAnswer);
        try
        {
            var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();
            return reply.Length > 0 ? reply : officialAnswer;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Kunne ikke formulere svaret; grafens tekst vises ordret.");
            return officialAnswer;
        }
    }
}
