using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.LlmProviders;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace DcrChatbot.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class GeminiLlmServiceIntegrationTests
{
    private readonly ITestOutputHelper output;

    public GeminiLlmServiceIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Theory]
    [InlineData("I am 42 years old.", "Age")]
    [InlineData("I live in Denmark.", "Country")]
    [InlineData("My height is 180 cm.", "height")]
    [InlineData("I am from Sweden.", "Country")]
    [InlineData("My age is 29.", "Age")]
    public async Task ExtractIntentAsync_ReturnsExpectedEvent(
        string userMessage,
        string expectedEventId)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("DcrChatbot-WebApi")
            .Build();
        var options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>()
            ?? throw new InvalidOperationException("Llm options were not found in User Secrets.");

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "Llm:ApiKey is missing. Configure it with dotnet user-secrets before running integration tests.");
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };
        var service = new GeminiLlmService(httpClient);
        var availableEvents = new[]
        {
            new DcrEvent { Id = "Age", Label = "How old are you?" },
            new DcrEvent { Id = "Country", Label = "What country are you in?" },
            new DcrEvent { Id = "height", Label = "What is your height?" }
        };

        var (result, usage) = await service.ExtractIntentAsync(
            userMessage,
            availableEvents,
            options);

        output.WriteLine($"Message matched event: {result.MatchedEventId}");
        output.WriteLine($"Prompt tokens: {usage.PromptTokens}");
        output.WriteLine($"Completion tokens: {usage.CompletionTokens}");

        Assert.Equal(expectedEventId, result.MatchedEventId);
    }
}