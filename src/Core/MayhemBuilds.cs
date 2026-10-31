using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Un objeto (o aumento) con su pick rate y win rate reales en Mayhem.</summary>
public sealed record MayhemItemStat(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pickRate")] double? PickRate,
    [property: JsonPropertyName("winRate")] double? WinRate,
    [property: JsonPropertyName("delta")] double? Delta);

/// <summary>Una combinación de hechizos de invocador ("Flash + Mark") y su rendimiento.</summary>
public sealed record MayhemSpellCombo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("spells")] List<string> Spells,
    [property: JsonPropertyName("pickRate")] double? PickRate,
    [property: JsonPropertyName("winRate")] double? WinRate,
    [property: JsonPropertyName("delta")] double? Delta);

/// <summary>Build y estadísticas de un campeón en ARAM Mayhem (patch actual).</summary>
public sealed record MayhemChampionBuild(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("championId")] int ChampionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("winRate")] double? WinRate,
    [property: JsonPropertyName("games")] int? Games,
    [property: JsonPropertyName("spells")] List<MayhemSpellCombo> Spells,
    [property: JsonPropertyName("boots")] List<MayhemItemStat> Boots,
    [property: JsonPropertyName("itemsCommon")] List<MayhemItemStat> ItemsCommon,
    [property: JsonPropertyName("itemsBest")] List<MayhemItemStat> ItemsBest,
    [property: JsonPropertyName("augmentsBest")] List<MayhemItemStat> AugmentsBest,
    [property: JsonPropertyName("augmentsPopular")] List<MayhemItemStat> AugmentsPopular)
{
    /// <summary>Los objetos que más suben el win rate (los "best items").</summary>
    public IReadOnlyList<MayhemItemStat> CoreItems =>
        (ItemsBest.Count > 0 ? ItemsBest : ItemsCommon).Take(4).ToList();

    /// <summary>Botas más usadas por win rate.</summary>
    public MayhemItemStat? BestBoots =>
        Boots.Where(b => b.WinRate.HasValue && b.PickRate >= 3).OrderByDescending(b => b.WinRate!.Value).FirstOrDefault()
        ?? Boots.FirstOrDefault();

    /// <summary>Datos de un aumento con este campeón (está entre sus mejores o más elegidos), o null.</summary>
    public MayhemItemStat? Augment(int id) =>
        AugmentsBest.FirstOrDefault(a => a.Id == id) ?? AugmentsPopular.FirstOrDefault(a => a.Id == id);

    /// <summary>Datos de un objeto (o botas) con este campeón, o null si no sale en sus tablas.</summary>
    public MayhemItemStat? Item(int id) =>
        ItemsBest.FirstOrDefault(i => i.Id == id) ?? ItemsCommon.FirstOrDefault(i => i.Id == id) ?? Boots.FirstOrDefault(i => i.Id == id);

    /// <summary>Todos los objetos y botas de sus tablas, sin repetir.</summary>
    public IEnumerable<MayhemItemStat> AllItems => ItemsBest.Concat(ItemsCommon).Concat(Boots).DistinctBy(i => i.Id);

    /// <summary>Todos los aumentos de sus tablas, sin repetir.</summary>
    public IEnumerable<MayhemItemStat> AllAugments => AugmentsBest.Concat(AugmentsPopular).DistinctBy(a => a.Id);

    /// <summary>Partidas aproximadas con esa elección: partidas del campeón × pick rate.</summary>
    public int GamesWith(MayhemItemStat stat) => (int)Math.Round((Games ?? 0) * (stat.PickRate ?? 0) / 100);
}

/// <summary>
/// Builds por campeón de Mayhem, de <c>mayhem-champion-builds.json</c>
/// (lo actualiza <c>herramientas/actualizar-mayhem.mjs</c>).
///
/// Solo ARAM Mayhem (cola 2400). Los ids de objetos y aumentos son los de Riot, los mismos
/// que ya usa la app.
/// </summary>
public sealed class MayhemBuilds
{
    sealed class FileShape
    {
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("champions")] public List<MayhemChampionBuild> Champions { get; set; } = [];
    }

    public string Patch { get; init; } = "";
    public DateTimeOffset FetchedAt { get; init; }
    public IReadOnlyList<MayhemChampionBuild> Champions { get; init; } = [];

    readonly Dictionary<string, MayhemChampionBuild> _byAlias = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _byAlias.Count == 0;

    /// <summary>Nombres en inglés de todos los objetos que salen en las builds (para leer descripciones en inglés).</summary>
    public IEnumerable<(int Id, string Name)> ItemNames =>
        Champions.SelectMany(c => c.AllItems).Where(i => i.Kind == "items").Select(i => (i.Id, i.Name)).DistinctBy(n => n.Id);

    /// <summary>Build de un campeón por alias ("Hecarim", "MonkeyKing"...), o null si no está.</summary>
    public MayhemChampionBuild? For(string alias) => _byAlias.GetValueOrDefault(alias);

    public static MayhemBuilds Load(string? path = null)
    {
        path ??= DataFiles.Resolve("mayhem-champion-builds.json");
        FileShape? file = null;
        try
        {
            if (File.Exists(path))
                file = JsonSerializer.Deserialize<FileShape>(System.IO.File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* archivo a medio escribir: se ignora */ }

        var builds = new MayhemBuilds
        {
            Patch = file?.Patch ?? "",
            FetchedAt = file?.FetchedAt ?? default,
            // Un campeón sin alias (el script no lo reconoció) se salta en vez de tumbar la carga.
            Champions = file?.Champions.Where(c => c.Alias is not null).ToList() ?? [],
        };
        foreach (var c in builds.Champions) builds._byAlias[c.Alias] = c;
        return builds;
    }
}
