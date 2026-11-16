using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Routing;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;
using DcrChatbot.Core.Domain.ValueObjects;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DcrChatbot.UnitTests;

/// <summary>
/// Den generelle chatbot: samtalen startes uden graf, og grafen findes ud
/// fra borgerens første besked.
/// </summary>
public sealed class GraphRoutingTests
{
    private const string SuGraphId = "2012392";
    private const string HousingGraphId = "3000001";

    private readonly Mock<IDcrRepository> dcrRepository = new();
    private readonly Mock<ILlmService> llmService = new();
    private readonly InMemorySessionStore sessionStore = new();
    private readonly TurnTrace trace = new();
    private readonly ChatService service;

    public GraphRoutingTests()
    {
        service = new ChatService(
            dcrRepository.Object,
            llmService.Object,
            sessionStore,
            Options.Create(new LlmOptions()),
            trace,
            new GraphCatalog(),
            NullLogger<ChatService>.Instance);

        dcrRepository
            .Setup(r => r.GetGraphsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DcrGraph { GraphId = SuGraphId, Title = "SU" },
                new DcrGraph { GraphId = HousingGraphId, Title = "Boligstøtte" }
            ]);
        dcrRepository
            .Setup(r => r.GetGraphDefinitionAsync(SuGraphId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Definition("minSU", "Hvordan søger jeg SU?"));
        dcrRepository
            .Setup(r => r.GetGraphDefinitionAsync(HousingGraphId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Definition("bolig", "Hvordan søger jeg boligstøtte?"));
        dcrRepository
            .Setup(r => r.CreateSimulationAsync(SuGraphId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("sim-su");
        dcrRepository
            .Setup(r => r.GetGraphStateAsync(SuGraphId, "sim-su", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState
            {
                Events =
                [
                    new DcrEvent
                    {
                        Id = "minSU", Label = "Hvordan søger jeg SU?", DataType = "choice",
                        IsEnabled = true, Included = true, ChoiceValues = "ja (ja), nej (nej)"
                    }
                ]
            });
    }

    [Fact]
    public async Task Start_WithoutGraphId_CreatesSessionWithoutGraphOrSimulation()
    {
        var response = await service.StartAsync(new ChatRequest());

        var session = (await sessionStore.GetSessionAsync(response.SessionId))!;
        Assert.Equal(string.Empty, session.GraphId);
        Assert.Null(session.SimulationId);
        Assert.Equal(["SU", "Boligstøtte"], response.Suggestions.Select(s => s.Label));
        dcrRepository.Verify(
            r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FirstMessage_AboutSu_IsRoutedToSuGraph_AndAnsweredInTheSameTurn()
    {
        var start = await service.StartAsync(new ChatRequest());
        SetupGraphMatch(SuGraphId);
        string? matchedMessage = null;
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<DcrEvent>, LlmOptions, CancellationToken>((message, _, _, _) => matchedMessage = message)
            .ReturnsAsync(("minSU", new TokenUsageResult()));

        var response = await service.SendMessageAsync(
            start.SessionId, new ChatRequest { Message = "jeg vil gerne søge SU" });

        var session = (await sessionStore.GetSessionAsync(start.SessionId))!;
        Assert.Equal(SuGraphId, session.GraphId);
        Assert.Equal("sim-su", session.SimulationId);
        Assert.Equal("jeg vil gerne søge SU", matchedMessage);
        Assert.Equal("minSU", response.PendingDraft?.EventId);
    }

    [Fact]
    public async Task FirstMessage_WithoutMatchingGraph_ListsTopics_AndCreatesNoSimulation()
    {
        var start = await service.StartAsync(new ChatRequest());
        SetupGraphMatch(null);

        var response = await service.SendMessageAsync(
            start.SessionId, new ChatRequest { Message = "hvornår har svømmehallen åbent?" });

        Assert.Contains("kan hjælpe dig med", response.Message);
        Assert.Equal(["SU", "Boligstøtte"], response.Suggestions.Select(s => s.Label));
        Assert.Null(response.PendingDraft);
        Assert.Equal(string.Empty, (await sessionStore.GetSessionAsync(start.SessionId))!.GraphId);
        dcrRepository.Verify(
            r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("graph_not_found", trace.Routing?.Outcome);
    }

    [Fact]
    public async Task Router_TreatsAGraphIdOutsideTheCatalog_AsNoGraph()
    {
        SetupGraphMatch("opfundet-graf", ["også-opfundet"]);
        var router = new GraphRouter(llmService.Object, new LlmOptions());
        var catalog = await new GraphCatalog().GetEntriesAsync(dcrRepository.Object);

        var decision = await router.RouteAsync("noget helt andet", catalog, CancellationToken.None);

        Assert.Equal(GraphRoutingKind.NotFound, decision.Kind);
        Assert.Null(decision.Graph);
        Assert.Empty(decision.Candidates);
    }

    [Fact]
    public async Task FirstMessage_WithUnknownGraphId_CreatesNoSimulation()
    {
        var start = await service.StartAsync(new ChatRequest());
        SetupGraphMatch("opfundet-graf");

        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "hej" });

        dcrRepository.Verify(
            r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AmbiguousMessage_AsksForTopic_ThenAnswersTheOriginalQuestion()
    {
        var start = await service.StartAsync(new ChatRequest());
        SetupGraphMatch(null, [SuGraphId, HousingGraphId]);

        var question = await service.SendMessageAsync(
            start.SessionId, new ChatRequest { Message = "hvordan søger jeg?" });

        Assert.Equal(["SU", "Boligstøtte"], question.Suggestions.Select(s => s.Label));
        Assert.Equal("graph_ambiguous", trace.Routing?.Outcome);

        string? matchedMessage = null;
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<DcrEvent>, LlmOptions, CancellationToken>((message, _, _, _) => matchedMessage = message)
            .ReturnsAsync(("minSU", new TokenUsageResult()));

        // Et tryk på emne-knappen sender titlen; den routes uden LLM.
        llmService.Invocations.Clear();
        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "SU" });

        Assert.Equal(SuGraphId, (await sessionStore.GetSessionAsync(start.SessionId))!.GraphId);
        Assert.Equal("hvordan søger jeg?", matchedMessage);
        llmService.Verify(
            s => s.MatchGraphAsync(It.IsAny<string>(), It.IsAny<IEnumerable<GraphCatalogEntry>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Start_WithExplicitGraphId_NeverRoutes_EvenWhenTheMessageMatchesNothing()
    {
        var start = await service.StartAsync(new ChatRequest { GraphId = SuGraphId });
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?)null, new TokenUsageResult()));
        SetupGraphMatch(HousingGraphId);

        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "hvordan søger jeg boligstøtte?" });

        Assert.Equal(SuGraphId, (await sessionStore.GetSessionAsync(start.SessionId))!.GraphId);
        dcrRepository.Verify(r => r.GetGraphsAsync(It.IsAny<CancellationToken>()), Times.Never);
        llmService.Verify(
            s => s.MatchGraphAsync(It.IsAny<string>(), It.IsAny<IEnumerable<GraphCatalogEntry>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Null(trace.Routing);
    }

    [Fact]
    public async Task LlmOnly_UsesTheSameRouting_AndOnlyTheRoutedGraphsKnowledge()
    {
        var start = await service.StartAsync(new ChatRequest { Mode = ExecutionMode.LlmOnly });
        SetupGraphMatch(SuGraphId);
        string? prompt = null;
        llmService
            .Setup(s => s.GenerateTextAsync(It.IsAny<string>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, LlmOptions, CancellationToken>((p, _, _) => prompt = p)
            .ReturnsAsync(("Svar", new TokenUsageResult()));

        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "hvordan søger jeg SU?" });

        Assert.Contains("Hvordan søger jeg SU?", prompt);
        Assert.DoesNotContain("boligstøtte", prompt, StringComparison.OrdinalIgnoreCase);
        dcrRepository.Verify(
            r => r.CreateSimulationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Routing_IsRecordedOnTheTurnTrace()
    {
        var start = await service.StartAsync(new ChatRequest());
        SetupGraphMatch(SuGraphId, explanation: "Handler om SU.");
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("minSU", new TokenUsageResult()));

        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "hvordan søger jeg SU?" });

        Assert.NotNull(trace.Routing);
        Assert.Equal("graph_routed", trace.Routing!.Outcome);
        Assert.Equal(SuGraphId, trace.Routing.GraphId);
        Assert.Equal([SuGraphId], trace.Routing.CandidateGraphIds);
        Assert.Equal("Handler om SU.", trace.Routing.Explanation);
        Assert.Equal(SuGraphId, trace.GraphId);
        Assert.Equal("draft_created", trace.Outcome);
    }

    [Fact]
    public async Task Catalog_IsCached_AndListsEachGraphsTopics()
    {
        var catalog = new GraphCatalog();

        var first = await catalog.GetEntriesAsync(dcrRepository.Object);
        await catalog.GetEntriesAsync(dcrRepository.Object);

        Assert.Equal(["Hvordan søger jeg SU?"], first.Single(g => g.GraphId == SuGraphId).Topics);
        dcrRepository.Verify(r => r.GetGraphsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageAboutAnotherGraph_SwitchesGraph_AndAnswersThereInTheSameTurn()
    {
        var sessionId = await StartInSuGraphAsync();

        var response = await service.SendMessageAsync(
            sessionId, new ChatRequest { Message = "kan jeg få boligstøtte?" });

        var session = (await sessionStore.GetSessionAsync(sessionId))!;
        Assert.Equal(HousingGraphId, session.GraphId);
        Assert.Equal("sim-bolig", session.SimulationId);
        Assert.Equal("bolig", response.PendingDraft?.EventId);
        Assert.Equal("sim-su", Assert.Single(session.SuspendedGraphs).SimulationId);
        Assert.Equal("graph_switched", trace.Routing?.Outcome);
        Assert.Equal(SuGraphId, trace.Routing?.PreviousGraphId);
        Assert.Equal(HousingGraphId, trace.Routing?.GraphId);
    }

    [Fact]
    public async Task SwitchingBack_ResumesTheEarlierSimulation_InsteadOfStartingOver()
    {
        var sessionId = await StartInSuGraphAsync();
        var housing = await service.SendMessageAsync(sessionId, new ChatRequest { Message = "kan jeg få boligstøtte?" });
        await service.RejectDraftAsync(sessionId, housing.PendingDraft!.Id);

        var su = await service.SendMessageAsync(sessionId, new ChatRequest { Message = "hvad med SU-ansøgningen?" });

        var session = (await sessionStore.GetSessionAsync(sessionId))!;
        Assert.Equal(SuGraphId, session.GraphId);
        Assert.Equal("sim-su", session.SimulationId);
        Assert.Equal("minSU", su.PendingDraft?.EventId);
        Assert.Equal(HousingGraphId, Assert.Single(session.SuspendedGraphs).GraphId);
        dcrRepository.Verify(r => r.CreateSimulationAsync(SuGraphId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageThatFitsNoGraph_StaysInTheCurrentGraph_AndShowsItsTopics()
    {
        var sessionId = await StartInSuGraphAsync();

        var response = await service.SendMessageAsync(
            sessionId, new ChatRequest { Message = "hvornår har svømmehallen åbent?" });

        Assert.Equal(SuGraphId, (await sessionStore.GetSessionAsync(sessionId))!.GraphId);
        Assert.Equal(["Hvordan søger jeg SU?"], response.Suggestions.Select(s => s.Label));
        Assert.Equal("graph_not_found", trace.Routing?.Outcome);
        Assert.Equal("no_match", trace.Outcome);
    }

    [Fact]
    public async Task LlmOnly_SwitchesGraph_WhenTheCurrentGraphHasNoAnswer()
    {
        var start = await service.StartAsync(new ChatRequest { Mode = ExecutionMode.LlmOnly });
        SetupGraphMatchByMessage();
        llmService
            .Setup(s => s.GenerateTextAsync(It.IsAny<string>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, LlmOptions _, CancellationToken _) =>
                (prompt.Contains("kan jeg få boligstøtte?") && !prompt.Contains("Hvordan søger jeg boligstøtte?")
                    ? "Det har jeg desværre ikke et svar på."
                    : "Svar fra den rigtige graf", new TokenUsageResult()));
        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "jeg vil gerne søge SU" });

        var response = await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "kan jeg få boligstøtte?" });

        Assert.Equal("Svar fra den rigtige graf", response.Message);
        Assert.Equal(HousingGraphId, (await sessionStore.GetSessionAsync(start.SessionId))!.GraphId);
        Assert.Equal("graph_switched", trace.Routing?.Outcome);
    }

    // En samtale uden fast graf, som er routet til SU-grafen og ikke har
    // nogen åben kladde. Emne-match og routing svarer ud fra beskeden.
    private async Task<string> StartInSuGraphAsync()
    {
        dcrRepository
            .Setup(r => r.CreateSimulationAsync(HousingGraphId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("sim-bolig");
        dcrRepository
            .Setup(r => r.GetGraphStateAsync(HousingGraphId, "sim-bolig", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphState
            {
                Events =
                [
                    new DcrEvent
                    {
                        Id = "bolig", Label = "Hvordan søger jeg boligstøtte?", DataType = "choice",
                        IsEnabled = true, Included = true, ChoiceValues = "ja (ja), nej (nej)"
                    }
                ]
            });
        SetupGraphMatchByMessage();
        llmService
            .Setup(s => s.MatchEventAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DcrEvent>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string message, IEnumerable<DcrEvent> topics, LlmOptions _, CancellationToken _) =>
            {
                var ids = topics.Select(t => t.Id).ToList();
                string? id = message.Contains("bolig") && ids.Contains("bolig") ? "bolig"
                    : message.Contains("SU-ansøgningen") && ids.Contains("minSU") ? "minSU"
                    : null;
                return (id, new TokenUsageResult());
            });

        var start = await service.StartAsync(new ChatRequest());
        // "jeg vil gerne søge SU" routes til SU, men matcher intet emne:
        // samtalen står i SU-grafen uden kladde.
        await service.SendMessageAsync(start.SessionId, new ChatRequest { Message = "jeg vil gerne søge SU" });
        Assert.Equal(SuGraphId, (await sessionStore.GetSessionAsync(start.SessionId))!.GraphId);
        return start.SessionId;
    }

    private void SetupGraphMatchByMessage() =>
        llmService
            .Setup(s => s.MatchGraphAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<GraphCatalogEntry>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string message, IEnumerable<GraphCatalogEntry> _, LlmOptions _, CancellationToken _) =>
            {
                string? id = message.Contains("bolig") ? HousingGraphId
                    : message.Contains("SU") ? SuGraphId
                    : null;
                return (new LlmGraphMatch { GraphId = id, CandidateGraphIds = id is null ? [] : [id] }, new TokenUsageResult());
            });

    private void SetupGraphMatch(string? graphId, List<string>? candidates = null, string explanation = "test") =>
        llmService
            .Setup(s => s.MatchGraphAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<GraphCatalogEntry>>(), It.IsAny<LlmOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                new LlmGraphMatch
                {
                    GraphId = graphId,
                    CandidateGraphIds = candidates ?? (graphId is null ? [] : [graphId]),
                    Explanation = explanation
                },
                new TokenUsageResult()));

    // En FAQ-graf med ét emne, som gør sit svar pending.
    private static DcrGraphDefinition Definition(string topicId, string topicLabel) => new()
    {
        Events =
        [
            new() { Id = topicId, Label = topicLabel, DataType = "choice", Options = [new() { Label = "ja", Value = "ja" }] },
            new() { Id = topicId + "Svar", Label = "Svaret på " + topicLabel, DataType = "label" }
        ],
        Relations = [new() { Type = "response", SourceId = topicId, TargetId = topicId + "Svar" }]
    };
}
