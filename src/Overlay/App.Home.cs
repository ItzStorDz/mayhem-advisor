using MayhemAdvisor.Core;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Overlay;

/// <summary>Datos de la ventana principal: inicio (tu historial y estadísticas) y la página de cada campeón.</summary>
public partial class App
{
    /// <summary>Pantalla de inicio: tu perfil, estadísticas de tus últimas 20 partidas, historial, campeones y consejos.</summary>
    object HomeMessage()
    {
        var all = MyMatches.Load();
        var recent = all.Take(20).ToList();
        var profile = MyMatches.LoadProfile();

        object? stats = null;
        if (recent.Count > 0)
        {
            int wins = recent.Count(m => m.Win);
            double Avg(Func<MyMatch, double> f) => recent.Average(f);
            // Tendencia: % de victorias de los últimos 7 días contra los 7 anteriores (si hay partidas en los dos).
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), week = 7L * 24 * 3600 * 1000;
            var thisWeek = all.Where(m => now - m.Date < week).ToList();
            var lastWeek = all.Where(m => now - m.Date >= week && now - m.Date < 2 * week).ToList();
            double? trend = thisWeek.Count >= 3 && lastWeek.Count >= 3
                ? thisWeek.Count(m => m.Win) / (double)thisWeek.Count - lastWeek.Count(m => m.Win) / (double)lastWeek.Count : null;
            var mostPlayed = all.GroupBy(m => m.Champion).Select(g => ChampDto(g.Key, g.ToList())).OrderByDescending(c => c.Games).ThenByDescending(c => c.WinRate).ToList();
            var best = mostPlayed.Where(c => c.Games >= 2).OrderByDescending(c => c.WinRate).ThenByDescending(c => c.Games).FirstOrDefault();
            // Tu aumento estrella: el que más eliges (sin % de victorias de aumentos: Riot no lo permite en apps).
            var star = all.SelectMany(m => m.Augments).GroupBy(a => a)
                .Where(g => g.Count() >= 2 && _catalog.Get(g.Key) is not null)
                .Select(g => (Aug: _catalog.Get(g.Key)!, Games: g.Count()))
                .OrderByDescending(x => x.Games).FirstOrDefault();
            int streak = recent.TakeWhile(m => m.Win == recent[0].Win).Count();
            stats = new
            {
                games = recent.Count, wins, losses = recent.Count - wins, winRate = wins / (double)recent.Count, trend,
                kills = Avg(m => m.Kills), deaths = Avg(m => m.Deaths), assists = Avg(m => m.Assists),
                kda = Avg(m => m.Kills + m.Assists) / Math.Max(0.5, Avg(m => m.Deaths)),
                damage = Avg(m => m.Damage),
                best = best is null ? null : new { best.Name, best.Icon, best.Games, best.WinRate },
                star = star.Aug is null ? null : new { name = star.Aug.DisplayName, icon = star.Aug.Icon, games = star.Games },
                last10 = recent.Take(10).Reverse().Select(m => new { win = m.Win, kda = Math.Round(m.Kda, 1) }),
                streak = new { win = recent[0].Win, count = streak },
                top = mostPlayed.Take(4).Select(c => new { c.Name, c.Icon, c.Games, c.WinRate, kda = c.Kda }),
            };
        }

        return new
        {
            type = "home",
            profile = profile is null ? null : new { name = profile.Name, tag = profile.Tag, level = profile.Level,
                icon = profile.Icon > 0 ? $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/profile-icons/{profile.Icon}.jpg" : null },
            stats,
            // Consejos: los de tus partidas (GameTips.cs) y los generales del modo y de la app (ModeTips.cs), también sin partidas.
            tips = new { personal = PersonalTips(all), general = ModeTips.All(SpellsFact()).Select(g => new { kind = g.Kind, text = g.Text }) },
            history = all.Take(30).Select(m => new
            {
                champion = _data.ChampionName(m.Champion), icon = _data.Champion(m.Champion)?.Icon, win = m.Win,
                k = m.Kills, d = m.Deaths, a = m.Assists, kda = Math.Round(m.Kda, 1), damage = m.Damage, gold = m.Gold,
                minutes = m.Duration / 60, date = m.Date,
                items = m.Items.Select(_data.Item).OfType<ItemInfo>().Where(i => i.PriceTotal > 0).Select(i => new { name = i.DisplayName, icon = i.Icon }),
                augments = m.Augments.Select(_catalog.Get).OfType<Augment>().Select(a => new { name = a.DisplayName, icon = a.Icon }),
            }),
        };
    }

    sealed record ChampStat(string Alias, string Name, string? Icon, int Games, double WinRate, double Kda);

    ChampStat ChampDto(string alias, List<MyMatch> games) =>
        new(alias, _data.ChampionName(alias), _data.Champion(alias)?.Icon, games.Count, games.Count(m => m.Win) / (double)games.Count,
            Math.Round(games.Sum(m => m.Kills + m.Assists) / (double)Math.Max(1, games.Sum(m => m.Deaths)), 1));

    /// <summary>
    /// Consejos de tus partidas (ver GameReports.HomeTips). Antes eran 4 reglas y como mucho 2; una decía «en N de tus
    /// derrotas no compraste botas», y en Caos el 26-38 % no las compra nunca (aramkit): fuera.
    /// </summary>
    List<string> PersonalTips(List<MyMatch> all)
    {
        if (all.Count < 5)
            return [L("Juega unas cuantas partidas de ARAM: Caos más y aquí verás consejos sacados de tus propias partidas.",
                      "Play a few more ARAM: Mayhem games and you'll see tips based on your own games here.")];
        try
        {
            var tips = Reports().HomeTips(all);
            return tips.Count > 0 ? tips : [L("Vas bien: sigue usando la nota de los aumentos y la pestaña «Adaptar» cuando el equipo enemigo vaya muy cargado a un tipo de daño.",
                                               "Looking good: keep using the augment scores and the “Adapt” tab when the enemy team leans hard into one damage type.")];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.Write($"No pude sacar los consejos: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>La pareja de hechizos más usada en Caos (mayhemmeta, por partidas de cada campeón), para los consejos.</summary>
    string? SpellsFact()
    {
        var pairs = new Dictionary<string, (double Games, string[] Keys)>();
        double total = 0;
        foreach (var c in _builds.Champions)
        {
            if (c.Games is not { } games) continue;
            foreach (var combo in c.Spells)
            {
                if (combo.Spells.Count != 2 || combo.PickRate is not { } pick) continue;
                var keys = combo.Spells.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var key = string.Join("+", keys);
                double n = games * pick / 100;
                pairs[key] = (pairs.GetValueOrDefault(key).Games + n, keys);
                total += n;
            }
        }
        if (total <= 0) return null;
        var top = pairs.Values.MaxBy(p => p.Games);
        return L($"La pareja de hechizos más usada en Caos es {SummonerSpells.PairName(top.Keys)}: la lleva el {top.Games / total:P0} de los jugadores (mayhemmeta). En la selección de campeón, el panel te dice cuál gana más con tu campeón.",
                 $"The most used summoner spell pair in Mayhem is {SummonerSpells.PairName(top.Keys)}: {top.Games / total:P0} of players take it (mayhemmeta). In champion select, the panel tells you which wins more on your champion.");
    }

    /// <summary>Los órdenes de subir habilidades del campeón (aramkit), con qué parte lo juega y su % de victorias; null sin datos.</summary>
    object? SkillRows(string alias)
    {
        var orders = _aramkitBuilds.Skills(alias);
        if (orders.Count == 0) return null;
        int total = Math.Max(_aramkitBuilds.Games(alias), orders.Sum(o => o.Games));
        return orders.Select(o => new { order = string.Join(" › ", o.Order.ToCharArray()), share = (double)o.Games / total, winRate = o.WinRate / 100, games = o.Games });
    }

    /// <summary>
    /// Consejos de la página de un campeón, de datos de todos los jugadores (como mucho 6): su puesto en Caos, la build que
    /// lleva la mayoría (o la que gana más), hechizos, cómo se suben sus habilidades, el objeto que más sube su %, su escalado
    /// y las botas más usadas. Sin % de victorias de aumentos (Riot no lo permite).
    /// </summary>
    List<string> ChampionTips(string alias, MayhemChampion? meta, MayhemChampionBuild? build, IReadOnlyList<BuildStyle> styles, ChampionKit? kit)
    {
        var tips = new List<string>();
        string name = _data.ChampionName(alias);
        if (meta is { WinRate: { } wr } && _mayhem.RankOf(alias) is var rank and > 0)
            tips.Add(L($"Es el n.º {rank} de {_mayhem.Champions.Count} campeones en Caos: gana el {wr:0.#} % de sus partidas (tier {meta.Tier}).",
                       $"It's #{rank} of {_mayhem.Champions.Count} champions in Mayhem: it wins {wr:0.#}% of its games ({meta.Tier} tier)."));
        int styleGames = styles.Sum(st => st.Games);
        if (styles.Count > 0 && styleGames > 0)
        {
            var first = styles[0];
            var better = styles.Skip(1).FirstOrDefault(st => st.Games >= styleGames * 0.1 && st.WinRate - first.WinRate >= 1);
            tips.Add(better is null
                ? L($"La mayoría va de {Advisor.StyleName(first.Key)}: el {(double)first.Games / styleGames:P0} de las partidas (aramkit).",
                    $"Most players go {Advisor.StyleName(first.Key)}: {(double)first.Games / styleGames:P0} of games (aramkit).")
                : L($"La mayoría va de {Advisor.StyleName(first.Key)} ({(double)first.Games / styleGames:P0}), pero la build de {Advisor.StyleName(better.Key)} ({(double)better.Games / styleGames:P0}) gana más: {better.WinRate:0.#} % frente a {first.WinRate:0.#} %. En la partida puedes fijarla en «TIPO DE BUILD».",
                    $"Most players go {Advisor.StyleName(first.Key)} ({(double)first.Games / styleGames:P0}), but the {Advisor.StyleName(better.Key)} build ({(double)better.Games / styleGames:P0}) wins more: {better.WinRate:0.#}% vs {first.WinRate:0.#}%. In game you can lock it in “BUILD TYPE”."));
        }
        if (SummonerSpells.Recommend(build) is { } spells)
            tips.Add(L($"Hechizos: {string.Join(" + ", spells.Names)} ({spells.Reason}).", $"Spells: {string.Join(" + ", spells.Names)} ({spells.Reason})."));
        if (new Advisor(_stats).CommonSkills(alias) is { } skills)
            tips.Add(L($"Habilidades: el {skills.Share:P0} maximiza {string.Join(" › ", skills.MaxOrder.ToCharArray())} (aramkit).",
                       $"Skills: {skills.Share:P0} max {string.Join(" › ", skills.MaxOrder.ToCharArray())} (aramkit)."));
        if (build is { WinRate: { } champWr } && build.ItemsBest.Concat(build.ItemsCommon)
                .Where(i => i.WinRate.HasValue && i.PickRate >= 5 && build.GamesWith(i) >= 300 && _data.Item(i.Id) is { IsBoots: false, InStore: true })
                .MaxBy(i => i.WinRate) is { } bestItem && bestItem.WinRate > champWr + 1)
            tips.Add(L($"De lo que se compra con {name}, lo que más gana es {_data.Item(bestItem.Id)!.DisplayName}: {bestItem.WinRate:0.#} % en ~{build.GamesWith(bestItem):N0} partidas (lo compra el {bestItem.PickRate:0} %).",
                       $"Of what {name} players buy, {_data.Item(bestItem.Id)!.DisplayName} wins the most: {bestItem.WinRate:0.#}% in ~{build.GamesWith(bestItem):N0} games ({bestItem.PickRate:0}% buy it)."));
        if (kit?.Late >= 2)
            tips.Add(kit.Late >= 3
                ? L("Escala sin límite (cargas que no paran de crecer): cuanto más dure la partida, más fuerte es.", "Scales without limit (stacks that keep growing): the longer the game, the stronger it gets.")
                : L("Es fuerte en partida larga: no te desesperes si el principio va lento.", "Strong in long games: don't despair if the start is slow."));
        if (build?.Boots.Where(b => b.PickRate.HasValue).MaxBy(b => b.PickRate) is { } boots && _data.Item(boots.Id) is { } bootsItem)
            tips.Add(L($"Botas más usadas: {bootsItem.DisplayName} (el {boots.PickRate:0} %), después del primer objeto.",
                       $"Most used boots: {bootsItem.DisplayName} ({boots.PickRate:0}%), after the first item."));
        return tips.Take(6).ToList();
    }

    /// <summary>Página de un campeón: cabecera, build más popular con su % y los aumentos con mejor nota de cada rareza.</summary>
    object ChampionMessage(string alias)
    {
        var info = _data.Champion(alias);
        var meta = _mayhem.For(alias);
        var build = _builds.For(alias);
        var mine = MyMatches.Load().Where(m => m.Champion.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToList();
        string lower = (info?.Alias ?? alias).ToLowerInvariant();

        object ItemRow(MayhemItemStat s) => new
        {
            name = _data.Item(s.Id)?.DisplayName ?? s.Name, icon = _data.Item(s.Id)?.Icon, winRate = s.WinRate / 100, games = build!.GamesWith(s),
        };
        var items = build is null ? [] : build.Boots.Take(1).Concat(build.ItemsCommon.Where(i => _data.Item(i.Id) is { InStore: true }).Take(6))
            .Where(i => i.WinRate.HasValue).Select(ItemRow).ToList();

        // Los aumentos con su nota 0-100 y rango, no con su % de victorias (Riot no deja enseñarlo en apps).
        var scale = AugmentScale.FromTable(info?.Alias ?? alias, _augmentTable, _catalog);
        object Augs(string rarity, int take) => _catalog.Augments
            .Where(a => a.Rarity == rarity && !a.IsAnvil)
            .Select(a => (Aug: a, Row: _augmentTable.For(info?.Alias ?? alias, a.Id)))
            .Where(x => x.Row is { Games: >= 30 })
            .OrderByDescending(x => x.Row!.Delta).Take(take)
            .Select(x =>
            {
                double score = scale.Score(x.Row!.Delta);
                return new { name = x.Aug.DisplayName, icon = x.Aug.Icon, desc = x.Aug.DisplayDescription, score, grade = AugmentScale.Grade(score),
                             games = x.Row.Games, confidence = AugmentScale.Confidence(x.Row.Games) };
            })
            .ToList();

        // Para qué sirve su kit (campeones-kit.json) y los estilos de build que se juegan con él (aramkit), con sus 4 objetos clave.
        var kit = _kits.For(info?.Alias ?? alias);
        var styles = _aramkitBuilds.Styles(info?.Alias ?? alias);
        int styleGames = styles.Sum(s => s.Games);
        var styleRows = styles.Where(s => s.Games >= styleGames * 0.03).Select(s => new
        {
            name = Advisor.StyleName(s.Key), share = (double)s.Games / Math.Max(1, styleGames), winRate = s.WinRate / 100,
            items = s.Items.Where(kv => _data.Item(kv.Key) is { IsBoots: false, InStore: true }).OrderByDescending(kv => kv.Value).Take(4)
                .Select(kv => new { name = _data.Item(kv.Key)!.DisplayName, icon = _data.Item(kv.Key)!.Icon }),
        }).ToList();

        return new
        {
            type = "champion", alias = info?.Alias ?? alias, name = info?.DisplayName ?? alias, icon = info?.Icon,
            roles = (info?.Roles ?? []).Select(RoleName),
            splash = $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/characters/{lower}/skins/base/images/{lower}_splash_uncentered_0.jpg",
            tier = meta?.Tier, winRate = meta?.WinRate / 100, games = meta?.Games,
            mine = mine.Count == 0 ? null : new { games = mine.Count, wins = mine.Count(m => m.Win), kda = Math.Round(mine.Sum(m => m.Kills + m.Assists) / (double)Math.Max(1, mine.Sum(m => m.Deaths)), 1) },
            items,
            augments = new { prismatic = Augs("kPrismatic", 8), gold = Augs("kGold", 8), silver = Augs("kSilver", 8) },
            kit = kit is null ? null : new
            {
                scales = kit.ScalesWith(), onHit = ChampionKit.Keys(kit.OnHit), attackSpeed = ChampionKit.Keys(kit.AttackSpeed),
                crit = ChampionKit.Keys(kit.Crit), heal = ChampionKit.Keys(kit.Heal), shield = ChampionKit.Keys(kit.Shield),
                stacks = ChampionKit.Keys(kit.Stacks), late = kit.Late,
            },
            styles = styleRows,
            tips = ChampionTips(info?.Alias ?? alias, meta, build, styles, kit),
            // Órdenes de subir habilidades (aramkit): parte de las partidas y % de victorias (de campeones, que sí se puede).
            skills = SkillRows(info?.Alias ?? alias),
        };
    }

    static string RoleName(string role) => role switch
    {
        "mage" => L("Mago", "Mage"), "support" => L("Apoyo", "Support"), "marksman" => L("Tirador", "Marksman"),
        "assassin" => L("Asesino", "Assassin"), "fighter" => L("Luchador", "Fighter"), "tank" => L("Tanque", "Tank"), _ => role,
    };
}
