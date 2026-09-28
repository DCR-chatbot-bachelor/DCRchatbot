namespace DcrChatbot.Core.Domain.Enums;

public enum ExecutionMode
{
    /// <summary>
    /// Mode A: Ren LLM med FAQ-tekst i system-prompten uden DCR-grafkobling (FR-TEST-1).
    /// </summary>
    Baseline = 0,

    /// <summary>
    /// Mode B: Den fulde neuro-symbolske DCR-løsning (FR-TEST-1).
    /// </summary>
    NeuroSymbolic = 1
}