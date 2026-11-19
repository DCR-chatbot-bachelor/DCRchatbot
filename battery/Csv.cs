using System.Text;

namespace DcrChatbot.Battery;

/// <summary>
/// CSV med semikolon og UTF-8 med BOM, så filen åbner korrekt i dansk Excel
/// (semikolon er listeseparatoren, og BOM'en får æøå med).
/// </summary>
public static class Csv
{
    private const char Separator = ';';

    public static void Write(string path, IEnumerable<IReadOnlyList<string>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.AppendLine(string.Join(Separator, row.Select(Quote)));
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0];
        return rows.Skip(1)
            .Where(row => row.Any(cell => cell.Length > 0))
            .Select(row => header
                .Select((name, i) => (name, value: i < row.Count ? row[i] : string.Empty))
                .ToDictionary(c => c.name, c => c.value, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Quote(string value) =>
        value.IndexOfAny([Separator, '"', '\n', '\r']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    // Felter i anførselstegn kan indeholde semikolon, linjeskift og "".
    // Excel kan også have gemt med komma, hvis den danske indstilling mangler.
    private static List<List<string>> Parse(string text)
    {
        var separator = DetectSeparator(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static char DetectSeparator(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        return firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
    }
}
