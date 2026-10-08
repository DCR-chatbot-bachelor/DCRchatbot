using System.Net;
using System.Text;
using System.Text.Json;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.LlmProviders;

namespace DcrChatbot.UnitTests;

public sealed class OpenAiLlmServiceTests
{
    [Fact]
    public async Task ExtractIntentAsync_SendsChatCompletionRequest_AndMapsStructuredJson()
    {
        var handler = new RecordingHandler(_ => OpenAiResponse(
            "{\"MatchedEventId\":\"minSU\",\"ExtractedValue\":\"ja\",\"InferredReplies\":{}," +
            "\"IsFaqQuestion\":false,\"UserIntentExplanation\":\"Matched.\"}"));
        var service = CreateService(handler);

        var (result, usage) = await service.ExtractIntentAsync(
            "Hvorfor skal jeg bruge minSU?",
            [new DcrEvent { Id = "minSU", Label = "minSU?", Description = "Intern note", DataType = "choice", ChoiceValues = "ja (ja), nej (nej)" }],
            CreateOptions("gpt-4.1-mini"));

        Assert.Equal("minSU", result.MatchedEventId);
        Assert.Equal("ja", result.ExtractedValue);
        Assert.Equal(12, usage.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);

        Assert.Equal("/v1/chat/completions", handler.LastRequestUri!.AbsolutePath);
        Assert.Equal("Bearer openai-test-key", handler.LastAuthorization);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("gpt-4.1-mini", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(200, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.0, root.GetProperty("temperature").GetDouble());
        var userPrompt = root.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("\"allowedValues\":[\"ja\",\"nej\"]", userPrompt);
        Assert.DoesNotContain("Intern note", handler.LastBody);
    }

    [Theory]
    [InlineData("gpt-5-mini")]
    [InlineData("o4-mini")]
    public async Task ExtractIntentAsync_OmitsTemperature_ForReasoningModels(string modelId)
    {
        var handler = new RecordingHandler(_ => OpenAiResponse(
            "{\"MatchedEventId\":null,\"ExtractedValue\":null,\"InferredReplies\":{}," +
            "\"IsFaqQuestion\":false,\"UserIntentExplanation\":\"\"}"));
        var service = CreateService(handler);

        await service.ExtractIntentAsync("hej", [], CreateOptions(modelId));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task ExtractIntentAsync_ThrowsForInvalidJson()
    {
        var service = CreateService(new RecordingHandler(_ => OpenAiResponse("not-json")));

        var exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.ExtractIntentAsync("hej", [], CreateOptions("gpt-4.1-mini")));

        Assert.Contains("OpenAI returned invalid structured JSON", exception.Message);
    }

    [Fact]
    public async Task ExtractIntentAsync_ThrowsForProviderError()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid key", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.ExtractIntentAsync("hej", [], CreateOptions("gpt-4.1-mini")));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task ExtractIntentAsync_RequiresOpenAiApiKey()
    {
        var service = CreateService(new RecordingHandler(_ => OpenAiResponse("{}")));
        var options = CreateOptions("gpt-4.1-mini");
        options.OpenAi.ApiKey = "";

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.ExtractIntentAsync("hej", [], options));
    }

    private static OpenAiLlmService CreateService(RecordingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") });

    private static LlmOptions CreateOptions(string modelId) => new()
    {
        Provider = LlmProvider.OpenAi,
        PromptVersion = "v1",
        Temperature = 0.0,
        MaxOutputTokens = 200,
        OpenAi = new OpenAiOptions { ApiKey = "openai-test-key", ModelId = modelId }
    };

    private static HttpResponseMessage OpenAiResponse(string content)
    {
        var response = new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 12, completion_tokens = 7 }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
