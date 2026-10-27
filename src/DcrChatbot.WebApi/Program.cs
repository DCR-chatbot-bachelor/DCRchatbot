using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.DcrRepo;
using DcrChatbot.Infrastructure.LlmProviders;
using DcrChatbot.Infrastructure.Logging;
using DcrChatbot.Infrastructure.Session;
using DcrChatbot.WebApi.Middleware;
using Serilog;
using Serilog.Filters;
using Serilog.Formatting.Compact;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Viser Serilog-fejl (f.eks. manglende skriverettigheder) i konsollen.
Serilog.Debugging.SelfLog.Enable(Console.Error);

// NFR-4: LLM-kald logges til konsol og til en JSON Lines-fil, der kan analyseres bagefter.
var logPath = Path.Combine(builder.Environment.ContentRootPath, "logs", "llm-calls-.jsonl");
Console.WriteLine($"LLM log: {logPath}");

builder.Host.UseSerilog((context, services, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Logger(sub => sub
        .Filter.ByIncludingOnly(Matching.FromSource<LoggingLlmService>())
        .WriteTo.File(new CompactJsonFormatter(), logPath, rollingInterval: RollingInterval.Day)));

// Add services to the container.

builder.Services
    .AddOptions<LlmOptions>()
    .Bind(builder.Configuration.GetSection(LlmOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.HasApiKeyForProvider(),
        "API-nøglen for den valgte Llm:Provider mangler (Llm:ApiKey for Gemini, Llm:OpenAi:ApiKey for OpenAi).")
    // FR-LLM-1: loggen må aldrig påstå en anden prompt-version end den, koden kører.
    .Validate(
        options => options.PromptVersion == PromptCatalog.Version,
        $"Llm:PromptVersion skal være '{PromptCatalog.Version}', den prompt-version koden indeholder.")
    .ValidateOnStart();

builder.Services
    .AddOptions<DcrOptions>()
    .Bind(builder.Configuration.GetSection(DcrOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddPolicy("Client", policy =>
    policy.WithOrigins("http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddSingleton<ISessionStore, JsonFileSessionStore>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddHttpClient<IDcrRepository, DcrRepositoryClient>((serviceProvider, client) =>
{
    var dcrOptions = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<DcrOptions>>()
        .Value;

    client.BaseAddress = new Uri(dcrOptions.RootUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// NFR-2: LLM-udbyderen vælges via Llm:Provider uden ændringer i Core.
var llmProvider = builder.Configuration.GetValue<LlmProvider?>(
    $"{LlmOptions.SectionName}:{nameof(LlmOptions.Provider)}") ?? LlmProvider.Gemini;

if (llmProvider == LlmProvider.OpenAi)
{
    builder.Services.AddHttpClient<OpenAiLlmService>(client =>
    {
        client.BaseAddress = new Uri("https://api.openai.com/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(60);
    });
}
else
{
    builder.Services.AddHttpClient<GeminiLlmService>(client =>
    {
        client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(60);
    });
}

// Decorator: ChatService beder om ILlmService og får LoggingLlmService,
// som pakker den rigtige provider ind og logger tokens og svartid.
builder.Services.AddTransient<ILlmService>(sp => new LoggingLlmService(
    llmProvider == LlmProvider.OpenAi
        ? sp.GetRequiredService<OpenAiLlmService>()
        : sp.GetRequiredService<GeminiLlmService>(),
    sp.GetRequiredService<ILogger<LoggingLlmService>>()));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Client");
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();