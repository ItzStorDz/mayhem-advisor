// Las mejores cartas que daría la app a cada campeón (sin objetos ni aumentos todavía), para revisar por tipo de campeón
// si recomienda aumentos que no le van. Uso: MAYHEM_DATA_DIR=<copia de datos> dotnet run -c Release -- Campeón [Campeón…] [n=10]
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
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));
var advisor = new Advisor(stats);
int n = args.FirstOrDefault(a => a.StartsWith("n=")) is { } na ? int.Parse(na[2..]) : 10;
// objetos=3031,3085: con esos objetos ya comprados (p. ej. Kog'Maw de crítico).
List<int> owned = args.FirstOrDefault(a => a.StartsWith("objetos=")) is { } oa ? oa[8..].Split(',').Select(int.Parse).ToList() : [];

foreach (var champion in args.Where(a => !a.StartsWith("n=") && a != "build" && !a.StartsWith("objetos=")))
{
    var scale = AugmentScale.For(champion, DamageMix.Mixed, stats, catalog);
    var cards = catalog.Augments.Where(a => !a.IsAnvil).Select(a =>
    {
        var rec = stats.Rate(champion, a.Id);
        var syn = advisor.SynergyWith(a, [], champion, owned);
        double score = scale.Score(AugmentScale.Evidence(rec) + (syn?.Bonus ?? 0), AugmentScale.IsReliable(rec));
        return (a, rec, syn, score);
    }).OrderByDescending(c => c.score).ToList();
    var style = stats.AramkitBuilds.Styles(champion).FirstOrDefault()?.Key;
    Console.WriteLine($"\n{champion} ({string.Join("/", data.Champion(champion)?.Roles ?? [])}, build {style})");
    if (args.Contains("build"))
    {
        (string, IReadOnlyList<int>)[] enemies = [("Garen", []), ("Lux", []), ("Jinx", []), ("Leona", []), ("Ahri", [])];
        var advice = advisor.Build(champion, owned, enemies, [], 0);
        Console.WriteLine($"   build: {string.Join(", ", (advice.FullBuild ?? []).Select(b => data.Item(b.Item.Item.Id)?.NameEn))}");
        Console.WriteLine($"   botas: {advice.Boots?.Item.NameEn} · primera compra: {advice.Next?.Item.NameEn}");
    }
    foreach (var (a, rec, syn, score) in cards.Take(n))
        Console.WriteLine($"   {score,5:0.0} {a.Names.GetValueOrDefault("default"),-26} {rec.Delta * 100:+0.0;-0.0} pts {rec.Games,7:N0} partidas [{string.Join(",", a.StatList)}]" +
                          (syn is null ? "" : $" · {syn.Bonus * 100:+0.0;-0.0} {syn.Reason}"));
}
