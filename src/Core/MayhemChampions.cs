using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>
/// Un campeón con sus estadísticas reales de ARAM Mayhem (la cola 2400 que recolecta la app),
/// publicadas por mayhemmeta.com. <see cref="Alias"/> es el de CommunityDragon ("Annie", "MonkeyKing").
/// </summary>
public sealed record MayhemChampion(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("championId")] int ChampionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] string Tier,
    // Todo puede venir vacío (null) si el script no pudo leer esa celda: así un dato raro no tira el archivo entero.
    [property: JsonPropertyName("winRate")] double? WinRate,
    [property: JsonPropertyName("pickRate")] double? PickRate,
    [property: JsonPropertyName("games")] int? Games,
    [property: JsonPropertyName("kills")] double? Kills,
    [property: JsonPropertyName("deaths")] double? Deaths,
    [property: JsonPropertyName("assists")] double? Assists);

/// <summary>
/// Estadísticas de campeones para Mayhem, de <c>mayhem-champions.json</c>
/// (lo actualiza <c>herramientas/actualizar-mayhem.mjs</c>).
///
/// Solo ARAM Mayhem (cola 2400). Es opcional: si el archivo no existe, <see cref="Load"/>
/// devuelve un catálogo vacío.
/// </summary>
public sealed class MayhemChampions
{
    sealed class FileShape
    {
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("champions")] public List<MayhemChampion> Champions { get; set; } = [];
    }

    public string Source { get; init; } = "";
    public string Patch { get; init; } = "";
    public DateTimeOffset FetchedAt { get; init; }
    /// <summary>Ordenados por win rate, de mejor a peor.</summary>
    public IReadOnlyList<MayhemChampion> Champions { get; init; } = [];

    readonly Dictionary<string, MayhemChampion> _byAlias = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _byAlias.Count == 0;

    /// <summary>Datos de un campeón por alias ("Annie", "MonkeyKing"...), o null si no está.</summary>
    public MayhemChampion? For(string alias) => _byAlias.GetValueOrDefault(alias);

    /// <summary>Posición por win rate (1 = mejor), o 0 si no está.</summary>
    public int RankOf(string alias)
    {
        for (int i = 0; i < Champions.Count; i++)
            if (string.Equals(Champions[i].Alias, alias, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    public static MayhemChampions Load(string? path = null)
    {
        path ??= DataFiles.Resolve("mayhem-champions.json");
        FileShape? file = null;
        try
        {
            if (File.Exists(path))
                file = JsonSerializer.Deserialize<FileShape>(System.IO.File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* archivo a medio escribir: se ignora */ }

        var catalog = new MayhemChampions
        {
            Source = "mayhemmeta.com",
            Patch = file?.Patch ?? "",
            FetchedAt = file?.FetchedAt ?? default,
            // Un campeón sin alias (el script no lo reconoció) o sin % se salta en vez de tumbar la carga.
            Champions = file?.Champions.Where(c => c.Alias is not null && c.WinRate is not null).ToList() ?? [],
        };
        foreach (var c in catalog.Champions) catalog._byAlias[c.Alias] = c;
        return catalog;
    }
}
