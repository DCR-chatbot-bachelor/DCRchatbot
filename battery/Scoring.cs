using System.Globalization;
using System.Text;

namespace DcrChatbot.Battery;

/// <summary>
/// Samler den blinde bedømmelse (scoring.csv) med nøglen til en tabel pr.
/// tilstand. Har en anden bedømmer udfyldt scoring-2.csv, beregnes enighed
/// og Cohens kappa, så bedømmelsens pålidelighed kan angives i rapporten.
/// </summary>
public static class Scoring
{
    // Bedømmelsesskalaen. Forkortelserne gør det hurtigere at udfylde skemaet.
    public static readonly string[] Ratings = ["korrekt", "delvist", "forkert", "hallucineret", "korrekt_afvist"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["k"] = "korrekt",
        ["d"] = "delvist",
        ["delvis"] = "delvist",
        ["f"] = "forkert",
        ["h"] = "hallucineret",
        ["a"] = "korrekt_afvist",
        ["afvist"] = "korrekt_afvist",
        ["korrekt afvist"] = "korrekt_afvist",
        ["korrekt-afvist"] = "korrekt_afvist"
    };

    public static int Run(string directory)
    {
        var keyPath = Path.Combine(directory, "scoring-key.csv");
        var sheetPath = Path.Combine(directory, "scoring.csv");
        if (!File.Exists(keyPath) || !File.Exists(sheetPath))
        {
            Console.Error.WriteLine($"Fandt ikke scoring.csv og scoring-key.csv i {directory}.");
            return 1;
        }

        var key = Csv.Read(keyPath).ToDictionary(r => r["BlindId"], StringComparer.OrdinalIgnoreCase);
        var first = ReadRatings(sheetPath);
        var secondPath = Path.Combine(directory, "scoring-2.csv");
        var second = File.Exists(secondPath) ? ReadRatings(secondPath) : null;

        var unknown = first.Values.Concat(second?.Values.AsEnumerable() ?? [])
            .Where(r => r.Rating is null && r.Raw.Length > 0)
            .Select(r => r.Raw)
            .Distinct()
            .ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Ukendte vurderinger: {string.Join(", ", unknown)}. Brug {string.Join(", ", Ratings)} (eller K, D, F, H, A).");
            return 1;
        }

        var rows = first
            .Where(r => r.Value.Rating is not null && key.ContainsKey(r.Key))
            .Select(r => (Mode: key[r.Key]["Tilstand"], r.Value.Category, Rating: r.Value.Rating!))
            .ToList();
        var modes = rows.Select(r => r.Mode).Distinct()
            .OrderBy(m => Enum.TryParse<DcrChatbot.Core.Domain.Enums.ExecutionMode>(m, out var mode) ? ResultWriter.ModeOrder(mode) : 9)
            .ToList();

        var md = new StringBuilder();
        md.AppendLine("# Blind bedømmelse");
        md.AppendLine();
        md.AppendLine($"{rows.Count} af {first.Count} svar er bedømt.");
        md.AppendLine();
        md.AppendLine("| Vurdering | " + string.Join(" | ", modes) + " |");
        md.AppendLine("|---|" + string.Concat(modes.Select(_ => "---|")));
        foreach (var rating in Ratings)
        {
            md.AppendLine($"| {rating} | " + string.Join(" | ", modes.Select(m => Share(rows.Where(r => r.Mode == m).ToList(), r => r.Rating == rating))) + " |");
        }

        md.AppendLine($"| **rigtige i alt** (korrekt + korrekt_afvist) | " + string.Join(" | ", modes.Select(m =>
            Share(rows.Where(r => r.Mode == m).ToList(), r => r.Rating is "korrekt" or "korrekt_afvist"))) + " |");

        md.AppendLine();
        md.AppendLine("## Rigtige pr. kategori");
        md.AppendLine();
        md.AppendLine("| Kategori | " + string.Join(" | ", modes) + " |");
        md.AppendLine("|---|" + string.Concat(modes.Select(_ => "---|")));
        foreach (var category in rows.Select(r => r.Category).Distinct())
        {
            md.AppendLine($"| {category} | " + string.Join(" | ", modes.Select(m =>
                Share(rows.Where(r => r.Mode == m && r.Category == category).ToList(), r => r.Rating is "korrekt" or "korrekt_afvist"))) + " |");
        }

        if (second is not null)
        {
            var pairs = first
                .Where(r => r.Value.Rating is not null && second.TryGetValue(r.Key, out var other) && other.Rating is not null)
                .Select(r => (A: r.Value.Rating!, B: second[r.Key].Rating!))
                .ToList();
            md.AppendLine();
            md.AppendLine("## Enighed mellem bedømmerne");
            md.AppendLine();
            md.AppendLine($"{pairs.Count} svar er bedømt af begge. Enige om {Share(pairs, p => p.A == p.B)}. " +
                          $"Cohens kappa: {CohensKappa(pairs).ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        var output = Path.Combine(directory, "scored-summary.md");
        File.WriteAllText(output, md.ToString(), new UTF8Encoding(false));
        Console.WriteLine(md.ToString());
        Console.WriteLine($"Gemt i {output}");
        return 0;
    }

    private static Dictionary<string, (string? Rating, string Raw, string Category)> ReadRatings(string path) =>
        Csv.Read(path).ToDictionary(
            r => r["BlindId"],
            r =>
            {
                var raw = r.GetValueOrDefault("Vurdering", string.Empty).Trim();
                return (Normalize(raw), raw, r.GetValueOrDefault("Kategori", string.Empty));
            },
            StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var value = raw.ToLowerInvariant();
        return Aliases.TryGetValue(value, out var alias) ? alias
            : Ratings.Contains(value) ? value
            : null;
    }

    // Enighed korrigeret for den enighed, man ville få ved tilfældighed.
    private static double CohensKappa(IReadOnlyList<(string A, string B)> pairs)
    {
        if (pairs.Count == 0)
        {
            return double.NaN;
        }

        var observed = pairs.Count(p => p.A == p.B) / (double)pairs.Count;
        var expected = Ratings.Sum(rating =>
            pairs.Count(p => p.A == rating) / (double)pairs.Count *
            (pairs.Count(p => p.B == rating) / (double)pairs.Count));
        return expected >= 1 ? 1 : (observed - expected) / (1 - expected);
    }

    private static string Share<T>(IReadOnlyList<T> rows, Func<T, bool> predicate) =>
        rows.Count == 0 ? "–" : $"{100.0 * rows.Count(predicate) / rows.Count:0}% ({rows.Count(predicate)}/{rows.Count})";
}
