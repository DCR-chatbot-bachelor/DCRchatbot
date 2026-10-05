namespace DcrChatbot.Core.Application;

public sealed class ChatConflictException(string message) : Exception(message);