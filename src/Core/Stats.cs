using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Una partida de Mayhem guardada en matches.jsonl (una línea por partida).</summary>
public sealed class MatchRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("v")] public string Version { get; set; } = "";
    [JsonPropertyName("p")] public List<PlayerRecord> Players { get; set; } = [];
}

public sealed class PlayerRecord
{
    [JsonPropertyName("c")] public string Champion { get; set; } = "";
    [JsonPropertyName("w")] public bool Win { get; set; }
    [JsonPropertyName("a")] public List<int> Augments { get; set; } = [];
    /// <summary>Equipo: 100 o 200.</summary>
    [JsonPropertyName("t")] public int Team { get; set; }
    /// <summary>Objetos al final de la partida.</summary>
    [JsonPropertyName("i")] public List<int> Items { get; set; } = [];
    /// <summary>Daño físico y mágico a campeones, y curación total.</summary>
    [JsonPropertyName("pd")] public int PhysicalDamage { get; set; }
    [JsonPropertyName("md")] public int MagicDamage { get; set; }
    [JsonPropertyName("h")] public int Heal { get; set; }
    /// <summary>Habilidades subidas en orden, ej. "QWEQQRQ..." (vacío si no se bajó la línea de tiempo).</summary>
    [JsonPropertyName("s")] public string Skills { get; set; } = "";
}

/// <summary>Tipo de daño predominante del equipo enemigo.</summary>
public enum DamageMix { Physical, Mixed, Magic }

/// <param name="Global">True si la cifra es de todos los jugadores de Mayhem (mayhemmeta.com); false si es de tus partidas.</param>
/// <param name="FromAverage">Sin datos de este campeón con el aumento: estimado con lo que hace el aumento con todos los campeones.</param>
public sealed record Recommendation(int AugmentId, double WinRate, int Games, double Delta, string Tier, bool Global = false, bool FromAverage = false);

/// <summary>
/// Estadísticas calculadas a partir de matches.jsonl. Con pocas partidas, cada cifra se "suaviza"
/// hacia una más general (campeón+aumento contra este tipo de equipo → campeón+aumento → aumento → 50 %)
/// para no fiarse de 2 o 3 juegos.
/// </summary>
public sealed class StatsStore
{
    const double Prior = 25;        // partidas "virtuales" al mezclar con la cifra más general
    const double GlobalPrior = 50;
    /// <summary>Partidas con las que la diferencia de % de un objeto (mayhemmeta.com) cuenta a medias.</summary>
    const double ItemShrinkGames = 400;

    readonly Dictionary<string, (int g, int w)> _champ = [];
    readonly Dictionary<int, (int g, int w)> _aug = [];
    readonly Dictionary<(string, int), (int g, int w)> _champAug = [];
    readonly Dictionary<(string, int, DamageMix), (int g, int w)> _champAugMix = [];
    readonly Dictionary<(string, int), (int g, int w)> _champItem = [];
    readonly Dictionary<(string, int, DamageMix), (int g, int w)> _champItemMix = [];
    readonly Dictionary<(string, int), (int g, int w)> _roleItem = [];
    readonly Dictionary<(int, int), (int g, int w)> _augItem = [];
    /// <summary>Parejas de aumentos que llevó un mismo jugador (clave con el id menor primero).</summary>
    readonly Dictionary<(int, int), (int g, int w)> _augPair = [];
    readonly HashSet<int> _seenItems = [];

    /// <summary>
    /// True si el objeto ha salido en alguna partida de Mayhem guardada. Sirve para no recomendar cosas
    /// que solo existen en la Grieta (p. ej. botas mejoradas). Sin partidas, se acepta todo.
    /// </summary>
    public bool SeenInMayhem(int itemId) => _seenItems.Count == 0 || _seenItems.Contains(itemId);
    readonly Dictionary<(string, string), (int g, int w)> _skillOrder = [];
    readonly Dictionary<(string, string), int> _skillStart = [];
    readonly Dictionary<string, (long phys, long magic, long heal, int n)> _profile = [];
    double _avgHeal;

    public int MatchCount { get; private set; }
    /// <summary>De esas, las tuyas (matches.jsonl); el resto son de la comunidad (comunidad.jsonl).</summary>
    public int OwnMatchCount { get; private set; }
    public string? Patch { get; private set; }
    public GameData Data { get; private set; } = new();
    /// <summary>
    /// Builds de todos los jugadores (mayhem-champion-builds.json). Cuando un campeón tiene datos ahí,
    /// mandan sobre los tuyos: son miles de partidas por aumento u objeto en vez de unas pocas.
    /// </summary>
    public MayhemBuilds Global { get; set; } = new();
    /// <summary>Objetos que piden o regalan los aumentos (ver <see cref="AugmentItemLinks"/>).</summary>
    public AugmentItemLinks Links { get; set; } = new();
    /// <summary>% de victorias de cada campeón con cada aumento, de todos los jugadores (mayhem-augments.json).</summary>
    public MayhemAugments AugmentTable { get; set; } = new();
    /// <summary>Lo mismo según AramHex (servidor chino, aramhex-augments.json): muchas más partidas, punto de partida de las notas.</summary>
    public ExternalAugments Aramhex { get; set; } = new();
    /// <summary>Según aramkit.com (aramkit-augments.json): solo para lo que AramHex no tiene (aumentos nuevos).</summary>
    public ExternalAugments Aramkit { get; set; } = new();
    /// <summary>Estilos de build (apoyo, AP, tanque…) con cada aumento según aramkit.com (aramkit-builds.json).</summary>
    public AugmentBuilds AramkitBuilds { get; set; } = new();
    /// <summary>Para qué sirve el kit de cada campeón (campeones-kit.json): on-hit, velocidad de ataque, escalado…</summary>
    public ChampionKits Kits { get; set; } = new();
    /// <summary>Mínimo de partidas de un campeón con un aumento para usar la cifra de todos los jugadores.</summary>
    const int MinGlobalGames = 30;

    /// <param name="communityPath">
    /// Partidas de la comunidad (las que comparten los demás usuarios, sin nombres). Se suman a las tuyas, sin las que
    /// ya tienes: la misma partida se reconoce por sus 10 campeones y su daño (el id viene cifrado del servidor).
    /// </param>
    public static StatsStore Load(string path, GameData data, string? communityPath = null)
    {
        var store = new StatsStore { Data = data };
        var records = ReadRecords(path);
        store.OwnMatchCount = records.Count;
        if (communityPath is not null)
        {
            var known = records.Select(Signature).ToHashSet();
            records.AddRange(ReadRecords(communityPath).Where(r => known.Add(Signature(r))));
        }
        if (records.Count == 0) return store;

        // Los aumentos cambian cada parche: si hay suficientes partidas del parche más nuevo, usamos solo esas.
        var byPatch = records.GroupBy(r => PatchOf(r.Version)).OrderByDescending(g => g.Key, PatchComparer.Instance).ToList();
        if (byPatch.Count > 0 && byPatch[0].Count() >= 1500)
        {
            store.Patch = byPatch[0].Key;
            records = byPatch[0].ToList();
        }

        // 1.ª pasada: perfil de cada campeón (cuánto daño mágico hace, cuánto se cura).
        foreach (var p in records.SelectMany(r => r.Players))
        {
            var key = p.Champion.ToLowerInvariant();
            store._profile.TryGetValue(key, out var v);
            store._profile[key] = (v.phys + p.PhysicalDamage, v.magic + p.MagicDamage, v.heal + p.Heal, v.n + 1);
        }
        var all = store._profile.Values;
        store._avgHeal = all.Sum(v => v.n) == 0 ? 0 : (double)all.Sum(v => v.heal) / all.Sum(v => v.n);

        // 2.ª pasada: victorias por aumento/objeto/habilidades, también según el tipo de equipo enemigo.
        foreach (var r in records) store.Add(r);
        return store;
    }

    static List<MatchRecord> ReadRecords(string path)
    {
        var records = new List<MatchRecord>();
        if (!File.Exists(path)) return records;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<MatchRecord>(line) is { } r) records.Add(r); }
                catch (JsonException) { /* línea cortada si se cerró el recolector a medias */ }
            }
        }
        catch (IOException) { }
        return records;
    }

    static string Signature(MatchRecord r) => string.Join(",", r.Players.Select(p => $"{p.Champion}:{p.PhysicalDamage}:{p.MagicDamage}"));

    void Add(MatchRecord r)
    {
        MatchCount++;
        foreach (var p in r.Players)
        {
            int w = p.Win ? 1 : 0;
            var champ = p.Champion.ToLowerInvariant();
            var enemies = r.Players.Where(o => o.Team != p.Team).Select(o => (o.Champion, (IReadOnlyList<int>)o.Items)).ToList();
            var mix = EnemyProfile.Build(enemies, this).Mix;

            Bump(_champ, champ, w);
            var augs = p.Augments.Distinct().OrderBy(a => a).ToList();
            for (int x = 0; x < augs.Count; x++)
                for (int y = x + 1; y < augs.Count; y++) Bump(_augPair, (augs[x], augs[y]), w);
            foreach (var a in p.Augments.Distinct())
            {
                Bump(_aug, a, w);
                Bump(_champAug, (champ, a), w);
                Bump(_champAugMix, (champ, a, mix), w);
            }

            var role = Data.Champion(p.Champion)?.Roles.FirstOrDefault() ?? "";
            _seenItems.UnionWith(p.Items);
            foreach (var i in p.Items.Distinct())
            {
                if (Data.Item(i) is not { IsFinal: true }) continue;
                Bump(_champItem, (champ, i), w);
                Bump(_champItemMix, (champ, i, mix), w);
                Bump(_roleItem, (role, i), w);
                foreach (var a in p.Augments.Distinct()) Bump(_augItem, (a, i), w);
            }

            if (p.Skills.Length >= 9)
            {
                Bump(_skillOrder, (champ, MaxOrder(p.Skills)), w);
                var start = new string(p.Skills.Where(c => c != 'R').Take(3).ToArray());
                _skillStart[(champ, start)] = _skillStart.GetValueOrDefault((champ, start)) + 1;
            }
        }
    }

    static void Bump<T>(Dictionary<T, (int g, int w)> d, T key, int win) where T : notnull
    {
        d.TryGetValue(key, out var v);
        d[key] = (v.g + 1, v.w + win);
    }

    static double Smooth((int g, int w) s, double prior, double weight) => (s.w + prior * weight) / (s.g + weight);

    public double ChampionBase(string champion) =>
        Smooth(_champ.GetValueOrDefault(champion.ToLowerInvariant()), 0.5, GlobalPrior);

    public int ChampionGames(string champion) => _champ.GetValueOrDefault(champion.ToLowerInvariant()).g;

    /// <summary>Proporción de daño mágico del campeón (0 = todo físico, 1 = todo mágico), o null si no hay datos.</summary>
    public double? MagicShare(string champion)
    {
        if (!_profile.TryGetValue(champion.ToLowerInvariant(), out var v) || v.n < 5 || v.phys + v.magic == 0) return PageApShare(champion);
        return (double)v.magic / (v.phys + v.magic);
    }

    /// <summary>
    /// Parte AP de lo que compra el campeón en mayhemmeta.com (0 = todo AD, 1 = todo AP), pesando cada objeto por su % de uso;
    /// null sin datos. Sin partidas tuyas del campeón se usaba su rol, y Kassadin ("asesino") salía de AD.
    /// </summary>
    public double? PageApShare(string champion)
    {
        if (Global.For(champion) is not { } build) return null;
        double ap = 0, ad = 0;
        foreach (var s in build.ItemsCommon)
        {
            if (Data.Item(s.Id) is not { } item) continue;
            bool isAp = item.Has("SpellDamage") || item.Has("MagicPenetration");
            bool isAd = item.Has("Damage") || item.Has("CriticalStrike") || item.Has("AttackSpeed") || item.Has("ArmorPenetration");
            double w = s.PickRate ?? 0;
            if (isAp) ap += isAd ? w / 2 : w;
            if (isAd) ad += isAp ? w / 2 : w;
        }
        return ap + ad > 0 ? ap / (ap + ad) : null;
    }

    /// <summary>True si el campeón se cura bastante más que la media.</summary>
    public bool IsHealer(string champion) =>
        _profile.TryGetValue(champion.ToLowerInvariant(), out var v) && v.n >= 5 && _avgHeal > 0 && (double)v.heal / v.n > _avgHeal * 1.6;

    public Recommendation Rate(string champion, int augmentId, DamageMix? mix = null)
    {
        champion = champion.ToLowerInvariant();
        var ca = _champAug.GetValueOrDefault((champion, augmentId));
        // Con datos de todos los jugadores: su % y cuánto sube o baja el % del campeón con el aumento.
        // Primero la tabla campeón × aumento (casi todos los aumentos); si no, las listas de la build del campeón.
        // Con pocas partidas, lo de este campeón es casi todo azar: se mezcla con lo que hace el aumento con todos los
        // campeones (MayhemAugments.ChampionShrinkGames), no con "no hace nada" como antes.
        // Si AramHex (o, si no, aramkit) tiene la combinación (casi siempre, con miles de partidas), el punto de partida es
        // 75 % esa web + 25 % esa media: falla ~30 % menos (ver ExternalAugments.Weight).
        double average = AugmentTable.AugmentMean(augmentId, champion) / 100;
        var external = Aramhex.For(champion, augmentId) ?? Aramkit.For(champion, augmentId);
        double prior = external is null ? average : ExternalAugments.Weight * external.Delta / 100 + (1 - ExternalAugments.Weight) * average;
        double shrink = external is null ? MayhemAugments.ChampionShrinkGames : ExternalAugments.ShrinkGames;
        double Blend(double ownDelta, int games) => (ownDelta * games + prior * shrink) / (games + shrink);
        if (AugmentTable.For(champion, augmentId) is { Games: >= MinGlobalGames } row)
        {
            double rowDelta = Blend((row.WinRate - row.ChampionWinRate) / 100, row.Games);
            return new Recommendation(augmentId, row.WinRate / 100, row.Games, rowDelta, TierOf(rowDelta), Global: true);
        }
        // La lista de "mejores aumentos" del campeón saca los de más % aunque tengan pocas partidas: se mezcla igual
        // (Kha'Zix + Mejora: Hidra Voraz salía S con ~40 partidas; con más partidas gana mucho menos que el campeón).
        if (Global.For(champion) is { } build && build.Augment(augmentId) is { WinRate: { } globalRate } g && build.GamesWith(g) is var listGames and >= MinGlobalGames)
        {
            double globalDelta = Blend((g.Delta ?? globalRate - (build.WinRate ?? globalRate)) / 100, listGames);
            return new Recommendation(augmentId, globalRate / 100, listGames, globalDelta, TierOf(globalDelta), Global: true);
        }
        // mayhemmeta no la tiene (o con menos de 30 partidas) pero la otra web sí: lo de esa web, más esas pocas partidas.
        if (external is not null)
        {
            double extDelta = AugmentTable.For(champion, augmentId) is { } few ? Blend((few.WinRate - few.ChampionWinRate) / 100, few.Games) : prior;
            return new Recommendation(augmentId, external.WinRate / 100, external.Games, extDelta, TierOf(extDelta), Global: true);
        }

        // Sin datos de esta combinación, partimos de lo que gana el campeón + lo que el aumento sube o baja en general:
        // con todos los campeones de mayhemmeta si lo hay (falla ~20 % menos que suponer 0), si no con tus partidas.
        double champBase = ChampionBase(champion);
        bool fromAverage = AugmentTable.HasAugment(augmentId);
        double augEffect = fromAverage ? average : Smooth(_aug.GetValueOrDefault(augmentId), 0.5, GlobalPrior) - 0.5;
        double rate = Smooth(ca, Math.Clamp(champBase + augEffect, 0.05, 0.95), Prior);
        if (mix is { } m) rate = Smooth(_champAugMix.GetValueOrDefault((champion, augmentId, m)), rate, Prior);

        double delta = rate - champBase;
        return new Recommendation(augmentId, rate, ca.g, delta, TierOf(delta), FromAverage: fromAverage);
    }

    static string TierOf(double delta) => delta switch
    {
        >= 0.04 => "S",
        >= 0.015 => "A",
        >= -0.015 => "B",
        >= -0.04 => "C",
        _ => "D",
    };

    /// <summary>Datos de todos los jugadores de un objeto con este campeón, o null si no los hay.</summary>
    public MayhemItemStat? GlobalItem(string champion, int itemId) => Global.For(champion)?.Item(itemId);

    /// <summary>Objetos finales que se ven con este campeón, con su % de victorias suavizado contra este tipo de equipo.</summary>
    public IEnumerable<(int Item, double Rate, int Games)> ItemRates(string champion, DamageMix mix)
    {
        // Con datos de todos los jugadores, primero esos objetos. El % se pone en tu escala (tu % con el campeón
        // + lo que sube o baja el objeto), que es como lo compara el Advisor; el motivo enseña el % real.
        var global = new HashSet<int>();
        if (Global.For(champion) is { } build)
            foreach (var g in build.AllItems.Where(i => i.Kind == "items" && i.WinRate.HasValue))
            {
                global.Add(g.Id);
                double delta = (g.Delta ?? g.WinRate!.Value - (build.WinRate ?? g.WinRate!.Value)) / 100;
                // Un objeto que casi nadie compra (Arco Axiomático con Aatrox: 57 % en ~110 partidas) suele ganar por quién
                // lo compra, no por el objeto: su diferencia cuenta según las partidas que la respaldan.
                int n = build.GamesWith(g);
                delta *= (double)n / (n + ItemShrinkGames);
                yield return (g.Id, Math.Clamp(ChampionBase(champion) + delta, 0.05, 0.95), n);
            }
        // Lo de tus partidas guardadas que no sale en mayhemmeta.com son pocas partidas (10-20): con miles de partidas
        // del campeón en la página, su diferencia cuenta igual que la de un objeto raro de la página (Cicloespada a Jax).
        double champBase = ChampionBase(champion);
        foreach (var r in OwnItemRates(champion, mix))
            if (!global.Contains(r.Item))
                yield return global.Count > 0 ? (r.Item, champBase + (r.Rate - champBase) * r.Games / (r.Games + ItemShrinkGames), r.Games) : r;
    }

    IEnumerable<(int Item, double Rate, int Games)> OwnItemRates(string champion, DamageMix mix)
    {
        champion = champion.ToLowerInvariant();
        int games = ChampionGames(champion);
        double champBase = ChampionBase(champion);
        var own = _champItem.Where(kv => kv.Key.Item1 == champion && kv.Value.g >= Math.Max(3, games / 50)).ToList();

        if (own.Count >= 4)
        {
            foreach (var (key, s) in own)
            {
                double rate = Smooth(_champItemMix.GetValueOrDefault((champion, key.Item2, mix)), Smooth(s, champBase, Prior), Prior);
                yield return (key.Item2, rate, s.g);
            }
            yield break;
        }

        // Pocos datos del campeón: usamos lo que compran los campeones de su mismo rol, pero solo objetos
        // de su tipo de daño (entre los "asesinos" hay AD como Talon y AP como Akali).
        var roles = Data.Champion(champion)?.Roles ?? [];
        double share = MagicShare(champion) ?? EnemyProfile.RoleMagicShare(roles);
        foreach (var (key, s) in _roleItem.Where(kv => kv.Key.Item1 == (roles.FirstOrDefault() ?? "") && kv.Value.g >= 10))
        {
            if (Data.Item(key.Item2) is not { } item) continue;
            bool ap = item.Has("SpellDamage"), ad = item.Has("Damage") || item.Has("CriticalStrike") || item.Has("AttackSpeed");
            if (share < 0.4 && ap && !ad) continue;
            if (share > 0.6 && ad && !ap) continue;
            yield return (key.Item2, Smooth(s, 0.5, GlobalPrior), s.g);
        }
    }

    /// <summary>
    /// Cuánto mejora (o empeora) un objeto cuando llevas este aumento, comparado con el aumento en general.
    /// Null si hay menos de 5 partidas con esa combinación.
    /// </summary>
    /// <summary>
    /// Cuánto mejor (o peor) ganan dos aumentos juntos que lo que se esperaría de cada uno por separado,
    /// en las partidas guardadas (los 10 jugadores). Null si hay menos de 6 partidas con la pareja.
    /// </summary>
    public (double Lift, int Games)? AugmentPairLift(int a, int b)
    {
        var s = _augPair.GetValueOrDefault(a < b ? (a, b) : (b, a));
        if (s.g < 6) return null;
        double expected = (Smooth(_aug.GetValueOrDefault(a), 0.5, GlobalPrior) + Smooth(_aug.GetValueOrDefault(b), 0.5, GlobalPrior)) / 2;
        return (Smooth(s, expected, 10) - expected, s.g);
    }

    public (double Lift, int Games)? AugmentItemLift(int augmentId, int itemId)
    {
        var s = _augItem.GetValueOrDefault((augmentId, itemId));
        if (s.g < 5) return null;
        double augRate = Smooth(_aug.GetValueOrDefault(augmentId), 0.5, GlobalPrior);
        return (Smooth(s, augRate, Prior) - augRate, s.g);
    }

    /// <summary>Todos los campeones con datos: % de victorias suavizado y partidas.</summary>
    public IEnumerable<(string Champion, double WinRate, int Games)> Champions() =>
        _champ.Select(kv => (kv.Key, Smooth(kv.Value, 0.5, GlobalPrior), kv.Value.g));

    /// <summary>Los aumentos con más datos para un campeón, puntuados como en el overlay.</summary>
    public IEnumerable<Recommendation> TopAugments(string champion, int count) =>
        // Con datos de todos los jugadores, los tuyos solo entran si tienen algo de base (3+ partidas):
        // si no, un aumento que ganaste una vez saldría por encima de otros con miles de partidas.
        _champAug.Where(kv => kv.Key.Item1 == champion.ToLowerInvariant() && (Global.For(champion) is null || kv.Value.g >= 3))
                 .Select(kv => kv.Key.Item2)
                 .Concat(Global.For(champion)?.AllAugments.Select(a => a.Id) ?? [])
                 .Concat(AugmentTable.AugmentsFor(champion).Where(id => AugmentTable.For(champion, id) is { Games: >= MinGlobalGames }))
                 .Concat(Aramhex.AugmentsFor(champion))
                 .Concat(Aramkit.AugmentsFor(champion))
                 .Distinct()
                 .Select(id => Rate(champion, id))
                 // Por lo que suben el % (no por el % a secas): así se comparan bien los tuyos con los de todos.
                 .OrderByDescending(r => r.Delta).Take(count);

    /// <summary>Orden de maximizar habilidades que más gana (ej. "QEW") y las 3 primeras más comunes.</summary>
    public (string MaxOrder, string Start, double Rate, int Games)? BestSkills(string champion)
    {
        champion = champion.ToLowerInvariant();
        var orders = _skillOrder.Where(kv => kv.Key.Item1 == champion).ToList();
        int total = orders.Sum(kv => kv.Value.g);
        if (total < 5) return null;

        double champBase = ChampionBase(champion);
        var candidates = orders.Where(kv => kv.Value.g >= total * 0.1).ToList();
        var starts = _skillStart.Where(kv => kv.Key.Item1 == champion).ToList();
        if (candidates.Count == 0 || starts.Count == 0) return null; // órdenes muy repartidos: no hay uno claro
        var best = candidates.MaxBy(kv => Smooth(kv.Value, champBase, 10));
        var start = starts.MaxBy(kv => kv.Value).Key.Item2;
        return (best.Key.Item2, start, Smooth(best.Value, champBase, 10), best.Value.g);
    }

    /// <summary>"QWEQQRQ..." → orden en que se llega al máximo de Q, W y E, ej. "QEW".</summary>
    public static string MaxOrder(string skills)
    {
        var count = new Dictionary<char, int> { ['Q'] = 0, ['W'] = 0, ['E'] = 0 };
        var order = new List<char>();
        foreach (var c in skills)
        {
            if (!count.ContainsKey(c)) continue;
            if (++count[c] == 5) order.Add(c);
        }
        order.AddRange(count.Where(kv => !order.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        return new string(order.ToArray());
    }

    static string PatchOf(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    sealed class PatchComparer : IComparer<string>
    {
        public static readonly PatchComparer Instance = new();
        public int Compare(string? x, string? y) =>
            Comparer<Version?>.Default.Compare(System.Version.TryParse(x, out var a) ? a : null, System.Version.TryParse(y, out var b) ? b : null);
    }
}
