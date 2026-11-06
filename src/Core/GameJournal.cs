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
    [property: JsonPropertyName("chosen")] int Chosen); // 0 = no se vio cuál (lo dice tu historial al acabar)

/// <summary>Lo que la app te recomendaba comprar a partir de ese minuto (se apunta cuando cambia).</summary>
public sealed record JournalBuy(
    [property: JsonPropertyName("min")] double Minute,
    [property: JsonPropertyName("item")] int ItemId,
    [property: JsonPropertyName("why")] string Reason);

/// <summary>
/// Una muerte: minuto, oro al morir y oro al reaparecer (<paramref name="Left"/>, desde la 1.8.3). El oro al morir NO es oro
/// sin gastar: muerto estás en la tienda y compras ahí. Lo que quedaba al reaparecer sí (antes, «moriste 89 veces con
/// 1.300+ de oro sin gastar» contaba compras normales). Null en los diarios de antes.
/// </summary>
public sealed record JournalDeath(
    [property: JsonPropertyName("min")] double Minute,
    [property: JsonPropertyName("gold")] int Gold,
    [property: JsonPropertyName("left")] int? Left = null);

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

    /// <summary>
    /// ¿Salieron las cartas justo al acabar la partida y no llegaste a elegir? (en el último minuto y ninguna está entre tus
    /// aumentos). Swain de Wakka, 2026-11-02: la partida acabó a los 14:47 con las cartas en pantalla y se apuntó una; en
    /// la nube, 3 de 274 elecciones (todas en el último minuto).
    /// </summary>
    public bool EndedBeforePick(JournalPick p, IReadOnlyCollection<int> augments) =>
        p.Minute >= Minutes - 1 && !augments.Contains(p.Chosen) && p.Offered.All(c => !augments.Contains(c.Id));

    /// <summary>
    /// Corrige las elecciones con tus aumentos de verdad (los del historial del cliente al acabar): si la carta apuntada no
    /// está entre ellos y sí lo está exactamente una de las que había, esa es la que elegiste. En la partida la app adivina
    /// por el clic y a veces se equivoca (Wakka, 2026-10-31: apuntó Archimaga y había cogido Crueldad). También completa
    /// las que no vio (elegida 0: las cartas se fueron sin ver tu clic).
    /// </summary>
    public GameJournal WithRealPicks(IReadOnlyCollection<int> augments)
    {
        for (int i = 0; i < Picks.Count; i++)
        {
            var p = Picks[i];
            if (augments.Contains(p.Chosen)) continue;
            var real = p.Offered.Where(c => augments.Contains(c.Id) && Picks.All(o => o.Chosen != c.Id)).ToList();
            if (real.Count == 1) Picks[i] = p with { Chosen = real[0].Id };
        }
        return this;
    }

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
