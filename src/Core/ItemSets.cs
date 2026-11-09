using System.Text.Json;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Core;

/// <summary>
/// La build de la app en la tienda del juego: un "item set" (como los que crea Riot) en
/// <c>&lt;League&gt;\Config\Global\Recommended\MayhemAdvisor-&lt;campeón&gt;.json</c>. El juego lee todos los .json de esa
/// carpeta al cargar la partida y lo enseña en la pestaña de builds recomendadas de la tienda, solo con ese campeón y en
/// el Abismo de los Lamentos (mapa 12, el de Mayhem). Solo tocamos nuestros archivos (empiezan por "MayhemAdvisor").
/// Idea de Hexsight (github.com/bard58/hexsight, licencia MIT/Apache); el código es nuestro.
/// </summary>
public static class ItemSets
{
    const string Prefix = "MayhemAdvisor";
    const int HowlingAbyss = 12;

    public static string Folder(string leagueDir) => Path.Combine(leagueDir, "Config", "Global", "Recommended");

    // Equipos enemigos de ejemplo para las variantes "contra daño mágico / físico" (sin objetos: solo cuenta el tipo de daño).
    static readonly string[] MagicTeam = ["Lux", "Brand", "Syndra", "Veigar", "Ziggs"];
    static readonly string[] PhysicalTeam = ["Caitlyn", "Draven", "Zed", "Jhin", "Talon"];

    /// <summary>
    /// Escribe el item set del campeón y borra los nuestros de otros campeones. Devuelve la ruta, o null si no hay build.
    /// Bloques: la build de la app en orden de compra, las botas, otras opciones, y qué cambiar si el equipo enemigo
    /// es casi todo de daño mágico o físico (lo que la app recomendaría en ese caso y no está en la build normal).
    /// </summary>
    public static string? Write(string leagueDir, ChampionInfo champion, StatsStore stats)
    {
        var advisor = new Advisor(stats);
        var normal = advisor.Build(champion.Alias, [], [], [], 0, phase: GamePhase.Mid);
        var build = (normal.FullBuild ?? []).Select(b => b.Item.Item).ToList();
        if (build.Count == 0) return null;

        var inBuild = build.Select(i => i.Id).ToHashSet();
        List<ItemInfo> Versus(string[] team)
        {
            var enemies = team.Select(c => (c, (IReadOnlyList<int>)[])).ToList();
            var a = advisor.Build(champion.Alias, [], enemies, [], 0, phase: GamePhase.Mid);
            return (a.FullBuild ?? []).Select(b => b.Item.Item).Where(i => !inBuild.Contains(i.Id)).Take(4).ToList();
        }
        // Otros objetos que compra mucha gente con ese campeón en Mayhem y no están en la build de la app.
        var others = normal.Items.Select(i => i.Item)
            .Concat((stats.Global.For(champion.Alias)?.ItemsCommon ?? []).Select(i => stats.Data.Item(i.Id)).OfType<ItemInfo>())
            .Where(i => i.IsFinal && i.InStore && !i.IsBoots && !inBuild.Contains(i.Id)).DistinctBy(i => i.Id).Take(5).ToList();

        string skills = normal.Skills is { } s ? " · " + T("maximiza ", "max ", "maximize ", "monter ", "maximiere ", "nâng tối đa ") + string.Join(" › ", s.MaxOrder.ToCharArray()) : "";
        var blocks = new List<object?>
        {
            Block(T("Build de Mayhem Advisor (en orden)", "Mayhem Advisor build (in order)", "Build do Mayhem Advisor (em ordem)", "Build de Mayhem Advisor (dans l'ordre)", "Build von Mayhem Advisor (in Reihenfolge)", "Build của Mayhem Advisor (theo thứ tự)") + skills, build.Where(i => !i.IsBoots)),
            Block(T("Botas", "Boots", "Botas", "Bottes", "Stiefel", "Giày"), build.Where(i => i.IsBoots)),
            Block(T("Otras buenas opciones", "Other good options", "Outras boas opções", "Autres bons choix", "Weitere gute Optionen", "Lựa chọn tốt khác"), others),
            Block(T("Si el enemigo es casi todo daño mágico", "If the enemy is mostly magic damage", "Se o inimigo for quase todo dano mágico", "Si l'ennemi fait surtout des dégâts magiques", "Wenn der Gegner fast nur magischen Schaden macht", "Nếu địch gần như toàn sát thương phép"), Versus(MagicTeam)),
            Block(T("Si el enemigo es casi todo daño físico", "If the enemy is mostly physical damage", "Se o inimigo for quase todo dano físico", "Si l'ennemi fait surtout des dégâts physiques", "Wenn der Gegner fast nur physischen Schaden macht", "Nếu địch gần như toàn sát thương vật lý"), Versus(PhysicalTeam)),
            Block(T("Consumibles", "Consumables", "Consumíveis", "Consommables", "Verbrauchsgegenstände", "Tiêu hao"), [2003, 2138, 2139, 2140], stats.Data),
        }.OfType<object>().ToList();

        var set = new
        {
            title = $"Mayhem Advisor · {champion.DisplayName}",
            associatedChampions = new[] { champion.Id },
            associatedMaps = new[] { HowlingAbyss },
            blocks,
            map = "any", mode = "any", type = "custom", priority = true, sortrank = 0, preferredItemSlots = Array.Empty<object>(),
            startedFrom = "blank",
        };

        var dir = Folder(leagueDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Prefix}-{champion.Alias}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(set));
        DeleteAll(leagueDir, keep: path);
        return path;
    }

    static object? Block(string title, IEnumerable<ItemInfo> items)
    {
        var rows = items.DistinctBy(i => i.Id).Select(i => new { id = i.Id.ToString(), count = 1 }).ToList();
        return rows.Count == 0 ? null : new { type = title, items = rows, showIfSummonerSpell = "", hideIfSummonerSpell = "" };
    }

    static object? Block(string title, int[] ids, GameData data) =>
        Block(title, ids.Select(data.Item).OfType<ItemInfo>().Where(i => i.InStore));

    /// <summary>Borra nuestros item sets (todos, o todos menos <paramref name="keep"/>). Los de Riot y otras apps no se tocan.</summary>
    public static void DeleteAll(string leagueDir, string? keep = null)
    {
        var dir = Folder(leagueDir);
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, Prefix + "*.json"))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
