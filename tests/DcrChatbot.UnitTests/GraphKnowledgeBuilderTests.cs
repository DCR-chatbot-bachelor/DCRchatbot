using DcrChatbot.Core.Application.DcrEngine;
using DcrChatbot.Core.Domain.Entities;

namespace DcrChatbot.UnitTests;

public sealed class GraphKnowledgeBuilderTests
{
    private static readonly List<GraphChoiceOption> JaNej =
        [new() { Label = "Ja", Value = "ja" }, new() { Label = "Nej", Value = "nej" }];

    // Som "SU faq": to FAQ-emner, et anker, og en formular bag "hjælp".
    private static DcrGraphDefinition SuFaq() => new()
    {
        Events =
        [
            new() { Id = "Velkommen", Label = "Velkommen til chatbotten", DataType = "label", Sequence = 0 },
            new() { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", Sequence = 2, Options = JaNej },
            new() { Id = "A2Copy", Label = "På minSU kan du følge med i din SU.", DataType = "label", Sequence = 3 },
            new() { Id = "ansøgning", Label = "Hvordan finder jeg ud af, om min ansøgning er under behandling?", DataType = "choice", Sequence = 4, Options = JaNej },
            new() { Id = "A2Copy_1", Label = "Det tager normalt to til fire uger.", DataType = "label", Sequence = 5 },
            new() { Id = "hjælp", Label = "Jeg vil gerne have hjælp til at søge SU", DataType = "choice", Sequence = 7, Options = JaNej },
            new() { Id = "A11", Label = "A11", Sequence = 8, IsNesting = true },
            new() { Id = "A9", Label = "Hvad er din adresse?", DataType = "text", Sequence = 10, ParentId = "A11" },
            new() { Id = "A8", Label = "Hvad er dit fulde navn?", DataType = "text", Sequence = 9, ParentId = "A11" },
            new()
            {
                Id = "A17", Label = "Hvilken uddannelse skal du i gang med?", DataType = "choice", Sequence = 16, ParentId = "A11",
                Options = [new() { Label = "Gymnasium", Value = "gym" }, new() { Label = "Erhvervsuddannelse", Value = "eud" }]
            },
            new() { Id = "A18", Label = "Tak! Vi har noteret dine oplysninger.", DataType = "label", Sequence = 17, ParentId = "A11" },
            new() { Id = "A12", Label = "jeg vil ikke ansøge alligevel", DataType = "choice", Sequence = 12, ParentId = "A11", Options = JaNej }
        ],
        Relations =
        [
            new() { Type = "response", SourceId = "minSU", TargetId = "A2Copy" },
            new() { Type = "response", SourceId = "ansøgning", TargetId = "A2Copy_1" },
            new() { Type = "include", SourceId = "hjælp", TargetId = "A11" },
            new() { Type = "condition", SourceId = "A8", TargetId = "A9" },
            new() { Type = "response", SourceId = "A17", TargetId = "A18" },
            new() { Type = "exclude", SourceId = "A12", TargetId = "A11" }
        ]
    };

    [Fact]
    public void Build_ListsEachFaqQuestionWithTheAnswerItMakesPending()
    {
        var text = GraphKnowledgeBuilder.Build(SuFaq());

        Assert.Contains("Spørgsmål: Hvorfor skal jeg bruge minSU?\nSvar: På minSU kan du følge med i din SU.", Normalize(text));
        Assert.Contains("Spørgsmål: Hvordan finder jeg ud af, om min ansøgning er under behandling?\nSvar: Det tager normalt to til fire uger.", Normalize(text));
    }

    [Fact]
    public void Build_DescribesTheFormWithFieldsInOrderOptionsClosingAndCancel()
    {
        var text = Normalize(GraphKnowledgeBuilder.Build(SuFaq()));

        Assert.Contains(
            "Formular: \"Jeg vil gerne have hjælp til at søge SU\"\n" +
            "Hvis borgeren ønsker det, stilles disse spørgsmål ét ad gangen:\n" +
            "1. Hvad er dit fulde navn?\n" +
            "2. Hvad er din adresse?\n" +
            "3. Hvilken uddannelse skal du i gang med? (vælg mellem: Gymnasium, Erhvervsuddannelse)\n" +
            "Når alle spørgsmål er besvaret: Tak! Vi har noteret dine oplysninger.\n" +
            "Borgeren kan afbryde formularen: \"jeg vil ikke ansøge alligevel\"",
            text);
    }

    [Fact]
    public void Build_LeavesOutTheAnchorAndTopicsWithoutAnswers()
    {
        var text = GraphKnowledgeBuilder.Build(SuFaq());

        // Ankeret er ikke et svar, og "hjælp" har intet svar-label (kun en formular).
        Assert.DoesNotContain("Velkommen til chatbotten", text);
        Assert.DoesNotContain("Spørgsmål: Jeg vil gerne have hjælp til at søge SU", text);
    }

    [Fact]
    public void Build_EmptyGraph_GivesEmptyText()
    {
        Assert.Equal(string.Empty, GraphKnowledgeBuilder.Build(new DcrGraphDefinition()));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
