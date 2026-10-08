using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.UnitTests;

public sealed class GraphStateTests
{
    [Fact]
    public void GetPendingEvent_ReturnsEvent_WhenEnabledAndPending()
    {
        var state = new GraphState
        {
            Events = [new DcrEvent { Id = "a", IsEnabled = true, IsPending = true }]
        };

        var result = state.GetPendingEvent();

        Assert.NotNull(result);
        Assert.Equal("a", result.Id);
    }

    [Fact]
    public void GetPendingEvent_ReturnsNull_WhenNothingIsPending()
    {
        var state = new GraphState
        {
            Events = [new DcrEvent { Id = "a", IsEnabled = true, IsPending = false, IsExecuted = true }]
        };

        Assert.Null(state.GetPendingEvent());
    }

    [Fact]
    public void GetPendingEvent_FallsBackToProductiveNotExecuted()
    {
        var state = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "a", IsEnabled = true, IsProductive = true, IsExecuted = false }
            ]
        };

        var result = state.GetPendingEvent();

        Assert.NotNull(result);
        Assert.Equal("a", result.Id);
    }

    [Fact]
    public void GetPendingEvents_ReturnsAllUnexecutedEventsInGraphOrder()
    {
        var state = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "second", Sequence = 2, IsEnabled = true, IsPending = true },
                new DcrEvent { Id = "done", Sequence = 1, IsEnabled = true, IsPending = true, IsExecuted = true },
                new DcrEvent { Id = "first", Sequence = 0, IsEnabled = true, IsPending = true }
            ]
        };

        var result = state.GetPendingEvents().Select(e => e.Id).ToArray();

        Assert.Equal(["first", "second"], result);
    }

    [Fact]
    public void GetEvent_ReturnsEvent_ForExactId()
    {
        var state = new GraphState
        {
            Events = [new DcrEvent { Id = "minSU" }]
        };

        Assert.NotNull(state.GetEvent("minSU"));
    }

    [Fact]
    public void GetEvent_ReturnsNull_ForUnknownId()
    {
        var state = new GraphState { Events = [new DcrEvent { Id = "minSU" }] };

        Assert.Null(state.GetEvent("does-not-exist"));
    }
}