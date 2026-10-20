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
                        DataType = "label", IsEnabled = true, Included = true, IsExecuted = conclusionExecuted ? true : null
                    }
                ]
            });

        var response = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Can you buy alcohol? Yes", response.Message);
        Assert.True(response.IsEnded);
    }

    [Fact]
    public async Task FaqQuestionWithoutAnswer_SaysSo_AndDoesNotRepeatEarlierAnswersOrTouchTheAnchor()
    {
        // Som "SU faq": minSU har et svar-label; førstegang peger kun på et
        // andet spørgsmål; ankeret er pending og har fået en værdi i grafen.
        var executed = new HashSet<string>();
        var pending = new HashSet<string> { "Velkommen" };
        GraphState State() => new()
        {
            Events =
            [
                new DcrEvent { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", Included = true, IsEnabled = true, IsPending = pending.Contains("Velkommen"), IsExecuted = executed.Contains("Velkommen") ? true : null, Value = "Jeg er en chatboit" },
                new DcrEvent { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", Included = true, IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)", IsExecuted = executed.Contains("minSU") ? true : null },
                new DcrEvent { Id = "førstegang", Label = "Det er første gang jeg søger SU - hvad gør jeg?", DataType = "choice", Included = true, IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)", IsExecuted = executed.Contains("førstegang") ? true : null },
                new DcrEvent { Id = "A2Copy", Label = "På minSU kan du følge med i din SU.", DataType = "label", Included = true, IsEnabled = true, IsPending = pending.Contains("A2Copy"), IsExecuted = executed.Contains("A2Copy") ? true : null }
            ]
        };
        var session = await StartSessionAsync(State());
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(State);
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((_, _, id, _, _) =>
            {
                executed.Add(id);
                pending.Remove(id);
                if (id == "minSU") pending.Add("A2Copy");
            })
            .ReturnsAsync(true);

        SetupLlmMatch("minSU", null);
        var minSuDraft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad er minsu?" });
        var minSu = await service.ConfirmDraftAsync(session.SessionId, minSuDraft.PendingDraft!.Id);
        Assert.Equal("På minSU kan du følge med i din SU.", minSu.Message);

        SetupLlmMatch("førstegang", null);
        var firstTimeDraft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "første gang?" });
        var firstTime = await service.ConfirmDraftAsync(session.SessionId, firstTimeDraft.PendingDraft!.Id);

        Assert.Equal("Jeg har desværre ikke et svar på det spørgsmål endnu.", firstTime.Message);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), "Velkommen", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

    [Fact]
    public async Task ConfirmDraft_ReturnsTheConfirmedAnswer_SoItCanBeEdited()
    {
        var (session, _) = await StartFormSessionAsync();
        var response = await AnswerAsync(session, "18");

        Assert.Equal("Age", response.ConfirmedAnswer!.EventId);
        Assert.Equal("Hvor gammel er du?", response.ConfirmedAnswer.Question);
        Assert.Equal("18", response.ConfirmedAnswer.Value);
    }

    [Fact]
    public async Task EditAnswer_CreatesRevisionDraft_AndConfirmExecutesTheNewValue()
    {
        var (session, _) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        SetupValueExtraction("24");

        var draft = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "jeg er 24" });

        Assert.Equal("Age", draft.PendingDraft!.EventId);
        Assert.Equal("18", draft.PendingDraft.PreviousValue);
        Assert.Equal("24", draft.PendingDraft.ProposedValue);
        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "Age", "24", It.IsAny<CancellationToken>()), Times.Never);

        var confirmed = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft.Id);

        dcrRepository.Verify(r => r.ExecuteEventAsync("graph-1", "sim-1", "Age", "24", It.IsAny<CancellationToken>()), Times.Once);
        Assert.StartsWith("Dit svar på 'Hvor gammel er du?' er ændret fra '18' til '24'.", confirmed.Message);
        Assert.Equal("24", confirmed.ConfirmedAnswer!.Value);
    }

    [Fact]
    public async Task EditAnswer_ClearsDependentAnswers_AndNamesThem()
    {
        // Grafens regel: er man under 18, er land og højde ikke relevante.
        var (session, graph) = await StartFormSessionAsync();
        graph.OnExecute = (g, id, value) =>
        {
            if (id == "Age" && int.TryParse(value, out var age) && age < 18)
            {
                g.Excluded.Add("Country");
                g.Excluded.Add("height");
            }
        };

        // Alder og land er besvaret; højden ligger som afledt svar i køen.
        SetupValueExtraction("18", new() { ["height"] = "180" });
        var first = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg er 18 og 180 høj" });
        await service.ConfirmDraftAsync(session.SessionId, first.PendingDraft!.Id);
        graph.Execute("Country", "Denmark");

        SetupValueExtraction("15");
        var revision = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "jeg er 15" });
        var response = await service.ConfirmDraftAsync(session.SessionId, revision.PendingDraft!.Id);

        Assert.Contains(
            "Det betyder, at disse svar ikke længere gælder:\n- Hvilket land bor du i?\n- Hvor høj er du?",
            response.Message);
        Assert.Null(response.PendingDraft);
        var persisted = await sessionStore.GetSessionAsync(session.SessionId);
        Assert.Empty(persisted!.PendingAnswersQueue);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), "height", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EditAnswer_WhenEventIsNoLongerEnabled_ExplainsWhy_WithoutDraft()
    {
        var (session, graph) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        graph.Disabled.Add("Age");
        llmService.Invocations.Clear();

        var response = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "jeg er 24" });

        Assert.Null(response.PendingDraft);
        Assert.Contains("kan ikke rettes lige nu", response.Message);
        llmService.Verify(
            s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EditAnswer_WhenEventIsExcluded_ExplainsThatItIsNoLongerPartOfTheFlow()
    {
        var (session, graph) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        graph.Excluded.Add("Age");

        var response = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "jeg er 24" });

        Assert.Null(response.PendingDraft);
        Assert.Contains("ikke længere en del af forløbet", response.Message);
    }

    [Fact]
    public async Task EditAnswer_WhenQuestionIsNotAnswered_HasNothingToEdit()
    {
        var (session, _) = await StartFormSessionAsync();

        var response = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Country", Message = "Sverige" });

        Assert.Null(response.PendingDraft);
        Assert.Contains("ikke svaret på", response.Message);
    }

    [Fact]
    public async Task EditAnswer_WithSameValue_DoesNotCreateDraft()
    {
        var (session, _) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        SetupValueExtraction("18");

        var response = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "18" });

        Assert.Null(response.PendingDraft);
        Assert.Contains("er allerede '18'", response.Message);
    }

    [Fact]
    public async Task EditAnswer_WithInvalidValue_DoesNotCreateDraft()
    {
        var (session, _) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        SetupValueExtraction("atten og et halvt");

        var response = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "atten og et halvt" });

        Assert.Null(response.PendingDraft);
    }

    // En FAQ-graf som "SU faq": et anker-label, svar-labels og FAQ-spørgsmål.
    private static GraphState FaqGraph() => new()
    {
        Events =
        [
            new DcrEvent { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", IsEnabled = true, IsPending = true },
            new DcrEvent { Id = "ansøgning", Label = "Hvordan finder jeg ud af, om min ansøgning er under behandling?", DataType = "choice", Sequence = 2, IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
            new DcrEvent { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", Sequence = 1, IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
            new DcrEvent { Id = "A2Copy", Label = "På minSU kan du følge med i din SU.", DataType = "label", IsEnabled = true },
            new DcrEvent { Id = "lukket", Label = "Et emne der ikke er åbent", DataType = "choice", IsEnabled = false, ChoiceValues = "ja (ja), nej (nej)" }
        ]
    };

    [Theory]
    [InlineData(null)]          // LLM'en fandt intet match, fx "hvad kan jeg få hjælp til?"
    [InlineData("opfundet")]    // LLM'en fandt på et event, der ikke findes
    public async Task SendMessage_NoMatch_ShowsTheGraphsTopicsAsSuggestions(string? matchedEventId)
    {
        var session = await StartSessionAsync(FaqGraph());
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((matchedEventId, new TokenUsageResult()));

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad kan jeg få hjælp til?" });

        Assert.Null(response.PendingDraft);
        Assert.Contains("jeg kan hjælpe dig med disse emner", response.Message);
        Assert.Equal(["minSU", "ansøgning"], response.Suggestions.Select(s => s.EventId));
        Assert.Equal("Hvorfor skal jeg bruge minSU?", response.Suggestions[0].Label);
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EventWithoutDataType_IsNeverAskedOrMatched_SoTheBotDoesNotGetStuck()
    {
        // Som "nyt faq" → "fakta om faq" i SU-grafen: faq har ingen datatype,
        // så den er hverken et svar (label) eller et spørgsmål med en værdi.
        var faqPending = false;
        GraphState State() => new()
        {
            Events =
            [
                new DcrEvent { Id = "nyt", Label = "nyt faq", DataType = "choice", IsEnabled = true, ChoiceValues = "ja (ja), nej (nej)" },
                new DcrEvent { Id = "faq", Label = "fakta om faq", DataType = "", IsEnabled = true, IsPending = faqPending }
            ]
        };
        var session = await StartSessionAsync(State());
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(State);
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", "nyt", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => faqPending = true)
            .ReturnsAsync(true);

        IEnumerable<DcrEvent>? offeredCandidates = null;
        SetupLlmMatch("nyt", null);
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<DcrEvent>, LlmOptions, CancellationToken>((_, candidates, _, _) => offeredCandidates = candidates)
            .ReturnsAsync(("nyt", new TokenUsageResult()));
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "fortæl om det nye" });
        var confirmed = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Jeg har desværre ikke et svar på det spørgsmål endnu.", confirmed.Message);
        Assert.DoesNotContain(offeredCandidates!, e => e.Id == "faq");

        // Næste besked bliver ikke tolket som et svar på "fakta om faq".
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?)null, new TokenUsageResult()));
        var next = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad kan du hjælpe med" });

        Assert.Null(next.PendingDraft);
        Assert.DoesNotContain("fakta om faq", next.Message);
        llmService.Verify(
            s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChoiceQuestion_ComesWithTheGraphsValuesAsAnswerOptions()
    {
        var (session, _) = await StartFormSessionAsync();

        // Første spørgsmål er et tal-spørgsmål: ingen knapper.
        SetupValueExtraction("18");
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "18" });
        Assert.Empty(draft.AnswerOptions);

        // Næste spørgsmål er et choice-spørgsmål: grafens værdier som knapper.
        var next = await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Hvilket land bor du i?", next.Message);
        Assert.Equal(["Denmark", "Sweden"], next.AnswerOptions.Select(o => o.Value));
    }

    [Fact]
    public async Task AnswerOptions_AreNotShown_WhileADraftIsOpen()
    {
        var (session, _) = await StartFormSessionAsync(alreadyExecuted: "Age");
        SetupValueExtraction("Denmark");

        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Danmark" });

        Assert.NotNull(draft.PendingDraft);
        Assert.Empty(draft.AnswerOptions);
    }

    [Fact]
    public async Task FormChoiceField_IsNeitherATopicNorReExecuted_AfterTheFormIsDone()
    {
        // Efter formularen: "uddannelse" er et udført choice-felt uden ja-valg.
        var session = await StartSessionAsync(new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "hjælp", Label = "Jeg vil gerne have hjælp til at søge SU", DataType = "choice", IsEnabled = true, IsExecuted = true, ChoiceValues = "Ja (ja), Nej (nej)" },
                new DcrEvent { Id = "A17", Label = "uddannelse", DataType = "choice", IsEnabled = true, IsExecuted = true, Value = "eud", ChoiceValues = "Gymnasium (gym), Erhvervsuddannelse (eud)" }
            ]
        });
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("A17", new TokenUsageResult()));

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "uddannelse" });

        Assert.Null(response.PendingDraft);
        Assert.Equal(["hjælp"], response.Suggestions.Select(s => s.EventId));
        dcrRepository.Verify(
            r => r.ExecuteEventAsync(It.IsAny<string>(), It.IsAny<string>(), "A17", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TopicButton_MatchesTheEventDirectly_WithoutAskingTheLlm()
    {
        // Emnet indeholder "hjælp", som LLM'en kan tolke som et generelt
        // spørgsmål; et tryk på knappen sender præcis emnets tekst.
        var session = await StartSessionAsync(new GraphState
        {
            Events = [new DcrEvent { Id = "hjælp", Label = "Jeg vil gerne have hjælp til at søge SU", DataType = "choice", IsEnabled = true, ChoiceValues = "Ja (ja), Nej (nej)" }]
        });
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?)null, new TokenUsageResult()));

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "jeg vil gerne have hjælp til at søge SU " });

        Assert.Equal("hjælp", response.PendingDraft!.EventId);
        llmService.Verify(
            s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnswerButton_UsesTheChoiceValueDirectly_WithoutAskingTheLlm()
    {
        var (session, _) = await StartFormSessionAsync(alreadyExecuted: "Age");
        llmService.Invocations.Clear();

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Denmark" });

        Assert.Equal("Country", response.PendingDraft!.EventId);
        Assert.Equal("Denmark", response.PendingDraft.ProposedValue);
        llmService.Verify(
            s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RejectingAFormAnswer_AsksTheQuestionAgain()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction("18");
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "18" });

        var response = await service.RejectDraftAsync(session.SessionId, draft.PendingDraft!.Id);

        Assert.Equal("Okay, så spørger jeg igen: Hvor gammel er du?", response.Message);
    }

    [Fact]
    public async Task RejectingAnEdit_KeepsTheEarlierAnswer()
    {
        var (session, _) = await StartFormSessionAsync();
        await AnswerAsync(session, "18");
        SetupValueExtraction("24");
        var revision = await service.EditAnswerAsync(session.SessionId, new ChatRequest { EventId = "Age", Message = "24" });

        var response = await service.RejectDraftAsync(session.SessionId, revision.PendingDraft!.Id);

        Assert.Equal("Okay, dit svar på 'Hvor gammel er du?' er stadig '18'.", response.Message);
    }

    [Fact]
    public async Task Start_FaqGraph_GreetsWithTheGraphsTopics()
    {
        dcrRepository.Setup(r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("sim-1");
        dcrRepository.Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>())).ReturnsAsync(FaqGraph());

        var response = await service.StartAsync(new ChatRequest { GraphId = "graph-1" });

        Assert.Equal("Hej! Hvad vil du vide noget om?", response.Message);
        Assert.Equal(["minSU", "ansøgning"], response.Suggestions.Select(s => s.EventId));
    }

    [Fact]
    public async Task Start_FormGraph_AsksTheFirstQuestion_WithoutSuggestions()
    {
        var graph = new FakeFormGraph();
        dcrRepository.Setup(r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("sim-1");
        dcrRepository.Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>())).ReturnsAsync(graph.State());

        var response = await service.StartAsync(new ChatRequest { GraphId = "graph-1" });

        Assert.Equal("Hvor gammel er du?", response.Message);
        Assert.Empty(response.Suggestions);
    }

    [Fact]
    public async Task FormFlow_MessageThatIsNotAnAnswer_RemindsOfTheQuestion()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction(null);

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "hvad kan jeg få hjælp til?" });

        Assert.Null(response.PendingDraft);
        Assert.Equal("Jeg mangler stadig et svar på: Hvor gammel er du?", response.Message);
    }

    [Fact]
    public async Task FormFlow_InvalidAnswer_SaysItDoesNotFitTheQuestion()
    {
        var (session, _) = await StartFormSessionAsync();
        SetupValueExtraction("atten");

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "atten" });

        Assert.Null(response.PendingDraft);
        Assert.Equal("Det svar passer ikke til spørgsmålet: Hvor gammel er du?", response.Message);
    }

    // Som formularen i "SU faq": "hjælp" (ja) åbner navn → adresse →
    // uddannelse med condition-pile; uddannelse gør "Tak!" pending, og
    // fortryd (A12) lukker formularen og forsvinder, når den er udfyldt.
    private sealed class FakeSuForm
    {
        private readonly Dictionary<string, string?> executed = new(StringComparer.OrdinalIgnoreCase);
        private bool formOpen;

        private bool Done(string id) => executed.ContainsKey(id);

        public GraphState State() => new()
        {
            Events =
            [
                new DcrEvent { Id = "hjælp", Label = "Jeg vil gerne have hjælp til at søge SU", DataType = "choice", Roles = "User", Sequence = 0, Included = true, IsEnabled = true, IsExecuted = Done("hjælp") ? true : null, ChoiceValues = "Ja (ja), Nej (nej)" },
                Field("A8", "Hvad er dit fulde navn?", "text", 1, enabled: formOpen),
                Field("A9", "Hvad er din adresse?", "text", 2, enabled: formOpen && Done("A8")),
                Field("A17", "Hvilken uddannelse skal du i gang med?", "choice", 3, enabled: formOpen && Done("A9"), "Gymnasium (gym), Erhvervsuddannelse (eud)"),
                new DcrEvent { Id = "A18", Label = "Tak! Vi har noteret dine oplysninger.", DataType = "label", Roles = "Chatbot", Sequence = 4, Included = formOpen, IsEnabled = formOpen, IsPending = Done("A17") && !Done("A18"), IsExecuted = Done("A18") ? true : null },
                new DcrEvent { Id = "A12", Label = "jeg vil ikke ansøge alligevel", DataType = "choice", Roles = "User", Sequence = 5, Included = formOpen && !Done("A17"), IsEnabled = formOpen && !Done("A17"), IsExecuted = Done("A12") ? true : null, ChoiceValues = "Ja (ja), Nej (nej)" }
            ]
        };

        public void Execute(string id, string? value)
        {
            executed[id] = value;
            if (id == "hjælp" && value == "ja") formOpen = true;
            if (id == "A12") formOpen = false;
        }

        private DcrEvent Field(string id, string label, string dataType, int sequence, bool enabled, string choiceValues = "") => new()
        {
            Id = id, Label = label, DataType = dataType, Roles = "User", Sequence = sequence, ChoiceValues = choiceValues,
            Included = formOpen, IsEnabled = enabled, IsPending = formOpen && !Done(id),
            IsExecuted = Done(id) ? true : null, Value = executed.GetValueOrDefault(id)
        };
    }

    private async Task<(ChatSession Session, FakeSuForm Graph)> StartSuFormAsync()
    {
        var graph = new FakeSuForm();
        var session = await StartSessionAsync(graph.State());
        dcrRepository
            .Setup(r => r.GetGraphStateAsync("graph-1", "sim-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => graph.State());
        dcrRepository
            .Setup(r => r.ExecuteEventAsync("graph-1", "sim-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>((_, _, id, value, _) => graph.Execute(id, value))
            .ReturnsAsync(true);

        // Formularen åbnes med emne-knappen og bekræftes.
        var open = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg vil gerne have hjælp til at søge SU" });
        await service.ConfirmDraftAsync(session.SessionId, open.PendingDraft!.Id);
        return (session, graph);
    }

    [Fact]
    public async Task Form_InferredReplyForAFieldWaitingOnACondition_IsQueuedAndConfirmedInTurn()
    {
        var (session, _) = await StartSuFormAsync();
        SetupValueExtraction("Jens", new() { ["A9"] = "Lige her 1" });

        var name = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "min adresse er lige her 1, og mit navn er Jens" });
        Assert.Equal("A8", name.PendingDraft!.EventId);

        var address = await service.ConfirmDraftAsync(session.SessionId, name.PendingDraft.Id);
        Assert.Equal("A9", address.PendingDraft!.EventId);
        Assert.Equal("Lige her 1", address.PendingDraft.ProposedValue);

        var next = await service.ConfirmDraftAsync(session.SessionId, address.PendingDraft.Id);
        Assert.Equal("Hvilken uddannelse skal du i gang med?", next.Message);
    }

    [Fact]
    public async Task Form_WhenOnlyInferredRepliesAreFound_AsksAgainForTheAskedQuestionAlone()
    {
        var (session, _) = await StartSuFormAsync();
        // Første kald (med de andre spørgsmål) finder kun adressen; andet
        // kald (kun det stillede spørgsmål) finder navnet.
        llmService
            .Setup(s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.Is<IEnumerable<DcrEvent>>(o => o.Any()), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new LlmValueResult { InferredReplies = new() { ["A9"] = "Lige her 1" } }, new TokenUsageResult()));
        llmService
            .Setup(s => s.ExtractValueAsync(It.IsAny<string>(), It.IsAny<DcrEvent>(), It.Is<IEnumerable<DcrEvent>>(o => !o.Any()), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new LlmValueResult { ExtractedValue = "Jens" }, new TokenUsageResult()));

        var name = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "jens, jeg bor lige her 1" });

        Assert.Equal("A8", name.PendingDraft!.EventId);
        Assert.Equal("Jens", name.PendingDraft.ProposedValue);
        var address = await service.ConfirmDraftAsync(session.SessionId, name.PendingDraft.Id);
        Assert.Equal("Lige her 1", address.PendingDraft!.ProposedValue);
    }

    [Fact]
    public async Task Form_AnswerForALaterField_IsParked_AndOfferedWhenTheFieldIsReady()
    {
        // "jens, jeg starter i gym snart": uddannelse venter på adressen.
        var (session, _) = await StartSuFormAsync();
        SetupValueExtraction("Jens", new() { ["A17"] = "gym" });
        var name = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "jens, jeg starter i gym snart" });
        Assert.Equal("A8", name.PendingDraft!.EventId);

        // Efter navnet spørges der om adressen; uddannelsen er ikke glemt.
        var askAddress = await service.ConfirmDraftAsync(session.SessionId, name.PendingDraft.Id);
        Assert.Null(askAddress.PendingDraft);
        Assert.Equal("Hvad er din adresse?", askAddress.Message);

        SetupValueExtraction("her");
        var address = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "her" });
        var education = await service.ConfirmDraftAsync(session.SessionId, address.PendingDraft!.Id);

        Assert.Equal("A17", education.PendingDraft!.EventId);
        Assert.Equal("gym", education.PendingDraft.ProposedValue);
    }

    [Fact]
    public async Task Form_InferredReplyForABlockedField_IsNotShownBeforeTheAskedQuestion()
    {
        var (session, _) = await StartSuFormAsync();
        SetupValueExtraction(null, new() { ["A9"] = "Lige her 1" });

        var response = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "jeg bor lige her 1" });

        Assert.Null(response.PendingDraft);
        Assert.Equal("Jeg mangler stadig et svar på: Hvad er dit fulde navn?", response.Message);
    }

    [Fact]
    public async Task Form_RejectingTheName_KeepsTheAddressParked_UntilANameIsGiven()
    {
        var (session, _) = await StartSuFormAsync();
        SetupValueExtraction("Jens", new() { ["A9"] = "Lige her 1" });
        var name = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jens, lige her 1" });

        // Adressen kan ikke vises, før et navn er bekræftet.
        var response = await service.RejectDraftAsync(session.SessionId, name.PendingDraft!.Id);
        Assert.Null(response.PendingDraft);
        Assert.Equal("Okay, så spørger jeg igen: Hvad er dit fulde navn?", response.Message);

        // Når det rigtige navn er bekræftet, kommer adressen frem igen.
        SetupValueExtraction("Jensen");
        var corrected = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jensen" });
        var address = await service.ConfirmDraftAsync(session.SessionId, corrected.PendingDraft!.Id);

        Assert.Equal("A9", address.PendingDraft!.EventId);
        Assert.Equal("Lige her 1", address.PendingDraft.ProposedValue);
    }

    [Fact]
    public async Task Form_CancelTopicMidForm_IsMatchedAndStopsTheForm()
    {
        var (session, _) = await StartSuFormAsync();
        SetupValueExtraction(null);
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("A12", new TokenUsageResult()));

        var cancel = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "afslut det her tak" });
        Assert.Equal("A12", cancel.PendingDraft!.EventId);
        Assert.True(cancel.PendingDraft.IsEventMatch);

        var response = await service.ConfirmDraftAsync(session.SessionId, cancel.PendingDraft.Id);

        Assert.Equal("Okay, så stopper vi der.", response.Message);
    }

    [Fact]
    public async Task Form_ChoosingTheFormAgainAfterFillingIt_PointsToThePencil()
    {
        var (session, _) = await StartSuFormAsync();
        await AnswerAsync(session, "Jens");
        await AnswerAsync(session, "Lige her 1");
        var done = await AnswerAsync(session, "gym");
        Assert.Equal("Tak! Vi har noteret dine oplysninger.", done.Message);

        var again = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = "Jeg vil gerne have hjælp til at søge SU" });
        var response = await service.ConfirmDraftAsync(session.SessionId, again.PendingDraft!.Id);

        Assert.Equal("Det har du allerede været igennem. Du kan rette dine svar med blyanten ✎ ved dem.", response.Message);
    }

    // Svarer på det næste spørgsmål i formular-grafen og bekræfter svaret.
    private async Task<ChatResponse> AnswerAsync(ChatSession session, string value)
    {
        SetupValueExtraction(value);
        var draft = await service.SendMessageAsync(session.SessionId, new ChatRequest { Message = value });
        return await service.ConfirmDraftAsync(session.SessionId, draft.PendingDraft!.Id);
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
        private readonly Dictionary<string, string?> executed = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Lader en test efterligne grafens regler, fx at en alder under 18
        // udelukker spørgsmålet om land.
        public Action<FakeFormGraph, string, string?>? OnExecute { get; set; }

        public GraphState State() => new()
        {
            Events =
            [
                Question("Age", "Hvor gammel er du?", "int", 1),
                Question("Country", "Hvilket land bor du i?", "choice", 2, "Denmark (Denmark), Sweden (Sweden)"),
                Question("height", "Hvor høj er du?", "int", 3)
            ]
        };

        public void Execute(string eventId, string? value = null)
        {
            executed[eventId] = value;
            OnExecute?.Invoke(this, eventId, value);
        }

        private DcrEvent Question(string id, string label, string dataType, int sequence, string choiceValues = "") => new()
        {
            Id = id,
            Label = label,
            DataType = dataType,
            Sequence = sequence,
            ChoiceValues = choiceValues,
            Roles = "User",
            Included = !Excluded.Contains(id),
            IsEnabled = !Disabled.Contains(id) && !Excluded.Contains(id),
            IsPending = !executed.ContainsKey(id),
            IsExecuted = executed.ContainsKey(id) ? true : null,
            Value = executed.GetValueOrDefault(id)
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
            .Callback<string, string, string, string?, CancellationToken>((_, _, id, value, _) => graph.Execute(id, value))
            .ReturnsAsync(true);
        return (session, graph);
    }
}
