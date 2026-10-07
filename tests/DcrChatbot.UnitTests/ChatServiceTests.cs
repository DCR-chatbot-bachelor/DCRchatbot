using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.Session;
using Microsoft.Extensions.Options;
using Moq;

namespace DcrChatbot.UnitTests;

public sealed class ChatServiceTests
{
    private readonly Mock<IDcrRepository> dcrRepository = new();
    private readonly Mock<ILlmService> llmService = new();
    private readonly InMemorySessionStore sessionStore = new();
    private readonly ChatService service;

    public ChatServiceTests()
    {
        service = new ChatService(
            dcrRepository.Object,
            llmService.Object,
            sessionStore,
            Options.Create(new LlmOptions()));
    }

    [Fact]
    public async Task SendMessage_OnlyOffersChoiceEventsAsCandidates()
    {
        var graphState = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "welcome", DataType = "label", IsEnabled = true },
                new DcrEvent { Id = "minSU", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }
            ]
        };
        var session = await StartSessionAsync(graphState);

        IEnumerable<DcrEvent>? offeredCandidates = null;
        llmService
            .Setup(s => s.ExtractIntentAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<DcrEvent>, LlmOptions, CancellationToken>((_, candidates, _, _) => offeredCandidates = candidates)
            .ReturnsAsync((new LlmMatchResult(), new TokenUsageResult()));

        await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hej" });

        Assert.NotNull(offeredCandidates);
        Assert.All(offeredCandidates!, e => Assert.Equal("choice", e.DataType));
    }

    [Fact]
    public async Task SendMessage_CreatesDraft_WhenLlmMatchIsValid()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", "ja");

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ja tak" });

        Assert.NotNull(response.PendingDraft);
        Assert.Equal("minSU", response.PendingDraft!.EventId);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_RejectsDraft_WhenMatchedValueFailsValidation()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", "måske");

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "måske?" });

        Assert.Null(response.PendingDraft);
    }

    [Fact]
    public async Task ConfirmDraft_ExecutesEvent_OnlyAfterConfirmation()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", "ja");
        dcrRepository
            .Setup(r => r.GetGraphStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(graphState);

        var draftResponse = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ja tak" });
        dcrRepository.Verify(r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await service.ConfirmDraftAsync(session.SessionId, draftResponse.PendingDraft!.Id);

        dcrRepository.Verify(
            r => r.ExecuteEventAsync(session.GraphId, session.SimulationId!, "minSU", "ja", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
public async Task SendMessage_CanAskMultipleQuestionsInSequence()
{
    var graphState = new GraphState
    {
        Events =
        [
            new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
            new DcrEvent { Id = "ansøgning", Label = "Ansøgning?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }
        ]
    };
    var session = await StartSessionAsync(graphState);
    dcrRepository
        .Setup(r => r.GetGraphStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(graphState);

    SetupLlmMatch("minSU", "ja");
    var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ja tak" });
    await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id);

    SetupLlmMatch("ansøgning", "nej");
    var second = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "nej" });

    Assert.NotNull(second.PendingDraft);
    Assert.Equal("ansøgning", second.PendingDraft!.EventId);
}

[Fact]
public async Task RejectDraft_DoesNotExecuteEvent()
{
    var graphState = new GraphState
    {
        Events = [new DcrEvent { Id = "minSU", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
    };
    var session = await StartSessionAsync(graphState);
    SetupLlmMatch("minSU", "ja");

    var draftResponse = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ja tak" });
    await service.RejectDraftAsync(session.SessionId, draftResponse.PendingDraft!.Id);

    dcrRepository.Verify(
        r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
        Times.Never);

    var sessionAfter = await sessionStore.GetSessionAsync(session.SessionId);
    Assert.Empty(sessionAfter!.PendingAnswersQueue);
}

    private async Task<ChatSession> StartSessionAsync(GraphState graphState)
    {
        dcrRepository.Setup(r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sim-1");
        dcrRepository.Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(graphState);

        var response = await service.StartAsync(new ChatRequest { GraphId = "graph-1" });
        return (await sessionStore.GetSessionAsync(response.SessionId))!;
    }

    private void SetupLlmMatch(string eventId, string value) =>
        llmService
            .Setup(s => s.ExtractIntentAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new LlmMatchResult { MatchedEventId = eventId, ExtractedValue = value }, new TokenUsageResult()));
}