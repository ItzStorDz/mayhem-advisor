using System.Text.Json;

namespace MayhemAdvisor.Core;

/// <summary>Lo que hay en la selección de campeón: tu campeón, el banco (ARAM), tus aliados y quién te pide cambiar.</summary>
public sealed record ChampSelectState(int QueueId, int MyChampionId, IReadOnlyList<int> Bench, IReadOnlyList<int> Allies,
    IReadOnlyList<TradeOffer>? Trades = null);

/// <summary>Un aliado te pide cambiar de campeón: <paramref name="ChampionId"/> es el que te daría.</summary>
public sealed record TradeOffer(long Id, int ChampionId);

/// <param name="WinRate">% de victorias en tus partidas (con <paramref name="Games"/> partidas).</param>
/// <param name="Mayhem">Estadísticas de todos los jugadores (mayhemmeta.com), o null si no hay datos de ese campeón.</param>
/// <param name="Spells">Hechizos de invocador recomendados (todos los jugadores), o null sin datos.</param>
public sealed record ChampionOption(ChampionInfo Champion, double WinRate, int Games, bool IsCurrent, MayhemChampion? Mayhem = null,
    SpellAdvice? Spells = null)
{
    /// <summary>El % que manda: el de todos los jugadores si lo hay (miles de partidas), si no el tuyo.</summary>
    public double BestWinRate => Mayhem?.WinRate is { } wr ? wr / 100 : WinRate;
}

public static class ChampSelect
{
    /// <summary>Devuelve el estado si estás en selección de campeón; si no, null.</summary>
    public static async Task<ChampSelectState?> ReadAsync(LcuClient lcu, CancellationToken ct = default)
    {
        using var phase = await lcu.GetAsync("/lol-gameflow/v1/gameflow-phase", ct);
        if (phase.RootElement.GetString() != "ChampSelect") return null;

        int queue = 0;
        using (var flow = await lcu.GetAsync("/lol-gameflow/v1/session", ct))
            if (flow.RootElement.TryGetProperty("gameData", out var gd) && gd.TryGetProperty("queue", out var q))
                queue = q.GetProperty("id").GetInt32();

        using var session = await lcu.GetAsync("/lol-champ-select/v1/session", ct);
        var root = session.RootElement;
        var myCell = root.GetProperty("localPlayerCellId").GetInt64();

        int mine = 0;
        var allies = new List<int>();
        var byCell = new Dictionary<long, int>();
        foreach (var p in root.GetProperty("myTeam").EnumerateArray())
        {
            int champ = p.GetProperty("championId").GetInt32();
            byCell[p.GetProperty("cellId").GetInt64()] = champ;
            if (p.GetProperty("cellId").GetInt64() == myCell) mine = champ;
            else if (champ > 0) allies.Add(champ);
        }

        // Peticiones de cambio que te llegan ("RECEIVED"); el cliente las llama trades o, en algunas versiones, championSwaps.
        var trades = new List<TradeOffer>();
        foreach (var key in new[] { "trades", "championSwaps" })
            if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var t in list.EnumerateArray())
                    if (t.TryGetProperty("state", out var st) && st.GetString() == "RECEIVED"
                        && t.TryGetProperty("id", out var id) && t.TryGetProperty("cellId", out var cell)
                        && byCell.GetValueOrDefault(cell.GetInt64()) is > 0 and var theirs && trades.All(x => x.Id != id.GetInt64()))
                        trades.Add(new TradeOffer(id.GetInt64(), theirs));

        var bench = root.TryGetProperty("benchChampions", out var b)
            ? b.EnumerateArray().Select(x => x.GetProperty("championId").GetInt32()).Where(id => id > 0).ToList()
            : [];
        return new ChampSelectState(queue, mine, bench, allies, trades);
    }

    /// <summary>Coger un campeón del banco (lo hace el cliente, como si pulsaras su icono). True si el cliente lo aceptó.</summary>
    public static Task<bool> SwapWithBenchAsync(LcuClient lcu, int championId, CancellationToken ct = default) =>
        lcu.PostAsync($"/lol-champ-select/v1/session/bench/swap/{championId}", ct);

    /// <summary>Aceptar o rechazar una petición de cambio. Prueba la ruta actual y la antigua.</summary>
    public static async Task<bool> AnswerTradeAsync(LcuClient lcu, long tradeId, bool accept, CancellationToken ct = default)
    {
        string word = accept ? "accept" : "decline";
        return await lcu.PostAsync($"/lol-champ-select/v1/session/trades/{tradeId}/{word}", ct)
               || await lcu.PostAsync($"/lol-champ-select/v1/session/champion-swaps/{tradeId}/{word}", ct);
    }

    /// <summary>Modo de ventana del juego en los ajustes del cliente: 0 pantalla completa, 1 ventana, 2 sin bordes; null si no se sabe.</summary>
    public static async Task<int?> WindowModeAsync(LcuClient lcu, CancellationToken ct = default)
    {
        using var doc = await lcu.GetAsync("/lol-game-settings/v1/game-settings", ct);
        return doc.RootElement.TryGetProperty("General", out var g) && g.TryGetProperty("WindowMode", out var m) && m.TryGetInt32(out var mode) ? mode : null;
    }

    /// <summary>
    /// Pasa el juego a "sin bordes" (lo necesita el lector de cartas: en pantalla completa no se puede capturar la
    /// pantalla). Cambia solo General.WindowMode en los ajustes del cliente y le pide guardarlos; vale desde la próxima
    /// partida. Solo cuando pulsas el botón.
    /// </summary>
    public static async Task<bool> SetBorderlessAsync(LcuClient lcu, CancellationToken ct = default)
    {
        // Se mandan los ajustes tal cual están, cambiando solo el modo de ventana (como hace Hexsight).
        using var doc = await lcu.GetAsync("/lol-game-settings/v1/game-settings", ct);
        if (System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText()) is not System.Text.Json.Nodes.JsonObject settings) return false;
        if (settings["General"] is not System.Text.Json.Nodes.JsonObject general) settings["General"] = general = new();
        general["WindowMode"] = 2;
        return await lcu.PatchJsonAsync("/lol-game-settings/v1/game-settings", settings.ToJsonString(), ct)
               && await lcu.PostAsync("/lol-game-settings/v1/save", ct);
    }

    /// <summary>
    /// Tu campeón y los del banco, ordenados por % de victorias: el de todos los jugadores de Mayhem
    /// si lo tenemos (<paramref name="mayhem"/>), si no el de tus partidas.
    /// </summary>
    public static List<ChampionOption> Rank(ChampSelectState state, StatsStore stats, MayhemChampions? mayhem = null) =>
        state.Bench.Prepend(state.MyChampionId)
            .Distinct()
            .Select(id => stats.Data.ChampionsById.GetValueOrDefault(id))
            .OfType<ChampionInfo>()
            .Select(c => new ChampionOption(c, stats.ChampionBase(c.Alias), stats.ChampionGames(c.Alias), c.Id == state.MyChampionId, mayhem?.For(c.Alias),
                SummonerSpells.Recommend(stats.Global.For(c.Alias))))
            .OrderByDescending(o => o.BestWinRate)
            .ToList();
}
