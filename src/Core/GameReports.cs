using System.Text.Json;
using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>
/// Resumen de tus partidas para la pestaña «Partidas»: la partida completa del cliente de LoL (<c>partidas\</c>, los 10
/// jugadores) junto con el diario de la app (<see cref="GameJournal"/>: cartas con su nota, compras recomendadas, muertes).
/// Todo sale de tu PC: no se pide nada a ningún servidor.
/// </summary>
public sealed partial class GameReports(GameData data, AugmentCatalog catalog, StatsStore stats)
{
    /// <summary>Un jugador de la partida con lo que hace falta para el resumen.</summary>
    sealed record P(int Id, int Team, string Alias, string Player, bool Win, int K, int D, int A, int Dmg, int Taken, int Mitigated,
                    int Heal, int Gold, int Cs, int Level, int[] Items, int[] Augs, int[] Spells, int Double, int Triple, int Quadra,
                    int Penta, int Spree, int Cc, int Pd, int Md)
    {
        public double Kda => (K + A) / (double)Math.Max(1, D);
        public int Tank => Taken + Mitigated;
    }

    sealed record Game(string Id, int Me, long Created, int Duration, string Version, List<P> Players);

    // ───────────── Lectura ─────────────

    static Game? Load(string id)
    {
        try
        {
            var path = MatchSync.ReportPath(id);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var g = root.GetProperty("game");
            var names = new Dictionary<int, string>();
            if (g.TryGetProperty("participantIdentities", out var ids))
                foreach (var pi in ids.EnumerateArray())
                    if (pi.TryGetProperty("player", out var pl))
                        names[Int(pi, "participantId")] = Str(pl, "gameName") is { Length: > 0 } gn ? gn : Str(pl, "summonerName");
            var players = new List<P>();
            foreach (var p in g.GetProperty("participants").EnumerateArray())
            {
                var st = p.GetProperty("stats");
                int pid = Int(p, "participantId");
                players.Add(new P(pid, Int(p, "teamId"), p.GetProperty("championId").GetInt32().ToString(), names.GetValueOrDefault(pid, ""),
                    st.TryGetProperty("win", out var w) && w.ValueKind == JsonValueKind.True,
                    Int(st, "kills"), Int(st, "deaths"), Int(st, "assists"), Int(st, "totalDamageDealtToChampions"), Int(st, "totalDamageTaken"),
                    Int(st, "damageSelfMitigated"), Int(st, "totalHeal"), Int(st, "goldEarned"), Int(st, "totalMinionsKilled") + Int(st, "neutralMinionsKilled"),
                    Int(st, "champLevel"),
                    Enumerable.Range(0, 7).Select(i => Int(st, $"item{i}")).Where(i => i > 0).ToArray(),
                    Enumerable.Range(1, 6).Select(i => Int(st, $"playerAugment{i}")).Where(i => i > 0).ToArray(),
                    [Int(p, "spell1Id"), Int(p, "spell2Id")],
                    Int(st, "doubleKills"), Int(st, "tripleKills"), Int(st, "quadraKills"), Int(st, "pentaKills"), Int(st, "largestKillingSpree"),
                    Int(st, "timeCCingOthers"), Int(st, "physicalDamageDealtToChampions"), Int(st, "magicDamageDealtToChampions")));
            }
            return new Game(id, Int(root, "me"), Long(g, "gameCreation"), Int(g, "gameDuration"), Str(g, "gameVersion"), players);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
    static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    ChampionInfo? Champ(string idOrAlias) =>
        int.TryParse(idOrAlias, out var n) ? data.ChampionsById.GetValueOrDefault(n) : data.Champion(idOrAlias);

    /// <summary>La ilustración del campeón (la misma que usa su ficha en «Campeones»).</summary>
    static string Splash(string alias) =>
        $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/characters/{alias.ToLowerInvariant()}/skins/base/images/{alias.ToLowerInvariant()}_splash_uncentered_0.jpg";

    static readonly Dictionary<int, string> SpellIcons = new()
    {
        [4] = "summoner_flash", [6] = "summoner_haste", [14] = "summonerignite", [21] = "summonerbarrier", [7] = "summoner_heal",
        [13] = "summonermana", [1] = "summoner_boost", [3] = "summoner_exhaust", [32] = "summoner_mark",
    };
    static string? SpellIcon(int id) => SpellIcons.TryGetValue(id, out var f)
        ? $"https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/data/spells/icons2d/{f}.png" : null;

    object ItemDto(int id) => data.Item(id) is { } i ? new { id, name = i.DisplayName, icon = i.Icon } : new { id, name = id.ToString(), icon = "" };

    object AugDto(int id) => catalog.Get(id) is { } a ? new { id, name = a.DisplayName, icon = a.Icon } : new { id, name = id.ToString(), icon = "" };

    // ───────────── Puntuación ─────────────

    /// <summary>
    /// Puntuación de la partida (0-100) de cada jugador, por su puesto entre los 10 en cinco cosas: daño a campeones (30 %),
    /// KDA (25 %), participación en asesinatos (20 %), daño aguantado (recibido + mitigado, 15 %) y curación (10 %).
    /// El 1.º en todo saca 100; el 10.º en todo, 0.
    /// </summary>
    static Dictionary<int, (double Score, Dictionary<string, int> Ranks)> Scores(List<P> players)
    {
        var teamKills = players.GroupBy(p => p.Team).ToDictionary(g => g.Key, g => Math.Max(1, g.Sum(p => p.K)));
        double Kp(P p) => (p.K + p.A) / (double)teamKills[p.Team];
        var metrics = new (string Key, double Weight, Func<P, double> Value)[]
        {
            ("dmg", 0.30, p => p.Dmg), ("kda", 0.25, p => p.Kda), ("kp", 0.20, Kp), ("tank", 0.15, p => p.Tank), ("heal", 0.10, p => p.Heal),
        };
        int n = players.Count;
        var result = players.ToDictionary(p => p.Id, _ => (Score: 0.0, Ranks: new Dictionary<string, int>()));
        foreach (var (key, weight, value) in metrics)
        {
            foreach (var p in players)
            {
                int rank = 1 + players.Count(o => value(o) > value(p));
                result[p.Id].Ranks[key] = rank;
                var r = result[p.Id];
                result[p.Id] = (r.Score + weight * (n - rank) / Math.Max(1, n - 1) * 100, r.Ranks);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (Math.Round(kv.Value.Score, 1), kv.Value.Ranks));
    }

    public static string ScoreLabel(double score) => score switch
    {
        >= 80 => L("MVP", "MVP"),
        >= 65 => L("Muy buena", "Great"),
        >= 50 => L("Buena", "Good"),
        >= 35 => L("Normal", "Average"),
        _ => L("Floja", "Rough"),
    };

    static string Rank(Dictionary<string, int> ranks, string key) => $"#{ranks[key]}";

    // ───────────── Una partida ─────────────

    /// <summary>Todo el resumen de una partida tuya, o null si no está guardada la partida completa.</summary>
    public object? Detail(string id)
    {
        if (Load(id) is not { } g || g.Players.FirstOrDefault(p => p.Id == g.Me) is not { } me) return null;
        var scores = Scores(g.Players);
        double minutes = Math.Max(1, g.Duration / 60.0);
        var champ = Champ(me.Alias);
        string alias = champ?.Alias ?? me.Alias;
        var journal = GameJournal.Find(alias, g.Created, g.Duration)?.WithRealPicks(me.Augs.ToList());
        var allies = g.Players.Where(p => p.Team == me.Team).ToList();
        var enemies = g.Players.Where(p => p.Team != me.Team).ToList();
        int teamKills = Math.Max(1, allies.Sum(p => p.K));
        var (myScore, myRanks) = scores[me.Id];
        double best = scores.Values.Max(s => s.Score);
        int place = 1 + scores.Values.Count(s => s.Score > myScore);

        // Tipo de daño del equipo enemigo: con él se calcula la nota de los aumentos que la app no vio elegir.
        double enemyMagic = enemies.Sum(p => p.Md) / (double)Math.Max(1, enemies.Sum(p => p.Pd + p.Md));
        var mix = enemyMagic < 0.45 ? DamageMix.Physical : enemyMagic > 0.55 ? DamageMix.Magic : DamageMix.Mixed;
        var scale = AugmentScale.For(alias, mix, stats, catalog);

        var augs = me.Augs.Select(augId =>
        {
            var a = catalog.Get(augId);
            var rec = stats.Rate(alias, augId, mix);
            var pick = journal?.Picks.FirstOrDefault(pk => pk.Chosen == augId);
            var chosenCard = pick?.Offered.FirstOrDefault(c => c.Id == augId);
            double score = chosenCard?.Score ?? scale.Score(AugmentScale.Evidence(rec), AugmentScale.IsReliable(rec));
            var others = pick?.Offered.Where(c => c.Id != augId).Select(c => new
            {
                id = c.Id, name = catalog.Get(c.Id)?.DisplayName ?? c.Id.ToString(), icon = catalog.Get(c.Id)?.Icon ?? "", score = c.Score, grade = c.Grade,
            }).ToList();
            bool bestPick = others is null || others.All(o => o.score <= score + 0.5);
            return new
            {
                id = augId, name = a?.DisplayName ?? augId.ToString(), icon = a?.Icon ?? "", rarity = a?.Rarity ?? "",
                score, grade = chosenCard?.Grade ?? AugmentScale.Grade(score), atPick = chosenCard is not null,
                minute = pick?.Minute, games = rec.Games, confidence = AugmentScale.Confidence(rec),
                offered = others, best = bestPick,
                seen = journal is null || pick is not null,
            };
        }).ToList();
        double augScore = augs.Count > 0 ? Math.Round(augs.Average(x => x.score), 1) : 0;

        // Build: lo que acabaste llevando frente a lo que recomendaba la app al final, y sus compras recomendadas.
        var finalItems = me.Items.Where(i => data.Item(i) is { } it && !it.Has("Trinket")).ToList();
        var recommended = (journal?.FinalBuild ?? []).Select(i => new { item = ItemDto(i), owned = finalItems.Contains(i) }).ToList();
        var buys = (journal?.Buys ?? []).Select(b => new { min = b.Minute, item = ItemDto(b.ItemId), why = journal!.Lang == Lang.Code ? b.Reason : "", bought = finalItems.Contains(b.ItemId) }).ToList();

        var deaths = journal?.Deaths ?? [];
        var tips = Tips(me, myRanks, myScore, place, augs.Select(x => (x.name, x.score, x.grade, x.offered?.Select(o => (o.name, o.score, o.grade)).ToList(), (double?)x.minute)).ToList(), deaths)
            // Y lo que dicen los 10 jugadores de esa partida: curación rival, tipo de daño, hechizos, participación (GameTips.cs).
            .Concat(GameRuleTips(g, me).OrderByDescending(t => t.Weight).Select(t => t.Text)).Distinct().Take(5).ToList();

        object Player(P p)
        {
            var c = Champ(p.Alias);
            var (s, _) = scores[p.Id];
            return new
            {
                alias = c?.Alias ?? p.Alias, champion = c?.DisplayName ?? p.Alias, icon = c?.Icon ?? "", player = p.Player,
                k = p.K, d = p.D, a = p.A, kda = Math.Round(p.Kda, 2), dmg = p.Dmg, taken = p.Tank, heal = p.Heal, gold = p.Gold, level = p.Level,
                items = p.Items.Where(i => data.Item(i) is { } it && !it.Has("Trinket")).Select(ItemDto),
                augs = p.Augs.Select(AugDto), score = s, me = p.Id == me.Id, mvp = s >= best,
            };
        }

        object Stat(string key, string label, int value, bool perMinute = true) => new
        {
            key, label, value, perMin = perMinute ? Math.Round(value / minutes) : (double?)null,
            rank = myRanks.TryGetValue(key, out var r) ? r : 1 + g.Players.Count(o => Value(o, key) > value),
            max = g.Players.Max(o => Value(o, key)),
        };

        return new
        {
            id, date = g.Created, minutes = Math.Round(minutes, 1), win = me.Win, version = g.Version,
            champion = new { alias, name = champ?.DisplayName ?? alias, icon = champ?.Icon ?? "", splash = champ is null ? "" : Splash(champ.Alias) },
            level = me.Level, spells = me.Spells.Select(SpellIcon).Where(s => s is not null),
            k = me.K, d = me.D, a = me.A, kda = Math.Round(me.Kda, 2), kp = Math.Round((me.K + me.A) / (double)teamKills, 3),
            score = myScore, scoreLabel = ScoreLabel(myScore), place, mvp = myScore >= best,
            breakdown = new[]
            {
                new { label = L("Daño", "Damage"), rank = myRanks["dmg"] }, new { label = "KDA", rank = myRanks["kda"] },
                new { label = L("Participación", "Kill share"), rank = myRanks["kp"] }, new { label = L("Aguante", "Tanking"), rank = myRanks["tank"] },
                new { label = L("Curación", "Healing"), rank = myRanks["heal"] },
            },
            stats = new[]
            {
                Stat("dmg", L("Daño a campeones", "Damage to champions"), me.Dmg),
                Stat("tank", L("Daño aguantado", "Damage tanked"), me.Tank),
                Stat("heal", L("Curación", "Healing"), me.Heal),
                Stat("gold", L("Oro", "Gold"), me.Gold),
                Stat("cc", L("Control (s)", "Crowd control (s)"), me.Cc, perMinute: false),
            },
            multi = new { doubles = me.Double, triples = me.Triple, quadras = me.Quadra, pentas = me.Penta, spree = me.Spree },
            augments = augs, augScore, augGrade = AugmentScale.Grade(augScore),
            items = finalItems.Select(ItemDto), recommended, buys,
            buildMatch = recommended.Count > 0 ? recommended.Count(r => r.owned) : (int?)null,
            deaths = new { count = me.D, logged = deaths.Count, avgGold = deaths.Count > 0 ? (int)deaths.Average(x => x.Gold) : (int?)null,
                           list = deaths.Select(x => new { min = x.Minute, gold = x.Gold }) },
            tips, hasJournal = journal is not null,
            teams = new[] { allies, enemies }.Select(t => new
            {
                win = t[0].Win, kills = t.Sum(p => p.K), dmg = t.Sum(p => p.Dmg),
                players = t.OrderByDescending(p => scores[p.Id].Score).Select(Player),
            }),
        };
    }

    static int Value(P p, string key) => key switch
    {
        "dmg" => p.Dmg, "tank" => p.Tank, "heal" => p.Heal, "gold" => p.Gold, "cc" => p.Cc, _ => 0,
    };

    /// <summary>Consejos para la próxima partida, sacados de esta (como mucho 3).</summary>
    static List<string> Tips(P me, Dictionary<string, int> ranks, double score, int place,
        List<(string Name, double Score, string Grade, List<(string Name, double Score, string Grade)>? Offered, double? Minute)> augs,
        List<JournalDeath> deaths)
    {
        var tips = new List<string>();
        if (place == 1) tips.Add(L("Fuiste el mejor de los 10 jugadores en esta partida. ¡Así se juega!", "You were the best of all 10 players this game. Well played!"));
        // Con el oro que te quedó al reaparecer (desde la 1.8.3): el de al morir no vale, muerto se compra.
        var spent = deaths.Where(x => x.Left is >= 1300).ToList();
        if (spent.Count >= 2)
            tips.Add(L($"Volviste a la partida {spent.Count} veces con más de 1.300 de oro sin gastar (de media {(int)spent.Average(x => x.Left!.Value):N0}). Muerto estás en la tienda: compra antes de reaparecer.",
                       $"You respawned {spent.Count} times with 1,300+ unspent gold (avg {(int)spent.Average(x => x.Left!.Value):N0}). When dead you're in the shop: buy before you respawn."));
        foreach (var a in augs)
            if (a.Offered is { Count: > 0 } off && off.MaxBy(o => o.Score) is var top && top.Score >= a.Score + 20)
            {
                tips.Add(L($"Min {a.Minute:0}: elegiste «{a.Name}» ({a.Grade}) teniendo «{top.Name}» ({top.Grade}). Si no buscabas algo concreto, la otra rendía más con tu campeón.",
                           $"Min {a.Minute:0}: you took “{a.Name}” ({a.Grade}) over “{top.Name}” ({top.Grade}). Unless you had a plan, the other one performs better on your champion."));
                break;
            }
        if (ranks["tank"] == 1 && tips.Count < 3) tips.Add(L("Fuiste el que más daño aguantó de la partida: tu equipo pudo pegar detrás de ti.", "You tanked the most damage in the game: your team could hit from behind you."));
        if (ranks["dmg"] >= 8 && ranks["tank"] > 3 && tips.Count < 3)
            tips.Add(L($"Hiciste poco daño (puesto {ranks["dmg"]} de 10) y tampoco aguantaste mucho: revisa tu build en «Adaptar» del panel durante la partida.",
                       $"Low damage (#{ranks["dmg"]} of 10) and not much tanking either: check the “Adapt” tab of the panel during the game."));
        if (me.D >= 10 && tips.Count < 3) tips.Add(L($"Moriste {me.D} veces: en Mayhem cada muerte da mucho oro al rival; elige peleas con tu equipo cerca.",
                                                     $"You died {me.D} times: each death feeds the enemy a lot of gold; pick fights with your team nearby."));
        return tips.Take(3).ToList();
    }

    // ───────────── Lista y resumen del día ─────────────

    /// <summary>Una fila de la lista de partidas (con puestos y puntuación si está la partida completa).</summary>
    object Row(MyMatch m)
    {
        var c = data.Champion(m.Champion);
        var g = Load(m.Id);
        var me = g?.Players.FirstOrDefault(p => p.Id == g.Me);
        var sc = g is not null ? Scores(g.Players) : null;
        var ranks = me is not null ? sc![me.Id].Ranks : null;
        return new
        {
            id = m.Id, date = m.Date, win = m.Win, minutes = Math.Round(m.Duration / 60.0, 1),
            champion = new { name = c?.DisplayName ?? m.Champion, icon = c?.Icon ?? "" },
            k = m.Kills, d = m.Deaths, a = m.Assists, kda = Math.Round(m.Kda, 2),
            dmg = me?.Dmg ?? m.Damage, taken = me?.Tank, heal = me?.Heal, gold = m.Gold,
            dmgRank = ranks?["dmg"], takenRank = ranks?["tank"], healRank = ranks?["heal"],
            score = me is not null ? sc![me.Id].Score : (double?)null,
            augs = m.Augments.Select(AugDto), items = m.Items.Where(i => data.Item(i) is { } it && !it.Has("Trinket")).Select(ItemDto),
            players = g is null || me is null ? null : g.Players.OrderBy(p => p.Team != me.Team).Select(p => new { icon = Champ(p.Alias)?.Icon ?? "", name = p.Player, me = p.Id == g.Me }),
            hasReport = g is not null,
        };
    }

    /// <summary>Resumen de hoy (partidas desde las 00:00 de tu hora) y la lista de tus últimas partidas.</summary>
    public object List(int count = 20)
    {
        var all = MyMatches.Load();
        var today = all.Where(m => DateTimeOffset.FromUnixTimeMilliseconds(m.Date).LocalDateTime.Date == DateTime.Now.Date).ToList();
        var reports = today.Select(m => (M: m, G: Load(m.Id))).ToList();
        // Una partida guardada sin tu fila (el cliente no dijo cuál eras) no cuenta: antes tumbaba toda la pestaña.
        var mine = reports.Where(r => r.G is not null && r.G.Players.Any(p => p.Id == r.G.Me))
            .Select(r => (r.M, r.G, Me: r.G!.Players.First(p => p.Id == r.G.Me), Sc: Scores(r.G.Players))).ToList();
        object? summary = today.Count == 0 ? null : new
        {
            games = today.Count, wins = today.Count(m => m.Win), losses = today.Count(m => !m.Win),
            winRate = today.Count(m => m.Win) / (double)today.Count,
            minutes = Math.Round(today.Sum(m => m.Duration) / 60.0),
            k = today.Average(m => m.Kills), d = today.Average(m => m.Deaths), a = today.Average(m => m.Assists),
            kda = today.Sum(m => m.Kills + m.Assists) / (double)Math.Max(1, today.Sum(m => m.Deaths)),
            dmg = today.Average(m => m.Damage),
            score = mine.Count > 0 ? Math.Round(mine.Average(x => x.Sc[x.Me.Id].Score), 1) : (double?)null,
            multi = new
            {
                doubles = mine.Sum(x => x.Me.Double), triples = mine.Sum(x => x.Me.Triple),
                quadras = mine.Sum(x => x.Me.Quadra), pentas = mine.Sum(x => x.Me.Penta),
            },
            best = mine.Count > 0 ? mine.MaxBy(x => x.Sc[x.Me.Id].Score) is var b ? new
            {
                id = b.M.Id, champion = data.ChampionName(b.M.Champion), icon = data.Champion(b.M.Champion)?.Icon ?? "",
                score = b.Sc[b.Me.Id].Score, k = b.Me.K, d = b.Me.D, a = b.Me.A, augs = b.M.Augments.Select(AugDto),
            } : null : null,
            champions = today.GroupBy(m => m.Champion).Select(gr => new
            {
                name = data.ChampionName(gr.Key), icon = data.Champion(gr.Key)?.Icon ?? "", games = gr.Count(), wins = gr.Count(m => m.Win),
            }).OrderByDescending(x => x.games),
        };
        return new { today = summary, games = all.Take(count).Select(Row).ToList() };
    }
}
