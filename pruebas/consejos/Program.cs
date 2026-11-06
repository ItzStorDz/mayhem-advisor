// Imprime los consejos que daría la app con una copia de tus datos: los de la pantalla de inicio (tus 20 últimas partidas)
// y los de «Para la próxima» de tus últimas partidas guardadas, en español e inglés.
// Uso: MAYHEM_DATA_DIR=<copia de la carpeta de datos, con partidas\ y diario\> dotnet run -c Release [-- en] [partidas=3]
using MayhemAdvisor.Core;

Lang.English = args.Contains("en");
int show = args.FirstOrDefault(a => a.StartsWith("partidas=")) is { } pa ? int.Parse(pa[9..]) : 3;
using var http = new HttpClient();
var catalog = await AugmentCatalog.LoadAsync(http);
var data = await GameData.LoadAsync(http);
var stats = StatsStore.Load(Paths.Matches, data);
var builds = MayhemBuilds.Load(DataFiles.Resolve("mayhem-champion-builds.json"));
var table = MayhemAugments.Load(DataFiles.Resolve("mayhem-augments.json"));
catalog = catalog.WithExtraDescriptions(table.Descriptions);
stats.Global = builds;
stats.AugmentTable = table;
stats.AramkitBuilds = AugmentBuilds.Load(DataFiles.Resolve("aramkit-builds.json"));
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));
var reports = new GameReports(data, catalog, stats);

var all = MyMatches.Load();
Console.WriteLine($"{all.Count} partidas tuyas guardadas\n\nPANTALLA DE INICIO");
foreach (var tip in reports.HomeTips(all)) Console.WriteLine($" • {tip}");

foreach (var m in all.Take(show))
{
    Console.WriteLine($"\n{data.ChampionName(m.Champion)} ({(m.Win ? "victoria" : "derrota")}, {m.Kills}/{m.Deaths}/{m.Assists}) · PARA LA PRÓXIMA");
    var detail = reports.Detail(m.Id);
    var tips = detail?.GetType().GetProperty("tips")?.GetValue(detail) as IEnumerable<string>;
    foreach (var tip in tips ?? []) Console.WriteLine($" • {tip}");
}

Console.WriteLine("\nGENERALES");
foreach (var tip in ModeTips.All()) Console.WriteLine($" [{tip.Kind}] {tip.Text}");
