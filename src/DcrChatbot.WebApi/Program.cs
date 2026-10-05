using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Application;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.DcrRepo;
using DcrChatbot.Infrastructure.LlmProviders;
using DcrChatbot.Infrastructure.Session;
using DcrChatbot.WebApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services
    .AddOptions<LlmOptions>()
    .Bind(builder.Configuration.GetSection(LlmOptions.SectionName))
     .ValidateDataAnnotations()
     .ValidateOnStart();

builder.Services
    .AddOptions<DcrOptions>()
    .Bind(builder.Configuration.GetSection(DcrOptions.SectionName))
     .ValidateDataAnnotations()
     .ValidateOnStart();

builder.Services.AddControllers();
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
builder.Services.AddHttpClient<ILlmService, GeminiLlmService>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(60);
});
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
