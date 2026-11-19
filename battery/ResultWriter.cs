using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcrChatbot.Core.Domain.Enums;

namespace DcrChatbot.Battery;

/// <summary>
/// Skriver en kørsel af testbatteriet til en mappe:
/// runs.jsonl (alt), summary.md/.csv (automatiske tal pr. tilstand),
/// scoring.csv + scoring-key.csv (blind manuel bedømmelse) og metadata.json.
/// </summary>
public static class ResultWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // Rækkefølgen i bedømmelsesskemaet blandes, så tilstanden ikke kan
    // gættes ud fra placeringen; et fast frø gør blandingen reproducerbar.
    private const int ShuffleSeed = 28;

    public static void AppendRun(string directory, RunResult result) =>
        File.AppendAllText(
            Path.Combine(directory, "runs.jsonl"),
            JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));

    public static void WriteMetadata(string directory, BatteryMetadata metadata) =>
        File.WriteAllText(
            Path.Combine(directory, "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }),
            new UTF8Encoding(false));

    public static void WriteSummary(string directory, IReadOnlyList<RunResult> results, BatteryMetadata metadata)
    {
        var modes = results.Select(r => r.Mode).Distinct().OrderBy(ModeOrder).ToList();
        var categories = results.Select(r => r.Category).Distinct().ToList();

        var md = new StringBuilder();
        md.AppendLine($"# Testbatteri {metadata.StartedAt:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"Graf `{metadata.GraphId}` · prompt `{metadata.PromptVersion}` · {metadata.Provider} `{metadata.ModelId}` · " +
                      $"temperatur {metadata.Temperature.ToString(CultureInfo.InvariantCulture)} · {metadata.Runs} kørsler pr. scenarie · commit `{metadata.GitCommit}`");
        md.AppendLine();
        md.AppendLine(metadata.ConfirmAllDrafts
            ? "Den simulerede borger bekræftede **alle** kladder (`--confirm-all`): tallene viser DCR-tilstandene uden bekræftelsens værn."
            : "Den simulerede borger bekræftede kladder, der passede til det forventede, og afviste resten, som en borger der ved, hvad den mente.");
        md.AppendLine();
        md.AppendLine("Automatiske tjek. Den endelige vurdering af svarene (korrekt, hallucineret osv.) kommer fra den blinde bedømmelse i `scoring.csv`.");
        md.AppendLine();
        md.AppendLine("| | " + string.Join(" | ", modes) + " |");
        md.AppendLine("|---|" + string.Concat(modes.Select(_ => "---|")));
        foreach (var (label, value) in OverallRows())
        {
            md.AppendLine($"| {label} | " + string.Join(" | ", modes.Select(m => value(results.Where(r => r.Mode == m).ToList()))) + " |");
        }

        md.AppendLine();
        md.AppendLine("## Automatisk bestået pr. kategori");
        md.AppendLine();
        md.AppendLine("| Kategori | " + string.Join(" | ", modes) + " |");
        md.AppendLine("|---|" + string.Concat(modes.Select(_ => "---|")));
        foreach (var category in categories)
        {
            md.AppendLine($"| {category} | " + string.Join(" | ", modes.Select(m =>
                Percent(results.Where(r => r.Mode == m && r.Category == category).ToList(), r => r.AutoPass))) + " |");
        }

        var failures = results.Where(r => !r.AutoPass).OrderBy(r => r.ScenarioId).ThenBy(r => ModeOrder(r.Mode)).ThenBy(r => r.Run).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Ikke bestået automatisk");
            md.AppendLine();
            md.AppendLine("| Scenarie | Tilstand | Kørsel | Hvorfor |");
            md.AppendLine("|---|---|---|---|");
            foreach (var failure in failures)
            {
                md.AppendLine($"| {failure.ScenarioId} | {failure.Mode} | {failure.Run} | {Reason(failure)} |");
            }
        }

        File.WriteAllText(Path.Combine(directory, "summary.md"), md.ToString(), new UTF8Encoding(false));

        var csv = new List<IReadOnlyList<string>> { new[] { "Tilstand", "Kategori", "Kørsler", "Automatisk bestået", "Korrekt afslag", "Fejl" } };
        foreach (var mode in modes)
        {
            foreach (var category in categories.Prepend("(alle)"))
            {
                var rows = results.Where(r => r.Mode == mode && (category == "(alle)" || r.Category == category)).ToList();
                csv.Add([mode.ToString(), category, rows.Count.ToString(), Percent(rows, r => r.AutoPass), Percent(rows, r => r.RefusalOk), rows.Count(r => r.Error is not null).ToString()]);
            }
        }

        Csv.Write(Path.Combine(directory, "summary.csv"), csv);
    }

    // Kun første kørsel bedømmes manuelt (scenarier × tilstande rækker); de
    // øvrige kørsler bruges til at måle, hvor stabile svarene er.
    public static void WriteScoringSheet(string directory, IReadOnlyList<RunResult> results, IReadOnlyList<Scenario> scenarios)
    {
        var byId = scenarios.ToDictionary(s => s.Id);
        var random = new Random(ShuffleSeed);
        var rows = results.Where(r => r.Run == 1).OrderBy(_ => random.Next()).ToList();

        var sheet = new List<IReadOnlyList<string>>
        {
            new[] { "BlindId", "Scenarie", "Kategori", "Borgeren skrev", "Forventet", "Samtale", "Vurdering", "Note" }
        };
        var key = new List<IReadOnlyList<string>> { new[] { "BlindId", "Scenarie", "Tilstand", "Kørsel" } };
        for (var i = 0; i < rows.Count; i++)
        {
            var result = rows[i];
            var scenario = byId[result.ScenarioId];
            var blindId = $"B{i + 1:000}";
            sheet.Add(
            [
                blindId,
                scenario.Id,
                scenario.Category,
                string.Join("\n", scenario.Turns.Select(t => t.Say)),
                scenario.Expected.Description,
                BlindTranscript(result),
                string.Empty,
                string.Empty
            ]);
            key.Add([blindId, scenario.Id, result.Mode.ToString(), result.Run.ToString()]);
        }

        Csv.Write(Path.Combine(directory, "scoring.csv"), sheet);
        Csv.Write(Path.Combine(directory, "scoring-key.csv"), key);
    }

    // Samtalerne til at læse: pr. scenarie det forventede svar og hvad hver
    // tilstand svarede, med de automatiske tjek. Første kørsel vises helt;
    // senere kørsler kun, hvis bottens svar blev anderledes.
    // Afslører tilstanden, så den, der bedømmer blindt, skal vente med den.
    public static void WriteTranscripts(string directory, IReadOnlyList<RunResult> results, IReadOnlyList<Scenario> scenarios)
    {
        var md = new StringBuilder();
        md.AppendLine("# Samtaler");
        md.AppendLine();
        md.AppendLine("> **Bedømmer du blindt, så vent med denne fil, til `scoring.csv` er udfyldt.** Den viser, hvilken tilstand der svarede hvad.");
        md.AppendLine();
        foreach (var scenario in scenarios.Where(s => results.Any(r => r.ScenarioId == s.Id)))
        {
            md.AppendLine($"## {scenario.Id} · {scenario.Category}");
            md.AppendLine();
            if (!string.IsNullOrWhiteSpace(scenario.Note))
            {
                md.AppendLine($"*{scenario.Note}*");
                md.AppendLine();
            }

            md.AppendLine($"**Forventet:** {scenario.Expected.Description}");
            md.AppendLine();
            foreach (var mode in results.Select(r => r.Mode).Distinct().OrderBy(ModeOrder))
            {
                var runs = results.Where(r => r.ScenarioId == scenario.Id && r.Mode == mode).OrderBy(r => r.Run).ToList();
                if (runs.Count == 0)
                {
                    continue;
                }

                var first = runs[0];
                var passed = runs.Count(r => r.AutoPass);
                md.AppendLine($"### {mode}: {(first.AutoPass ? "bestået" : "IKKE BESTÅET")} automatisk" +
                              (runs.Count > 1 ? $" ({passed}/{runs.Count} kørsler)" : string.Empty));
                md.AppendLine();
                AppendConversation(md, first);
                foreach (var other in runs.Skip(1).Where(r => BotReplies(r) != BotReplies(first)))
                {
                    md.AppendLine($"<details><summary>Kørsel {other.Run} svarede anderledes ({(other.AutoPass ? "bestået" : "ikke bestået")})</summary>");
                    md.AppendLine();
                    AppendConversation(md, other);
                    md.AppendLine("</details>");
                    md.AppendLine();
                }
            }
        }

        File.WriteAllText(Path.Combine(directory, "transcripts.md"), md.ToString(), new UTF8Encoding(false));
    }

    private static void AppendConversation(StringBuilder md, RunResult result)
    {
        foreach (var line in result.Transcript.Skip(1))
        {
            var text = line.Text.Replace("\n", "<br>");
            md.AppendLine(line.Speaker switch
            {
                "Borger" => $"- **Borger:** {text}",
                "Handling" => $"- *{text}*",
                _ => $"- Bot: {text}"
            });
        }

        md.AppendLine();
        var facts = new List<string>();
        if (!result.AutoPass)
        {
            facts.Add($"**Hvorfor ikke bestået:** {Reason(result)}");
        }

        if (result.Mode != ExecutionMode.LlmOnly)
        {
            var executed = result.Executed.Where(e => !string.IsNullOrEmpty(e.Value)).Select(e => $"{e.EventId}={e.Value}").ToList();
            facts.Add($"Udført i DCR: {(executed.Count == 0 ? "intet" : string.Join(", ", executed))}");
            if (result.WrongDraftsRejected > 0)
            {
                facts.Add($"Fejltolkninger afvist af borgeren: {result.WrongDraftsRejected}");
            }
        }

        facts.Add($"{result.LlmCalls} LLM-kald · {result.TotalTokens} tokens · {result.DurationMs / 1000.0:0.0} s");
        md.AppendLine(string.Join("  \n", facts));
        md.AppendLine();
    }

    private static string BotReplies(RunResult result) =>
        string.Join("\n", result.Transcript.Where(t => t.Speaker == "Bot").Select(t => t.Text));

    // Kladderne ("Jeg har forstået … Er det korrekt?") og bekræftelserne
    // findes kun i DCR-tilstandene og ville afsløre tilstanden; det, der
    // bedømmes, er bottens svar.
    private static string BlindTranscript(RunResult result) =>
        string.Join("\n", result.Transcript
            .Where(t => t.Speaker != "Handling" && !IsDraftPrompt(t))
            .Select(t => $"{t.Speaker}: {t.Text}"));

    private static bool IsDraftPrompt(TranscriptLine line) =>
        line.Speaker == "Bot" &&
        (line.Text.StartsWith("Jeg har forstået '", StringComparison.Ordinal) ||
         line.Text.StartsWith("Vil du ændre dit svar", StringComparison.Ordinal));

    private static IEnumerable<(string Label, Func<List<RunResult>, string> Value)> OverallRows() =>
    [
        ("Kørsler", rows => rows.Count.ToString()),
        ("Automatisk bestået", rows => Percent(rows, r => r.AutoPass)),
        ("Korrekt afslag (uden for grafen)", rows => Percent(rows.Where(r => r.ExpectedRefusal == true).ToList(), r => r.Refused)),
        ("Afslag, hvor der fandtes et svar", rows => Percent(rows.Where(r => r.ExpectedRefusal == false).ToList(), r => r.Refused)),
        ("Nøgleord fundet", rows => Percent(rows.Where(r => r.KeywordsOk is not null).ToList(), r => r.KeywordsOk == true)),
        ("DCR-tilstand som forventet", rows => Percent(rows.Where(r => r.DcrStateOk is not null).ToList(), r => r.DcrStateOk == true)),
        ("Formularværdier korrekte", rows => Percent(rows.Where(r => r.ValuesOk is not null).ToList(), r => r.ValuesOk == true)),
        ("Stabil på tværs af kørsler", Stability),
        ("Kladder til bekræftelse", rows => rows.Sum(r => r.DraftsShown).ToString()),
        ("Fejltolkninger fanget af bekræftelsen", rows => rows.Sum(r => r.WrongDraftsRejected).ToString()),
        ("LLM-kald pr. scenarie (gns.)", rows => Average(rows, r => r.LlmCalls)),
        ("Tokens pr. scenarie (gns.)", rows => Average(rows, r => r.TotalTokens)),
        ("Svartid pr. tur, ms (gns.)", rows => Average(rows.SelectMany(r => r.TurnDurationsMs).Select(d => (double)d).ToList())),
        ("Fejl", rows => rows.Count(r => r.Error is not null).ToString())
    ];

    // Andelen af scenarier, hvor alle kørsler fik samme automatiske resultat
    // og (i DCR-tilstandene) endte i samme tilstand.
    private static string Stability(List<RunResult> rows)
    {
        var groups = rows.GroupBy(r => r.ScenarioId).Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0)
        {
            return "–";
        }

        var stable = groups.Count(g =>
            g.Select(r => r.AutoPass).Distinct().Count() == 1 &&
            g.Select(r => string.Join(",", r.Executed.Select(e => $"{e.EventId}={e.Value}").Order())).Distinct().Count() == 1);
        return $"{100.0 * stable / groups.Count:0}% ({stable}/{groups.Count})";
    }

    private static string Reason(RunResult result)
    {
        var reasons = new List<string>();
        if (result.Error is not null) reasons.Add($"fejl: {result.Error}");
        if (!result.RefusalOk) reasons.Add(result.Refused ? "afslog, men grafen har svaret" : "svarede, men burde afslå");
        if (result.MissingKeywords.Count > 0) reasons.Add($"mangler: {string.Join(", ", result.MissingKeywords)}");
        if (result.MissingExecuted.Count > 0) reasons.Add($"ikke udført: {string.Join(", ", result.MissingExecuted)}");
        if (result.UnexpectedExecuted.Count > 0) reasons.Add($"udført ved en fejl: {string.Join(", ", result.UnexpectedExecuted)}");
        if (result.WrongValues.Count > 0) reasons.Add($"forkerte værdier: {string.Join(", ", result.WrongValues)}");
        return string.Join("; ", reasons).Replace("|", "/");
    }

    private static string Percent(List<RunResult> rows, Func<RunResult, bool> predicate) =>
        rows.Count == 0 ? "–" : $"{100.0 * rows.Count(predicate) / rows.Count:0}% ({rows.Count(predicate)}/{rows.Count})";

    private static string Average(List<RunResult> rows, Func<RunResult, int> selector) =>
        rows.Count == 0 ? "–" : rows.Average(selector).ToString("0", CultureInfo.InvariantCulture);

    private static string Average(List<double> values) =>
        values.Count == 0 ? "–" : values.Average().ToString("0", CultureInfo.InvariantCulture);

    public static int ModeOrder(ExecutionMode mode) => mode switch
    {
        ExecutionMode.DcrStrict => 0,
        ExecutionMode.DcrGuided => 1,
        _ => 2
    };
}

public sealed class BatteryMetadata
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string GraphId { get; set; } = string.Empty;
    public string ScenarioFile { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
    public List<ExecutionMode> Modes { get; set; } = [];
    public int Runs { get; set; }
    public string PromptVersion { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public string GitCommit { get; set; } = string.Empty;

    /// <summary>
    /// Sandt, når den simulerede borger bekræftede alle kladder; ellers
    /// afviste den kladder, der ikke passede til det forventede.
    /// </summary>
    public bool ConfirmAllDrafts { get; set; }
}
