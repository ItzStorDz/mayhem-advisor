using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Cómo le va a un campeón con un aumento en Mayhem (todos los jugadores, mayhemmeta.com).</summary>
/// <param name="WinRate">% de victorias con el aumento (0-100).</param>
/// <param name="ChampionWinRate">% de victorias del campeón en general (0-100), para comparar.</param>
public sealed record MayhemAugmentChampion(int Games, double WinRate, double PickRate, double ChampionWinRate)
{
    /// <summary>Partidas "virtuales" sin efecto: con pocas partidas, la diferencia se acerca a 0 (no nos fiamos).</summary>
    const double ShrinkGames = 150;

    /// <summary>Cuánto sube o baja el % del campeón con este aumento, en puntos; encogido si hay pocas partidas.</summary>
    public double Delta => (WinRate - ChampionWinRate) * Games / (Games + ShrinkGames);
}

/// <summary>Cómo combinan dos aumentos en Mayhem (todos los jugadores, mayhemmeta.com).</summary>
/// <param name="Games">Partidas en las que alguien llevó los dos (aprox.).</param>
/// <param name="WinRate">% de victorias con los dos (0-100).</param>
/// <param name="Synergy">Puntos de % por encima (o debajo) de lo esperado contando lo bueno que es cada uno por separado.</param>
public sealed record MayhemAugmentPair(int Games, double WinRate, double Synergy)
{
    const double ShrinkGames = 200;

    /// <summary>La sinergia encogida hacia 0 si hay pocas partidas (no nos fiamos de 60 partidas como de 2000).</summary>
    public double ShrunkSynergy => Synergy * Games / (Games + ShrinkGames);
}

/// <summary>
/// Datos de los aumentos de Mayhem según mayhemmeta.com, de <c>mayhem-augments.json</c>
/// (lo actualiza <c>herramientas/actualizar-mayhem.mjs</c>). Solo ARAM Mayhem, nada de Arena:
///  • la descripción de cada aumento (en inglés): el juego deja unos 80 sin texto y sin ella no sabríamos qué objetos piden;
///  • el % de victorias de cada campeón con cada aumento (unos 170 campeones por aumento).
/// </summary>
public sealed class MayhemAugments
{
    sealed class FileShape
    {
        [JsonPropertyName("augments")] public List<Entry> Augments { get; set; } = [];
    }

    sealed class Entry
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        /// <summary>Filas [alias, partidas, % con el aumento, pick rate, % del campeón].</summary>
        [JsonPropertyName("champions")] public List<JsonElement[]>? Champions { get; set; }
        /// <summary>Filas [otro aumento, partidas juntos, % victorias juntos, sinergia].</summary>
        [JsonPropertyName("coPicked")] public List<JsonElement[]>? CoPicked { get; set; }
    }

    readonly Dictionary<(string Alias, int Augment), MayhemAugmentChampion> _rows = [];
    readonly Dictionary<string, List<int>> _byChampion = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(int, int), MayhemAugmentPair> _pairs = [];

    /// <summary>Cómo combinan dos aumentos, o null si mayhemmeta.com no lo enseña (solo lista las mejores y más comunes).</summary>
    public MayhemAugmentPair? Pair(int a, int b) => _pairs.GetValueOrDefault(a < b ? (a, b) : (b, a));

    public int PairCount => _pairs.Count;

    /// <summary>Id del aumento → descripción (inglés).</summary>
    public IReadOnlyDictionary<int, string> Descriptions { get; private init; } = new Dictionary<int, string>();

    public bool IsEmpty => Descriptions.Count == 0 && _rows.Count == 0 && _pairs.Count == 0;

    /// <summary>Cómo le va a ese campeón con ese aumento, o null si no hay datos.</summary>
    public MayhemAugmentChampion? For(string alias, int augmentId) => _rows.GetValueOrDefault((alias.ToLowerInvariant(), augmentId));

    /// <summary>
    /// Lo elegido que es un aumento con ese campeón frente a los demás de su rareza (<paramref name="peers"/>):
    /// 0 = muy poco elegido … 4 = muy elegido, por su puesto en el pick rate. Null si el aumento
    /// tiene menos de 30 partidas o hay menos de 8 aumentos de esa rareza con datos para comparar.
    /// </summary>
    public int? Popularity(string alias, int augmentId, IEnumerable<int> peers)
    {
        if (For(alias, augmentId) is not { Games: >= 30 } mine) return null;
        var picks = peers.Distinct().Select(id => For(alias, id)?.PickRate).OfType<double>().ToList();
        if (picks.Count < 8) return null;
        double rank = (double)picks.Count(p => p < mine.PickRate) / picks.Count;
        return rank >= 0.9 ? 4 : rank >= 0.7 ? 3 : rank >= 0.3 ? 2 : rank >= 0.1 ? 1 : 0;
    }

    /// <summary>Texto corto para la carta; null en el término medio (2), que no aporta nada.</summary>
    public static string? PopularityLabel(int level) => level switch
    {
        4 => Lang.L("Muy elegido", "Very popular"),
        3 => Lang.L("Elegido", "Popular"),
        1 => Lang.L("Poco elegido", "Unpopular"),
        0 => Lang.L("Casi nadie", "Rarely picked"),
        _ => null,
    };

    /// <summary>
    /// Partidas "virtuales" con las que pesa la media del aumento en todos los campeones frente a lo de un campeón.
    /// Medido el 2026-10-27 con estos datos: tomando las combinaciones con 800+ partidas como verdad y simulando
    /// 30-300 partidas, encoger hacia la media del aumento con ~600 partidas falla un 15-20 % menos que encoger hacia 0
    /// con 150 (lo de antes). Con unos cientos de partidas, lo de un campeón
    /// es sobre todo azar (las dos webs coincidían solo 0,44) y lo que más pesa es lo bueno que es el aumento en general.
    /// </summary>
    public const double ChampionShrinkGames = 600;
    /// <summary>La media de un aumento poco jugado también se encoge hacia 0 (ruido / varianza entre aumentos).</summary>
    const double AugmentShrinkGames = 1250;
    readonly Dictionary<int, (double Sum, double Games)> _augmentTotals = [];

    /// <summary>
    /// Cuánto sube o baja un aumento el % de victorias, de media con todos los campeones (en puntos), sin contar al
    /// campeón <paramref name="exceptAlias"/> (para no contar dos veces sus propias partidas). 0 si no hay datos.
    /// </summary>
    public double AugmentMean(int augmentId, string? exceptAlias = null)
    {
        if (!_augmentTotals.TryGetValue(augmentId, out var t)) return 0;
        if (exceptAlias is not null && For(exceptAlias, augmentId) is { } own)
            t = (t.Sum - (own.WinRate - own.ChampionWinRate) * own.Games, t.Games - own.Games);
        return t.Games <= 0 ? 0 : t.Sum / (t.Games + AugmentShrinkGames);
    }

    /// <summary>¿Hay datos de este aumento con algún campeón?</summary>
    public bool HasAugment(int augmentId) => _augmentTotals.ContainsKey(augmentId);

    /// <summary>Aumentos con datos para ese campeón.</summary>
    public IReadOnlyList<int> AugmentsFor(string alias) => _byChampion.GetValueOrDefault(alias) ?? [];

    public static MayhemAugments Load(string? path = null)
    {
        path ??= DataFiles.Resolve("mayhem-augments.json");
        FileShape? file = null;
        try
        {
            if (File.Exists(path)) file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* a medio escribir: se ignora */ }

        var result = new MayhemAugments
        {
            Descriptions = (file?.Augments ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Description))
                                                 .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Description!),
        };
        foreach (var a in file?.Augments ?? [])
            foreach (var row in a.Champions ?? [])
            {
                // Una fila rara (formato antiguo, dato vacío) se salta sin tirar el resto.
                if (row.Length < 5 || row[0].ValueKind != JsonValueKind.String || !row[1].TryGetInt32(out var games)
                    || !row[2].TryGetDouble(out var wr) || !row[3].TryGetDouble(out var pick) || !row[4].TryGetDouble(out var baseline)) continue;
                var alias = row[0].GetString()!;
                if (result._rows.TryAdd((alias.ToLowerInvariant(), a.Id), new MayhemAugmentChampion(games, wr, pick, baseline)))
                {
                    if (!result._byChampion.TryGetValue(alias, out var list)) result._byChampion[alias] = list = [];
                    list.Add(a.Id);
                    var total = result._augmentTotals.GetValueOrDefault(a.Id);
                    result._augmentTotals[a.Id] = (total.Sum + (wr - baseline) * games, total.Games + games);
                }
            }
        foreach (var a in file?.Augments ?? [])
            foreach (var row in a.CoPicked ?? [])
            {
                if (row.Length < 4 || !row[0].TryGetInt32(out var other) || !row[1].TryGetInt32(out var together)
                    || !row[2].TryGetDouble(out var wr) || row[3].ValueKind != JsonValueKind.Number || !row[3].TryGetDouble(out var synergy)) continue;
                // Cada pareja sale en la página de los dos aumentos: nos quedamos con la que tiene más partidas.
                var key = a.Id < other ? (a.Id, other) : (other, a.Id);
                if (!result._pairs.TryGetValue(key, out var prev) || together > prev.Games)
                    result._pairs[key] = new MayhemAugmentPair(together, wr, synergy);
            }
        return result;
    }
}
