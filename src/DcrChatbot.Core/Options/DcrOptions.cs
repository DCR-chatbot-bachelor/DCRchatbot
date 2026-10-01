using System.ComponentModel.DataAnnotations;

namespace DcrChatbot.Core.Options;

public sealed class DcrOptions
{
    public const string SectionName = "Dcr";

    [Required, Url]
    public string RootUrl { get; init; } = string.Empty;

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string Token { get; init; } = string.Empty;
}