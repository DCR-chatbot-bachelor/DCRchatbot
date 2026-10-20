namespace DcrChatbot.Core.Application.Dtos;

/// <summary>
/// Et gyldigt svar på det choice-spørgsmål, botten lige har stillet, taget
/// fra grafens choiceValues. Vises som en knap, borgeren kan trykke på.
/// </summary>
public class AnswerOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
