namespace DcrChatbot.Infrastructure.LlmProviders;

public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message)
        : base(message)
    {
    }

    public LlmProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
