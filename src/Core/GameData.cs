using System.Text.Json;
using System.Text.RegularExpressions;

namespace MayhemAdvisor.Core;

/// <param name="From">Componentes de la receta (ids).</param>
/// <param name="Counters">Lo que contrarresta o da, sacado de su descripción (ver <see cref="ItemCounters"/>).</param>
public sealed record ItemInfo(int Id, string Name, int PriceTotal, string[] Categories, bool AppliesWounds, bool IsFinal, bool InStore, string Icon,
    int[]? From = null, string[]? Counters = null, string? NameEn = null)
{
    /// <summary>El nombre en el idioma elegido (ver <see cref="Lang"/>).</summary>
    public string DisplayName => Lang.English && !string.IsNullOrEmpty(NameEn) ? NameEn : Name;
    public bool Has(string category) => Categories.Contains(category);
    public bool Is(string counter) => Counters?.Contains(counter) == true;
    public bool IsBoots => Has("Boots") && PriceTotal >= 900;
    public bool IsDefensive => Has("Armor") || Has("SpellBlock") || Has("Health");
    public bool IsOffensive => Has("Damage") || Has("SpellDamage") || Has("CriticalStrike") || Has("AttackSpeed");
}

public sealed record ChampionInfo(int Id, string Alias, string Name, string[] Roles, string Icon, string? NameEn = null)
{
    public string DisplayName => Lang.English && !string.IsNullOrEmpty(NameEn) ? NameEn : Name;
}

/// <summary>
/// Objetos y campeones del juego (nombres en español), de CommunityDragon. Se guarda en caché un día.
/// </summary>
public sealed class GameData
{
    const string Base = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global";

    public Dictionary<int, ItemInfo> Items { get; init; } = [];
    public Dictionary<string, ChampionInfo> Champions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, ChampionInfo> ChampionsById { get; init; } = [];

    public ItemInfo? Item(int id) => Items.GetValueOrDefault(id);
    public ChampionInfo? Champion(string alias) => Champions.GetValueOrDefault(alias);
    /// <summary>Nombre del campeón en el idioma elegido.</summary>
    public string ChampionName(string alias) => Champion(alias)?.DisplayName ?? alias;

    /// <summary>"/lol-game-data/assets/ASSETS/Items/Icons2D/X.png" → URL pública de la imagen.</summary>
    public static string IconUrl(string gamePath) =>
        string.IsNullOrEmpty(gamePath) ? "" : $"{Base}/default/{gamePath.Replace("/lol-game-data/assets/", "").TrimStart('/').ToLowerInvariant()}";

    /// <summary>
    /// Lo que te cuesta de verdad un objeto: el precio total menos los componentes de su receta que ya
    /// llevas (cada uno se usa una sola vez), igual que calcula la tienda del juego.
    /// </summary>
    public int RemainingCost(ItemInfo item, IReadOnlyList<int> inventory)
    {
        var bag = inventory.ToList();
        int Owned(ItemInfo it)
        {
            int value = 0;
            foreach (var id in it.From ?? [])
            {
                if (Item(id) is not { } part) continue;
                if (bag.Remove(id)) value += part.PriceTotal;
                else value += Owned(part);
            }
            return value;
        }
        return Math.Max(0, item.PriceTotal - Owned(item));
    }

    /// <summary>
    /// La pieza más cara de la receta de <paramref name="item"/> que ya puedes comprar con <paramref name="gold"/> (sin las
    /// que llevas), o null si te llega para el objeto entero o para ninguna. Al morir con 1.158 de oro y «te faltan 1.007»
    /// no se sabía qué comprar ya; ahora: «Con tu oro ya puedes: Capa de Negatrón (900)» (2026-11-04).
    /// </summary>
    public ItemInfo? AffordablePart(ItemInfo item, IReadOnlyList<int> inventory, int gold)
    {
        if (RemainingCost(item, inventory) <= gold) return null;
        var bag = inventory.ToList();
        ItemInfo? best = null;
        int bestCost = 0;
        void Walk(ItemInfo it)
        {
            foreach (var id in it.From ?? [])
            {
                if (Item(id) is not { } part) continue;
                if (bag.Remove(id)) continue; // ya la llevas (con todo lo de debajo)
                int cost = RemainingCost(part, bag);
                if (cost > 0 && cost <= gold && cost > bestCost) { best = part; bestCost = cost; }
                Walk(part);
            }
        }
        Walk(item);
        return best;
    }

    sealed record Cache(List<ItemInfo> Items, List<ChampionInfo> Champions);

    public static async Task<GameData> LoadAsync(HttpClient http)
    {
        var path = Path.Combine(Paths.DataDir, "gamedata-v6.json");
        // Un archivo a medio escribir (apagón) no debe impedir que arranque la app: se lee como si no estuviera.
        static async Task<Cache?> Read(string file)
        {
            try { return JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(file)) is { Items.Count: > 0 } c ? c : null; }
            catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        }
        Cache? cache = null;
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1))
            cache = await Read(path);

        if (cache is null)
        {
            try
            {
                cache = await DownloadAsync(http);
                Directory.CreateDirectory(Paths.DataDir);
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(cache));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch when (File.Exists(path))
            {
                cache = await Read(path) ?? throw new InvalidDataException("gamedata-v6.json dañado y sin conexión");
            }
        }

        return new GameData
        {
            Items = cache.Items.ToDictionary(i => i.Id),
            Champions = new(cache.Champions.ToDictionary(c => c.Alias, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            ChampionsById = cache.Champions.ToDictionary(c => c.Id),
        };
    }

    static async Task<Cache> DownloadAsync(HttpClient http)
    {
        // Las descripciones en inglés nos dicen qué objetos aplican Heridas Graves ("Wounds").
        using var en = JsonDocument.Parse(await http.GetStringAsync($"{Base}/default/v1/items.json"));
        using var es = JsonDocument.Parse(await http.GetStringAsync($"{Base}/es_mx/v1/items.json"));
        var esNames = es.RootElement.EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32(), i => i.GetProperty("name").GetString() ?? "");

        var items = new List<ItemInfo>();
        foreach (var i in en.RootElement.EnumerateArray())
        {
            var id = i.GetProperty("id").GetInt32();
            var desc = i.GetProperty("description").GetString() ?? "";
            var cats = i.GetProperty("categories").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            int price = i.GetProperty("priceTotal").GetInt32();
            // Las botas normales (nivel 2) "mejoran" a botas de la Grieta, pero en ARAM son el último paso: cuentan como finales.
            bool isBoots = cats.Contains("Boots") && price >= 900;
            bool isFinal = (i.GetProperty("to").GetArrayLength() == 0 || isBoots) && !cats.Contains("Consumable") && !cats.Contains("Trinket")
                           && (price >= 2000 || isBoots);
            items.Add(new ItemInfo(id, (esNames.GetValueOrDefault(id) is { Length: > 0 } n ? n : i.GetProperty("name").GetString() ?? "").Trim(),
                price, cats, desc.Contains("Grievous", StringComparison.OrdinalIgnoreCase) || desc.Contains("<keyword>Wounds"), isFinal,
                i.GetProperty("inStore").GetBoolean(), IconUrl(i.GetProperty("iconPath").GetString() ?? ""),
                i.GetProperty("from").EnumerateArray().Select(f => f.GetInt32()).ToArray(), ItemCounters.FromDescription(desc),
                (i.GetProperty("name").GetString() ?? "").Trim()));
        }

        using var champs = JsonDocument.Parse(await http.GetStringAsync($"{Base}/es_mx/v1/champion-summary.json"));
        using var champsEn = JsonDocument.Parse(await http.GetStringAsync($"{Base}/default/v1/champion-summary.json"));
        var enChamp = champsEn.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32(), c => c.GetProperty("name").GetString() ?? "");
        var champions = champs.RootElement.EnumerateArray()
            .Where(c => c.GetProperty("id").GetInt32() > 0)
            .Select(c => new ChampionInfo(c.GetProperty("id").GetInt32(), c.GetProperty("alias").GetString()!, c.GetProperty("name").GetString()!,
                c.GetProperty("roles").EnumerateArray().Select(r => r.GetString() ?? "").ToArray(),
                $"{Base}/default/v1/champion-icons/{c.GetProperty("id").GetInt32()}.png", enChamp.GetValueOrDefault(c.GetProperty("id").GetInt32())))
            .ToList();

        return new Cache(items, champions);
    }
}

/// <summary>Etiquetas de "para qué sirve contra el enemigo", leídas de la descripción en inglés del objeto.</summary>
public static class ItemCounters
{
    public const string AntiCrit = "AntiCrit", AntiAttackSpeed = "AntiAttackSpeed", AntiShield = "AntiShield",
        PercentHealth = "PercentHealth", GivesShield = "GivesShield", Stacks = "Stacks";

    static readonly (string Tag, Regex Pattern)[] Rules =
    [
        (AntiCrit, new(@"less damage from Critical Strike", RegexOptions.IgnoreCase)),
        (AntiAttackSpeed, new(@"Reduce the Attack Speed of", RegexOptions.IgnoreCase)),
        (AntiShield, new(@"Shield Reaver|reduces? Shields", RegexOptions.IgnoreCase)),
        (PercentHealth, new(@"max(imum)? Health (magic |physical |true )?damage|current Health as bonus|damage .{0,40}based on their (bonus|maximum) Health|reduces the target's Armor", RegexOptions.IgnoreCase)),
        (Stacks, new(@"permanent|Glory", RegexOptions.IgnoreCase)), // acumula cargas para siempre: venderlo las pierde
        (GivesShield, new(@"grants? (you )?an? (decaying )?(magic damage )?Shield|gain an? (magic )?shield", RegexOptions.IgnoreCase)),
    ];

    public static string[] FromDescription(string html)
    {
        var text = Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ");
        return Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Tag).ToArray();
    }
}
