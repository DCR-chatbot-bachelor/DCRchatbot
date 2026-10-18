using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.Session;
using Microsoft.Extensions.Logging.Abstractions;
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
            Options.Create(new LlmOptions()),
            NullLogger<ChatService>.Instance);
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
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<DcrEvent>, LlmOptions, CancellationToken>((_, candidates, _, _) => offeredCandidates = candidates)
            .ReturnsAsync(((string?)"minSU", new TokenUsageResult()));

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
    public async Task SendMessage_DraftUsesGraphsYesValue_RegardlessOfExtractedValue()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", null);

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });

        Assert.NotNull(response.PendingDraft);
        Assert.Equal("ja", response.PendingDraft!.ProposedValue);
        Assert.Equal("Hvorfor skal jeg bruge minSU?", response.PendingDraft.Question);
    }

    [Fact]
    public async Task SendMessage_NoDraft_WhenLlmMatchesUnknownEvent()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("opfundet", "ja");

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad med noget andet?" });

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
    public async Task ConfirmDraft_ShowsNextPendingFormEvent()
    {
        var firstEvent = new DcrEvent
        {
            Id = "country",
            Label = "Hvilket land er du i?",
            DataType = "text",
            IsEnabled = true,
            IsPending = true,
            Sequence = 1
        };
        var secondEvent = new DcrEvent
        {
            Id = "age",
            Label = "Hvor gammel er du?",
            DataType = "integer",
            IsEnabled = true,
            IsPending = false,
            Sequence = 2
        };
        var session = await StartSessionAsync(new GraphState { Events = [firstEvent, secondEvent] });
        SetupLlmMatch("country", "Danmark");

        var draft = await service.SendMessageAsync(
            session.SessionId,
            new ChatRequest { Message = "Jeg bor i Danmark" });

        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState
            {
                Events =
                [
                    new DcrEvent
                    {
                        Id = "country",
                        Label = firstEvent.Label,
                        DataType = "text",
                        IsEnabled = true,
                        IsExecuted = true,
                        Sequence = 1
                    },
                    new DcrEvent
                    {
                        Id = "age",
                        Label = secondEvent.Label,
                        DataType = "integer",
                        IsEnabled = true,
                        IsPending = true,
                        Sequence = 2
                    }
                ]
            });

        var response = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Contains("Hvor gammel er du?", response.Message);
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

    [Fact]
    public async Task ConfirmDraft_ShowsNewlyPendingLabelAsAnswer_AndLeavesAnchorAlone()
    {
        DcrEvent Choice() => new() { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" };
        DcrEvent Answer(bool pending) => new() { Id = "A2Copy", Label = "På minSU kan du følge med i din SU.", DataType = "label", IsEnabled = true, IsPending = pending };
        DcrEvent Anchor() => new() { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", IsEnabled = true, IsPending = true };

        var session = await StartSessionAsync(new GraphState { Events = [Choice(), Answer(false), Anchor()] });
        SetupLlmMatch("minSU", null);
        var draftResponse = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        dcrRepository
            .SetupSequence(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState { Events = [Choice(), Answer(true), Anchor()] })
            .ReturnsAsync(new GraphState { Events = [Choice(), Answer(false), Anchor()] });

        var response = await service.ConfirmDraftAsync(session.SessionId, draftResponse.PendingDraft!.Id);

        Assert.Equal("På minSU kan du følge med i din SU.", response.Message);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "minSU", "ja", It.IsAny<CancellationToken>()), Times.Once);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "A2Copy", "", It.IsAny<CancellationToken>()), Times.Once);
        dcrRepository.Verify(r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), "Velkommen", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmDraft_LabelExecutionFails_StillShowsAnswer_AndRetriesOnNextConfirm()
    {
        DcrEvent Choice(string id) => new() { Id = id, Label = id + "?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" };
        DcrEvent Answer(bool pending) => new() { Id = "A2Copy", Label = "Svaret.", Description = "Intern note", DataType = "label", IsEnabled = true, IsPending = pending };

        var session = await StartSessionAsync(new GraphState { Events = [Choice("minSU"), Choice("ansøgning"), Answer(false)] });
        SetupLlmMatch("minSU", null);
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState { Events = [Choice("minSU"), Choice("ansøgning"), Answer(true)] });
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "A2Copy", "", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DCR nede"));

        var response = await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id);

        Assert.Equal("Svaret.", response.Message);
        Assert.Null(response.PendingDraft);
        var afterFailure = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Empty(afterFailure!.PendingAnswersQueue);
        Assert.Equal(["A2Copy"], afterFailure.AnswerLabelsToExecute);

        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "A2Copy", "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupLlmMatch("ansøgning", null);
        var second = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ansøgning?" });
        await service.ConfirmDraftAsync(session.SessionId, second.PendingDraft!.Id);

        var afterRetry = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Empty(afterRetry!.AnswerLabelsToExecute);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "ansøgning", "ja", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmDraft_RetryPersistsEachExecutedLabel_SoItIsNotExecutedTwice()
    {
        DcrEvent Choice(string id) => new() { Id = id, Label = id + "?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" };
        DcrEvent Label(string id, bool pending) => new() { Id = id, Label = id, DataType = "label", IsEnabled = true, IsPending = pending };

        var session = await StartSessionAsync(new GraphState { Events = [Choice("minSU"), Choice("ansøgning"), Label("A", false), Label("B", false)] });
        SetupLlmMatch("minSU", null);
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState { Events = [Choice("minSU"), Choice("ansøgning"), Label("A", true), Label("B", true)] });
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", It.IsIn("A", "B"), "", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DCR nede"));
        await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id);

        // Ved næste bekræftelse lykkes A, men B fejler stadig.
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "A", "", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupLlmMatch("ansøgning", null);
        var second = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ansøgning?" });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.ConfirmDraftAsync(session.SessionId, second.PendingDraft!.Id));

        var persisted = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Equal(["B"], persisted!.AnswerLabelsToExecute);
    }

    [Fact]
    public async Task ConfirmDraft_StateRefreshFails_SessionIsMarkedStale_AndNextMessageRefreshesFirst()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", null);
        var draftResponse = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DCR nede"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.ConfirmDraftAsync(session.SessionId, draftResponse.PendingDraft!.Id));

        // Eventet er udført, så kladden må ikke blive hængende, men
        // tilstanden er markeret som forældet.
        var afterFailure = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Empty(afterFailure!.PendingAnswersQueue);
        Assert.True(afterFailure.IsGraphStateStale);

        // Mens DCR stadig er nede, går næste besked ikke videre til LLM'en.
        llmService.Invocations.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" }));
        llmService.Verify(
            s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Når DCR svarer igen, hentes tilstanden før matching.
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(graphState);
        await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        var afterRecovery = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.False(afterRecovery!.IsGraphStateStale);
    }

    [Fact]
    public async Task ConfirmDraft_AnswerStuckAfterFailedRefresh_IsRecovered_AndDoesNotBlockLaterAnswers()
    {
        // Lille fake-DCR: et choice gør sit svar-label pending, og et udført
        // label er ikke længere pending.
        var answerFor = new Dictionary<string, string> { ["minSU"] = "A2Copy", ["ansøgning"] = "A2Copy_1" };
        var pending = new HashSet<string> { "Velkommen" };
        GraphState CurrentState() => new()
        {
            Events =
            [
                new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
                new DcrEvent { Id = "ansøgning", Label = "ansøgning?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
                .. new[] { "Velkommen", "A2Copy", "A2Copy_1" }.Select(id => new DcrEvent
                {
                    Id = id, Label = "Svar " + id, DataType = "label", IsEnabled = true, IsPending = pending.Contains(id)
                })
            ]
        };
        var session = await StartSessionAsync(CurrentState());
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((_, _, id, _, _) =>
            {
                if (answerFor.TryGetValue(id, out var answer)) pending.Add(answer);
                else pending.Remove(id);
            })
            .ReturnsAsync(true);

        // DCR svarer ikke på tilstands-kaldet lige efter, at minSU er udført.
        SetupLlmMatch("minSU", null);
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DCR nede"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id));

        // DCR er oppe igen: det hængende svar findes og udføres, og det næste
        // spørgsmål får sit eget svar.
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrentState);
        SetupLlmMatch("ansøgning", null);
        var second = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "ansøgning?" });
        var secondAnswer = await service.ConfirmDraftAsync(session.SessionId, second.PendingDraft!.Id);
        Assert.Equal("Svar A2Copy_1", secondAnswer.Message);

        // Og minSU kan nu stilles igen og får sit svar.
        SetupLlmMatch("minSU", null);
        var third = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });
        var thirdAnswer = await service.ConfirmDraftAsync(session.SessionId, third.PendingDraft!.Id);
        Assert.Equal("Svar A2Copy", thirdAnswer.Message);
        Assert.Equal(["Velkommen"], pending);
    }

    [Fact]
    public async Task SendMessage_TypedJa_ConfirmsPendingDraft()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", null);
        await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Ja!" });

        Assert.Null(response.PendingDraft);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "minSU", "ja", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_TypedNej_RejectsPendingDraft_WithoutExecuting()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", null);
        await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "nej" });

        Assert.Null(response.PendingDraft);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_OtherTextWhileDraftPending_ThrowsConflict()
    {
        var graphState = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU", Label = "minSU?", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" }]
        };
        var session = await StartSessionAsync(graphState);
        SetupLlmMatch("minSU", null);
        await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "minsu?" });

        await Assert.ThrowsAsync<ChatConflictException>(() =>
            service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad med boligstøtte?" }));
    }

    [Fact]
    public async Task ConfirmDraft_WhenNoMorePendingEvents_ShowsConclusionWithDisplayValue()
    {
        var finalQuestion = new DcrEvent { Id = "height", Label = "height", DataType = "int", IsEnabled = true, IsPending = true };
        var conclusion = new DcrEvent
        {
            Id = "A4",
            Label = "Conclusion",
            Value = "Can you buy alcohol? Yes",
            DisplayValue = "Can you buy alcohol? Yes",
            DataType = "label",
            IsEnabled = true,
            IsPending = false
        };

        var session = await StartSessionAsync(new GraphState { Events = [finalQuestion, conclusion] });
        SetupLlmMatch("height", "180");
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "180" });

        // Som i DCR: højden er udført med værdien 180, og konklusionen er
        // først udført, når botten selv har udført den.
        var conclusionExecuted = false;
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "A4", "", It.IsAny<CancellationToken>()))
            .Callback(() => conclusionExecuted = true)
            .ReturnsAsync(true);
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GraphState
            {
                IsAccepting = true,
                Events = [
                    new DcrEvent { Id = "height", Label = "height", DataType = "int", IsEnabled = true, IsExecuted = true, IsPending = false, Value = "180" },
                    new DcrEvent
                    {
                        Id = conclusion.Id, Label = conclusion.Label, Value = conclusion.Value, DisplayValue = conclusion.DisplayValue,
                        DataType = "label", IsEnabled = true, IsExecuted = conclusionExecuted ? true : null
                    }
                ]
            });

        var response = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Can you buy alcohol? Yes", response.Message);
        Assert.True(response.IsEnded);
    }

    [Fact]
    public async Task ConfirmDraft_ConclusionLeavesOutResultThatCouldNotBeExecuted()
    {
        // FR-DCR-5: fejler udførslen af resultatet, må det ikke stå i
        // konklusionen, som om det var sket.
        var session = await StartSessionAsync(new GraphState
        {
            Events = [new DcrEvent { Id = "height", Label = "height", DataType = "int", IsEnabled = true, IsPending = true }]
        });
        SetupLlmMatch("height", "180");
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "180" });
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "A4", "", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("DCR nede"));
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState
            {
                Events = [
                    new DcrEvent { Id = "height", Label = "height", DataType = "int", IsEnabled = true, IsExecuted = true, Value = "180" },
                    new DcrEvent { Id = "A4", Label = "Conclusion", DataType = "label", IsEnabled = true, Value = "Can you buy alcohol? Yes" }
                ]
            });

        var response = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Tak for dine svar. Forløbet er nu afsluttet.", response.Message);
    }

    [Fact]
    public async Task InferredReplies_MessageWithTwoAnswers_GivesTwoConfirmationsInARow_WithoutRepeats()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction("18", new() { ["country"] = "denmark" });

        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg er 18 og bor i Danmark" });
        Assert.Equal("Age", first.PendingDraft!.EventId);
        Assert.Equal("18", first.PendingDraft.ProposedValue);
        Assert.False(first.PendingDraft.IsAutoInferred);

        var second = await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft.Id);
        Assert.Equal("Country", second.PendingDraft!.EventId);
        Assert.Equal("Denmark", second.PendingDraft.ProposedValue);
        Assert.True(second.PendingDraft.IsAutoInferred);
        Assert.Equal(string.Empty, second.Message);

        // Begge svar er udført, og botten går videre til det næste åbne
        // spørgsmål uden at spørge om alder eller land igen.
        var third = await service.ConfirmDraftAsync(session.SessionId, second.PendingDraft.Id);
        Assert.Null(third.PendingDraft);
        Assert.Equal("Hvor høj er du?", third.Message);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "Age", "18", It.IsAny<CancellationToken>()), Times.Once);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "Country", "Denmark", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InferredReplies_SkipsUnknownPrimaryInvalidAndAlreadyExecutedEvents()
    {
        var (session, _) = await StartFormSessionAsync(alreadyExecuted: "height");
        SetupValueExtraction("18", new()
        {
            ["Age"] = "99",          // det stillede spørgsmål selv
            ["Country"] = "Norway",  // ikke en gyldig værdi
            ["height"] = "180",      // allerede udført
            ["opfundet"] = "ja"      // findes ikke i grafen
        });

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg er 18" });

        var persisted = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Equal(["Age"], persisted!.PendingAnswersQueue.Select(d => d.EventId));
        Assert.Equal("18", response.PendingDraft!.ProposedValue);
    }

    [Fact]
    public async Task InferredReplies_QueuedDraftIsDropped_WhenEventNoLongerApplies()
    {
        var (session, graph) = await StartFormSessionAsync();
        SetupValueExtraction("18", new() { ["Country"] = "Denmark" });
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg er 18 og bor i Danmark" });

        // Svaret på alderen gør, at landet ikke længere er relevant.
        graph.Disabled.Add("Country");
        var response = await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id);

        Assert.Null(response.PendingDraft);
        Assert.Equal("Hvor høj er du?", response.Message);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), "Country", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InferredReplies_AreQueued_WhenAskedQuestionIsNotAnswered()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction(null, new() { ["Country"] = "Sweden" });

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg bor i Sverige" });

        Assert.Equal("Country", response.PendingDraft!.EventId);
        Assert.Equal("Sweden", response.PendingDraft.ProposedValue);
    }

    [Fact]
    public async Task InferredReplies_RejectingFirstDraft_StillShowsTheNext()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction("18", new() { ["Country"] = "Denmark" });
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg er 18 og bor i Danmark" });

        var response = await service.RejectDraftAsync(session.SessionId, first.PendingDraft!.Id);

        Assert.Equal("Country", response.PendingDraft!.EventId);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    private void SetupLlmMatch(string eventId, string? value)
    {
        llmService
            .Setup(s => s.MatchEventAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<DcrEvent>>(),
                It.IsAny<LlmOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((eventId, new TokenUsageResult()));
        SetupValueExtraction(value);
        llmService
            .Setup(s => s.ClassifyConfirmationAsync(
                It.IsAny<string>(),
                It.IsAny<PendingAnswer>(),
                It.IsAny<LlmOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((string message, PendingAnswer _, LlmOptions _, CancellationToken _) =>
            {
                var normalized = message.Trim().TrimEnd('.', '!', '?').Trim();
                bool? result = normalized.StartsWith("nej", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                        ? true
                        : null;
                return Task.FromResult((result, new TokenUsageResult()));
            });
    }

    private void SetupValueExtraction(string? value, Dictionary<string, string>? inferredReplies = null) =>
        llmService
            .Setup(s => s.ExtractValueAsync(
                It.IsAny<string>(),
                It.IsAny<DcrEvent>(),
                It.IsAny<IEnumerable<DcrEvent>>(),
                It.IsAny<LlmOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                new LlmValueResult
                {
                    ExtractedValue = value,
                    Explanation = "Extracted by test",
                    InferredReplies = inferredReplies ?? new()
                },
                new TokenUsageResult()));

    // Lille fake af en formular-graf i DCR: alle spørgsmål starter pending,
    // et udført event er ikke længere pending, og events kan slås fra.
    private sealed class FakeFormGraph
    {
        private readonly HashSet<string> executed = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);

        public GraphState State() => new()
        {
            Events =
            [
                Question("Age", "Hvor gammel er du?", "int", 1),
                Question("Country", "Hvilket land bor du i?", "choice", 2, "Denmark (Denmark), Sweden (Sweden)"),
                Question("height", "Hvor høj er du?", "int", 3)
            ]
        };

        public void Execute(string eventId) => executed.Add(eventId);

        private DcrEvent Question(string id, string label, string dataType, int sequence, string choiceValues = "") => new()
        {
            Id = id,
            Label = label,
            DataType = dataType,
            Sequence = sequence,
            ChoiceValues = choiceValues,
            IsEnabled = !Disabled.Contains(id),
            IsPending = !executed.Contains(id),
            IsExecuted = executed.Contains(id) ? true : null
        };
    }

    private async Task<(ChatSession Session, FakeFormGraph Graph)> StartFormSessionAsync(params string[] alreadyExecuted)
    {
        var graph = new FakeFormGraph();
        foreach (var eventId in alreadyExecuted)
        {
            graph.Execute(eventId);
        }

        var session = await StartSessionAsync(graph.State());
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => graph.State());
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((_, _, id, _, _) => graph.Execute(id))
            .ReturnsAsync(true);
        return (session, graph);
    }
}
