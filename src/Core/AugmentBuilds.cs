using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>
/// Un estilo de build de aramkit ("utility" = apoyo, "ap", "ad", "tank"…): partidas, % de victorias (0-100) y en qué
/// parte de esas partidas sale cada objeto (0-1).
/// </summary>
public sealed record BuildStyle(string Key, int Games, double WinRate, IReadOnlyDictionary<int, double> Items);

/// <summary>
/// El estilo de build que conviene con tus aumentos. <paramref name="Usual"/> es el más jugado con el campeón;
/// <paramref name="Share"/>, la parte de las partidas con tus aumentos que lo juegan; <paramref name="WinRate"/> y
/// <paramref name="UsualWinRate"/>, cuánto ganan los dos con tus aumentos (0-100, sin suavizar, para el texto).
/// <paramref name="Items"/>: en qué parte de las partidas con ese estilo y tus aumentos sale cada objeto (0-1).
/// </summary>
/// <param name="FromItems">Si el estilo lo dicen tus objetos (no tus aumentos): los que lo deciden, para el texto ("" = aún sin objetos).</param>
/// <param name="Generic">El campeón no tiene ese estilo en sus datos: sale de la media de todos los campeones.</param>
public sealed record StylePick(string Key, string Usual, double Share, double WinRate, double UsualWinRate, int Games,
                               int AugmentId, IReadOnlyDictionary<int, double> Items, string? FromItems = null, bool Generic = false)
{
    public bool Changed => Key != Usual;
}

/// <summary>
/// Builds de aramkit.com por campeón y aumento (<c>aramkit-builds.json</c>, herramientas/actualizar-aramkit.mjs).
/// Con un aumento de daño, un encantador como Sona o Janna a veces gana más de AP que de apoyo: aquí se ve con qué
/// aumentos pasa y qué objetos compra la gente en ese estilo.
/// </summary>
public sealed class AugmentBuilds
{
    /// <summary>
    /// Partidas "de mentira" con las que cada estilo parte del % que tendría (su % normal con el campeón + lo que sube o
    /// baja el aumento): con pocas partidas no se cambia de estilo por azar. Medido el 2026-10-28 con las 11.972 parejas
    /// de estilos de aramkit: cuánto cambia de verdad la diferencia entre dos estilos según el aumento (unos 2 puntos)
    /// frente a lo que cambia por azar con n partidas → unas 1.200 partidas.
    /// </summary>
    public const double ShrinkGames = 1200;
    /// <summary>Un estilo que no es el habitual solo se recomienda si lo juega al menos esta parte de la gente con tus aumentos.</summary>
    public const double MinShare = 0.15;
    /// <summary>Si al menos esta parte de la gente con tus aumentos juega otro estilo (y no gana menos), se sigue a la mayoría.</summary>
    public const double MajorityShare = 0.5;
    /// <summary>Partidas mínimas del estilo con tus aumentos para cambiar de estilo.</summary>
    public const int MinGames = 800;

    sealed class FileShape
    {
        [JsonPropertyName("patch")] public string? Patch { get; set; }
        [JsonPropertyName("champions")] public Dictionary<string, Champion>? Champions { get; set; }
    }

    sealed class Champion
    {
        [JsonPropertyName("winRate")] public double WinRate { get; set; }
        [JsonPropertyName("styles")] public List<JsonElement[]>? Styles { get; set; }
        [JsonPropertyName("augments")] public Dictionary<string, AugmentRow>? Augments { get; set; }
    }

    sealed class AugmentRow
    {
        [JsonPropertyName("games")] public int Games { get; set; }
        [JsonPropertyName("styles")] public List<JsonElement[]>? Styles { get; set; }
    }

    sealed record ChampionStyles(double WinRate, IReadOnlyList<BuildStyle> Styles, Dictionary<int, IReadOnlyList<BuildStyle>> Augments);

    readonly Dictionary<string, ChampionStyles> _champions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cada estilo con todos los campeones juntos (por partidas): para el que no tiene ese estilo en sus datos.</summary>
    readonly Dictionary<string, BuildStyle> _generic = [];

    public string? Patch { get; private init; }
    public bool IsEmpty => _champions.Count == 0;
    public int ChampionCount => _champions.Count;
    public int RowCount => _champions.Values.Sum(c => c.Augments.Count);

    /// <summary>Estilos del campeón con cualquier aumento, el más jugado primero (vacío si no hay datos).</summary>
    public IReadOnlyList<BuildStyle> Styles(string alias) => _champions.GetValueOrDefault(alias)?.Styles ?? [];

    /// <summary>Estilos del campeón con ese aumento, o null si hay pocas partidas.</summary>
    public IReadOnlyList<BuildStyle>? Styles(string alias, int augmentId) =>
        _champions.GetValueOrDefault(alias)?.Augments.GetValueOrDefault(augmentId);

    /// <summary>
    /// El estilo que más gana con tus aumentos (con <see cref="ShrinkGames"/>), entre el habitual y los que juega al
    /// menos <see cref="MinShare"/> de la gente. Null si no hay datos del campeón o de ninguno de tus aumentos.
    /// </summary>
    public StylePick? Pick(string alias, IEnumerable<int> augmentIds)
    {
        if (_champions.GetValueOrDefault(alias) is not { Styles.Count: > 0 } champ) return null;
        var rows = augmentIds.Distinct().Select(id => (Id: id, Styles: champ.Augments.GetValueOrDefault(id))).Where(r => r.Styles is { Count: > 0 }).ToList();
        if (rows.Count == 0) return null;
        string usual = champ.Styles[0].Key;

        // Lo que sube o baja cada aumento al campeón (todos los estilos juntos), de media entre tus aumentos.
        double effect = rows.Average(r => r.Styles!.Sum(s => s.Games * s.WinRate) / r.Styles!.Sum(s => s.Games) - champ.WinRate);
        int total = rows.Sum(r => r.Styles!.Sum(s => s.Games));

        var options = champ.Styles.Select(overall =>
        {
            var mine = rows.Select(r => (r.Id, Style: r.Styles!.FirstOrDefault(s => s.Key == overall.Key))).Where(x => x.Style is not null).ToList();
            int games = mine.Sum(x => x.Style!.Games);
            double raw = games > 0 ? mine.Sum(x => x.Style!.Games * x.Style.WinRate) / games : overall.WinRate + effect;
            double prior = overall.WinRate + effect;
            double rate = (raw * games + prior * ShrinkGames) / (games + ShrinkGames);
            // Objetos: lo que compran con tus aumentos en ese estilo, partiendo de lo normal del estilo con el campeón.
            var items = new Dictionary<int, double>();
            foreach (var id in mine.SelectMany(x => x.Style!.Items.Keys).Concat(overall.Items.Keys).Distinct())
                items[id] = (mine.Sum(x => x.Style!.Games * x.Style.Items.GetValueOrDefault(id)) + overall.Items.GetValueOrDefault(id) * ShrinkGames)
                            / (games + ShrinkGames);
            int lead = mine.OrderByDescending(x => x.Style!.Games).Select(x => x.Id).FirstOrDefault();
            return (overall.Key, Games: games, Share: (double)games / total, Raw: raw, Rate: rate, Items: items, Lead: lead, Overall: overall.WinRate);
        }).ToList();

        var usualOption = options.First(o => o.Key == usual);
        // Solo cuenta si es el aumento lo que hace mejor al otro estilo: con él, la diferencia con el habitual tiene que ser
        // mayor que la de siempre. Karma de apoyo gana 8 puntos más que de AP con cualquier aumento; eso no es cosa del
        // aumento y sin aumentos la build es la habitual, así que no se cambia (antes salía Redención a Karma con casi todo).
        bool AugmentFavors((string Key, int Games, double Share, double Raw, double Rate, Dictionary<int, double> Items, int Lead, double Overall) o) =>
            o.Raw - usualOption.Raw > o.Overall - usualOption.Overall;
        var best = options.Where(o => o.Key == usual || (o.Share >= MinShare && o.Games >= MinGames && AugmentFavors(o))).MaxBy(o => o.Rate);
        // Lo que hace la mayoría con tus aumentos (como las builds de op.gg): si casi todos cambian de estilo y no ganan
        // menos que los que siguen con el habitual, se sigue a la mayoría aunque el % suavizado diga lo de siempre.
        // Sona con Mago Tirador: 3.682 partidas de AP (50,3 %) frente a 617 de apoyo (48,5 %); suavizado, el apoyo
        // ganaba solo porque Sona de apoyo suele ganar 6 puntos más con otros aumentos.
        var popular = options.MaxBy(o => o.Games);
        if (best.Key == usual && popular.Key != usual && popular.Share >= MajorityShare && popular.Games >= MinGames && popular.Raw >= usualOption.Raw)
            best = popular;
        return new StylePick(best.Key, usual, best.Share, best.Raw, usualOption.Raw, best.Games, best.Lead, best.Items);
    }

    /// <summary>Estilos que se pueden sacar de la media de todos los campeones (apoyo e híbrido dependen demasiado de cada uno).</summary>
    static readonly string[] GenericStyles = ["on_hit", "crit", "lethality", "bruiser", "ap", "tank"];
    /// <summary>Lo que se le supone a un estilo que el campeón no tiene en sus datos (frente a sus estilos de verdad).</summary>
    const double GenericPrior = 0.02;
    /// <summary>Parte de un objeto en un estilo cuando no sale en él (casi nadie lo compra así; no puede ser 0).</summary>
    const double MissingShare = 0.01;
    /// <summary>A partir de qué probabilidad se da por hecho el estilo de tus objetos.</summary>
    public const double ItemStyleConfidence = 0.75;

    /// <summary>
    /// El estilo de build que dicen tus objetos terminados (Yi con Rey Arruinado y Fin del Ingenio → on-hit), con lo
    /// probable que es (0-1). Cuenta en qué parte de las partidas de cada estilo sale cada objeto tuyo y lo jugado que es
    /// el estilo. Los estilos que el campeón no tiene en sus datos (Twitch on-hit) salen de la media de todos los campeones,
    /// con poca probabilidad de partida (más si su kit va con ese estilo, ver <paramref name="kitFits"/>).
    /// Null sin objetos o sin datos.
    /// </summary>
    public (BuildStyle Style, double Probability, bool Generic)? StyleFromItems(string alias, IReadOnlyCollection<int> items, Func<string, bool>? kitFits = null)
    {
        if (items.Count == 0 || _generic.Count == 0) return null;
        var own = Styles(alias);
        int total = own.Sum(s => s.Games);
        var options = own.Select(s => (Style: s, Prior: (double)s.Games / Math.Max(1, total), Generic: false)).ToList();
        foreach (var key in GenericStyles)
            if (own.All(s => s.Key != key) && _generic.TryGetValue(key, out var generic))
                options.Add((generic, GenericPrior * (kitFits?.Invoke(key) == true ? 3 : 1), true));
        if (options.Count == 0) return null;

        var scored = options.Select(o => (o.Style, o.Generic,
            Log: Math.Log(o.Prior) + items.Sum(id => Math.Log(Math.Clamp(o.Style.Items.GetValueOrDefault(id), MissingShare, 0.98))))).ToList();
        double max = scored.Max(s => s.Log);
        double sum = scored.Sum(s => Math.Exp(s.Log - max));
        var best = scored.MaxBy(s => s.Log);
        return (best.Style, 1 / sum, best.Generic);
    }

    static IReadOnlyList<BuildStyle> ReadStyles(List<JsonElement[]>? rows)
    {
        var list = new List<BuildStyle>();
        foreach (var row in rows ?? [])
        {
            if (row.Length < 4 || row[0].GetString() is not { } key || !row[1].TryGetInt32(out var games) || !row[2].TryGetDouble(out var wr)
                || row[3].ValueKind != JsonValueKind.Object || games <= 0) continue;
            var items = new Dictionary<int, double>();
            foreach (var p in row[3].EnumerateObject())
                if (int.TryParse(p.Name, out var id) && p.Value.TryGetDouble(out var share)) items[id] = share;
            list.Add(new BuildStyle(key, games, wr, items));
        }
        return list.OrderByDescending(s => s.Games).ToList();
    }

    public static AugmentBuilds Load(string path)
    {
        FileShape? data = null;
        try
        {
            if (File.Exists(path)) data = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* a medio escribir: se ignora */ }

        var result = new AugmentBuilds { Patch = data?.Patch };
        foreach (var (alias, champ) in data?.Champions ?? [])
        {
            var augments = new Dictionary<int, IReadOnlyList<BuildStyle>>();
            foreach (var (id, row) in champ.Augments ?? [])
                if (int.TryParse(id, out var augmentId) && !ExternalAugments.Biased.Contains(augmentId) && ReadStyles(row.Styles) is { Count: > 0 } styles)
                    augments[augmentId] = styles;
            result._champions[alias] = new ChampionStyles(champ.WinRate, ReadStyles(champ.Styles), augments);
        }
        foreach (var key in GenericStyles)
        {
            var styles = result._champions.Values.SelectMany(c => c.Styles).Where(s => s.Key == key).ToList();
            int games = styles.Sum(s => s.Games);
            if (games == 0) continue;
            var items = styles.SelectMany(s => s.Items.Keys).Distinct()
                .ToDictionary(id => id, id => styles.Sum(s => s.Games * s.Items.GetValueOrDefault(id)) / games);
            result._generic[key] = new BuildStyle(key, games, styles.Sum(s => s.Games * s.WinRate) / games, items);
        }
        return result;
    }
}
