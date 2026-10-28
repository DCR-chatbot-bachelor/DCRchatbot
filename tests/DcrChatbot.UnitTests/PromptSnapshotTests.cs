using System.Security.Cryptography;
using System.Text;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.LlmProviders;

namespace DcrChatbot.UnitTests;

/// <summary>
/// Låser prompt-versionen (FR-LLM-1): ændres en prompt, uden at
/// PromptCatalog.Version hæves, fejler testen. Så betyder "v1" i et
/// testresultat altid præcis de samme prompts.
/// </summary>
public sealed class PromptSnapshotTests
{
    // Hashen af alle prompts i PromptCatalog.Version.
    private const string ExpectedVersion = "v3";
    private const string ExpectedHash = "EDE790D292C03877";

    [Fact]
    public void Prompts_MatchTheirVersion()
    {
        var actual = HashAllPrompts();

        Assert.True(
            PromptCatalog.Version == ExpectedVersion && actual == ExpectedHash,
            $"Prompterne er ændret. Hæv PromptCatalog.Version og Llm:PromptVersion (fx {ExpectedVersion} → v2), " +
            $"og sæt ExpectedVersion og ExpectedHash i denne test til den nye version og '{actual}'.");
    }

    // Alle prompts bygget med faste input. Linjeskift normaliseres, så hashen
    // er den samme på Windows og macOS/Linux.
    private static string HashAllPrompts()
    {
        var question = new DcrEvent { Id = "q", Label = "Spørgsmål?", DataType = "choice", ChoiceValues = "Ja (ja), Nej (nej)" };
        var other = new DcrEvent { Id = "o", Label = "Andet?", DataType = "text" };
        var draft = new PendingAnswer { Question = "Spørgsmål?", ProposedValue = "ja" };
        var options = new LlmOptions();

        var prompts = new[]
        {
            LlmIntentPrompt.BuildSystemPrompt(options),
            LlmIntentPrompt.TextSystemPrompt,
            LlmIntentPrompt.BuildIntentPrompt("besked", [question, other]),
            LlmIntentPrompt.BuildMatchEventPrompt("besked", [question]),
            LlmIntentPrompt.BuildValuePrompt("besked", question, [other]),
            LlmIntentPrompt.BuildConfirmationPrompt("besked", draft),
            ModePrompts.LlmOnly("viden", [(true, "hej"), (false, "svar")], "besked"),
            ModePrompts.RephraseAnswer("besked", "Spørgsmål?", "Officielt svar.")
        };

        var text = string.Join("\n---\n", prompts).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
