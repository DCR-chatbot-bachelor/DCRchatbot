using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcrChatbot.Battery;

/// <summary>
/// Testbatteriets scenarier (#28): borgerens beskeder og det forventede
/// resultat, skrevet før systemet blev kørt, så de ikke tilpasses svarene.
/// </summary>
public sealed class ScenarioFile
{
    public string GraphId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<Scenario> Scenarios { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ScenarioFile Load(string path) =>
        JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"Kunne ikke læse scenarierne i {path}.");
}

public sealed class Scenario
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Note { get; set; }
    public List<ScenarioTurn> Turns { get; set; } = [];
    public Expectation Expected { get; set; } = new();
}

/// <summary>
/// Én besked fra borgeren. Som udgangspunkt bekræfter borgeren en kladde,
/// når den passer til det forventede, og afviser den ellers (se
/// BatteryRunner). Draft = "confirm" eller "reject" fastlægger svaret på den
/// første kladde efter beskeden. LlmOnly har ingen kladder; ved "reject"
/// sendes RejectText i stedet, den eneste måde den tilstand kan afvises på.
/// </summary>
[JsonConverter(typeof(ScenarioTurnConverter))]
public sealed class ScenarioTurn
{
    public string Say { get; set; } = string.Empty;
    public string? Draft { get; set; }
    public string RejectText { get; set; } = "Nej, det er ikke rigtigt.";

    /// <summary>
    /// Beskeden er ikke et gyldigt svar (fx "efterskole", som ikke er et af
    /// valgene). Et svar, botten forstår ud fra den, er en fejltolkning, også
    /// selvom det tilfældigvis ligner den værdi, borgeren ender med.
    /// </summary>
    public bool Invalid { get; set; }

    /// <summary>
    /// Sendes kun, hvis botten lige har stillet et spørgsmål (fx et "ja" til
    /// "Vil du starte på Erhvervsuddannelse?"). En borger svarer, når der
    /// bliver spurgt, men skriver ikke uopfordret, når forløbet er slut.
    /// </summary>
    public bool Optional { get; set; }

    public bool RejectsDraft => string.Equals(Draft, "reject", StringComparison.OrdinalIgnoreCase);
    public bool ConfirmsDraft => string.Equals(Draft, "confirm", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Det forventede resultat. Executed, NotExecuted og Values tjekkes i DCR's
/// tilstand og gælder kun DCR-tilstandene; MustContain og Refusal tjekkes i
/// bottens tekst og gælder alle tre.
/// </summary>
public sealed class Expectation
{
    /// <summary>Events, der skal være udført til sidst (fx emnet og dets svar).</summary>
    public List<string> Executed { get; set; } = [];

    /// <summary>Events, der ikke må være udført (fx formularens afslutning ved afbrydelse).</summary>
    public List<string> NotExecuted { get; set; } = [];

    /// <summary>Felt → værdi, som det udførte svar skal indeholde (uden forskel på store og små bogstaver).</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>Tekst, bottens svar skal indeholde. "a|b" betyder a eller b.</summary>
    public List<string> MustContain { get; set; } = [];

    /// <summary>
    /// Om det korrekte svar er, at botten ikke kan svare (spørgsmålet ligger
    /// uden for grafen). Null, når det ikke afgør noget (fx "hvad kan du
    /// hjælpe med?", hvor DCR-tilstandene viser emnerne efter et "desværre").
    /// </summary>
    public bool? Refusal { get; set; } = false;

    /// <summary>Det rigtige svar med ord, til den manuelle bedømmelse.</summary>
    public string Description { get; set; } = string.Empty;
}

// En tur kan skrives som en tekst ("Jens Hansen") eller som et objekt med
// "say" og "draft", når kladden skal afvises.
internal sealed class ScenarioTurnConverter : JsonConverter<ScenarioTurn>
{
    public override ScenarioTurn Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ScenarioTurn { Say = reader.GetString() ?? string.Empty };
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var turn = new ScenarioTurn();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "say": turn.Say = property.Value.GetString() ?? string.Empty; break;
                case "draft": turn.Draft = property.Value.GetString(); break;
                case "rejecttext": turn.RejectText = property.Value.GetString() ?? turn.RejectText; break;
                case "invalid": turn.Invalid = property.Value.GetBoolean(); break;
                case "optional": turn.Optional = property.Value.GetBoolean(); break;
            }
        }

        return turn;
    }

    public override void Write(Utf8JsonWriter writer, ScenarioTurn value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Say);
}
