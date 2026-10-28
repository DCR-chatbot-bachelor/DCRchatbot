using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Logging;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// DcrGuided: DCR vælger stadig svaret, men LLM'en formulerer det. Fejler
/// et kald, falder den tilbage til DcrStrict, så borgeren altid får grafens svar.
/// </summary>
internal sealed class GuidedReplies(ILlmService llmService, LlmOptions llmOptions, ILogger logger) : IDcrReplyStrategy
{
    // Svar-teksten fra grafen formuleres af LLM'en, kun ud fra den tekst.
    // Fejler kaldet, vises grafens tekst ordret som i DcrStrict.
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

    // En besked uden emne må besvares ud fra grafens viden, men kun hvis
    // svaret står der. Ellers vises grafens emner som i DcrStrict.
    public async Task<string?> AnswerUnmatchedAsync(
        ChatSession session, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.GraphKnowledge))
        {
            return null;
        }

        try
        {
            var prompt = ModePrompts.GroundedAnswer(session.GraphKnowledge, message);
            var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();
            return reply.Length > 0 && !reply.Contains(ModePrompts.UnknownAnswer, StringComparison.OrdinalIgnoreCase)
                ? reply
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Kunne ikke svare ud fra grafens viden; viser grafens emner.");
            return null;
        }
    }
}
