using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.LlmProviders;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace DcrChatbot.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OpenAiLlmServiceIntegrationTests
{
    private readonly ITestOutputHelper output;

    public OpenAiLlmServiceIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Theory]
    [InlineData("Hvorfor skal jeg bruge minSU?", "minSU")]
    [InlineData("Hvordan kan jeg se, om min ansøgning er under behandling?", "ansøgning")]
    [InlineData("Det er første gang, jeg søger SU", "førstegang")]
    public async Task ExtractIntentAsync_ReturnsExpectedEvent(
        string userMessage,
        string expectedEventId)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("DcrChatbot-WebApi")
            .Build();
        var options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>()
            ?? throw new InvalidOperationException("Llm options were not found in User Secrets.");

        if (string.IsNullOrWhiteSpace(options.OpenAi.ApiKey))
        {
            throw new InvalidOperationException(
                "Llm:OpenAi:ApiKey is missing. Configure it with dotnet user-secrets before running integration tests.");
        }

        using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        var service = new OpenAiLlmService(httpClient);
        var availableEvents = new[]
        {
            new DcrEvent { Id = "førstegang", Label = "Det er første gang jeg søger SU - hvad gør jeg?", DataType = "choice", ChoiceValues = "ja (ja), nej (nej)" },
            new DcrEvent { Id = "minSU", Label = "Hvorfor skal jeg bruge minSU?", DataType = "choice", ChoiceValues = "ja (ja), nej (nej)" },
            new DcrEvent { Id = "ansøgning", Label = "Hvordan finder jeg ud af, om min ansøgning er under behandling?", DataType = "choice", ChoiceValues = "ja (ja), nej (nej)" }
        };

        var (result, usage) = await service.ExtractIntentAsync(userMessage, availableEvents, options);

        output.WriteLine($"Model: {options.OpenAi.ModelId}");
        output.WriteLine($"Message matched event: {result.MatchedEventId}");
        output.WriteLine($"Prompt tokens: {usage.PromptTokens}, completion tokens: {usage.CompletionTokens}");

        Assert.Equal(expectedEventId, result.MatchedEventId);
    }
}
