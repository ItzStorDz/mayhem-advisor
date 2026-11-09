// Casos de compra concretos para comprobar el consejo de la app (1.8.4, opiniones de los jugadores): inventario lleno por
// una poción, una build con la Lágrima de la Diosa, oro de sobra con el inventario lleno y «cambia X por Y» en Adaptar.
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
string Name(int id) => data.Item(id)?.DisplayName ?? id.ToString();
(string, IReadOnlyList<int>)[] magicTeam = [("Lux", []), ("Brand", []), ("Syndra", []), ("Veigar", []), ("Ziggs", [])];
(string, IReadOnlyList<int>)[] mixedTeam = [("Garen", []), ("Lux", []), ("Jinx", []), ("Leona", []), ("Ahri", [])];

void Case(string title, string champ, int[] items, double gold, (string, IReadOnlyList<int>)[] enemies, int[]? augs = null)
{
    var mine = (augs ?? []).Select(catalog.Get).OfType<Augment>().ToList();
    var a = advisor.Build(champ, items, enemies, mine, gold);
    Console.WriteLine($"\n== {title}: {data.ChampionName(champ)} con [{string.Join(", ", items.Select(Name))}], {gold} de oro");
    Console.WriteLine($"   build: {string.Join(", ", (a.FullBuild ?? []).Select(b => (b.Owned ? "✓" : "") + b.Item.Item.DisplayName))}");
    Console.WriteLine($"   compra ahora: {(a.Next is { } n ? $"{n.Item.DisplayName} (faltan {n.GoldMissing})" : "-")} · lleno: {a.InventoryFull}");
    if (a.Next is { } nb) Console.WriteLine($"   piezas: {string.Join(" + ", (nb.Item.From ?? []).Select(Name))}");
    if (a.MakeRoom is { } room) Console.WriteLine($"   hacer sitio: vende o usa {room.DisplayName}");
    if (a.EarlyPart is { } early) Console.WriteLine($"   pronto: {early.Part.DisplayName} para {early.For.DisplayName}");
    if (a.Elixir is { } elx) Console.WriteLine($"   oro de sobra: {elx.DisplayName}{(a.StatBonus is { } sb ? $" o {sb.DisplayName}" : "")}");
    if (a.Swap is { } sw) Console.WriteLine($"   vender y cambiar: {sw.Sell.DisplayName} → {sw.Buy.Item.DisplayName} ({sw.SellReason})");
    if (a.Plan is { } plan)
    {
        foreach (var (newId, old) in plan.Replaced ?? new Dictionary<int, ItemInfo>())
            Console.WriteLine($"   adaptar: cambia {old.DisplayName} por {Name(newId)}");
        Console.WriteLine($"   categorías: {string.Join(" | ", plan.Categories.Select(c => c.Title + (c.Offensive ? " (aprovechar)" : "")))}");
    }
}

Case("Empieza la partida", "Ezreal", [], 1400, mixedTeam);
Case("Empieza la partida", "Ryze", [], 1400, mixedTeam);
Case("5 objetos y una poción", "Annie", [3020, 4645, 3089, 3157, 4646, 2003], 900, mixedTeam);
Case("6 objetos y oro de sobra", "Annie", [3020, 4645, 3089, 3157, 4646, 3135], 900, mixedTeam);
Case("Contra mucho daño mágico", "Jinx", [3006, 3031], 600, magicTeam);
Case("Contra mucho daño mágico", "Garen", [3047, 3071], 600, magicTeam);
// Opinión de un jugador (2026-11-08): con el Sombrero de Wooglet (la misión ya hecha) se seguía pidiendo el Rabadon.
Case("Sombrero de Wooglet ya hecho", "Kayle", [228002, 3057, 2031, 3006, 3067, 1042], 800, mixedTeam, [1156, 1058, 3094]);
Case("Misión de Wooglet a medias (solo Rabadon)", "Kayle", [3089, 3006], 800, mixedTeam, [1156]);
