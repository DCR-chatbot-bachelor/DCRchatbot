using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.Logging;
using DcrChatbot.Infrastructure.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DcrChatbot.UnitTests;

/// <summary>
/// FR-TEST-1: hver besked giver én trace-linje, og samtalen kan genskabes fra loggen.
/// </summary>
public sealed class TraceLoggingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dcr-trace-tests", Guid.NewGuid().ToString());
    private readonly Mock<IDcrRepository> dcrRepository = new();
    private readonly Mock<ILlmService> provider = new();
    private readonly InMemorySessionStore sessionStore = new();
    private readonly JsonLinesTraceStore traceStore;

    public TraceLoggingTests()
    {
        traceStore = new JsonLinesTraceStore(directory);

        var state = new GraphState
        {
            Events = [new DcrEvent
            {
                Id = "minSU", Label = "Har du MitID?", DataType = "choice",
                IsEnabled = true, IsPending = true, ChoiceValues = "ja (ja), nej (nej)"
            }]
        };
        dcrRepository.Setup(r => r.CreateSimulationAsync("g1", It.IsAny<CancellationToken>())).ReturnsAsync("sim1");
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("g1", "sim1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
        provider
            .Setup(s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new LlmValueResult { ExtractedValue = "ja" }, new TokenUsageResult { PromptTokens = 100, CompletionTokens = 7 }));
    }

    // Hver tur får sit eget scope, ligesom ét HTTP-kald i API'et.
    private IChatService CreateScope()
    {
        var turn = new TurnTrace();
        var llm = new LoggingLlmService(provider.Object, turn, NullLogger<LoggingLlmService>.Instance);
        var chat = new ChatService(
            dcrRepository.Object, llm, sessionStore, Options.Create(new LlmOptions()), turn, NullLogger<ChatService>.Instance);
        return new TracingChatService(chat, turn, traceStore, NullLogger<TracingChatService>.Instance);
    }

    [Fact]
    public async Task EachMessage_WritesOneTraceLine_WithEventValidationReplyModeAndTokens()
    {
        var start = await CreateScope().StartAsync(new ChatRequest { GraphId = "g1" });
        await CreateScope().SendMessageAsync(start.SessionId, new ChatRequest { Message = "ja det har jeg" });

        var turns = await traceStore.ReadSessionAsync(start.SessionId);

        Assert.Equal(2, turns.Count);
        var message = turns[1];
        Assert.Equal("message", message.Action);
        Assert.Equal("ja det har jeg", message.Input);
        Assert.Equal("g1", message.GraphId);
        Assert.Equal(start.Mode, message.Mode);
        Assert.Equal("minSU", message.EventId);
        Assert.Equal("ja", message.ProposedValue);
        Assert.Equal("draft_created", message.Outcome);
        var validation = Assert.Single(message.Validations);
        Assert.True(validation.IsValid);
        Assert.Contains("Har du MitID?", message.Reply);
        Assert.Equal(107, message.TotalTokens);
        Assert.Equal("ExtractValue", Assert.Single(message.LlmCalls).Operation);
    }

    [Fact]
    public async Task Conversation_CanBeRebuiltFromLog()
    {
        var start = await CreateScope().StartAsync(new ChatRequest { GraphId = "g1" });
        var draft = await CreateScope().SendMessageAsync(start.SessionId, new ChatRequest { Message = "ja" });
        await CreateScope().RejectDraftAsync(start.SessionId, draft.PendingDraft!.Id);

        var transcript = ConversationReplay.Rebuild(await traceStore.ReadSessionAsync(start.SessionId));

        Assert.Equal(
            ["Bot", "User", "Bot", "User", "Bot"],
            transcript.Select(m => m.Sender));
        Assert.Equal("Har du MitID?", transcript[0].Content);
        Assert.Equal("ja", transcript[1].Content);
        Assert.Equal("[Afviste svaret]", transcript[3].Content);
    }

    [Fact]
    public async Task FailedTurn_IsStillLogged_WithError()
    {
        var start = await CreateScope().StartAsync(new ChatRequest { GraphId = "g1" });

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateScope().ConfirmDraftAsync(start.SessionId, "findes-ikke"));

        var failed = (await traceStore.ReadSessionAsync(start.SessionId)).Last();
        Assert.Equal("confirm", failed.Action);
        Assert.Equal("error", failed.Outcome);
        Assert.StartsWith("KeyNotFoundException", failed.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
