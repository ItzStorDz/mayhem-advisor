// Mide la nota de las cartas con las elecciones reales que llegan a la nube (partidas con el diario de la app): con las
// cartas que había, ¿la mejor puntuada es la que eligieron los que ganaron? (y los que perdieron, para comparar).
// Los aumentos que ya tenía se toman de sus elecciones anteriores. Los objetos de cada elección no se conocen: sin nada se
// dejan vacíos; con EVAL_ITEMS=1 se le dan sus objetos del final de la partida (dicen su build, aunque no los tuviera aún).
// Uso: MAYHEM_DATA_DIR=<copia de datos> dotnet run -c Release -- <partidas de la nube .json (salida de wrangler d1 --json)>
// Para comparar con una versión anterior: -p:Antiguo=<carpeta codigo de un respaldo> (desde una copia de esta carpeta).
using System.Text.Json;
using MayhemAdvisor.Core;

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
#if !ANTIGUO
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));
#endif
var advisor = new Advisor(stats);

var text = File.ReadAllText(args[0]);
using var doc = JsonDocument.Parse(text[text.IndexOf('[')..]);
var tally = new Dictionary<string, (int Picks, int Top)>();
foreach (var row in doc.RootElement[0].GetProperty("results").EnumerateArray())
{
    using var match = JsonDocument.Parse(row.GetProperty("data").GetString()!);
    var m = match.RootElement;
    if (!m.TryGetProperty("journal", out var journal) || journal.ValueKind != JsonValueKind.Object) continue;
    var me = m.GetProperty("p")[m.GetProperty("me").GetInt32()];
    string champion = me.GetProperty("c").GetString()!;
    bool win = me.GetProperty("w").GetBoolean();
    var actual = me.GetProperty("a").EnumerateArray().Select(a => a.GetInt32()).ToHashSet();
    var items = Environment.GetEnvironmentVariable("EVAL_ITEMS") == "1" && me.TryGetProperty("i", out var it)
        ? it.EnumerateArray().Select(x => x.GetInt32()).ToList() : [];
    var mine = new List<Augment>();
    var scale = AugmentScale.For(champion, DamageMix.Mixed, stats, catalog);
    foreach (var pick in journal.GetProperty("picks").EnumerateArray())
    {
        var offered = pick.GetProperty("offered").EnumerateArray().Select(o => catalog.Get(o.GetProperty("id").GetInt32())).OfType<Augment>().ToList();
        var real = offered.Where(o => actual.Contains(o.Id)).ToList();
        if (offered.Count < 2 || real.Count != 1 || offered.Any(o => o.IsAnvil)) continue;
        double Card(Augment a)
        {
            var rec = stats.Rate(champion, a.Id);
            var syn = advisor.SynergyWith(a, mine, champion, items);
            return scale.Score(AugmentScale.Evidence(rec) + (syn?.Bonus ?? 0), AugmentScale.IsReliable(rec));
        }
        var top = offered.MaxBy(Card)!;
        foreach (var key in new[] { "todos", win ? "ganadores" : "perdedores" })
        {
            var t = tally.GetValueOrDefault(key);
            tally[key] = (t.Picks + 1, t.Top + (top.Id == real[0].Id ? 1 : 0));
        }
        mine.Add(real[0]);
    }
}
foreach (var (key, t) in tally.OrderBy(kv => kv.Key))
    Console.WriteLine($"{key,-12} {t.Picks,4} elecciones · eligieron la carta mejor puntuada: {(double)t.Top / t.Picks:P1}");
