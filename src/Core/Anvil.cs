using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>Una opción de Yunque puntuada: cuanto más alta la puntuación, más te conviene.</summary>
public sealed record AnvilRating(Augment Fragment, double Score, string Reason);

/// <summary>
/// Fragmentos de los Yunques de Estadísticas. Tu historial no guarda qué fragmento eliges, así que no hay
/// % de victorias: se recomiendan por reglas claras (tipo de campeón, equipo enemigo, tus aumentos y objetos).
/// </summary>
public static class Anvil
{
    public const string Rarity = "anvil";

    /// <summary>Qué da cada fragmento (por el nombre interno del juego: "ad", "mpen", "hybrid3"...).</summary>
    public static string[] TagsFor(string key) => key switch
    {
        "ad" => ["AD"],
        "ap" => ["AP"],
        "ah" => ["Haste"],
        "ar" => ["Armor"],
        "mr" => ["MR"],
        "as" => ["AS"],
        "critchance" or "critdamage" => ["Crit"],
        "hp" => ["Health"],
        "hybrid1" => ["AS", "Haste"],
        "hybrid2" => ["AD", "AP"],
        "hybrid3" => ["Armor", "MR"],
        "lethality" or "armorpen" => ["Lethality"],
        "mpen" => ["MagicPen"],
        "ms" => ["MoveSpeed"],
        "omnivamp" => ["Sustain"],
        "spirit" => ["HealShield"],
        "tenacity" => ["Tenacity"],
        "gold" or "goldroundloss" => ["Gold"],
        "pristine" => ["Multiplier"],
        _ => ["Random"],
    };

    /// <summary>
    /// Nombre en inglés de cada fragmento (tabla de textos del juego en inglés, en_us). Va escrito aquí para no descargar
    /// otra tabla de 32 MB solo por 23 nombres; si Riot añade un fragmento nuevo, sale solo en español hasta añadirlo.
    /// </summary>
    public static string? EnglishName(string key) => key switch
    {
        "ad" => "Attack Damage Shard", "ah" => "Ability Haste Shard", "ap" => "Ability Power Shard", "ar" => "Armor Shard",
        "armorpen" => "Armor Penetration Shard", "as" => "Attack Speed Shard", "critchance" => "Critical Strike Shard",
        "critdamage" => "Critical Damage Shard", "gold" => "Fortune Shard", "goldroundloss" => "Care Package", "hp" => "Health Shard",
        "hybrid1" => "Swiftness Shard", "hybrid2" => "Might Shard", "hybrid3" => "Unbreakable Shard", "lethality" => "Lethality Shard",
        "mpen" => "Magic Penetration Shard", "mr" => "Magic Resistance Shard", "ms" => "Move Speed Shard", "omnivamp" => "Omnivamp Shard",
        "pristine" => "Shardholder Value Shard", "random" => "Two Random Shards", "spirit" => "Heal & Shield Power Shard",
        "tenacity" => "Tenacity Shard",
        _ => null,
    };

    public static string Label(string tag) => tag switch
    {
        "HealShield" => L("curaciones y escudos", "heals and shields"),
        "Tenacity" => L("tenacidad", "tenacity"),
        "Gold" => L("oro", "gold"),
        "Multiplier" => L("mejora tus otros fragmentos", "boosts your other shards"),
        "Random" => L("aleatorio", "random"),
        _ => StatTags.Label(tag),
    };

    /// <summary>Ordena los fragmentos en pantalla de mejor a peor para tu campeón en esta partida.</summary>
    public static List<AnvilRating> Rank(IEnumerable<Augment> fragments, string champion, StatsStore stats,
                                          EnemyProfile enemies, IReadOnlyList<Augment> myAugments, IReadOnlyList<int> myItems)
    {
        var data = stats.Data;
        var roles = data.Champion(champion)?.Roles ?? [];
        bool magic = new Advisor(stats).PlaysMagic(champion, myItems, myAugments); // tu build de esta partida (Shaco AD ≠ Shaco AP)
        bool marksman = roles.FirstOrDefault() == "marksman";
        bool frontline = roles.FirstOrDefault() is "tank" or "fighter";
        bool tanky = enemies.Tanks >= 2;

        // Estadísticas que ya potencian tus aumentos y tus objetos: los fragmentos que las suman encajan mejor.
        var augmentTags = myAugments.SelectMany(a => a.StatList).Select(t => t == "Adaptive" ? (magic ? "AP" : "AD") : t).ToHashSet();
        var itemCats = myItems.Select(data.Item).OfType<ItemInfo>().SelectMany(i => i.Categories).ToHashSet();

        return fragments.Select(f =>
        {
            double score = 0;
            var why = new List<string>();
            void Add(bool when, double pts, string reason) { if (when) { score += pts; why.Add(reason); } }

            foreach (var t in f.StatList)
            {
                Add(t == "AP" && magic, 3, L("tu daño es mágico", "your damage is magic"));
                Add(t == "AD" && !magic, 3, L("tu daño es físico", "your damage is physical"));
                Add(t == "MagicPen" && magic, tanky || enemies.MagicResistItems >= 3 ? 3 : 2, tanky ? L("penetración contra tanques", "penetration vs tanks") : L("más daño mágico", "more magic damage"));
                Add(t == "Lethality" && !magic, tanky || enemies.ArmorItems >= 3 ? 3 : 2, tanky ? L("penetración contra tanques", "penetration vs tanks") : L("más daño físico", "more physical damage"));
                Add(t == "Crit" && marksman && !magic, 2.5, L("eres tirador", "you are a marksman"));
                Add(t == "AS" && marksman, 2.5, L("eres tirador", "you are a marksman"));
                Add(t == "Haste" && !marksman, 1.5, L("más habilidades", "more abilities"));
                Add(t == "Armor" && enemies.Mix == DamageMix.Physical, frontline ? 3 : 2, L("mucho daño físico enemigo", "lots of enemy physical damage"));
                Add(t == "MR" && enemies.Mix == DamageMix.Magic, frontline ? 3 : 2, L("mucho daño mágico enemigo", "lots of enemy magic damage"));
                Add(t == "Health", frontline ? 2.5 : 1, frontline ? L("aguantas en primera línea", "you tank the frontline") : L("más vida", "more health"));
                Add(t == "Sustain", frontline || marksman ? 1.5 : 1, L("curación", "healing"));
                Add(t == "Multiplier", 2, L("mejora todos tus fragmentos", "boosts all your shards"));
                Add(t == "Gold", 1, L("oro para comprar antes", "gold to buy sooner"));
                Add(t is "MoveSpeed" or "Tenacity" or "HealShield", 0.5, Label(t));

                // Sinergias con lo que ya llevas.
                Add(augmentTags.Contains(t), 1.5, L("va con tus aumentos", "fits your augments"));
                Add(StatTags.All.TryGetValue(t, out var info) && info.ItemCategories.Any(itemCats.Contains), 1, L("va con tus objetos", "fits your items"));
                // Estadísticas que tu campeón casi no aprovecha.
                Add(t == "AP" && !magic, -2, "");
                Add(t == "AD" && magic, -2, "");
                Add(t == "Crit" && magic, -2, "");
            }

            var reason = why.Where(r => r != "").Distinct().Take(2).ToList();
            return new AnvilRating(f, score, reason.Count > 0 ? string.Join(" · ", reason) : L("poco útil para ti", "not very useful for you"));
        })
        .OrderByDescending(r => r.Score)
        .ToList();
    }
}
