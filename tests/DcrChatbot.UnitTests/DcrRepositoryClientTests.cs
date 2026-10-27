using System.Net;
using System.Text;
using System.Text.Json;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.DcrRepo;
using Microsoft.Extensions.Options;

namespace DcrChatbot.UnitTests;

public sealed class DcrRepositoryClientTests
{
    [Fact]
    public async Task GetGraphsAsync_MapsGraphList()
    {
        var handler = new RecordingHandler(_ => Response(
            "{\"graphs\":[{\"id\":\"1826984\",\"title\":\"Citizen graph\",\"language\":\"da\"}," +
            "{\"graphId\":\"second\",\"name\":\"Second graph\",\"graphLanguage\":\"en\"}]}",
            "application/json"));
        var client = CreateClient(handler);

        var graphs = await client.GetGraphsAsync();

        Assert.Equal(2, graphs.Count);
        Assert.Equal("1826984", graphs[0].GraphId);
        Assert.Equal("Citizen graph", graphs[0].Title);
        Assert.Equal("da", graphs[0].Language);
        Assert.Equal("second", graphs[1].GraphId);
        Assert.Equal("GET", handler.LastRequest!.Method.Method);
        Assert.Equal("/api/graphs", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetGraphsAsync_MapsXmlGraphList()
    {
        var handler = new RecordingHandler(_ => Response(
            "<graphs><graph id=\"1826984\" title=\"Citizen graph\">" +
            "<language>da</language></graph></graphs>",
            "application/xml"));
        var client = CreateClient(handler);

        var graphs = await client.GetGraphsAsync();

        var graph = Assert.Single(graphs);
        Assert.Equal("1826984", graph.GraphId);
        Assert.Equal("Citizen graph", graph.Title);
        Assert.Equal("da", graph.Language);
    }

    [Fact]
    public async Task GetGraphAsync_MapsMetadataFromXml()
    {
        var handler = new RecordingHandler(_ => Response(
            "<graph title=\"Citizen graph\"><specification><resources><custom>" +
            "<graphLanguage>da</graphLanguage></custom></resources></specification></graph>",
            "application/xml"));
        var client = CreateClient(handler);

        var graph = await client.GetGraphAsync("1826984");

        Assert.Equal("1826984", graph.GraphId);
        Assert.Equal("Citizen graph", graph.Title);
        Assert.Equal("da", graph.Language);
        Assert.Equal("GET", handler.LastRequest!.Method.Method);
        Assert.Equal("/api/graphs/1826984", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("test-api-key", handler.LastRequest.Headers.GetValues("X-DCR-AuthToken").Single());
    }

    [Fact]
    public async Task CreateSimulationAsync_ReturnsSimulationIdFromHeader()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Add("X-DCR-simulation-ID", "42");
            return response;
        });
        var client = CreateClient(handler);

        var simulationId = await client.CreateSimulationAsync("graph/1");

        Assert.Equal("42", simulationId);
        Assert.Equal("/api/graphs/graph%2F1/sims", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetGraphStateAsync_MapsEventsFromJson()
    {
        var handler = new RecordingHandler(_ => Response(
            "{\"isAccepting\":false,\"currentTime\":\"2026-10-01T08:42:15.7764149+00:00\"," +
            "\"nextDelay\":null,\"nextDeadline\":null,\"currentPhase\":null," +
            "\"currentPhaseTitle\":null,\"events\":[{\"id\":\"income\"," +
            "\"included\":true,\"IsProductive\":false,\"enabled\":true," +
            "\"pending\":true,\"executed\":null,\"label\":\"Income\"," +
            "\"sequence\":3,\"value\":\"\",\"displayValue\":\"\"," +
            "\"dataType\":\"int\",\"choiceValues\":\"1000,2000\"," +
            "\"roles\":\"User\",\"engineDataType\":12}]} ",
            "application/json"));
        var client = CreateClient(handler);

        var state = await client.GetGraphStateAsync("graph-1", "simulation-2");

        Assert.False(state.IsAccepting);
        Assert.Equal(
            "2026-10-01T08:42:15.7764149+00:00",
            state.CurrentTime!.Value.ToString("O"));
        var dcrEvent = Assert.Single(state.Events);
        Assert.Equal("income", dcrEvent.Id);
        Assert.Equal("int", dcrEvent.DataType);
        Assert.True(dcrEvent.Included);
        Assert.False(dcrEvent.IsProductive);
        Assert.Equal(3, dcrEvent.Sequence);
        Assert.Null(dcrEvent.IsExecuted);
        Assert.Equal(12, dcrEvent.EngineDataType);
        Assert.True(dcrEvent.IsEnabled);
        Assert.Equal(["1000", "2000"], dcrEvent.AllowedValues);
    }

    [Fact]
    public async Task GetGraphDefinitionAsync_ReadsEventsNestingsLabelsChoicesAndRelations()
    {
        // Uddrag af "SU faq", som DCR.Repo returnerer grafen som XML.
        var xml = """
            <dcrgraph title="SU faq"><specification><resources><events>
              <event id="minSU"><custom><roles><role>User</role></roles><sequence>2</sequence>
                <eventData><dataType format="string">choice</dataType>
                  <dictionary><item label="ja" value="ja" /><item label="nej" value="nej" /></dictionary></eventData></custom></event>
              <event id="A2Copy"><custom><roles><role>Chatbot</role></roles><sequence>3</sequence>
                <eventData><dataType>label</dataType></eventData></custom></event>
              <event id="A11"><custom><sequence>8</sequence></custom>
                <event id="A17"><custom><roles><role>User</role></roles><sequence>16</sequence>
                  <eventData><dataType>choice</dataType>
                    <dictionary><item label="Gymnasium" value="gym" /><item label="Erhvervsuddannelse" value="eud" /></dictionary></eventData></custom></event>
              </event>
            </events>
            <labelMappings>
              <labelMapping eventId="minSU" labelId="Hvorfor skal jeg bruge minSU?" />
              <labelMapping eventId="A2Copy" labelId="På minSU kan du følge med i din SU." />
              <labelMapping eventId="A17" labelId="Hvilken uddannelse skal du i gang med?" />
            </labelMappings></resources>
            <constraints>
              <responses><response sourceId="minSU" targetId="A2Copy" /></responses>
              <includes><include sourceId="hjælp" targetId="A11" /></includes>
              <conditions />
            </constraints></specification></dcrgraph>
            """;
        var handler = new RecordingHandler(_ => Response(xml, "application/xml"));
        var client = CreateClient(handler);

        var definition = await client.GetGraphDefinitionAsync("2012392");

        var minSu = Assert.Single(definition.Events, e => e.Id == "minSU");
        Assert.Equal("Hvorfor skal jeg bruge minSU?", minSu.Label);
        Assert.Equal("choice", minSu.DataType);
        Assert.Equal("User", minSu.Roles);
        Assert.Equal(["ja", "nej"], minSu.Options.Select(o => o.Value));

        Assert.True(Assert.Single(definition.Events, e => e.Id == "A11").IsNesting);
        var education = Assert.Single(definition.Events, e => e.Id == "A17");
        Assert.Equal("A11", education.ParentId);
        Assert.Equal(["Gymnasium", "Erhvervsuddannelse"], education.Options.Select(o => o.Label));

        Assert.Contains(definition.Relations, r => r.Type == "response" && r.SourceId == "minSU" && r.TargetId == "A2Copy");
        Assert.Contains(definition.Relations, r => r.Type == "include" && r.SourceId == "hjælp" && r.TargetId == "A11");
    }

    [Theory]
    [InlineData("\"2026-10-18T23:52:26.5425687+02:00\"", true)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task GetGraphStateAsync_ReadsExecutedTimestampAsExecuted(string executedJson, bool? expected)
    {
        // DCR.Repo sender tidspunktet for udførslen, ikke en boolean.
        var handler = new RecordingHandler(_ => Response(
            "{\"isAccepting\":true,\"events\":[{\"id\":\"Age\",\"enabled\":true,\"pending\":false," +
            $"\"executed\":{executedJson},\"label\":\"How old are you?\",\"dataType\":\"int\",\"value\":\"18\"}}]}}",
            "application/json"));
        var client = CreateClient(handler);

        var state = await client.GetGraphStateAsync("graph-1", "simulation-2");

        Assert.Equal(expected, Assert.Single(state.Events).IsExecuted);
    }

    [Fact]
    public async Task ExecuteEventAsync_SendsExpectedPayload()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var result = await client.ExecuteEventAsync("graph-1", "simulation-2", "income", "2000");

        Assert.True(result);
        Assert.Equal("POST", handler.LastRequest!.Method.Method);
        Assert.Equal(
            "/api/graphs/graph-1/simulation/simulation-2/event",
            handler.LastRequest.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("income", body.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("2000", body.RootElement.GetProperty("eventValue").GetString());
        Assert.False(body.RootElement.GetProperty("isNull").GetBoolean());
    }

    private static DcrRepositoryClient CreateClient(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dcr.test/")
        };

        return new DcrRepositoryClient(
            httpClient,
            Options.Create(new DcrOptions
            {
                RootUrl = "https://dcr.test/",
                ApiKey = "test-api-key",
                Token = "test-token"
            }));
    }

    private static HttpResponseMessage Response(string content, string mediaType) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}