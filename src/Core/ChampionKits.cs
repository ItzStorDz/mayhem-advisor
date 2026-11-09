using System.Text.Json;
using System.Text.Json.Serialization;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Core;

/// <summary>
/// Para qué sirve el kit de un campeón (<c>campeones-kit.json</c>, herramientas/actualizar-kits.mjs, datos del cliente
/// vía CommunityDragon). Las habilidades van como letras: P (pasiva), Q, W, E, R.
/// </summary>
/// <param name="Ratios">Cuánto escala su daño con cada estadística ("ap", "ad", "bonusAd", "bonusHealth"…), sumando sus habilidades.</param>
/// <param name="OnHit">Habilidades que aplican efectos al impactar o hacen daño al impactar (Q de Ezreal, W de Kog'Maw…).</param>
/// <param name="Stacks">Cargas que crecen toda la partida sin tope (Q de Nasus, pasiva de Veigar…).</param>
/// <param name="Late">1 normal, 2 escala bien en partida larga, 3 escala sin límite (cargas permanentes).</param>
public sealed record ChampionKit(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attack")] string Attack,
    [property: JsonPropertyName("damageType")] string DamageType,
    [property: JsonPropertyName("ratios")] Dictionary<string, double>? Ratios,
    [property: JsonPropertyName("onHit")] string[]? OnHit,
    [property: JsonPropertyName("attackSpeed")] string[]? AttackSpeed,
    [property: JsonPropertyName("crit")] string[]? Crit,
    [property: JsonPropertyName("heal")] string[]? Heal,
    [property: JsonPropertyName("shield")] string[]? Shield,
    [property: JsonPropertyName("stacks")] string[]? Stacks,
    [property: JsonPropertyName("late")] int Late)
{
    public double Ratio(string stat) => Ratios?.GetValueOrDefault(stat) ?? 0;
    public bool HasOnHit => OnHit is { Length: > 0 };
    public bool HasAttackSpeed => AttackSpeed is { Length: > 0 };
    public bool HasCrit => Crit is { Length: > 0 };
    public bool Heals => Heal is { Length: > 0 };
    public bool Shields => Shield is { Length: > 0 };

    /// <summary>"Q y E", "la pasiva"… para los textos.</summary>
    public static string Keys(string[]? keys) => keys is not { Length: > 0 } ? ""
        : string.Join(T(" y ", " and ", " e ", " et ", " und ", " và "), keys.Select(k => k == "P" ? T("la pasiva", "the passive", "a passiva", "le passif", "die Passive", "nội tại") : k));

    /// <summary>
    /// Qué estadísticas aprovecha su kit, de más a menos (ratios de daño), para el texto de la página del campeón:
    /// "AD adicional, AP". Solo lo que suma al menos medio ratio.
    /// </summary>
    public IReadOnlyList<string> ScalesWith() =>
        (Ratios ?? []).GroupBy(kv => kv.Key switch
            {
                "ad" or "bonusAd" => "AD", "ap" => "AP", "health" or "bonusHealth" => T("vida", "health", "vida", "PV", "Leben", "máu"),
                "armor" or "bonusArmor" => T("armadura", "armor", "armadura", "armure", "Rüstung", "giáp"), "mr" or "bonusMr" => T("resist. mágica", "magic resist", "resist. mágica", "résist. magique", "Magieresistenz", "kháng phép"),
                "crit" => T("crítico", "crit", "crítico", "critique", "Krit", "chí mạng"), "as" => T("vel. ataque", "attack speed", "vel. de ataque", "vit. d'attaque", "Angriffstempo", "tốc độ đánh"), _ => kv.Key,
            })
            .Select(g => (Name: g.Key, Sum: g.Sum(kv => kv.Value)))
            .Where(x => x.Sum >= 0.5 || x.Name is "vida" or "health" or "armadura" or "armor" or "resist. mágica" or "magic resist" && x.Sum >= 0.05)
            .OrderByDescending(x => x.Sum).Select(x => x.Name).ToList();
}

public sealed class ChampionKits
{
    sealed class FileShape
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("champions")] public Dictionary<string, ChampionKit>? Champions { get; set; }
    }

    readonly Dictionary<string, ChampionKit> _kits = new(StringComparer.OrdinalIgnoreCase);
    public string Version { get; private init; } = "";
    public bool IsEmpty => _kits.Count == 0;
    public int Count => _kits.Count;

    public ChampionKit? For(string alias) => _kits.GetValueOrDefault(alias);

    public static ChampionKits Load(string? path)
    {
        FileShape? data = null;
        try
        {
            if (path is not null && File.Exists(path)) data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* a medio escribir: se ignora */ }
        var result = new ChampionKits { Version = data?.Version ?? "" };
        foreach (var (alias, kit) in data?.Champions ?? []) result._kits[alias] = kit;
        return result;
    }
}
