using System.Net;
using System.Text;
using System.Text.Json;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.LlmProviders;
using Microsoft.Extensions.Options;

namespace DcrChatbot.UnitTests;

public sealed class GeminiLlmServiceTests
{
    [Theory]
    [InlineData("I am 42 years old.", "Age")]
    [InlineData("I live in Denmark.", "Country")]
    [InlineData("My height is 180 cm.", "height")]
    [InlineData("I am from Sweden.", "Country")]
    [InlineData("My age is 29.", "Age")]
    public async Task ExtractIntentAsync_MapsStructuredJsonToExpectedEvent(
        string userMessage,
        string expectedEventId)
    {
        var handler = new RecordingHandler(_ => GeminiResponse(
            $"{{\"MatchedEventId\":\"{expectedEventId}\",\"ExtractedValue\":\"42\"," +
            "\"InferredReplies\":{},\"IsFaqQuestion\":false," +
            "\"UserIntentExplanation\":\"Matched the available event.\"}"));
        var service = CreateService(handler);

        var (result, usage) = await service.ExtractIntentAsync(
            userMessage,
            [
                new DcrEvent { Id = "Age", Label = "How old are you?" },
                new DcrEvent { Id = "Country", Label = "What country are you in?" },
                new DcrEvent { Id = "height", Label = "What is your height?" }
            ],
            CreateOptions());

        Assert.Equal(expectedEventId, result.MatchedEventId);
        Assert.False(result.IsFaqQuestion);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(5, usage.CompletionTokens);

        using var requestBody = JsonDocument.Parse(handler.LastBody!);
        var prompt = requestBody.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("Age", prompt);
        Assert.Contains("How old are you?", prompt);
        Assert.Equal("/v1beta/models/test-model:generateContent", handler.LastRequestUri!.AbsolutePath);
        Assert.Equal("test-key", handler.LastApiKeyHeader);
        Assert.DoesNotContain("key=", handler.LastRequestUri.Query);
    }

    [Fact]
    public async Task ExtractIntentAsync_ThrowsForInvalidJson()
    {
        var service = CreateService(new RecordingHandler(_ => GeminiResponse("not-json")));

        var exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.ExtractIntentAsync("I am 42", [], CreateOptions()));

        Assert.Contains("invalid structured JSON", exception.Message);
    }

    [Fact]
    public async Task ExtractIntentAsync_ThrowsForProviderError()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("provider error", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            service.ExtractIntentAsync("I am 42", [], CreateOptions()));

        Assert.Contains("400", exception.Message);
    }

    private static GeminiLlmService CreateService(RecordingHandler handler) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        });

    private static LlmOptions CreateOptions() => new()
    {
        ApiKey = "test-key",
        ModelId = "test-model",
        PromptVersion = "v1",
        Temperature = 0.0,
        MaxOutputTokens = 200
    };

    private static HttpResponseMessage GeminiResponse(string text)
    {
        var response = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[] { new { text } }
                    }
                }
            },
            usageMetadata = new
            {
                promptTokenCount = 10,
                candidatesTokenCount = 5
            }
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastApiKeyHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.FirstOrDefault()
                : null;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}