using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcrChatbot.Core.Application.Tracing;
using DcrChatbot.Core.Interfaces;

namespace DcrChatbot.Infrastructure.Logging;

/// <summary>
/// Skriver én JSON-linje pr. tur til en fil pr. dag (trace-yyyyMMdd.jsonl),
/// så loggen kan analyseres bagefter og samtaler kan genskabes (FR-TEST-1).
/// </summary>
public sealed class JsonLinesTraceStore(string directory) : ITraceStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim writeLock = new(1, 1);

    public string Directory { get; } = directory;

    public async Task AppendAsync(TraceRecord record, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        var path = Path.Combine(Directory, $"trace-{record.Timestamp.UtcDateTime:yyyyMMdd}.jsonl");

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.AppendAllTextAsync(path, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<TraceRecord>> ReadSessionAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var records = new List<TraceRecord>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "trace-*.jsonl").Order())
        {
            // FileShare.ReadWrite, så filen kan læses mens der skrives til den.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                TraceRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<TraceRecord>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue; // En halvskrevet eller ødelagt linje springes over.
                }

                if (record?.SessionId == sessionId)
                {
                    records.Add(record);
                }
            }
        }

        return records.OrderBy(r => r.Timestamp).ToList();
    }
}
