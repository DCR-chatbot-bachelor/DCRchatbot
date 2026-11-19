using System.Diagnostics;
using DcrChatbot.Core.Application;
using DcrChatbot.Core.Application.Dtos;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Domain.Entities;
using DcrChatbot.Core.Domain.Enums;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DcrChatbot.Battery;

/// <summary>
/// Kører ét scenarie i én tilstand gennem den rigtige ChatService, mod den
/// rigtige graf og LLM. Hver tur får sin egen TurnTrace, ligesom ét HTTP-kald
/// i Web API'et, så LLM-kald og tokens kan tælles pr. tur.
/// </summary>
public sealed class BatteryRunner(
    IDcrRepository dcrRepository,
    ILlmService llmProvider,
    ISessionStore sessionStore,
    LlmOptions llmOptions,
    bool confirmAllDrafts)
{
    // Flere kladder i køen (fx navn og adresse i én besked) besvares én ad
    // gangen, men aldrig uendeligt.
    private const int MaxDraftsPerTurn = 5;

    // Svar, hvor botten ikke giver et svar: hjælpesvaret, "intet svar" og
    // svaret på en afvist kladde i DCR-tilstandene, og den sætning, LlmOnly
    // skal svare med (regel 2 i ModePrompts.LlmOnly).
    private static readonly string[] RefusalMarkers =
    [
        "Det kan jeg desværre ikke svare på",
        "Det kan jeg desværre ikke hjælpe med",
        "Jeg har desværre ikke et svar på det spørgsmål",
        "Prøv at omformulere dit spørgsmål",
        ModePrompts.LlmOnlyNoAnswer.TrimEnd('.')
    ];

    public async Task<RunResult> RunAsync(
        string graphId, Scenario scenario, ExecutionMode mode, int run, CancellationToken cancellationToken)
    {
        var result = new RunResult { ScenarioId = scenario.Id, Category = scenario.Category, Mode = mode, Run = run };
        var stopwatch = Stopwatch.StartNew();
        string? sessionId = null;
        try
        {
            var start = await TurnAsync(result, chat => chat.StartAsync(new ChatRequest { GraphId = graphId, Mode = mode }, cancellationToken));
            sessionId = start.SessionId;
            result.Transcript.Add(new("Bot", BotText(start)));

            var lastMessage = start.Message;
            foreach (var turn in scenario.Turns)
            {
                if (turn.Optional && !lastMessage.TrimEnd().EndsWith('?'))
                {
                    continue;
                }

                lastMessage = await SayAsync(result, sessionId, turn, scenario.Expected, mode, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.Error = $"{exception.GetType().Name}: {exception.Message}";
        }

        result.DurationMs = stopwatch.ElapsedMilliseconds;
        if (sessionId is not null && await sessionStore.GetSessionAsync(sessionId, cancellationToken) is { } session)
        {
            result.Executed = session.ExecutedEvents
                .Select(e => new ExecutedValue(e.EventId, e.Value))
                .ToList();
        }

        Check(result, scenario.Expected, mode);
        return result;
    }

    // Returnerer bottens sidste besked, så næste tur kan se, om den stillede et spørgsmål.
    private async Task<string> SayAsync(
        RunResult result,
        string sessionId,
        ScenarioTurn turn,
        Expectation expected,
        ExecutionMode mode,
        CancellationToken cancellationToken)
    {
        result.Transcript.Add(new("Borger", turn.Say));
        var response = await TurnAsync(result, chat => chat.SendMessageAsync(
            sessionId, new ChatRequest { Message = turn.Say, MessageId = Guid.NewGuid().ToString() }, cancellationToken));
        result.Transcript.Add(new("Bot", BotText(response)));

        // LlmOnly har ingen kladder; borgeren afviser ved at skrive det.
        if (mode == ExecutionMode.LlmOnly && turn.RejectsDraft)
        {
            result.Transcript.Add(new("Borger", turn.RejectText));
            var reply = await TurnAsync(result, chat => chat.SendMessageAsync(
                sessionId, new ChatRequest { Message = turn.RejectText, MessageId = Guid.NewGuid().ToString() }, cancellationToken));
            result.Transcript.Add(new("Bot", BotText(reply)));
            return reply.Message;
        }

        // Den første kladde kan være fastlagt i scenariet; ellers svarer
        // borgeren, som en borger der ved, hvad den mente, ville gøre. Et svar
        // udledt af en ugyldig besked er altid en fejltolkning.
        for (var i = 0; i < MaxDraftsPerTurn && response.PendingDraft is { } draft; i++)
        {
            var scripted = i == 0 && (turn.RejectsDraft || turn.ConfirmsDraft);
            var misread = turn.Invalid && !draft.IsEventMatch;
            var confirm = scripted
                ? turn.ConfirmsDraft
                : confirmAllDrafts || (!misread && IsExpected(draft, expected));
            result.DraftsShown++;
            if (!confirm && !scripted)
            {
                result.WrongDraftsRejected++;
            }

            result.Transcript.Add(new("Handling", confirm ? "[bekræfter kladden]" : "[afviser kladden]"));
            var draftId = draft.Id;
            response = await TurnAsync(result, chat => confirm
                ? chat.ConfirmDraftAsync(sessionId, draftId, cancellationToken)
                : chat.RejectDraftAsync(sessionId, draftId, cancellationToken));
            if (!string.IsNullOrWhiteSpace(BotText(response)))
            {
                result.Transcript.Add(new("Bot", BotText(response)));
            }
        }

        return response.Message;
    }

    // Den simulerede borger bekræfter en kladde, der passer til det, den
    // mente: et emne, som scenariet forventer udført, eller et svar, som
    // scenariet forventer, med den forventede værdi. Alt andet er en
    // fejltolkning (fx "jeg vil ikke ansøge alligevel" forstået som adressen).
    // Bekræftelsen (FR-HITL-1) er DCR-tilstandenes værn mod fejltolkninger,
    // så en borger, der bekræfter alt, ville skjule netop den forskel.
    private static bool IsExpected(PendingAnswer draft, Expectation expected)
    {
        if (draft.IsEventMatch)
        {
            return expected.Executed.Contains(draft.EventId, StringComparer.OrdinalIgnoreCase);
        }

        return expected.Values.TryGetValue(draft.EventId, out var value)
            ? draft.ProposedValue.Contains(value, StringComparison.OrdinalIgnoreCase)
            : expected.Executed.Contains(draft.EventId, StringComparer.OrdinalIgnoreCase);
    }

    // Bottens tekst med de knapper, borgeren får vist (grafens emner og
    // svarmuligheder), så de også tæller med, når svaret bedømmes.
    private static string BotText(ChatResponse response)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            parts.Add(response.Message);
        }

        if (response.Suggestions.Count > 0)
        {
            parts.Add($"[forslag: {string.Join(" | ", response.Suggestions.Select(s => s.Label))}]");
        }

        if (response.AnswerOptions.Count > 0)
        {
            parts.Add($"[svarknapper: {string.Join(" | ", response.AnswerOptions.Select(o => o.Label))}]");
        }

        return string.Join("\n", parts);
    }

    // Én tur: en ny ChatService med sin egen TurnTrace, som i Web API'et.
    private async Task<ChatResponse> TurnAsync(RunResult result, Func<ChatService, Task<ChatResponse>> action)
    {
        var trace = new TurnTrace();
        var llm = new LoggingLlmService(llmProvider, trace, NullLogger<LoggingLlmService>.Instance);
        var chat = new ChatService(
            dcrRepository, llm, sessionStore, Options.Create(llmOptions), trace, NullLogger<ChatService>.Instance);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await action(chat);
            result.TurnDurationsMs.Add(stopwatch.ElapsedMilliseconds);
            return response;
        }
        finally
        {
            result.LlmCalls += trace.LlmCalls.Count;
            result.FailedLlmCalls += trace.LlmCalls.Count(c => c.Failed);
            result.PromptTokens += trace.LlmCalls.Sum(c => c.PromptTokens);
            result.CompletionTokens += trace.LlmCalls.Sum(c => c.CompletionTokens);
        }
    }

    private static void Check(RunResult result, Expectation expected, ExecutionMode mode)
    {
        // Velkomsten tæller ikke med; den er den samme, uanset hvad borgeren spørger om.
        var botText = string.Join("\n", result.Transcript.Where(t => t.Speaker == "Bot").Skip(1).Select(t => t.Text));

        result.ExpectedRefusal = expected.Refusal;
        result.Refused = RefusalMarkers.Any(marker => botText.Contains(marker, StringComparison.OrdinalIgnoreCase));
        result.RefusalOk = expected.Refusal is null || result.Refused == expected.Refusal;

        if (expected.MustContain.Count > 0)
        {
            result.MissingKeywords = expected.MustContain
                .Where(keyword => !keyword.Split('|').Any(option => botText.Contains(option, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            result.KeywordsOk = result.MissingKeywords.Count == 0;
        }

        // DCR-tjek giver kun mening, når der er en DCR-simulation.
        if (mode != ExecutionMode.LlmOnly)
        {
            var executed = result.Executed.ToDictionary(e => e.EventId, e => e.Value, StringComparer.OrdinalIgnoreCase);
            if (expected.Executed.Count > 0 || expected.NotExecuted.Count > 0)
            {
                result.MissingExecuted = expected.Executed.Where(id => !executed.ContainsKey(id)).ToList();
                result.UnexpectedExecuted = expected.NotExecuted.Where(executed.ContainsKey).ToList();
                result.DcrStateOk = result.MissingExecuted.Count == 0 && result.UnexpectedExecuted.Count == 0;
            }

            if (expected.Values.Count > 0)
            {
                result.WrongValues = expected.Values
                    .Where(pair => !executed.TryGetValue(pair.Key, out var value) ||
                                   !(value ?? string.Empty).Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => $"{pair.Key}={(executed.TryGetValue(pair.Key, out var actual) ? actual : "(ikke udført)")}")
                    .ToList();
                result.ValuesOk = result.WrongValues.Count == 0;
            }
        }

        result.AutoPass = result.Error is null &&
                          result.RefusalOk &&
                          result.KeywordsOk != false &&
                          result.DcrStateOk != false &&
                          result.ValuesOk != false;
    }
}

public sealed record TranscriptLine(string Speaker, string Text);

public sealed record ExecutedValue(string EventId, string? Value);

/// <summary>Resultatet af én kørsel af ét scenarie i én tilstand.</summary>
public sealed class RunResult
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; }
    public int Run { get; set; }

    public List<TranscriptLine> Transcript { get; set; } = [];
    public List<ExecutedValue> Executed { get; set; } = [];

    /// <summary>Kladder, botten bad borgeren bekræfte (kun DCR-tilstandene).</summary>
    public int DraftsShown { get; set; }

    /// <summary>Kladder, der var en fejltolkning, og som borgeren derfor afviste.</summary>
    public int WrongDraftsRejected { get; set; }

    public int LlmCalls { get; set; }
    public int FailedLlmCalls { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public long DurationMs { get; set; }
    public List<long> TurnDurationsMs { get; set; } = [];
    public string? Error { get; set; }

    // Automatiske tjek. Null betyder "ikke relevant" for scenariet eller tilstanden.
    public bool? ExpectedRefusal { get; set; }
    public bool Refused { get; set; }
    public bool RefusalOk { get; set; }
    public bool? KeywordsOk { get; set; }
    public List<string> MissingKeywords { get; set; } = [];
    public bool? DcrStateOk { get; set; }
    public List<string> MissingExecuted { get; set; } = [];
    public List<string> UnexpectedExecuted { get; set; } = [];
    public bool? ValuesOk { get; set; }
    public List<string> WrongValues { get; set; } = [];
    public bool AutoPass { get; set; }
}
