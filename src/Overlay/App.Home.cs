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
                tips = Tips(all, mostPlayed),
            };
        }

        return new
        {
            type = "home",
            profile = profile is null ? null : new { name = profile.Name, tag = profile.Tag, level = profile.Level,
                icon = profile.Icon > 0 ? $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/profile-icons/{profile.Icon}.jpg" : null },
            stats,
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

    /// <summary>Consejos sacados de tus partidas (solo si hay datos suficientes para que digan algo).</summary>
    List<string> Tips(List<MyMatch> all, List<ChampStat> champs)
    {
        var tips = new List<string>();
        if (all.Count < 5)
        {
            tips.Add(L("Juega unas cuantas partidas de ARAM: Caos más y aquí verás consejos sacados de tus propias partidas.",
                       "Play a few more ARAM: Mayhem games and you'll see tips based on your own games here."));
            return tips;
        }
        if (champs.Where(c => c.Games >= 3).OrderByDescending(c => c.WinRate).FirstOrDefault() is { WinRate: >= 0.6 } good)
            tips.Add(L($"Con {good.Name} ganas el {good.WinRate:P0} ({good.Games} partidas): si te sale en el banco, cógelo.",
                       $"You win {good.WinRate:P0} with {good.Name} ({good.Games} games): grab it if it shows up on the bench."));
        if (champs.Where(c => c.Games >= 3).OrderBy(c => c.WinRate).FirstOrDefault() is { WinRate: <= 0.34 } bad)
            tips.Add(L($"Con {bad.Name} solo ganas el {bad.WinRate:P0} ({bad.Games} partidas): mira su pestaña en Campeones para ver qué compra la gente que gana.",
                       $"You only win {bad.WinRate:P0} with {bad.Name} ({bad.Games} games): check its Champions page to see what winners build."));
        var recent = all.Take(20).ToList();
        double deaths = recent.Average(m => m.Deaths);
        if (deaths >= 9)
            tips.Add(L($"Mueres de media {deaths:0.#} veces por partida: un objeto defensivo antes (la pestaña «Adaptar» del panel te dice cuál) puede ayudarte.",
                       $"You die {deaths:0.#} times per game on average: an earlier defensive item (the in-game “Adapt” tab tells you which) can help."));
        var lossesWithoutBoots = recent.Where(m => !m.Win).Count(m => !m.Items.Select(_data.Item).Any(i => i?.IsBoots == true));
        if (lossesWithoutBoots >= 3)
            tips.Add(L($"En {lossesWithoutBoots} de tus derrotas recientes no compraste botas.", $"In {lossesWithoutBoots} of your recent losses you didn't buy boots."));
        if (tips.Count == 0)
            tips.Add(L("Vas bien: sigue usando la nota de los aumentos y la pestaña «Adaptar» cuando el equipo enemigo vaya muy cargado a un tipo de daño.",
                       "Looking good: keep using the augment scores and the “Adapt” tab when the enemy team leans hard into one damage type."));
        return tips.Take(2).ToList();
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

        return new
        {
            type = "champion", alias = info?.Alias ?? alias, name = info?.DisplayName ?? alias, icon = info?.Icon,
            roles = (info?.Roles ?? []).Select(RoleName),
            splash = $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/characters/{lower}/skins/base/images/{lower}_splash_uncentered_0.jpg",
            tier = meta?.Tier, winRate = meta?.WinRate / 100, games = meta?.Games,
            mine = mine.Count == 0 ? null : new { games = mine.Count, wins = mine.Count(m => m.Win), kda = Math.Round(mine.Sum(m => m.Kills + m.Assists) / (double)Math.Max(1, mine.Sum(m => m.Deaths)), 1) },
            items,
            augments = new { prismatic = Augs("kPrismatic", 8), gold = Augs("kGold", 8), silver = Augs("kSilver", 8) },
        };
    }

    static string RoleName(string role) => role switch
    {
        "mage" => L("Mago", "Mage"), "support" => L("Apoyo", "Support"), "marksman" => L("Tirador", "Marksman"),
        "assassin" => L("Asesino", "Assassin"), "fighter" => L("Luchador", "Fighter"), "tank" => L("Tanque", "Tank"), _ => role,
    };
}
