using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Una carta que te ofrecieron, con la nota (0-100) y el rango que le puso la app en ese momento.</summary>
public sealed record JournalCard(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("grade")] string Grade);

/// <summary>Una elección de aumento: minuto, las cartas que había (con re-rolls, las últimas) y la que elegiste.</summary>
public sealed record JournalPick(
    [property: JsonPropertyName("min")] double Minute,
    [property: JsonPropertyName("offered")] List<JournalCard> Offered,
    [property: JsonPropertyName("chosen")] int Chosen);

/// <summary>Lo que la app te recomendaba comprar a partir de ese minuto (se apunta cuando cambia).</summary>
public sealed record JournalBuy(
    [property: JsonPropertyName("min")] double Minute,
    [property: JsonPropertyName("item")] int ItemId,
    [property: JsonPropertyName("why")] string Reason);

/// <summary>Una muerte: minuto y oro que llevabas sin gastar.</summary>
public sealed record JournalDeath(
    [property: JsonPropertyName("min")] double Minute,
    [property: JsonPropertyName("gold")] int Gold);

/// <summary>
/// El diario de una partida: lo que la app vio y recomendó mientras jugabas (cartas y su nota, compras, muertes).
/// Se guarda al acabar en <c>diario\</c> y el resumen de la partida lo junta con los datos del cliente de LoL.
/// </summary>
public sealed class GameJournal
{
    [JsonPropertyName("start")] public DateTime StartedUtc { get; set; }
    [JsonPropertyName("champion")] public string Champion { get; set; } = "";
    [JsonPropertyName("picks")] public List<JournalPick> Picks { get; set; } = [];
    [JsonPropertyName("buys")] public List<JournalBuy> Buys { get; set; } = [];
    [JsonPropertyName("deaths")] public List<JournalDeath> Deaths { get; set; } = [];
    /// <summary>La build completa que recomendaba la app al final (ids de objeto).</summary>
    [JsonPropertyName("build")] public List<int> FinalBuild { get; set; } = [];
    [JsonPropertyName("minutes")] public double Minutes { get; set; }
    /// <summary>
    /// Idioma de los textos guardados (las razones de compra): con la app en el otro idioma no se enseñan, para no
    /// mezclar español e inglés. Los diarios de antes de 1.3.9 no lo tienen: eran en español.
    /// </summary>
    [JsonPropertyName("lang")] public string Lang { get; set; } = "es";

    static string Dir => Path.Combine(Paths.DataDir, "diario");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, $"{StartedUtc:yyyyMMdd-HHmmss}-{Champion}.json"), JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
    }

    /// <summary>
    /// El diario de una partida del historial: mismo campeón y empezado poco después de crearse la partida
    /// (el cliente da la hora de creación; la app la detecta al cargar, 1-3 min después). Null si no hay.
    /// </summary>
    public static GameJournal? Find(string champion, long gameCreationMs, int durationSeconds)
    {
        if (!Directory.Exists(Dir)) return null;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(gameCreationMs).UtcDateTime;
        GameJournal? best = null;
        double bestGap = double.MaxValue;
        foreach (var file in Directory.EnumerateFiles(Dir, $"*-{champion}.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<GameJournal>(File.ReadAllText(file)) is not { } j) continue;
                double gap = (j.StartedUtc - created).TotalMinutes;
                if (gap < -2 || gap > 12 + durationSeconds / 60.0 / 4) continue;
                if (Math.Abs(gap) < bestGap) { best = j; bestGap = Math.Abs(gap); }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return best;
    }
}
