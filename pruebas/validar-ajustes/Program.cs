// ¿Los ajustes a ojo de la nota de las cartas («pide habilidades / AP», «encaja con la build», el kit) acercan la nota a la
// verdad? Verdad: lo que gana cada campeón con cada aumento en mayhemmeta con 800+ partidas. Punto de partida: lo que la
// app estimaría sin esas partidas (75 % AramHex o aramkit + 25 % la media del aumento con todos los campeones). Si el
// ajuste vale, en las parejas donde resta la verdad queda por debajo del punto de partida, y al revés.
// Uso: MAYHEM_DATA_DIR=<copia de datos> dotnet run -c Release
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

var groups = new Dictionary<string, List<(double Adj, double Resid)>>();
void Add(string g, double adj, double resid) { if (!groups.TryGetValue(g, out var l)) groups[g] = l = []; l.Add((adj, resid)); }
int pairs = 0;
foreach (var c in data.Champions.Values.Select(c => c.Alias))
    foreach (var a in catalog.Augments.Where(a => !a.IsAnvil))
    {
        if (table.For(c, a.Id) is not { Games: >= 800 } row) continue;
        var external = stats.Aramhex.For(c, a.Id) ?? stats.Aramkit.For(c, a.Id);
        double average = table.AugmentMean(a.Id, c);
        double prior = external is null ? average : ExternalAugments.Weight * external.Delta + (1 - ExternalAugments.Weight) * average;
        double truth = row.WinRate - row.ChampionWinRate, resid = truth - prior;
        pairs++;
        Add("todas", 0, resid);
        if (advisor.BuildFit(a, c, [], []) is { } fit) Add(fit.Bonus < 0 ? "encaje con la build: resta" : "encaje con la build: suma", fit.Bonus * 100, resid);
        if (stats.Kits.For(c) is { } kit)
        {
            if (kit.HasOnHit && (StatTags.IsOnHitEffect(a) || a.StatList.Contains("AS"))) Add("kit: efectos al impactar / vel. ataque", 1, resid);
            if (kit.HasCrit && a.StatList.Contains("Crit")) Add("kit: usa el crítico", 1, resid);
        }
    }
// Curaciones y escudos sin tenerlos en el kit (la app resta 2 puntos).
var healShield = new System.Text.RegularExpressions.Regex(@"curaciones y escudos|heal(ing)? and shield(ing)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
foreach (var c in data.Champions.Values.Select(c => c.Alias))
    foreach (var a in catalog.Augments.Where(a => !a.IsAnvil && healShield.IsMatch(a.Description + " " + a.ExtraDescription)))
        if (table.For(c, a.Id) is { Games: >= 800 } row && stats.Kits.For(c) is { } kit)
        {
            var external = stats.Aramhex.For(c, a.Id) ?? stats.Aramkit.For(c, a.Id);
            double average = table.AugmentMean(a.Id, c);
            double prior = external is null ? average : ExternalAugments.Weight * external.Delta + (1 - ExternalAugments.Weight) * average;
            Add(!kit.Heals && !kit.Shields ? "curaciones/escudos sin tenerlos: resta" : "curaciones/escudos teniéndolos", !kit.Heals && !kit.Shields ? -2 : 0, row.WinRate - row.ChampionWinRate - prior);
        }
Console.WriteLine($"{pairs} parejas campeón + aumento con 800+ partidas en mayhemmeta");
Console.WriteLine($"{"ajuste",-42} {"parejas",7} {"ajuste medio",13} {"verdad − punto de partida (media)",34}");
foreach (var (g, l) in groups.OrderBy(kv => kv.Key))
    Console.WriteLine($"{g,-42} {l.Count,7} {l.Average(x => x.Adj),+13:+0.00;-0.00} {l.Average(x => x.Resid),34:+0.00;-0.00}");

// «Los dos potencian X» entre dos aumentos: ¿combinan mejor las parejas medidas (1.000+ partidas juntos) que comparten
// estadísticas útiles? (lo que la app suma a ojo: hasta +1,5).
var shared = new List<double>(); var notShared = new List<double>();
var augs = catalog.Augments.Where(a => !a.IsAnvil).ToList();
for (int i = 0; i < augs.Count; i++)
    for (int j = i + 1; j < augs.Count; j++)
        if (table.Pair(augs[i].Id, augs[j].Id) is { Games: >= 1000 } p)
        {
            var common = augs[i].StatList.Intersect(augs[j].StatList).Where(t => t is not "Abilities" and not "Haste").ToList();
            (common.Count > 0 ? shared : notShared).Add(p.Synergy);
        }
Console.WriteLine();
Console.WriteLine($"Parejas de aumentos medidas (1.000+ partidas): comparten estadística {shared.Count} (sinergia media {shared.DefaultIfEmpty(0).Average():+0.00;-0.00}) · no comparten {notShared.Count} ({notShared.DefaultIfEmpty(0).Average():+0.00;-0.00})");
