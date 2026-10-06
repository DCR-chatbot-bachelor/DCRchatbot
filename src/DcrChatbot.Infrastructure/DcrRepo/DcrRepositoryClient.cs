using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using Microsoft.Extensions.Options;

namespace DcrChatbot.Infrastructure.DcrRepo;

public sealed class DcrRepositoryClient : IDcrRepository
{
    private readonly HttpClient httpClient;
    private readonly DcrOptions options;

    public DcrRepositoryClient(HttpClient httpClient, IOptions<DcrOptions> options)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
    }

    public async Task<string> CreateSimulationAsync(
        string graphId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/graphs/{Uri.EscapeDataString(graphId)}/sims");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "create DCR simulation", cancellationToken);

        if (!response.Headers.TryGetValues("X-DCR-simulation-ID", out var values) ||
            !int.TryParse(values.SingleOrDefault(), out var simulationId))
        {
            throw new InvalidOperationException(
                "DCR.Repo created a simulation but did not return X-DCR-simulation-ID.");
        }

        return simulationId.ToString();
    }

    public async Task<DcrGraph> GetGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"api/graphs/{Uri.EscapeDataString(graphId)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "get DCR graph", cancellationToken);

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(content, LoadOptions.None, cancellationToken);
        var root = document.Root ?? throw new JsonException("DCR.Repo graph response had no root element.");
        var language = root
            .Element("specification")?
            .Element("resources")?
            .Element("custom")?
            .Element("graphLanguage")?
            .Value;

        return new DcrGraph
        {
            GraphId = graphId,
            Language = language ?? string.Empty,
            Title = root.Attribute("title")?.Value ?? string.Empty
        };
    }

    public async Task<IReadOnlyList<DcrGraph>> GetGraphsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/graphs");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "list DCR graphs", cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (content.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            return ParseXmlGraphs(content);
        }

        using var document = JsonDocument.Parse(content);
        var graphArray = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : GetProperty(document.RootElement, "graphs", "data", "items");

        if (graphArray.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("DCR.Repo graph response did not contain a graph array.");
        }

        return graphArray.EnumerateArray()
            .Select(ReadGraph)
            .Where(graph => !string.IsNullOrWhiteSpace(graph.GraphId))
            .ToArray();
    }

    public async Task<GraphState> GetGraphStateAsync(
        string graphId,
        string simulationId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"api/graphs/{Uri.EscapeDataString(graphId)}/simulation/" +
            $"{Uri.EscapeDataString(simulationId)}/event");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "get DCR graph state", cancellationToken);

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        return new GraphState
        {
            GraphId = graphId,
            SimulationId = simulationId,
            IsAccepting = GetBoolean(document.RootElement, "isAccepting"),
            CurrentTime = GetDateTimeOffset(document.RootElement, "currentTime"),
            NextDelay = GetString(document.RootElement, "nextDelay"),
            NextDeadline = GetString(document.RootElement, "nextDeadline"),
            CurrentPhase = GetString(document.RootElement, "currentPhase"),
            CurrentPhaseTitle = GetString(document.RootElement, "currentPhaseTitle"),
            Events = ReadEvents(document.RootElement)
        };
    }

    public async Task<bool> ExecuteEventAsync(
        string graphId,
        string simulationId,
        string eventId,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new ExecuteEventRequest(
            eventId,
            value,
            null,
            false));

        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/graphs/{Uri.EscapeDataString(graphId)}/simulation/" +
            $"{Uri.EscapeDataString(simulationId)}/event");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "execute DCR event", cancellationToken);

        return response.IsSuccessStatusCode;
    }

private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri)
{
    var request = new HttpRequestMessage(method, relativeUri);
    request.Headers.Add("X-DCR-AuthToken", options.ApiKey);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return request;
}

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Failed to {operation}. Status {(int)response.StatusCode}: {details}");
    }

    private static List<DcrEvent> ReadEvents(JsonElement root)
    {
        var eventArray = root.ValueKind == JsonValueKind.Array
            ? root
            : GetProperty(root, "events", "data");

        if (eventArray.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("DCR.Repo event response did not contain an event array.");
        }

        return eventArray.EnumerateArray().Select(ReadEvent).ToList();
    }

    private static DcrGraph ReadGraph(JsonElement element)
    {
        return new DcrGraph
        {
            GraphId = GetString(element, "id", "graphId", "graphID") ?? string.Empty,
            Title = GetString(element, "title", "name") ?? string.Empty,
            Language = GetString(element, "language", "graphLanguage") ?? string.Empty
        };
    }

    private static IReadOnlyList<DcrGraph> ParseXmlGraphs(string content)
    {
        var document = XDocument.Parse(content, LoadOptions.None);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("graph", StringComparison.OrdinalIgnoreCase))
            .Select(ReadGraph)
            .Where(graph => !string.IsNullOrWhiteSpace(graph.GraphId))
            .ToArray();
    }

    private static DcrGraph ReadGraph(XElement element)
    {
        return new DcrGraph
        {
            GraphId = GetXmlValue(element, "id", "graphId", "graphID"),
            Title = GetXmlValue(element, "title", "name"),
            Language = GetXmlValue(element, "language", "graphLanguage")
        };
    }

    private static string GetXmlValue(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var attribute = element.Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null)
            {
                return attribute.Value;
            }

            var child = element.Elements()
                .FirstOrDefault(child =>
                    child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child is not null)
            {
                return child.Value;
            }
        }

        return string.Empty;
    }

    private static DcrEvent ReadEvent(JsonElement element)
    {
        return new DcrEvent
        {
            Id = GetString(element, "id", "eventId") ?? string.Empty,
            Included = GetBoolean(element, "included"),
            IsProductive = GetBoolean(element, "IsProductive", "isProductive"),
            Sequence = GetInt32(element, "sequence"),
            Label = GetString(element, "label", "name") ?? string.Empty,
            Value = GetString(element, "value"),
            DisplayValue = GetString(element, "displayValue"),
            Description = GetString(element, "description"),
            Explanation = GetString(element, "explanation", "guidance"),
            DataType = GetString(element, "dataType", "datatype") ?? "string",
            Roles = GetString(element, "roles"),
            Type = GetString(element, "type"),
            Tags = GetString(element, "tags"),
            Deadline = GetString(element, "deadline"),
            Phases = GetString(element, "phases"),
            EventType = GetString(element, "eventType"),
            EngineDataType = GetNullableInt32(element, "engineDataType"),
            IsEnabled = GetBoolean(element, "enabled", "isEnabled"),
            IsExecuted = GetNullableBoolean(element, "executed", "isExecuted"),
            IsPending = GetBoolean(element, "pending", "isPending"),
            ChoiceValues = GetString(element, "choiceValues") ?? string.Empty,
            AllowedValues = ReadStringArray(element, "allowedValues", "choiceValues")
        };
    }

    private static JsonElement GetProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        return default;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int GetInt32(JsonElement element, params string[] names) =>
        GetNullableInt32(element, names) ?? 0;

    private static int? GetNullableInt32(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool? GetNullableBoolean(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool GetBoolean(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.True ||
            (value.ValueKind == JsonValueKind.String &&
             bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    private static List<string> ReadStringArray(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList();
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
    }

    private sealed record ExecuteEventRequest(
        [property: JsonPropertyName("eventId")] string EventId,
        [property: JsonPropertyName("eventValue")] string? EventValue,
        [property: JsonPropertyName("comment")] string? Comment,
        [property: JsonPropertyName("isNull")] bool IsNull);
}