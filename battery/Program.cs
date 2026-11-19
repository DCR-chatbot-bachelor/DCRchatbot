using System.Diagnostics;
using DcrChatbot.Battery;
using DcrChatbot.Core.Application.Llm;
using DcrChatbot.Core.Domain.Enums;
using DcrChatbot.Core.Interfaces;
using DcrChatbot.Core.Options;
using DcrChatbot.Infrastructure.DcrRepo;
using DcrChatbot.Infrastructure.LlmProviders;
using DcrChatbot.Infrastructure.Session;
using Microsoft.Extensions.Configuration;

// Testbatteri af borgerscenarier (#28, FR-TEST-1).
//
//   dotnet run --project battery                         alle scenarier, alle tilstande, 3 kørsler
//   dotnet run --project battery -- --runs 1 --only S01,S14
//   dotnet run --project battery -- --modes DcrStrict,LlmOnly
//   dotnet run --project battery -- --confirm-all            borgeren bekræfter alle kladder
//   dotnet run --project battery -- score battery/results/<mappe>

var repoRoot = FindRepoRoot();
if (args.Length > 0 && args[0] == "score")
{
    return args.Length > 1 ? Scoring.Run(Path.GetFullPath(args[1])) : Usage();
}

var options = ParseArgs(args);
if (options is null)
{
    return Usage();
}

var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(repoRoot, "src", "DcrChatbot.WebApi", "appsettings.json"), optional: false)
    .AddUserSecrets(typeof(BatteryRunner).Assembly)
    .Build();
var llmOptions = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
var dcrOptions = configuration.GetSection(DcrOptions.SectionName).Get<DcrOptions>() ?? new DcrOptions();

// Samme krav som Web API'et stiller ved opstart (Program.cs), så resultaterne
// altid hører til de prompts, koden faktisk indeholder (FR-LLM-1).
if (llmOptions.PromptVersion != PromptCatalog.Version)
{
    Console.Error.WriteLine($"Llm:PromptVersion er '{llmOptions.PromptVersion}', men koden indeholder '{PromptCatalog.Version}'.");
    return 1;
}

if (!llmOptions.HasApiKeyForProvider() || string.IsNullOrWhiteSpace(dcrOptions.RootUrl))
{
    Console.Error.WriteLine("API-nøglen til LLM'en eller Dcr:RootUrl mangler. Sæt dem med dotnet user-secrets (se README).");
    return 1;
}

var scenarioPath = Path.GetFullPath(options.ScenarioPath ?? Path.Combine(repoRoot, "battery", "scenarios", "su-faq.jsonc"));
var scenarioFile = ScenarioFile.Load(scenarioPath);
var scenarios = scenarioFile.Scenarios
    .Where(s => options.Only.Count == 0 || options.Only.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
    .ToList();
if (scenarios.Count == 0)
{
    Console.Error.WriteLine("Ingen scenarier matcher --only.");
    return 1;
}

using var dcrHttp = new HttpClient { BaseAddress = new Uri(dcrOptions.RootUrl, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(30) };
using var llmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
ILlmService provider;
string modelId;
if (llmOptions.Provider == LlmProvider.OpenAi)
{
    llmHttp.BaseAddress = new Uri("https://api.openai.com/", UriKind.Absolute);
    provider = new OpenAiLlmService(llmHttp);
    modelId = llmOptions.OpenAi.ModelId;
}
else
{
    llmHttp.BaseAddress = new Uri("https://generativelanguage.googleapis.com/", UriKind.Absolute);
    provider = new GeminiLlmService(llmHttp);
    modelId = llmOptions.ModelId;
}

var runner = new BatteryRunner(
    new DcrRepositoryClient(dcrHttp, Microsoft.Extensions.Options.Options.Create(dcrOptions)),
    provider,
    new InMemorySessionStore(),
    llmOptions,
    options.ConfirmAllDrafts);

var metadata = new BatteryMetadata
{
    StartedAt = DateTimeOffset.Now,
    GraphId = scenarioFile.GraphId,
    ScenarioFile = Path.GetRelativePath(repoRoot, scenarioPath).Replace('\\', '/'),
    ScenarioCount = scenarios.Count,
    Modes = options.Modes,
    Runs = options.Runs,
    PromptVersion = PromptCatalog.Version,
    Provider = llmOptions.Provider.ToString(),
    ModelId = modelId,
    Temperature = llmOptions.Temperature,
    GitCommit = GitCommit(repoRoot),
    ConfirmAllDrafts = options.ConfirmAllDrafts
};

var outputDir = Path.GetFullPath(options.OutputDir ?? Path.Combine(
    repoRoot, "battery", "results", $"{metadata.StartedAt:yyyyMMdd-HHmmss}-{PromptCatalog.Version}"));
Directory.CreateDirectory(outputDir);
ResultWriter.WriteMetadata(outputDir, metadata);

var total = scenarios.Count * options.Modes.Count * options.Runs;
Console.WriteLine($"Testbatteri: {scenarios.Count} scenarier × {options.Modes.Count} tilstande × {options.Runs} kørsler = {total} kørsler");
Console.WriteLine($"Graf {metadata.GraphId}, prompt {metadata.PromptVersion}, {metadata.Provider} {metadata.ModelId}");
Console.WriteLine($"Resultater: {outputDir}");
Console.WriteLine();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var results = new List<RunResult>();
var done = 0;
try
{
    // Kørslerne ligger yderst, så en afbrudt kørsel stadig har alle
    // scenarier i alle tilstande mindst én gang.
    for (var run = 1; run <= options.Runs; run++)
    {
        foreach (var scenario in scenarios)
        {
            foreach (var mode in options.Modes)
            {
                var result = await runner.RunAsync(scenarioFile.GraphId, scenario, mode, run, cancellation.Token);
                results.Add(result);
                ResultWriter.AppendRun(outputDir, result);
                done++;
                Console.WriteLine(
                    $"[{done,3}/{total}] {scenario.Id,-4} {mode,-9} #{run}  {(result.AutoPass ? "OK  " : "FEJL")}  " +
                    $"{result.LlmCalls} LLM-kald, {result.TotalTokens} tokens, {result.DurationMs / 1000.0:0.0} s" +
                    (result.Error is null ? string.Empty : $"  ({result.Error})"));
            }
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Afbrudt; de kørsler, der nåede at blive færdige, gemmes.");
}

metadata.FinishedAt = DateTimeOffset.Now;
ResultWriter.WriteMetadata(outputDir, metadata);
if (results.Count > 0)
{
    ResultWriter.WriteSummary(outputDir, results, metadata);
    ResultWriter.WriteScoringSheet(outputDir, results, scenarios);
    ResultWriter.WriteTranscripts(outputDir, results, scenarios);
    Console.WriteLine();
    Console.WriteLine(File.ReadAllText(Path.Combine(outputDir, "summary.md")));
    Console.WriteLine($"Læs samtalerne i {Path.Combine(outputDir, "transcripts.md")}");
    Console.WriteLine($"Bedøm svarene blindt i {Path.Combine(outputDir, "scoring.csv")} og kør derefter:");
    Console.WriteLine($"  dotnet run --project battery -- score \"{outputDir}\"");
}

return 0;

static int Usage()
{
    Console.Error.WriteLine("""
        Brug:
          dotnet run --project battery [-- --runs N] [--modes DcrStrict,DcrGuided,LlmOnly] [--only S01,S02] [--confirm-all] [--scenarios fil] [--out mappe]
          dotnet run --project battery -- score <resultatmappe>
        """);
    return 1;
}

static BatteryOptions? ParseArgs(string[] args)
{
    var options = new BatteryOptions();
    for (var i = 0; i < args.Length; i++)
    {
        var value = i + 1 < args.Length ? args[i + 1] : null;
        switch (args[i])
        {
            case "--runs" when int.TryParse(value, out var runs) && runs > 0:
                options.Runs = runs;
                i++;
                break;
            case "--modes" when value is not null:
                var modes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(m => Enum.TryParse<ExecutionMode>(m, ignoreCase: true, out var mode) ? mode : (ExecutionMode?)null)
                    .ToList();
                if (modes.Any(m => m is null))
                {
                    return null;
                }

                options.Modes = modes.Select(m => m!.Value).ToList();
                i++;
                break;
            case "--only" when value is not null:
                options.Only = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                i++;
                break;
            case "--scenarios" when value is not null:
                options.ScenarioPath = value;
                i++;
                break;
            case "--confirm-all":
                options.ConfirmAllDrafts = true;
                break;
            case "--out" when value is not null:
                options.OutputDir = value;
                i++;
                break;
            default:
                return null;
        }
    }

    return options;
}

static string FindRepoRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DcrChatbot.slnx")))
        {
            return directory.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}

static string GitCommit(string repoRoot)
{
    try
    {
        using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        var commit = git?.StandardOutput.ReadToEnd().Trim();
        git?.WaitForExit();
        return string.IsNullOrWhiteSpace(commit) ? "ukendt" : commit;
    }
    catch (Exception)
    {
        return "ukendt";
    }
}

internal sealed class BatteryOptions
{
    public int Runs { get; set; } = 3;
    public List<ExecutionMode> Modes { get; set; } = [ExecutionMode.DcrStrict, ExecutionMode.DcrGuided, ExecutionMode.LlmOnly];
    public List<string> Only { get; set; } = [];
    public string? ScenarioPath { get; set; }
    public string? OutputDir { get; set; }
    public bool ConfirmAllDrafts { get; set; }
}
