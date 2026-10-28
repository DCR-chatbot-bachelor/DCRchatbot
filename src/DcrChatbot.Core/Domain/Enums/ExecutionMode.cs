namespace DcrChatbot.Core.Domain.Enums;

/// <summary>
/// De tre tilstande, chatbotten kan køre i, så samme spørgsmål kan
/// sammenlignes på tværs af arkitekturer (FR-TEST-1).
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// Ren LLM: får grafens FAQ som tekst i prompten, men ingen DCR-kobling,
    /// ingen validering og ingen bekræftelse. Svarer frit.
    /// </summary>
    LlmOnly = 0,

    /// <summary>
    /// Den fulde neuro-symbolske løsning: DCR vælger svaret, og svar-teksten
    /// vises ordret fra grafen.
    /// </summary>
    DcrStrict = 1,

    /// <summary>
    /// Som DcrStrict, men LLM'en må formulere svaret ud fra grafens tekst og
    /// svare ud fra grafens FAQ, når en besked ikke matcher et emne.
    /// </summary>
    DcrGuided = 2
}
