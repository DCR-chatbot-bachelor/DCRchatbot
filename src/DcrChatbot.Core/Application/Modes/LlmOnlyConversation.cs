using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;

namespace DcrChatbot.Core.Application.Modes;

/// <summary>
/// LlmOnly (baseline): LLM'en fører samtalen alene ud fra grafens viden som
/// tekst. Intet valideres, bekræftes eller udføres i DCR (FR-TEST-1).
/// </summary>
internal sealed class LlmOnlyConversation(ILlmService llmService, LlmOptions llmOptions, TurnTrace trace)
{
    public const string Greeting = "Hej! Hvad kan jeg hjælpe dig med?";

    private const int HistoryLength = 20;

    public async Task<ChatResponse> ReplyAsync(ChatSession session, string message, CancellationToken cancellationToken)
    {
        var history = session.History
            .TakeLast(HistoryLength)
            .Select(m => (FromCitizen: m.Sender == "User", Text: m.Content));
        var prompt = ModePrompts.LlmOnly(session.GraphKnowledge ?? string.Empty, history, message);
        var reply = (await llmService.GenerateTextAsync(prompt, llmOptions, cancellationToken)).Text.Trim();

        session.History.Add(new ChatMessage { Sender = "User", Content = message });
        session.History.Add(new ChatMessage { Sender = "Bot", Content = reply });
        trace.RecordOutcome("llm_only_reply");
        return ChatResponses.ToResponse(session, reply);
    }
}
