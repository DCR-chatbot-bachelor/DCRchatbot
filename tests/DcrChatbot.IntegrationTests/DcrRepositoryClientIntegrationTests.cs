using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.DcrRepo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;




namespace DcrChatbot.UnitTests;

[Trait("Category", "Integration")] // Allows filtering out in CI pipelines

public sealed class DcrRepositoryClientIntegrationTests
{
    private readonly DcrOptions _options;

    private readonly ITestOutputHelper _output;



    public DcrRepositoryClientIntegrationTests(ITestOutputHelper output)
    {
         _output = output;
        // 1. Build configuration loading from WebApi User Secrets
        var config = new ConfigurationBuilder()
            .AddUserSecrets("DcrChatbot-WebApi") // Ensure this matches the User Secrets ID of your WebApi project
            .Build();
            
        _options = config.GetSection("Dcr").Get<DcrOptions>()
            ?? throw new InvalidOperationException("Dcr options not found in User Secrets.");

             // Sanity checks: fail early if secrets weren't loaded
    Assert.False(string.IsNullOrWhiteSpace(_options.ApiKey), "ApiKey is empty - secrets not loaded");
    Assert.False(string.IsNullOrWhiteSpace(_options.RootUrl), "RootUrl is empty");
    Assert.False(string.IsNullOrWhiteSpace(_options.Token), "Token is empty");
    }

    

    [Fact]
    public async Task GetGraphAsync_HitsLiveDcrEndpoint_ReturnsGraph()
    {
        // Arrange
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.RootUrl)
        };
        var client = new DcrRepositoryClient(httpClient, Options.Create(_options));

        // Act (Pass a valid graph ID from DCR.Repo; this should be a graph you have access to)
        var graph = await client.GetGraphAsync("2012389");

        // Assert
        Assert.NotNull(graph);
        Assert.NotNull(graph.Title);

        _output.WriteLine($"Graph Title: {graph.Title}");
    }

    [Fact]
    public async Task FullSimulationWorkflow_HitsLiveDcrEndpoint()
    {
        // Arrange
        using var httpClient = new HttpClient { BaseAddress = new Uri(_options.RootUrl) };
        var client = new DcrRepositoryClient(httpClient, Options.Create(_options));
        string graphId = "2012389";
        _output.WriteLine($"Using Graph ID: {graphId}");

        // 1. Create Simulation
        var simulationId = await client.CreateSimulationAsync(graphId);
        Assert.False(string.IsNullOrWhiteSpace(simulationId));
        _output.WriteLine($"Created Simulation ID: {simulationId}");

        // 2. Fetch Graph State
        var state = await client.GetGraphStateAsync(graphId, simulationId);
        Assert.NotNull(state);

        // 3. Execute Event (optional depending on graph setup)
        // var success = await client.ExecuteEventAsync(graphId, simulationId, "event_id", "value");
        // Assert.True(success);
    }
}