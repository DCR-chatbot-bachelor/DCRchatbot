using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.UnitTests;

public sealed class ConclusionBuilderTests
{
    [Fact]
    public void Build_NoExecutedEvents_GivesEmptyConclusion()
    {
        var state = new GraphState
        {
            Events =
            [
                // Synlige, enabled og endda med værdi, men intet er udført.
                new DcrEvent { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", IsEnabled = true, IsPending = true },
                new DcrEvent { Id = "A4", Label = "Conclusion", DataType = "label", IsEnabled = true, Value = "Can you buy alcohol? Yes" },
                new DcrEvent { Id = "Age", Label = "How old are you?", DataType = "int", IsEnabled = true, Value = "18" }
            ]
        };

        var conclusion = ConclusionBuilder.Build(state);

        Assert.True(conclusion.IsEmpty);
        Assert.Empty(conclusion.Results);
        Assert.Empty(conclusion.Answers);
        Assert.Equal(string.Empty, conclusion.ToText());
    }

    [Fact]
    public void Build_NullState_GivesEmptyConclusion()
    {
        Assert.True(ConclusionBuilder.Build(null).IsEmpty);
    }

    [Fact]
    public void Build_OnlyExecutedEventsAreIncluded()
    {
        var state = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "Country", Label = "What country are you in?", DataType = "choice", Sequence = 2, Included = true, IsExecuted = true, Value = "Denmark", DisplayValue = "Danmark" },
                new DcrEvent { Id = "Age", Label = "How old are you?", DataType = "int", Sequence = 1, Included = true, IsExecuted = true, Value = "18" },
                new DcrEvent { Id = "height", Label = "What is your height?", DataType = "int", Sequence = 3, IsEnabled = true, IsPending = true, Value = "180" },
                new DcrEvent { Id = "A4", Label = "Conclusion", DataType = "label", Sequence = 4, Included = true, IsExecuted = true, Value = "Can you buy alcohol? Yes" },
                new DcrEvent { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", IsEnabled = true, IsPending = true },
                new DcrEvent { Id = "A2", Label = "Ikke vist svar", DataType = "label", IsEnabled = true, IsExecuted = false }
            ]
        };

        var conclusion = ConclusionBuilder.Build(state);

        Assert.Equal(["Can you buy alcohol? Yes"], conclusion.Results);
        Assert.Equal(
            [new ConclusionAnswer("How old are you?", "18"), new ConclusionAnswer("What country are you in?", "Danmark")],
            conclusion.Answers);
    }

    [Fact]
    public void Build_AlcoholGraphAsDcrReturnsIt_ShowsResultAndOnlyTheCitizensAnswers()
    {
        // Sådan ser "Can I purchase alcohol?" ud i DCR, når alle tre spørgsmål
        // er besvaret: DCR har selv udført DMN-beslutningen og konklusionen.
        var state = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "Age", Label = "How old are you?", DataType = "int", Roles = "User", Sequence = 0, Included = true, IsExecuted = true, Value = "18", DisplayValue = "18" },
                new DcrEvent { Id = "Country", Label = "What country are you in?", DataType = "choice", Roles = "User", Sequence = 1, Included = true, IsExecuted = true, Value = "Denmark", DisplayValue = "Denmark" },
                new DcrEvent { Id = "DMN", Label = "DMN", DataType = "", Roles = "Robot", Sequence = 2, Included = true, IsExecuted = true, Value = "Yes", DisplayValue = "Yes" },
                new DcrEvent { Id = "A4", Label = "Conclusion", DataType = "label", Roles = "Robot", Sequence = 3, Included = true, IsExecuted = true, Value = "Can you buy alcohol? Yes", DisplayValue = "Can you buy alcohol? Yes" },
                new DcrEvent { Id = "height", Label = "What is your height?", DataType = "int", Roles = "User", Sequence = 4, Included = true, IsExecuted = true, Value = "180", DisplayValue = "180" }
            ]
        };

        var conclusion = ConclusionBuilder.Build(state);

        Assert.Equal("Can you buy alcohol? Yes", conclusion.ToText());
        Assert.Equal(
            [
                new ConclusionAnswer("How old are you?", "18"),
                new ConclusionAnswer("What country are you in?", "Denmark"),
                new ConclusionAnswer("What is your height?", "180")
            ],
            conclusion.Answers);
    }

    [Fact]
    public void Build_ExecutedButExcludedEvent_IsLeftOut()
    {
        // Efter en rettelse kan grafen udelukke et svar, der allerede er
        // udført; det gælder ikke længere (FR-HITL-2).
        var state = new GraphState
        {
            Events =
            [
                new DcrEvent { Id = "Age", Label = "How old are you?", DataType = "int", Included = true, IsExecuted = true, Value = "15" },
                new DcrEvent { Id = "Country", Label = "What country are you in?", DataType = "choice", Included = false, IsExecuted = true, Value = "Denmark" },
                new DcrEvent { Id = "A4", Label = "Gammelt resultat", DataType = "label", Included = false, IsExecuted = true, Value = "Yes" }
            ]
        };

        var conclusion = ConclusionBuilder.Build(state);

        Assert.Empty(conclusion.Results);
        Assert.Equal([new ConclusionAnswer("How old are you?", "15")], conclusion.Answers);
    }

    [Fact]
    public void Build_ExecutedFaqAnswerWithoutValue_IsNotRepeated()
    {
        // Et FAQ-svar står i labelens tekst og er allerede vist i chatten;
        // det er ikke et resultat, DCR har beregnet.
        var state = new GraphState
        {
            Events = [new DcrEvent { Id = "A2Copy", Label = "På minSU kan du følge med i din SU.", DataType = "label", Included = true, IsExecuted = true }]
        };

        Assert.Empty(ConclusionBuilder.Build(state).Results);
    }

    [Fact]
    public void Build_ExecutedQuestionWithoutValue_IsLeftOut()
    {
        var state = new GraphState
        {
            Events = [new DcrEvent { Id = "start", Label = "Start", DataType = "string", Included = true, IsExecuted = true }]
        };

        Assert.True(ConclusionBuilder.Build(state).IsEmpty);
    }

    [Fact]
    public void ToText_ShowsOnlyTheResults()
    {
        var conclusion = new Conclusion(
            ["Can you buy alcohol? Yes", "Husk legitimation."],
            [new ConclusionAnswer("How old are you?", "18")]);

        Assert.Equal("Can you buy alcohol? Yes\n\nHusk legitimation.", conclusion.ToText());
    }
}
