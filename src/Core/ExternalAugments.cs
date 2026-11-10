using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Cómo le va a un campeón con un aumento según otra web (WinRate y ChampionWinRate en 0-100).</summary>
public sealed record ExternalAugmentChampion(int Games, double WinRate, double ChampionWinRate)
{
    /// <summary>Cuánto sube o baja el % del campeón con este aumento, en puntos.</summary>
    public double Delta => WinRate - ChampionWinRate;
}

/// <summary>
/// % de victorias de cada campeón con cada aumento según otra web con muchas más partidas que mayhemmeta:
/// <c>aramhex-augments.json</c> (AramHex, servidor chino, ~5 millones de partidas por parche; herramientas/actualizar-aramhex.mjs)
/// o <c>aramkit-augments.json</c> (aramkit.com; herramientas/actualizar-aramkit.mjs). Mismo formato los dos.
/// </summary>
public sealed class ExternalAugments
{
    /// <summary>
    /// Punto de partida de la nota: este peso de la otra web + el resto de la media del aumento con todos los campeones
    /// (mayhemmeta), y luego las partidas del campeón en mayhemmeta pesan contra <see cref="ShrinkGames"/>.
    /// Medido el 2026-10-27 con 2.112 combinaciones de mayhemmeta con 800+ partidas como verdad (error con 100 partidas
    /// en mayhemmeta / sin datos): media sola 2,05 / 2,24 · aramkit 1,50 / 1,57 · AramHex 1,43 / 1,49.
    /// Vuelto a medir el 2026-11-10 con AramHex de 19,8 millones de partidas y 4.492 filas de mayhemmeta (300+ partidas,
    /// validación cruzada por campeón): los pesos que salen son 0,72-0,85 y 0,25, o sea los mismos; aramkit encima no suma
    /// (peso negativo); y la banda «casual» de AramHex acierta más que el total (1,651 → 1,633; con 800+, 1,544 → 1,512),
    /// así que herramientas/actualizar-aramhex.mjs baja esa banda. Separar «media del aumento» y «desvío del campeón» o
    /// suavizar AramHex (k = 300-10.000) no mejora más de un 1 %: el resto del error es ruido de la propia verdad.
    /// </summary>
    public const double Weight = 0.75;
    public const double ShrinkGames = 1000;
    const int MinGames = 100;

    /// <summary>
    /// Aumentos cuyo % no sirve: "Soy el Rey" se cambia por otro al completar su misión, así que quien lo tiene al acabar
    /// es quien no la completó (-15 puntos de media en aramkit); 3176 es una marca repetida de Pentakill. Lista de AramHex.
    /// </summary>
    public static readonly IReadOnlySet<int> Biased = new HashSet<int> { 1337, 3176 };

    sealed class FileShape
    {
        [JsonPropertyName("patch")] public string? Patch { get; set; }
        [JsonPropertyName("champions")] public Dictionary<string, Champion>? Champions { get; set; }
    }

    sealed class Champion
    {
        [JsonPropertyName("winRate")] public double WinRate { get; set; }
        /// <summary>Filas [aumento, partidas, % de victorias].</summary>
        [JsonPropertyName("augments")] public List<JsonElement[]>? Augments { get; set; }
    }

    readonly Dictionary<(string Alias, int Augment), ExternalAugmentChampion> _rows = [];
    readonly Dictionary<string, List<int>> _byChampion = new(StringComparer.OrdinalIgnoreCase);

    public string? Patch { get; private init; }
    public int ChampionCount { get; private init; }
    public bool IsEmpty => _rows.Count == 0;
    public int RowCount => _rows.Count;

    /// <summary>Cómo le va a ese campeón con ese aumento, o null si no hay datos (o muy pocas partidas).</summary>
    public ExternalAugmentChampion? For(string alias, int augmentId) => _rows.GetValueOrDefault((alias.ToLowerInvariant(), augmentId));

    /// <summary>Aumentos con datos para ese campeón.</summary>
    public IReadOnlyList<int> AugmentsFor(string alias) => _byChampion.GetValueOrDefault(alias) ?? [];

    public static ExternalAugments Load(string path)
    {
        FileShape? data = null;
        try
        {
            if (File.Exists(path)) data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* a medio escribir: se ignora */ }

        var result = new ExternalAugments { Patch = data?.Patch, ChampionCount = data?.Champions?.Count ?? 0 };
        foreach (var (alias, champ) in data?.Champions ?? [])
            foreach (var row in champ.Augments ?? [])
            {
                if (row.Length < 3 || !row[0].TryGetInt32(out var id) || !row[1].TryGetInt32(out var games)
                    || !row[2].TryGetDouble(out var wr) || games < MinGames || Biased.Contains(id)) continue;
                if (!result._rows.TryAdd((alias.ToLowerInvariant(), id), new ExternalAugmentChampion(games, wr, champ.WinRate))) continue;
                if (!result._byChampion.TryGetValue(alias, out var list)) result._byChampion[alias] = list = [];
                list.Add(id);
            }
        return result;
    }
}
