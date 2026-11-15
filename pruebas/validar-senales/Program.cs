// ¿Lo que la app cree que ayuda a ganar, ayuda de verdad? Con las partidas reales guardadas (matches.jsonl +
// comunidad.jsonl, los 10 jugadores de cada una) se mira si quien llevaba «lo que la app puntúa alto» ganó más:
//   1. Aumento con campeón: la media de lo que la app dice que suben sus aumentos (el punto de partida de la nota).
//   2. Cartas entre sí y con la build: la suma de lo que SynergyWith da a cada aumento con los demás (sin objetos y con
//      sus objetos del final).
//   3. Objetos según tus aumentos: cuántos de sus objetos finales salen en la build que la app le daría con sus aumentos
//      (sin objetos comprados) y con la build «a ciegas» (sin aumentos), para ver qué aportan los aumentos.
// Cada señal se parte en tres grupos de jugadores (bajo, medio, alto) y se enseña el % de victorias de cada uno: si la
// señal vale, el grupo alto gana más. Las estadísticas propias NO se cargan (serían las mismas partidas).
// Uso: MAYHEM_DATA_DIR=<copia de la carpeta de datos> dotnet run --project pruebas\validar-senales -c Release
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
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));
var advisor = new Advisor(stats);

var matches = new Dictionary<string, MatchRecord>();
foreach (var file in new[] { "matches.jsonl", "comunidad.jsonl", "nube.jsonl" })
{
    var path = Path.Combine(Paths.DataDir, file);
    if (!File.Exists(path)) continue;
    foreach (var line in File.ReadLines(path))
        if (line.Trim() is { Length: > 0 } json && JsonSerializer.Deserialize<MatchRecord>(json) is { Players.Count: 10 } m)
            matches.TryAdd(string.Join(",", m.Players.Select(p => $"{p.Champion}:{p.PhysicalDamage}:{p.MagicDamage}")), m);
}

var rows = new List<(bool Win, double Aug, double Pairs, double PairsItems, double Build, double BuildBlind, double BuildGain, int Augs)>();
foreach (var m in matches.Values)
    foreach (var p in m.Players)
    {
        var augs = p.Augments.Distinct().Select(catalog.Get).OfType<Augment>().Where(a => !a.IsAnvil).ToList();
        if (augs.Count < 2 || stats.AramkitBuilds.Styles(p.Champion).Count == 0) continue;
        var finals = p.Items.Distinct().Where(i => data.Item(i) is { IsFinal: true, IsBoots: false, PriceTotal: > 0 }).ToList();
        if (finals.Count < 3) continue;
        double aug = augs.Average(a => AugmentScale.Evidence(stats.Rate(p.Champion, a.Id)));
        double Pairs(IReadOnlyList<int>? items) => augs.Sum(a => advisor.SynergyWith(a, augs.Where(o => o.Id != a.Id).ToList(), p.Champion, items)?.Bonus ?? 0);
        var enemies = m.Players.Where(o => o.Team != p.Team).Select(o => (o.Champion, (IReadOnlyList<int>)o.Items)).ToList();
        double Hits(IReadOnlyList<Augment> mine)
        {
            var build = (advisor.Build(p.Champion, [], enemies, mine, 99999, phase: GamePhase.Mid).FullBuild ?? [])
                .Where(s => !s.Item.Item.IsBoots).Select(s => s.Item.Item.Id).ToList();
            return build.Count == 0 ? 0 : build.Count(finals.Contains) / (double)Math.Min(build.Count, finals.Count);
        }
        bool quick = Environment.GetEnvironmentVariable("VS_RAPIDO") == "1"; // solo las señales de las cartas (sin calcular builds)
        double with = quick ? 0 : Hits(augs), blind = quick ? 0 : Hits([]);
        rows.Add((p.Win, aug, Pairs(null), Pairs(p.Items), with, blind, with - blind, augs.Count));
    }

Console.WriteLine($"{matches.Count} partidas · {rows.Count} jugadores con 2+ aumentos y 3+ objetos · ganan el {rows.Average(r => r.Win ? 1.0 : 0):P1}");
void Show(string name, Func<(bool Win, double Aug, double Pairs, double PairsItems, double Build, double BuildBlind, double BuildGain, int Augs), double> f)
{
    var sorted = rows.OrderBy(f).ToList();
    int n = sorted.Count, third = n / 3;
    var groups = new[] { sorted.Take(third).ToList(), sorted.Skip(third).Take(n - 2 * third).ToList(), sorted.Skip(n - third).ToList() };
    string G(List<(bool Win, double Aug, double Pairs, double PairsItems, double Build, double BuildBlind, double BuildGain, int Augs)> g) =>
        $"{g.Average(r => r.Win ? 1.0 : 0),6:P1} (señal {g.Average(f),7:0.000})";
    // Correlación de la señal con ganar (0 = nada).
    double mx = rows.Average(f), my = rows.Average(r => r.Win ? 1.0 : 0);
    double cov = rows.Average(r => (f(r) - mx) * ((r.Win ? 1.0 : 0) - my));
    double sx = Math.Sqrt(rows.Average(r => Math.Pow(f(r) - mx, 2))), sy = Math.Sqrt(my * (1 - my));
    double corr = sx > 0 ? cov / (sx * sy) : 0;
    Console.WriteLine($"{name,-44} bajo {G(groups[0])} · medio {G(groups[1])} · alto {G(groups[2])} · alto − bajo {groups[2].Average(r => r.Win ? 1.0 : 0) - groups[0].Average(r => r.Win ? 1.0 : 0),6:+0.0%;-0.0%} · r {corr:+0.000;-0.000} (±{2 / Math.Sqrt(n):0.000})");
}
Show("1. aumento con campeón (media de lo que suben)", r => r.Aug);
Show("2a. cartas entre sí (sin objetos)", r => r.Pairs);
Show("2b. cartas entre sí y con su build", r => r.PairsItems);
Show("3a. sus objetos salen en la build de la app", r => r.Build);
Show("3b. …en la build sin contar sus aumentos", r => r.BuildBlind);
Show("3c. lo que añaden los aumentos a la build", r => r.BuildGain);
Console.WriteLine($"Aciertos de la build con sus aumentos {rows.Average(r => r.Build):P1} · sin contarlos {rows.Average(r => r.BuildBlind):P1}");

