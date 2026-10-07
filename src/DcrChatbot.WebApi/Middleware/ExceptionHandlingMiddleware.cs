using System.Net;
using System.Text.Json;
using DcrChatbot.Core.Application;
using DcrChatbot.Infrastructure.LlmProviders;

namespace DcrChatbot.WebApi.Middleware;

public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled API exception");
            await WriteErrorAsync(context, exception);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            ArgumentException => (int)HttpStatusCode.BadRequest,
            KeyNotFoundException => (int)HttpStatusCode.NotFound,
            ChatConflictException => (int)HttpStatusCode.Conflict,
            LlmProviderException => (int)HttpStatusCode.BadGateway,
            _ => (int)HttpStatusCode.InternalServerError
        };

        // Interne fejlbeskeder (fx rå svar fra Gemini) vises ikke for borgeren.
        var detail = exception switch
        {
            LlmProviderException => "Sprogmodellen svarede ikke korrekt. Prøv igen.",
            _ when statusCode == 500 => "Prøv igen senere.",
            _ => exception.Message
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://httpstatuses.com/" + statusCode,
            title = statusCode == 500 ? "Der opstod en intern fejl." : "Anmodningen kunne ikke behandles.",
            status = statusCode,
            detail
        }));
    }
}