// Mide lo bien que la app adivina los objetos con partidas reales guardadas (matches.jsonl + comunidad.jsonl).
// A cada jugador se le dan sus 2 primeros objetos terminados (el inventario se llena por orden de compra) y se mira
// cuántos de los que compró después salen en la build completa que recomienda la app y entre las 3 siguientes compras.
// Las estadísticas propias NO se cargan (serían las mismas partidas): solo los datos de todos los jugadores.
// Uso: MAYHEM_DATA_DIR=<copia de la carpeta de datos> dotnet run [-- detalle]
using System.Text.Json;
using MayhemAdvisor.Core;

bool detail = args.Contains("detalle");
// Cuántos objetos terminados se le dan a cada jugador (por defecto 2): 1 = principio de partida, 3 = media partida.
int given = int.TryParse(Environment.GetEnvironmentVariable("EVAL_OWNED"), out var n) ? n : 2;
using var http = new HttpClient();
var catalog = await AugmentCatalog.LoadAsync(http);
var data = await GameData.LoadAsync(http);
var stats = StatsStore.Load(Path.Combine(Paths.DataDir, "no-existe.jsonl"), data);
var builds = MayhemBuilds.Load(DataFiles.Resolve("mayhem-champion-builds.json"));
var table = MayhemAugments.Load(DataFiles.Resolve("mayhem-augments.json"));
catalog = catalog.WithExtraDescriptions(table.Descriptions);
// EVAL_EXTRA_DESC=<json {id: {text}}>: otras descripciones en inglés (p. ej. las de la wiki de LoL) además de las de
// mayhemmeta, para medir si leer mejor los aumentos acierta más objetos. Se suman sus estadísticas, no se pierden las de antes.
if (Environment.GetEnvironmentVariable("EVAL_EXTRA_DESC") is { Length: > 0 } extraFile)
{
    using var extraDoc = JsonDocument.Parse(File.ReadAllText(extraFile));
    var extra = extraDoc.RootElement.EnumerateObject()
        .Where(p => int.TryParse(p.Name, out _) && p.Value.TryGetProperty("text", out _))
        .ToDictionary(p => int.Parse(p.Name), p => p.Value.GetProperty("text").GetString() ?? "");
    var before = catalog.Augments.ToDictionary(a => a.Id, a => a.StatList.Length);
    var merged = catalog.Augments.Select(a => extra.TryGetValue(a.Id, out var t) && t != ""
        ? a with { Stats = a.StatList.Union(StatTags.FromDescription(t)).ToArray(), ExtraDescription = (a.ExtraDescription + " " + t).Trim() } : a).ToList();
    catalog = AugmentCatalog.FromList(merged);
    Console.WriteLine($"Descripciones extra: {extra.Count}; aumentos con más estadísticas: {catalog.Augments.Count(a => a.StatList.Length > before.GetValueOrDefault(a.Id))}" +
                      $"; sin ninguna: {before.Count(kv => kv.Value == 0)} → {catalog.Augments.Count(a => !a.IsAnvil && a.StatList.Length == 0)}");
}
// EVAL_STYLE_MINSHARE / EVAL_STYLE_MINGAMES: desde qué parte de la gente (y cuántas partidas) con tus aumentos se cambia
// a otro estilo de build (AugmentBuilds.Pick). Para medir si conviene cambiar antes (p. ej. a on-hit con aumentos on-hit).
if (double.TryParse(Environment.GetEnvironmentVariable("EVAL_STYLE_MINSHARE"), System.Globalization.CultureInfo.InvariantCulture, out var ms)) AugmentBuilds.MinShare = ms;
if (int.TryParse(Environment.GetEnvironmentVariable("EVAL_STYLE_MINGAMES"), out var mg)) AugmentBuilds.MinGames = mg;
stats.Global = builds;
stats.AugmentTable = table;
stats.Aramhex = ExternalAugments.Load(DataFiles.Resolve("aramhex-augments.json"));
stats.Aramkit = ExternalAugments.Load(DataFiles.Resolve("aramkit-augments.json"));
stats.AramkitBuilds = AugmentBuilds.Load(DataFiles.Resolve("aramkit-builds.json"));
stats.Links = new AugmentItemLinks(catalog.Augments, data, builds.ItemNames);
#if !ANTIGUO
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));
#endif

var matches = new Dictionary<string, MatchRecord>();
// nube.jsonl: las partidas que llegan a la nube (los 10 jugadores), exportadas a mano de la base de datos.
foreach (var file in new[] { "matches.jsonl", "comunidad.jsonl", "nube.jsonl" })
{
    var path = Path.Combine(Paths.DataDir, file);
    if (!File.Exists(path)) continue;
    foreach (var line in File.ReadLines(path))
        if (line.Trim() is { Length: > 0 } json && JsonSerializer.Deserialize<MatchRecord>(json) is { Players.Count: 10 } m)
            matches.TryAdd(string.Join(",", m.Players.Select(p => $"{p.Champion}:{p.PhysicalDamage}:{p.MagicDamage}")), m);
}

int[] onHitItems = [3153, 6672, 3302, 3124, 3091, 3115];
int[] critItems = [3031, 6676, 3036, 3033, 3072, 6675, 3046, 3094, 3508];
// Tipo de jugador por su build final y su campeón (para ver dónde acierta menos la app).
string Archetype(string champion, List<int> finals)
{
    bool support = data.Champion(champion)?.Roles.FirstOrDefault() == "support";
    string style = stats.AramkitBuilds.StyleFromItems(champion, finals) is { Probability: >= 0.75 } f ? f.Style.Key
        : stats.AramkitBuilds.Styles(champion).FirstOrDefault()?.Key ?? "?";
    int ap = finals.Count(id => data.Item(id)?.Has("SpellDamage") == true), crit = finals.Count(id => data.Item(id)?.Has("CriticalStrike") == true);
    if (ap >= 2 && crit >= 1 && style is "ap" or "hybrid") return "AP crítico";
    if (support) return style switch { "utility" => "soporte encantador", "ap" => "soporte AP", "tank" => "soporte tanque", _ => "soporte " + style };
    return style switch { "crit" => "crítico", "on_hit" => "on-hit", "ap" => "AP", "lethality" => "letalidad", "ad" => "AD", "bruiser" => "luchador",
        "tank" => "tanque", "utility" => "encantador", _ => style };
}
string StyleOf(IEnumerable<int> items) =>
    items.Count(onHitItems.Contains) >= 2 ? "on-hit" : items.Count(critItems.Contains) >= 2 ? "crítico" : "otros";

var totals = new Dictionary<string, (int Players, int BuildHits, int BuildPredicted, int Actual, int NextHits)>();
void Add(string key, int buildHits, int predicted, int actual, int nextHits)
{
    var t = totals.GetValueOrDefault(key);
    totals[key] = (t.Players + 1, t.BuildHits + buildHits, t.BuildPredicted + predicted, t.Actual + actual, t.NextHits + nextHits);
}

var advisor = new Advisor(stats);
// Por objeto: cuántas veces sale en la build recomendada (sin tenerlo) y cuántas de esas lo compró el jugador; y cuántas
// veces lo compró sin que se recomendara. Los que salen mucho y casi nadie compra son los que «sobran».
var perItem = new Dictionary<int, (int Shown, int Bought, int Missed)>();
var whyItem = new List<string>();
var detection = new Dictionary<string, (int Players, int Right, int Changed, int Usual)>();
var misses = new List<string>();
foreach (var m in matches.Values)
    foreach (var p in m.Players)
    {
        bool Final(int id) => data.Item(id) is { IsFinal: true, IsBoots: false, PriceTotal: > 0 } i && !i.Has("Trinket") && !i.Has("Consumable") && !stats.Links.IsReward(id);
        var finals = p.Items.Where(Final).Distinct().ToList();
        if (finals.Count < given + 2 || data.Champion(p.Champion) is null) continue;
        var owned = finals.Take(given).Concat(p.Items.Where(id => data.Item(id)?.IsBoots == true).Take(1)).ToList();
        var actual = finals.Skip(given).ToHashSet();
        var augments = p.Augments.Select(catalog.Get).OfType<Augment>().ToList();
        var enemies = m.Players.Where(e => e.Team != p.Team).Select(e => (e.Champion, (IReadOnlyList<int>)e.Items)).ToList();

        var advice = advisor.Build(p.Champion, owned, enemies, augments, 0);
        var predicted = (advice.FullBuild ?? []).Where(s => !s.Owned && !s.Item.Item.IsBoots).Select(s => s.Item.Item.Id).ToList();
        var next = advice.Items.Select(a => a.Item.Id).ToList();
        int buildHits = predicted.Count(actual.Contains), nextHits = next.Count(actual.Contains);
        foreach (var id in predicted) { var pi = perItem.GetValueOrDefault(id); perItem[id] = (pi.Shown + 1, pi.Bought + (actual.Contains(id) ? 1 : 0), pi.Missed); }
        // EVAL_OBJETO=<id>: con quién y por qué se recomienda ese objeto (para ver de dónde sale).
        if (int.TryParse(Environment.GetEnvironmentVariable("EVAL_OBJETO"), out var watch) && predicted.Contains(watch))
            whyItem.Add($"{p.Champion} ({Archetype(p.Champion, finals)}, {(actual.Contains(watch) ? "lo compró" : "no lo compró")}): {(advice.FullBuild ?? []).First(sl => sl.Item.Item.Id == watch).Item.Reason}");
        foreach (var id in actual.Where(a => !predicted.Contains(a))) { var pi = perItem.GetValueOrDefault(id); perItem[id] = (pi.Shown, pi.Bought, pi.Missed + 1); }
        string style = StyleOf(finals);
#if !ANTIGUO
        // ¿Acierta el estilo con 2 objetos? Se compara con el que sale con todos sus objetos.
        var styles = stats.AramkitBuilds.Styles(p.Champion);
        string usual = styles.FirstOrDefault()?.Key ?? "?";
        string early = advisor.StyleFromItems(p.Champion, owned)?.Key ?? usual;
        string full = stats.AramkitBuilds.StyleFromItems(p.Champion, finals) is { Probability: >= 0.75 } f ? f.Style.Key : usual;
        var dt = detection.GetValueOrDefault(full);
        detection[full] = (dt.Players + 1, dt.Right + (early == full ? 1 : 0), dt.Changed + (early != usual ? 1 : 0), dt.Usual + (full == usual ? 1 : 0));
#endif
        foreach (var key in new[] { "todos", $"tipo {Archetype(p.Champion, finals)}", p.Win ? "ganadores" : "perdedores" })
            Add(key, buildHits, predicted.Count, actual.Count, nextHits);
        if (detail && Environment.GetEnvironmentVariable("EVAL_TIPO") is { } only && Archetype(p.Champion, finals) == only && buildHits * 2 < actual.Count)
            misses.Add($"{p.Champion,-12} tiene {Names(owned)}{StyleText(p.Champion, owned)}\n   recomienda {Names(predicted)}\n   compró     {Names(actual)}");
    }

string StyleText(string champion, List<int> owned)
{
#if ANTIGUO
    return "";
#else
    return advisor.StyleFromItems(champion, owned) is { } st ? $"  → estilo {st.Key}" : "  → sin estilo";
#endif
}
string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => data.Item(id)?.NameEn ?? id.ToString()));

Console.WriteLine($"{matches.Count} partidas distintas");
Console.WriteLine($"{"grupo",-18} {"jugadores",9} {"build: aciertos",16} {"de lo comprado",15} {"3 siguientes",13}");
foreach (var (key, t) in totals.OrderBy(kv => kv.Key == "todos" ? 0 : 1).ThenBy(kv => kv.Key))
    Console.WriteLine($"{key,-18} {t.Players,9} {(double)t.BuildHits / Math.Max(1, t.BuildPredicted),16:P1} {(double)t.BuildHits / Math.Max(1, t.Actual),15:P1} {(double)t.NextHits / Math.Max(1, t.Players * 3),13:P1}");
Console.WriteLine();
Console.WriteLine("Estilo con todos sus objetos → ¿lo adivina con 2? (cambiado = no es el habitual del campeón)");
foreach (var (key, d) in detection.OrderByDescending(kv => kv.Value.Players))
    Console.WriteLine($"{key,-10} {d.Players,5} jugadores  acierto {(double)d.Right / d.Players,6:P0}  es el habitual {(double)d.Usual / d.Players,6:P0}  cambiado {d.Changed,4}");
foreach (var miss in misses.Take(30)) Console.WriteLine(miss);
foreach (var w in whyItem.GroupBy(x => x).OrderByDescending(g => g.Count()).Take(25)) Console.WriteLine($"   {g_count(w)} × {w.Key}");
static int g_count(IGrouping<string, string> g) => g.Count();
if (args.Contains("objetos"))
{
    Console.WriteLine();
    Console.WriteLine("Objetos que más se recomiendan sin que se compren (recomendado · comprado de esos · comprado sin recomendar):");
    foreach (var (id, pi) in perItem.Where(kv => kv.Value.Shown >= 30).OrderByDescending(kv => kv.Value.Shown - kv.Value.Bought).Take(20))
        Console.WriteLine($"   {data.Item(id)?.NameEn,-28} {pi.Shown,5} · {(double)pi.Bought / pi.Shown,6:P0} · {pi.Missed,5}");
    Console.WriteLine("Objetos que se compran mucho sin que la app los recomiende:");
    foreach (var (id, pi) in perItem.OrderByDescending(kv => kv.Value.Missed).Take(15))
        Console.WriteLine($"   {data.Item(id)?.NameEn,-28} recomendado {pi.Shown,5} (comprado {(double)pi.Bought / Math.Max(1, pi.Shown),4:P0}) · comprado sin recomendar {pi.Missed,5}");
}
