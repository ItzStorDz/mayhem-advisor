// Explica la nota de cada carta de una partida tuya: con el diario de la app (carpeta diario), qué datos tenía cada
// carta con tu campeón, cómo combinaba con los aumentos que ya llevabas (todos los jugadores) y la nota que sale.
// Los objetos no se tienen en cuenta (el diario no guarda qué tenías en cada elección).
// Uso: MAYHEM_DATA_DIR=<copia de datos> dotnet run -c Release -- <diario\AAAAMMDD-HHMMSS-Campeón.json>
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

using var doc = JsonDocument.Parse(File.ReadAllText(args[0]));
var j = doc.RootElement;
string champion = j.GetProperty("champion").GetString()!;
var scale = AugmentScale.For(champion, DamageMix.Mixed, stats, catalog);
var mine = new List<Augment>();
Console.WriteLine($"{champion}");
foreach (var pick in j.GetProperty("picks").EnumerateArray())
{
    int chosen = pick.TryGetProperty("chosen", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
    Console.WriteLine($"\nmin {pick.GetProperty("min").GetDouble():0.0} · ya tenías: {string.Join(", ", mine.Select(m => m.DisplayName))}");
    foreach (var o in pick.GetProperty("offered").EnumerateArray())
    {
        if (catalog.Get(o.GetProperty("id").GetInt32()) is not { } a) continue;
        var rec = stats.Rate(champion, a.Id);
        var syn = advisor.SynergyWith(a, mine, champion, []);
        double score = scale.Score(AugmentScale.Evidence(rec) + (syn?.Bonus ?? 0), AugmentScale.IsReliable(rec));
        var row = table.For(champion, a.Id);
        Console.WriteLine($"{(a.Id == chosen ? ">>" : "  ")} {a.DisplayName,-28} nota {score,5:0.0} (en partida {o.GetProperty("score").GetDouble(),5:0.0})" +
                          $" · {champion} con él: {rec.Delta * 100:+0.0;-0.0} pts, {rec.Games:N0} partidas" +
                          (row is null ? "" : $" (mayhemmeta {row.WinRate:0.0} % vs {row.ChampionWinRate:0.0} %, {row.Games:N0})") +
                          (syn is null ? "" : $" · sinergia {syn.Bonus * 100:+0.0;-0.0}: {syn.Reason}"));
        if (args.Contains("detalle"))
        {
            var fit = advisor.BuildFit(a, champion, [], mine);
            Console.WriteLine($"       etiquetas [{string.Join(", ", a.StatList)}] · encaje con la build: " + (fit is null ? "nada" : $"{fit.Bonus * 100:+0.0;-0.0} {fit.Reason}"));
        }
        foreach (var m in mine.Where(m => !m.IsAnvil))
            if (table.Pair(a.Id, m.Id) is { } p)
                Console.WriteLine($"       con «{m.DisplayName}»: {p.Games:N0} partidas juntos, {p.WinRate:0.0} %, sinergia {p.Synergy:+0.0;-0.0} (encogida {p.ShrunkSynergy:+0.0;-0.0})");
    }
    if (catalog.Get(chosen) is { } ch) mine.Add(ch);
}
