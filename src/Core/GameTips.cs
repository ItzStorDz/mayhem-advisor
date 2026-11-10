using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>
/// Consejos sacados de tus partidas (2026-11-05, «haz muchos más consejos para la app»): los de la pantalla de inicio, de
/// tus 20 últimas partidas con los 10 jugadores (partidas\) y el diario de la app (diario\); y los de «Para la próxima» en
/// el resumen de una partida. Solo salen si hay datos detrás, y comparan contigo mismo o con los demás jugadores de tus
/// partidas, no con números inventados. Sin % de victorias de aumentos (Riot no lo permite); los de campeones, objetos y
/// hechizos sí.
/// </summary>
public sealed partial class GameReports
{
    /// <summary>Hechizos por id (los de los datos del cliente) → nombre en inglés, como en los datos de mayhemmeta.</summary>
    static readonly Dictionary<int, string> SpellKeys = new()
    {
        [4] = "Flash", [6] = "Ghost", [14] = "Ignite", [21] = "Barrier", [7] = "Heal", [13] = "Clarity", [1] = "Cleanse", [3] = "Exhaust", [32] = "Mark",
    };

    // Objetos de ejemplo para los textos (el nombre sale del juego, en el idioma elegido).
    const int MortalReminder = 3033, Morellonomicon = 3165, Thornmail = 3075, MercurysTreads = 3111, PlatedSteelcaps = 3047;

    string ItemName(int id) => data.Item(id)?.DisplayName ?? id.ToString();
    string ChampName(P p) => Champ(p.Alias)?.DisplayName ?? p.Alias;
    string AliasOf(P p) => Champ(p.Alias)?.Alias ?? p.Alias;
    bool Owns(P p, Func<ItemInfo, bool> what) => p.Items.Select(data.Item).OfType<ItemInfo>().Any(what);

    /// <summary>Los rivales que se curan mucho: 1,6 veces la media de los 10 en esa partida, o curanderos por sus datos.</summary>
    List<P> EnemyHealers(Game g, P me)
    {
        double avg = g.Players.Average(p => (double)p.Heal);
        return g.Players.Where(p => p.Team != me.Team && ((avg > 0 && p.Heal > avg * 1.6) || stats.IsHealer(AliasOf(p)))).ToList();
    }

    static double EnemyMagicShare(Game g, P me)
    {
        var enemies = g.Players.Where(p => p.Team != me.Team).ToList();
        return enemies.Sum(p => (double)p.Md) / Math.Max(1, enemies.Sum(p => (double)p.Pd + p.Md));
    }

    static double KillShare(Game g, P p) => (p.K + p.A) / (double)Math.Max(1, g.Players.Where(o => o.Team == p.Team).Sum(o => o.K));

    /// <summary>
    /// Si llevaste otros hechizos que los que más ganan con ese campeón en Caos (mayhemmeta, 100+ partidas con cada pareja y
    /// 1,5 puntos o más de diferencia), el consejo; si no, null.
    /// </summary>
    string? SpellTip(string alias, int[] spells)
    {
        if (spells.Length != 2 || !SpellKeys.TryGetValue(spells[0], out var a) || !SpellKeys.TryGetValue(spells[1], out var b)) return null;
        if (stats.Global.For(alias) is not { Games: > 0 } build || SummonerSpells.Recommend(build) is not { Keys: { Count: 2 } best } advice) return null;
        bool Same(IReadOnlyList<string> pair) => pair.Count == 2 && pair.Contains(a, StringComparer.OrdinalIgnoreCase) && pair.Contains(b, StringComparer.OrdinalIgnoreCase);
        if (Same(best)) return null;
        var mine = build.Spells.FirstOrDefault(c => Same(c.Spells));
        double? mineRate = mine is { WinRate: { } wr, PickRate: { } pick } && build.Games.Value * pick / 100 >= 100 ? wr / 100 : null;
        if (mineRate is { } r && advice.WinRate - r < 0.015) return null;
        string name = data.ChampionName(alias), yours = SummonerSpells.PairName([a, b]), better = SummonerSpells.PairName(best);
        return mineRate is { } m
            ? LF($"Con {name} llevaste {yours}; en Caos {better} gana más con él: {advice.WinRate:P1} frente a {m:P1}.",
                $"With {name} you took {yours}; in Mayhem {better} wins more on it: {advice.WinRate:P1} vs {m:P1}.")
            : LF($"Con {name} llevaste {yours}, que casi nadie lleva con él en Caos; {better} gana el {advice.WinRate:P1}.",
                $"With {name} you took {yours}, which almost nobody takes on it in Mayhem; {better} wins {advice.WinRate:P1}.");
    }

    // ───────────── Una partida («Para la próxima») ─────────────

    /// <summary>Lo que pasó en una partida y conviene mirar, con su peso (más peso, antes sale).</summary>
    List<(double Weight, string Text)> GameRuleTips(Game g, P me)
    {
        var tips = new List<(double, string)>();
        var healers = EnemyHealers(g, me);
        if (healers.Count >= 2 && !Owns(me, i => i.AppliesWounds))
            tips.Add((2.5, LF($"El rival se curaba mucho ({string.Join(", ", healers.Take(3).Select(ChampName))}) y no llevabas heridas graves: {ItemName(MortalReminder)}, {ItemName(Morellonomicon)} o {ItemName(Thornmail)}, según tu build (la pestaña «Adaptar» del panel te dice cuál).",
                $"The enemy healed a lot ({string.Join(", ", healers.Take(3).Select(ChampName))}) and you had no grievous wounds: {ItemName(MortalReminder)}, {ItemName(Morellonomicon)} or {ItemName(Thornmail)}, depending on your build (the panel's “Adapt” tab tells you which).")));
        double magic = EnemyMagicShare(g, me);
        if (magic >= 0.65 && me.D >= 7 && !Owns(me, i => i.Has("SpellBlock")))
            tips.Add((2.2, LF($"El {magic:P0} del daño rival era mágico y moriste {me.D} veces sin resistencia mágica: unas {ItemName(MercurysTreads)} o un objeto con resistencia mágica te habrían ayudado.",
                $"{magic:P0} of the enemy damage was magic and you died {me.D} times with no magic resist: {ItemName(MercurysTreads)} or an item with magic resist would have helped.")));
        if (magic <= 0.30 && me.D >= 7 && !Owns(me, i => i.Has("Armor")))
            tips.Add((2.2, LF($"El {1 - magic:P0} del daño rival era físico y moriste {me.D} veces sin armadura: unas {ItemName(PlatedSteelcaps)} o un objeto con armadura te habrían ayudado.",
                $"{1 - magic:P0} of the enemy damage was physical and you died {me.D} times with no armor: {ItemName(PlatedSteelcaps)} or an item with armor would have helped.")));
        if (SpellTip(AliasOf(me), me.Spells) is { } spells) tips.Add((1.5, spells));
        double kp = KillShare(g, me);
        if (kp < 0.4 && g.Players.Where(p => p.Team == me.Team).Sum(p => p.K) >= 15)
            tips.Add((1.2, LF($"Participaste en el {kp:P0} de los asesinatos de tu equipo: en ARAM se gana peleando juntos, sin quedarte lejos ni adelantarte solo.",
                $"You took part in {kp:P0} of your team's kills: ARAM is won by fighting together, without hanging back or diving alone.")));
        return tips;
    }

    // ───────────── Pantalla de inicio ─────────────

    /// <summary>
    /// Consejos de tus últimas partidas, del más útil al menos (como mucho 8). Vacío con menos de 5 partidas.
    /// </summary>
    public List<string> HomeTips(List<MyMatch> all)
    {
        var recent = all.Take(20).ToList();
        if (recent.Count < 5) return [];
        var tips = new List<(double Weight, string Text)>();
        // Las partidas con los 10 jugadores (se guardan las 20 últimas) y los diarios de la app de esas partidas.
        var games = recent.Select(m => (M: m, G: Load(m.Id)))
            .Where(x => x.G is { } g && g.Players.Any(p => p.Id == g.Me))
            .Select(x => (x.M, G: x.G!, Me: x.G!.Players.First(p => p.Id == x.G!.Me))).ToList();
        var journals = recent.Select(m => (M: m, J: GameJournal.Find(m.Champion, m.Date, m.Duration)))
            .Where(x => x.J is not null).Select(x => (x.M, J: x.J!.WithRealPicks(x.M.Augments))).ToList();

        // Tus campeones: con el que más ganas y con el que menos (5+ partidas: con 3 salía «ganas el 0 %» con 0 de 3).
        var champs = all.GroupBy(m => m.Champion).Where(g => g.Count() >= 5)
            .Select(g => (Name: data.ChampionName(g.Key), Games: g.Count(), WinRate: g.Count(m => m.Win) / (double)g.Count())).ToList();
        if (champs.OrderByDescending(c => c.WinRate).FirstOrDefault() is { WinRate: >= 0.6 } good)
            tips.Add((1.6, LF($"Con {good.Name} ganas el {good.WinRate:P0} ({good.Games} partidas): si te sale en el banco, cógelo.",
                $"You win {good.WinRate:P0} with {good.Name} ({good.Games} games): grab it if it shows up on the bench.")));
        if (champs.OrderBy(c => c.WinRate).FirstOrDefault() is { WinRate: <= 0.34 } bad)
            tips.Add((1.4, LF($"Con {bad.Name} solo ganas el {bad.WinRate:P0} ({bad.Games} partidas): mira su página en «Campeones» para ver qué build gana más.",
                $"You only win {bad.WinRate:P0} with {bad.Name} ({bad.Games} games): check its page in “Champions” to see which build wins more.")));

        if (games.Count >= 5)
        {
            // Muertes y participación: contigo frente a los demás jugadores de tus mismas partidas.
            double myDeaths = games.Average(x => x.Me.D), lobbyDeaths = games.SelectMany(x => x.G.Players.Where(p => p.Id != x.Me.Id)).Average(p => p.D);
            if (myDeaths >= lobbyDeaths + 2 && myDeaths >= 7)
                tips.Add((3, LF($"Mueres {myDeaths:0.#} veces por partida y los demás jugadores de tus partidas, {lobbyDeaths:0.#}: un objeto defensivo antes (la pestaña «Adaptar» del panel te dice cuál) o pelear más cerca de tu equipo puede cambiarlo.",
                    $"You die {myDeaths:0.#} times per game and the other players in your games {lobbyDeaths:0.#}: an earlier defensive item (the panel's “Adapt” tab tells you which) or fighting closer to your team can change that.")));
            double myKp = games.Average(x => KillShare(x.G, x.Me)), lobbyKp = games.SelectMany(x => x.G.Players.Where(p => p.Id != x.Me.Id).Select(p => KillShare(x.G, p))).Average();
            if (myKp < lobbyKp - 0.12)
                tips.Add((2.2, LF($"Participas en el {myKp:P0} de los asesinatos de tu equipo; los demás jugadores de tus partidas, en el {lobbyKp:P0}. En ARAM las peleas se ganan juntos: no te quedes lejos ni te adelantes solo.",
                    $"You take part in {myKp:P0} of your team's kills; the other players in your games in {lobbyKp:P0}. ARAM fights are won together: don't hang back or dive alone.")));

            // Daño con campeones de daño (no tanques ni apoyos): tu puesto entre los 10.
            var damageGames = games.Where(x => data.ChampionsById.GetValueOrDefault(int.TryParse(x.Me.Alias, out var id) ? id : 0)?.Roles.FirstOrDefault() is not ("tank" or "support"))
                .Select(x => Scores(x.G.Players)[x.Me.Id].Ranks["dmg"]).ToList();
            if (damageGames.Count >= 4 && damageGames.Average() is var rank)
            {
                if (rank >= 6.5)
                    tips.Add((2, LF($"Con campeones de daño sueles quedar el {rank:0}.º de 10 en daño: mira si tus aumentos van con tu build (en la carta, «⚡ …») y sigue «Compra ahora» del panel.",
                        $"On damage champions you usually place #{rank:0} of 10 in damage: check whether your augments fit your build (on the card, “⚡ …”) and follow the panel's “Buy now”.")));
                else if (rank <= 2.5)
                    tips.Add((0.6, LF($"Con campeones de daño sueles ser de los que más pegan: {rank:0.#}.º de 10 de media. ¡Así se juega!",
                        $"On damage champions you're usually among the top damage dealers: #{rank:0.#} of 10 on average. Well played!")));
            }
        }

        if (games.Count >= 3)
        {
            // Heridas graves contra mucha curación, y resistencias contra un tipo de daño (cuando además moriste mucho).
            var noWounds = games.Where(x => EnemyHealers(x.G, x.Me).Count >= 2 && !Owns(x.Me, i => i.AppliesWounds)).ToList();
            if (noWounds.Count >= 2)
            {
                var names = noWounds.SelectMany(x => EnemyHealers(x.G, x.Me)).GroupBy(ChampName).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key);
                tips.Add((2.6, LF($"En {noWounds.Count} de tus últimas partidas el rival se curaba mucho ({string.Join(", ", names)}…) y no llevabas heridas graves: {ItemName(MortalReminder)}, {ItemName(Morellonomicon)} o {ItemName(Thornmail)}, según tu build.",
                    $"In {noWounds.Count} of your recent games the enemy healed a lot ({string.Join(", ", names)}…) and you had no grievous wounds: {ItemName(MortalReminder)}, {ItemName(Morellonomicon)} or {ItemName(Thornmail)}, depending on your build.")));
            }
            int noMr = games.Count(x => EnemyMagicShare(x.G, x.Me) >= 0.65 && x.Me.D >= 7 && !Owns(x.Me, i => i.Has("SpellBlock")));
            if (noMr >= 2)
                tips.Add((2.3, LF($"En {noMr} partidas contra mucho daño mágico moriste 7 veces o más sin nada de resistencia mágica: unas {ItemName(MercurysTreads)} o un objeto con resistencia mágica suelen bastar.",
                    $"In {noMr} games against heavy magic damage you died 7+ times with no magic resist at all: {ItemName(MercurysTreads)} or one item with magic resist is usually enough.")));
            int noArmor = games.Count(x => EnemyMagicShare(x.G, x.Me) <= 0.30 && x.Me.D >= 7 && !Owns(x.Me, i => i.Has("Armor")));
            if (noArmor >= 2)
                tips.Add((2.3, LF($"En {noArmor} partidas contra mucho daño físico moriste 7 veces o más sin nada de armadura: unas {ItemName(PlatedSteelcaps)} o un objeto con armadura suelen bastar.",
                    $"In {noArmor} games against heavy physical damage you died 7+ times with no armor at all: {ItemName(PlatedSteelcaps)} or one item with armor is usually enough.")));
            // Hechizos: el campeón que más jugaste en estas partidas, con los que llevaste la última vez.
            foreach (var g in games.GroupBy(x => AliasOf(x.Me)).OrderByDescending(g => g.Count()))
                if (SpellTip(g.Key, g.First().Me.Spells) is { } spell) { tips.Add((1.8, spell)); break; }
            int pentas = games.Sum(x => x.Me.Penta);
            if (pentas > 0)
                tips.Add((0.4, LF($"¡{pentas} pentakill{(pentas > 1 ? "s" : "")} en tus últimas partidas!", $"{pentas} pentakill{(pentas > 1 ? "s" : "")} in your recent games!")));
        }

        // Del diario de la app: oro sin gastar al morir, cartas elegidas y cuánto se siguió su build.
        // El oro que te quedó AL REAPARECER (desde la 1.8.3): el de al morir no dice nada, muerto estás en la tienda y compras.
        var richDeaths = journals.Select(x => x.J.Deaths.Count(d => d.Left is >= 1300)).ToList();
        if (richDeaths.Sum() >= 3 && richDeaths.Count(n => n > 0) >= 2)
            tips.Add((2.4, LF($"En tus últimas partidas volviste a pelear {richDeaths.Sum()} veces con más de 1.300 de oro sin gastar: muerto estás en la tienda, compra antes de reaparecer (el panel se abre solo en «Compra»).",
                $"In your recent games you went back to fight {richDeaths.Sum()} times with 1,300+ unspent gold: when dead you're in the shop, buy before respawning (the panel opens on “Buy” by itself).")));
        var picks = journals.SelectMany(x => x.J.Picks).Where(p => p.Chosen > 0 && p.Offered.Count >= 2 && p.Offered.Any(c => c.Id == p.Chosen)).ToList();
        int lower = picks.Count(p => p.Offered.First(c => c.Id == p.Chosen).Score < p.Offered.Max(c => c.Score) - 15);
        if (picks.Count >= 5 && lower >= 3 && lower >= picks.Count * 0.3)
            tips.Add((1.7, LF($"En {lower} de tus {picks.Count} últimas elecciones de aumento cogiste una carta con 15 puntos o más por debajo de la mejor. Si no buscabas algo concreto (un combo, tu build), la de mejor nota suele rendir más con tu campeón.",
                $"In {lower} of your last {picks.Count} augment picks you took a card 15+ points below the best one. Unless you were going for something (a combo, your build), the top-scored card usually performs better on your champion.")));
        else if (picks.Count >= 8 && lower == 0)
            tips.Add((0.5, LF($"En tus {picks.Count} últimas elecciones nunca te alejaste mucho de la carta con mejor nota. Bien hecho.",
                $"In your last {picks.Count} picks you never strayed far from the top-scored card. Nicely done.")));
        var builds = journals.Where(x => x.J.FinalBuild.Count >= 5).Select(x => x.J.FinalBuild.Count(id => x.M.Items.Contains(id))).ToList();
        if (builds.Count >= 3 && builds.Average() is var match and < 2.5)
            tips.Add((1.3, LF($"Tu build final coincide de media en {match:0.#} de 6 objetos con la que recomendaba la app. Si una compra no te convence, «Otra opción ›» del panel te da la siguiente; y si prefieres otra build entera, fíjala en «TIPO DE BUILD».",
                $"Your final build matches the app's in {match:0.#} of 6 items on average. If a purchase doesn't convince you, the panel's “Other option ›” gives you the next one; and if you'd rather play another build, lock it in “BUILD TYPE”.")));

        return tips.OrderByDescending(t => t.Weight).Select(t => t.Text).Distinct().Take(8).ToList();
    }
}
