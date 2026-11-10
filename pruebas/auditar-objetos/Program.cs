// Auditoría de OBJETOS con todos los campeones: la app, por fuera, en miles de situaciones, y se comprueba que nada de lo
// que recomienda es imposible o absurdo (un objeto que no se vende, dos que la tienda no deja llevar juntos, dos botas,
// repetir lo que ya tienes, un objeto del daño contrario a tu build, vender lo que te acaba de recomendar…).
//   A. Partida entera siguiendo a la app: cada campeón × cada build (la de la app y cada una fijada) × 4 equipos enemigos,
//      comprando siempre «lo siguiente» hasta llenar el inventario.
//   B. Cada objeto de la tienda como primer objeto de cada campeón (todos los objetos × todos los campeones).
//   C. Cada aumento que pide objetos (misiones, «Mejorar X») con cada campeón.
//   D. Cada aumento con cada campeón, sin objetos (solo con AUDIT_TODO=1: tarda varios minutos).
//   E. Los aumentos que se llaman igual que otro (de Arena, de otra rareza): que el lector de cartas elija el de Mayhem.
// Además: «traza <Campeón> [equipo 0-3] [build fijada]» enseña una partida paso a paso (qué recomienda y por qué); con
// TRAZA_OBJETO=<id> dice antes cuánto se compra ese objeto según la página del campeón y según cada build de aramkit.
// Uso: MAYHEM_DATA_DIR=<copia de tus datos y de datos\> dotnet run -c Release [-- detalle]
using System.Diagnostics;
using MayhemAdvisor.Core;

bool detail = args.Contains("detalle");
using var http = new HttpClient();
var catalog = await AugmentCatalog.LoadAsync(http);
var data = await GameData.LoadAsync(http);
var stats = StatsStore.Load(Path.Combine(Paths.DataDir, "no-existe.jsonl"), data);
var builds = MayhemBuilds.Load(DataFiles.Resolve("mayhem-champion-builds.json"));
var table = MayhemAugments.Load(DataFiles.Resolve("mayhem-augments.json"));
catalog = catalog.WithExtraDescriptions(table.Descriptions);
stats.Global = builds;
stats.AugmentTable = table;
stats.Aramhex = ExternalAugments.Load(DataFiles.Resolve("aramhex-augments.json"));
stats.Aramkit = ExternalAugments.Load(DataFiles.Resolve("aramkit-augments.json"));
stats.AramkitBuilds = AugmentBuilds.Load(DataFiles.Resolve("aramkit-builds.json"));
stats.Links = new AugmentItemLinks(catalog.Augments, data, builds.ItemNames);
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));

var advisor = new Advisor(stats);
var champions = data.Champions.Values.Select(c => c.Alias).Where(a => stats.AramkitBuilds.Styles(a).Count > 0).OrderBy(a => a).ToList();
var augments = catalog.Augments.Where(a => !a.IsAnvil).ToList();
string Name(int id) => data.Item(id)?.NameEn ?? id.ToString();
string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(Name));

// Equipos enemigos con objetos, para que salten todas las reglas «contra el enemigo».
int[] apKit = [3089, 4645, 3135], critKit = [3031, 3046, 3094], tankKit = [3083, 3084, 3075, 3065], healKit = [6617, 3107, 3504];
var teams = new (string Name, (string, IReadOnlyList<int>)[] Enemies)[]
{
    ("mixto", [("Garen", []), ("Lux", []), ("Jinx", []), ("Leona", []), ("Ahri", [])]),
    ("AP", [("Lux", apKit), ("Ahri", apKit), ("Veigar", apKit), ("Brand", apKit), ("Syndra", apKit)]),
    ("AD crítico", [("Jinx", critKit), ("Caitlyn", critKit), ("Yasuo", critKit), ("Tryndamere", critKit), ("Draven", critKit)]),
    ("tanques y curas", [("Soraka", healKit), ("DrMundo", tankKit), ("Sion", tankKit), ("Lulu", healKit), ("Aatrox", [3071, 6333, 3053])]),
};

// Fallos (algo imposible o incoherente) y avisos (raro, para mirar a mano): tipo → caso → cuántas veces.
var problems = new SortedDictionary<string, Dictionary<string, int>>();
var checks = new Dictionary<string, int>();
void Bad(string kind, string what)
{
    if (!problems.TryGetValue(kind, out var list)) problems[kind] = list = [];
    list[what] = list.GetValueOrDefault(what) + 1;
}
void Checked(string kind) => checks[kind] = checks.GetValueOrDefault(kind) + 1;

// Los objetos propios de ARAM llevan el id 12xxxx (Hubris es 126697); los 77xxxx son de otro modo de juego y no están en la tienda.
bool Buyable(ItemInfo i) => i.IsFinal && i.InStore && (i.Id < 10000 || i.Id / 10000 == 12) && i.PriceTotal > 0 && !i.Has("Lane") && !i.Has("Consumable") && !i.Has("Trinket")
                            && stats.SeenInMayhem(i.Id) && !stats.Links.IsReward(i.Id);
bool IsFinalOwned(int id) => data.Item(id) is { IsFinal: true, PriceTotal: > 0 } i && !i.Has("Consumable") && !i.Has("Trinket");
// Lo que se compra un objeto con el campeón (0-1): lo más alto entre sus builds (aramkit) y su página (mayhemmeta).
// La página cuenta los objetos que se transforman con el nombre del final (Invierno Nórdico por Umbral del Invierno).
var transformed = new Dictionary<int, int> { [3003] = 3040, [3004] = 3042, [3119] = 3121 };
double Share(string c, int id) => Math.Max(stats.AramkitBuilds.Styles(c).Select(s => s.Items.GetValueOrDefault(id)).DefaultIfEmpty(0).Max(),
    Math.Max(stats.GlobalItem(c, id)?.PickRate ?? 0, transformed.TryGetValue(id, out int later) ? stats.GlobalItem(c, later)?.PickRate ?? 0 : 0) / 100);
string[] adCats = ["Damage", "CriticalStrike", "ArmorPenetration"], apCats = ["SpellDamage", "MagicPenetration"];

/// Comprueba un consejo entero. `who` identifica el caso (campeón · build · equipo); `quest` = objetos que una misión deja llevar juntos.
void Check(string who, string champion, IReadOnlyList<int> items, IReadOnlyList<Augment> augs, Advice advice, string? locked)
{
    var owned = items.ToHashSet();
    var questItems = augs.Where(a => a.ApiName.Contains("Quest", StringComparison.OrdinalIgnoreCase)).SelectMany(a => stats.Links.RequiredBy(a.Id)).ToHashSet();
    bool Clash(int a, int b) => Advisor.SameGroup(a, b) && !(questItems.Contains(a) && questItems.Contains(b));
    var ownedFinals = items.Where(IsFinalOwned).Distinct().ToList();
    bool ownBoots = items.Any(id => data.Item(id)?.IsBoots == true);
    int slots = items.Select(data.Item).OfType<ItemInfo>().Count(i => i.PriceTotal > 0 && !i.Has("Trinket") && !i.Has("Consumable"));
    bool full = slots >= 6;

    // 1. Las 3 opciones.
    Checked("consejos");
    if (advice.Items.Select(a => a.Item.Id).Distinct().Count() != advice.Items.Count) Bad("FALLO opciones repetidas", who);
    foreach (var a in advice.Items)
    {
        if (!Buyable(a.Item)) Bad("FALLO opción que no se puede comprar", $"{a.Item.NameEn} ({a.Item.Id}) · {who} · {a.Reason}");
        if (a.Item.IsBoots) Bad("FALLO botas entre las 3 opciones", $"{champion}: {a.Item.NameEn}");
        if (owned.Contains(a.Item.Id)) Bad("FALLO opción que ya tienes", $"{champion}: {a.Item.NameEn}");
        if (ownedFinals.Any(o => Clash(o, a.Item.Id))) Bad("FALLO opción que choca con lo que llevas", $"{a.Item.NameEn} con {Names(ownedFinals.Where(o => Clash(o, a.Item.Id)))}");
        if (string.IsNullOrWhiteSpace(a.Reason)) Bad("aviso: opción sin motivo", $"{champion}: {a.Item.NameEn}");
    }
    if (!full && advice.Items.Count < 3) Bad("aviso: menos de 3 opciones", $"{champion} ({advice.Items.Count})");

    // 2. La build completa y la situacional.
    foreach (var (label, build) in new[] { ("build completa", advice.FullBuild), ("build situacional", advice.Plan?.Situational) })
    {
        if (build is null) { Bad($"FALLO sin {label}", who); continue; }
        var ids = build.Select(s => s.Item.Item.Id).ToList();
        if (ids.Distinct().Count() != ids.Count) Bad($"FALLO {label} con objetos repetidos", $"{who}: {Names(ids)}");
        if (build.Count > 6) Bad($"FALLO {label} con más de 6", $"{who}: {Names(ids)}");
        if (build.Count(s => s.Item.Item.IsBoots) > 1) Bad($"FALLO {label} con dos botas", $"{who}: {Names(ids)}");
        if (build.Count < 6) Bad($"aviso: {label} con menos de 6", $"{champion} ({build.Count})");
        for (int x = 0; x < ids.Count; x++)
            for (int y = x + 1; y < ids.Count; y++)
                if (Clash(ids[x], ids[y])) Bad($"FALLO {label} con dos objetos que no se pueden llevar juntos", $"{Name(ids[x])} + {Name(ids[y])}");
        foreach (var s in build.Where(s => !s.Owned))
        {
            if (!Buyable(s.Item.Item)) Bad($"FALLO {label} con un objeto que no se puede comprar", $"{s.Item.Item.NameEn} ({s.Item.Item.Id}) · {who} · {s.Item.Reason}");
            if (owned.Contains(s.Item.Item.Id)) Bad($"FALLO {label}: marca como no comprado algo que tienes", $"{champion}: {s.Item.Item.NameEn}");
        }
        foreach (var s in build.Where(s => s.Owned && !owned.Contains(s.Item.Item.Id))) Bad($"FALLO {label}: marca como comprado algo que no tienes", $"{champion}: {s.Item.Item.NameEn}");
        // Lo que ya tienes terminado sigue en la build (salvo lo que manda vender).
        foreach (var o in ownedFinals.Where(o => data.Item(o) is { IsBoots: false } && !ids.Contains(o) && advice.Swap?.Sell.Id != o).Take(label == "build completa" ? 9 : 0))
            if (ownedFinals.Count(f => data.Item(f)?.IsBoots == false) <= 5) Bad("FALLO build completa sin un objeto que ya tienes", $"{champion}: {Name(o)}");
    }

    // 3. Botas y siguiente compra.
    if (!ownBoots && !full)
    {
        if (advice.Boots is null) Bad("FALLO sin botas recomendadas", who);
        else if (!advice.Boots.Item.IsBoots || advice.Boots.Item.PriceTotal > 1300 || !Buyable(advice.Boots.Item)) Bad("FALLO botas raras", $"{advice.Boots.Item.NameEn}");
    }
    if (ownBoots && advice.Boots is not null) Bad("FALLO recomienda botas teniendo botas", who);
    if (!full)
    {
        if (advice.Next is null) Bad("FALLO sin siguiente compra", who);
        else
        {
            if (owned.Contains(advice.Next.Item.Id)) Bad("FALLO la siguiente compra ya la tienes", $"{champion}: {advice.Next.Item.NameEn}");
            if (advice.Next.Cost <= 0) Bad("FALLO siguiente compra gratis", $"{champion}: {advice.Next.Item.NameEn} ({advice.Next.Cost})");
            bool known = advice.Items.Any(a => a.Item.Id == advice.Next.Item.Id) || advice.Boots?.Item.Id == advice.Next.Item.Id;
            if (!known) Bad("FALLO la siguiente compra no es ninguna de las opciones", $"{who}: {advice.Next.Item.NameEn}");
            if (advice.FullBuild is { } fb && !advice.Next.Item.IsBoots && fb.All(s => s.Item.Item.Id != advice.Next.Item.Id))
                Bad("aviso: la siguiente compra no está en la build completa", $"{champion}: {advice.Next.Item.NameEn}");
        }
    }
    // 4. El cambio (vender X, comprar Y).
    if (advice.Swap is { } swap)
    {
        if (!owned.Contains(swap.Sell.Id)) Bad("FALLO vender algo que no tienes", $"{who}: {swap.Sell.NameEn}");
        if (swap.Sell.IsBoots) Bad("FALLO vender las botas", who);
        if (owned.Contains(swap.Buy.Item.Id)) Bad("FALLO cambiar por algo que ya tienes", $"{who}: {swap.Buy.Item.NameEn}");
        if (!Buyable(swap.Buy.Item)) Bad("FALLO cambiar por algo que no se compra", $"{swap.Buy.Item.NameEn}");
        if (ownedFinals.Any(o => o != swap.Sell.Id && Clash(o, swap.Buy.Item.Id))) Bad("FALLO el cambio choca con lo que te quedas", $"{swap.Buy.Item.NameEn} con {Names(ownedFinals.Where(o => o != swap.Sell.Id && Clash(o, swap.Buy.Item.Id)))}");
    }
    // 5. La pieza que conviene comprar pronto (Lágrima).
    if (advice.EarlyPart is { } early)
    {
        if (owned.Contains(early.Part.Id)) Bad("FALLO pieza temprana que ya tienes", who);
        if (advice.FullBuild?.Any(s => s.Item.Item.Id == early.For.Id && !s.Owned) != true) Bad("FALLO pieza temprana para un objeto que no está en la build", $"{champion}: {early.For.NameEn}");
    }
    // 6. Coherencia con el daño de tu build y con lo que se compra con el campeón (avisos para mirar a mano).
    bool magic = locked is "ap" || (locked is null && advisor.PlaysMagic(champion, items, augs));
    bool physical = locked is "crit" or "lethality" or "on_hit" or "ad" || (locked is null && !magic);
    var needed = augs.SelectMany(a => stats.Links.RequiredBy(a.Id)).ToHashSet();
    // Con un solo objeto (apartado B) se dice cuál: casi siempre es lo que explica el aviso.
    string With() => items.Count == 1 ? $" · llevando {Names(items)}" : "";
    foreach (var s in (advice.FullBuild ?? []).Where(s => !s.Owned && !s.Item.Item.IsBoots))
    {
        var i = s.Item.Item;
        if (needed.Contains(i.Id)) continue;
        bool adOnly = adCats.Any(i.Has) && !apCats.Any(i.Has) && !i.IsDefensive, apOnly = apCats.Any(i.Has) && !adCats.Any(i.Has) && !i.Has("AttackSpeed") && !i.IsDefensive;
        double share = Share(champion, i.Id);
        if (magic && locked is "ap" or null && adOnly && share < 0.1) Bad("aviso: objeto de AD en build de AP", $"{champion}: {i.NameEn} ({share:P0}) · {s.Item.Reason}{With()}");
        if (physical && apOnly && share < 0.1) Bad("aviso: objeto de AP en build de AD", $"{champion}: {i.NameEn} ({share:P0}) · {s.Item.Reason}{With()}");
        if (share < 0.01 && augs.Count == 0) Bad("aviso: objeto que casi nadie compra con el campeón (<1 %)", $"{champion}: {i.NameEn} · {s.Item.Reason}{With()}");
    }
}

// «traza Campeón [equipo 0-3] [build fijada]»: una partida paso a paso (estilo, las 3 opciones con su nota, lo siguiente y la build).
if (args.Length >= 2 && args[0] == "traza")
{
    var enemies = teams[args.Length > 2 ? int.Parse(args[2]) : 0].Enemies;
    string? lockedTo = args.Length > 3 ? args[3] : null;
    var mine = new List<int>();
    // TRAZA_OBJETO=<id>: cuánto se compra y cuánto gana ese objeto con el campeón, para ver por qué sale o no sale.
    if (int.TryParse(Environment.GetEnvironmentVariable("TRAZA_OBJETO"), out int watched))
    {
        var page = stats.Global.For(args[1]);
        Console.WriteLine($"{data.Item(watched)?.NameEn} con {args[1]} · página: lo compra el {stats.GlobalItem(args[1], watched)?.PickRate:0.#} %, gana el {stats.GlobalItem(args[1], watched)?.WinRate:0.#} % (el campeón, {page?.WinRate:0.#} %)");
        foreach (var s in stats.AramkitBuilds.Styles(args[1])) Console.WriteLine($"   build {s.Key}: {s.Items.GetValueOrDefault(watched):P0} ({s.Games} partidas)");
    }
    for (int step = 0; step < 9; step++)
    {
        var a = advisor.Build(args[1], mine, enemies, [], 99999, phase: step < 2 ? GamePhase.Early : GamePhase.Mid, lockedStyle: lockedTo);
        Console.WriteLine($"── paso {step}: tienes [{Names(mine)}]");
        Console.WriteLine($"   estilo: {string.Join(" | ", (a.Styles ?? []).Select(s => $"{s.Key}{(s.Active ? "*" : "")} {s.Share:P0}"))} · {a.StyleSource}");
        foreach (var o in a.Items) Console.WriteLine($"   opción {o.Item.NameEn,-26} {o.Score,7:0.000}  {o.Reason}");
        Console.WriteLine($"   botas: {a.Boots?.Item.NameEn} · siguiente: {a.Next?.Item.NameEn}");
        Console.WriteLine($"   build: {string.Join(", ", (a.FullBuild ?? []).Select(s => (s.Owned ? "✓" : "") + s.Item.Item.NameEn))}");
        if (a.Swap is { } sw) Console.WriteLine($"   CAMBIO: vende {sw.Sell.NameEn} por {sw.Buy.Item.NameEn} ({sw.Buy.Score:0.000}) · {sw.SellReason}");
        if (mine.Count >= 6 || a.Next is null) break;
        mine.Add(a.Next.Item.Id);
    }
    return;
}

var watch = Stopwatch.StartNew();
int crashes = 0;
Advice? Try(string who, Func<Advice> build)
{
    try { return build(); }
    catch (Exception e) { crashes++; Bad("FALLO la app revienta", $"{who}: {e.GetType().Name} {e.Message}"); return null; }
}

// ── A. Partida entera siguiendo a la app ────────────────────────────────────────────────────────────────────────────
int games = 0, sellsOwnAdvice = 0, planKept = 0, planTotal = 0, unfinished = 0;
var flips = new List<string>(); var changes = new List<(int Lost, string Text)>();
var recommendedBy = new Dictionary<int, HashSet<string>>(); // objeto → campeones a los que se les llega a recomendar
foreach (var c in champions)
{
    var lockOptions = new List<string?> { null };
    lockOptions.AddRange(stats.AramkitBuilds.Styles(c).Select(s => (string?)s.Key));
    foreach (var locked in lockOptions)
        foreach (var (teamName, enemies) in teams)
        {
            string who = $"{c} · {locked ?? "app"} · {teamName}";
            var items = new List<int>();
            List<int>? firstPlan = null;
            Advice? advice = null;
            for (int step = 0; step < 9; step++)
            {
                advice = Try(who, () => advisor.Build(c, items, enemies, [], 99999, phase: step < 2 ? GamePhase.Early : GamePhase.Mid, lockedStyle: locked));
                if (advice is null) break;
                Check(who, c, items, [], advice, locked);
                foreach (var s in advice.FullBuild ?? []) if (!s.Owned) (recommendedBy.TryGetValue(s.Item.Item.Id, out var set) ? set : recommendedBy[s.Item.Item.Id] = []).Add(c);
                firstPlan ??= (advice.FullBuild ?? []).Select(s => s.Item.Item.Id).ToList();
                if (items.Count >= 6 || advice.Next is null) break;
                items.Add(advice.Next.Item.Id);
            }
            if (advice is null) continue;
            games++;
            if (items.Count < 6) { unfinished++; Bad("FALLO la partida no llega a 6 objetos siguiendo a la app", $"{who}: {Names(items)}"); continue; }
            if (items.Count(id => data.Item(id)?.IsBoots == true) != 1) Bad("FALLO build final sin botas o con dos", $"{who}: {Names(items)}");
            // Con la build que ella misma te dio, ¿manda vender algo?
            if (advice.Swap is { } swap) { sellsOwnAdvice++; flips.Add($"{who}: vende {swap.Sell.NameEn} por {swap.Buy.Item.NameEn} · {swap.SellReason}"); }
            // ¿Cuánto de la build que enseñó al principio acabó comprándose? (si cambia de idea por el camino, confunde)
            var plan = firstPlan ?? [];
            int kept = plan.Count(items.Contains);
            planKept += kept; planTotal += plan.Count;
            if (plan.Count - kept >= 2) changes.Add((plan.Count - kept, $"{who}: enseñó {Names(plan)} → compró {Names(items)}"));
        }
}
Console.WriteLine($"A. {games} partidas enteras siguiendo a la app ({watch.Elapsed.TotalSeconds:0} s)");
Console.WriteLine($"   sin terminar: {unfinished} · al acabar manda vender algo que ella recomendó: {sellsOwnAdvice} · de la build del principio se acaba comprando el {(double)planKept / Math.Max(1, planTotal):P1}");
foreach (var f in flips.Take(detail ? 40 : 6)) Console.WriteLine("   vende: " + f);
Console.WriteLine($"   partidas donde cambian 2+ objetos del plan inicial: {changes.Count}");
foreach (var ch in changes.OrderByDescending(x => x.Lost).Take(detail ? 40 : 6)) Console.WriteLine("   cambia: " + ch.Text);

// Objetos de la tienda que no se le recomiendan nunca a nadie (ni en la build completa de ninguna partida de arriba).
var store = data.Items.Values.Where(i => Buyable(i) && !i.IsBoots).OrderBy(i => i.NameEn).ToList();
var never = store.Where(i => !recommendedBy.ContainsKey(i.Id)).ToList();
Console.WriteLine($"   objetos de la tienda: {store.Count} · nunca recomendados a nadie: {never.Count}");
foreach (var i in never)
{
    // ¿Con qué campeón se compra más? Si alguien lo compra mucho y aun así no sale, es un hueco.
    var top = champions.Select(c => (c, s: Share(c, i.Id))).OrderByDescending(x => x.s).First();
    Console.WriteLine($"     {i.NameEn,-28} más comprado con {top.c} ({top.s:P0})");
}

// ── B. Cada objeto de la tienda como primer objeto de cada campeón ──────────────────────────────────────────────────
watch.Restart();
int casesB = 0;
var allFinals = data.Items.Values.Where(Buyable).Select(i => i.Id).Concat([3040, 3042, 3121]).Distinct().ToList();
foreach (var c in champions)
    foreach (var id in allFinals)
    {
        string who = $"{c} con {Name(id)}";
        if (Try(who, () => advisor.Build(c, [id], teams[0].Enemies, [], 3000)) is not { } advice) continue;
        casesB++;
        Check(who, c, [id], [], advice, null);
    }
Console.WriteLine($"B. {casesB} casos de campeón + un objeto ({allFinals.Count} objetos, {watch.Elapsed.TotalSeconds:0} s)");

// ── C. Aumentos que piden objetos, con cada campeón ─────────────────────────────────────────────────────────────────
watch.Restart();
int casesC = 0;
var linked = augments.Where(a => stats.Links.RequiredBy(a.Id).Count > 0).ToList();
var ignored = new Dictionary<string, List<string>>();
var inMayhem = new HashSet<string>(); // los demás son del catálogo pero no salen en los datos de Mayhem (de Arena o de modos antiguos)
foreach (var a in linked)
    foreach (var c in champions)
    {
        string who = $"{c} + {a.Names.GetValueOrDefault("default")}";
        if (Try(who, () => advisor.Build(c, [], teams[0].Enemies, [a], 3000)) is not { } advice) continue;
        casesC++;
        if (table.HasAugment(a.Id)) inMayhem.Add(a.Names.GetValueOrDefault("default") ?? a.ApiName);
        Check(who, c, [], [a], advice, null);
        // ¿Sale en la build alguno de los objetos que pide? (si no, la app te deja el aumento a medias)
        var req = stats.Links.RequiredBy(a.Id);
        if (!(advice.FullBuild ?? []).Any(s => req.Contains(s.Item.Item.Id)))
            (ignored.TryGetValue(a.Names.GetValueOrDefault("default") ?? a.ApiName, out var l) ? l : ignored[a.Names.GetValueOrDefault("default") ?? a.ApiName] = []).Add(c);
    }
Console.WriteLine($"C. {casesC} casos de campeón + aumento que pide objetos ({linked.Count} aumentos, {watch.Elapsed.TotalSeconds:0} s)");
foreach (var (aug, list) in ignored.OrderByDescending(kv => kv.Value.Count))
    Console.WriteLine($"   «{aug}»{(inMayhem.Contains(aug) ? "" : " [sin datos de Mayhem]")} pide {Names(stats.Links.RequiredBy(linked.First(a => (a.Names.GetValueOrDefault("default") ?? a.ApiName) == aug).Id).Take(4))}: no sale en la build de {list.Count} campeones (p. ej. {string.Join(", ", list.Take(5))})");

// ── D. Cada aumento con cada campeón ────────────────────────────────────────────────────────────────────────────────
if (Environment.GetEnvironmentVariable("AUDIT_TODO") == "1")
{
    watch.Restart();
    int casesD = 0;
    foreach (var a in augments)
        foreach (var c in champions)
        {
            string who = $"{c} + {a.Names.GetValueOrDefault("default")}";
            if (Try(who, () => advisor.Build(c, [], teams[0].Enemies, [a], 3000)) is not { } advice) continue;
            casesD++;
            Check(who, c, [], [a], advice, null);
        }
    Console.WriteLine($"D. {casesD} casos de campeón + aumento ({watch.Elapsed.TotalSeconds:0} s)");
}

// ── E. Aumentos que se llaman igual ───────────────────────────────────────────────────────────────
// El catálogo del juego trae los aumentos de Arena y de modos antiguos junto a los de Mayhem, y algunos comparten nombre.
// Se le da al lector de cartas una tanda con ese nombre y otras dos cartas de la misma rareza: tiene que salir el de Mayhem.
{
    var matcher = new AugmentMatcher(catalog, table.HasAugment);
    int casesE = 0, wrongE = 0;
    var mayhem = augments.Where(a => table.HasAugment(a.Id)).ToList();
    foreach (var lang in augments.SelectMany(a => a.Names.Keys).Distinct())
        foreach (var same in augments.Where(a => a.Names.GetValueOrDefault(lang) is { Length: >= 4 })
                     .GroupBy(a => AugmentCatalog.Normalize(a.Names[lang])).Where(g => g.Count() > 1 && g.Any(a => table.HasAugment(a.Id))))
            foreach (var want in same.Where(a => table.HasAugment(a.Id)))
            {
                var twins = same.Where(a => a.Id != want.Id && table.HasAugment(a.Id) && a.Rarity == want.Rarity).ToList();
                string label = $"{lang} «{want.Names[lang]}» ({want.Id}, {want.Rarity})";
                if (twins.Count > 0) { Bad("aviso: dos aumentos de Mayhem con el mismo nombre y rareza", $"{label} y {twins[0].Id}"); continue; }
                var mates = mayhem.Where(a => a.Rarity == want.Rarity && !same.Contains(a) && a.Names.GetValueOrDefault(lang) is { Length: >= 6 }).Take(2).ToList();
                var lines = new List<OcrLine> { new(want.Names[lang], new Box(100, 100, 300, 30)) };
                for (int k = 0; k < mates.Count; k++) lines.Add(new(mates[k].Names[lang], new Box(600 + 500 * k, 100, 300, 30)));
                var read = matcher.Find(lines).FirstOrDefault(h => h.Bounds.X == 100)?.Augment;
                casesE++;
                if (read?.Id != want.Id)
                {
                    wrongE++;
                    Bad("FALLO carta leída como otro aumento que se llama igual", $"{label}: se lee {read?.Id.ToString() ?? "nada"} ({read?.Rarity}; los otros: {string.Join(", ", same.Where(a => a.Id != want.Id).Select(a => a.Id))})");
                }
            }
    Console.WriteLine($"E. {casesE} cartas con nombre repetido en algún idioma · mal leídas: {wrongE}");
}

Console.WriteLine($"\nConsejos comprobados: {checks.GetValueOrDefault("consejos"):N0} · veces que la app reventó: {crashes}");
foreach (var (kind, list) in problems.OrderBy(kv => kv.Key.StartsWith("FALLO") ? 0 : 1).ThenBy(kv => kv.Key))
{
    Console.WriteLine($"\n== {kind}: {list.Values.Sum()} veces, {list.Count} casos distintos");
    foreach (var (what, n) in list.OrderByDescending(kv => kv.Value).Take(detail ? 60 : 10)) Console.WriteLine($"   {n,5}× {what}");
}
