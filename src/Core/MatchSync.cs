using System.Text.Json;

namespace MayhemAdvisor.Core;

/// <summary>
/// Copia tus partidas de Mayhem del historial del cliente de LoL a matches.jsonl.
/// La API pública de Riot no entrega partidas de Mayhem, pero tu cliente sí tiene las tuyas,
/// con los aumentos, objetos y daño de los 10 jugadores.
/// </summary>
public static class MatchSync
{
    public const int MayhemQueueId = 2400;

    /// <summary>
    /// Devuelve cuántas partidas nuevas se guardaron (en las estadísticas o en tu historial, lo que sea más), o null si el
    /// cliente no está abierto.
    /// </summary>
    public static async Task<int?> SyncOwnHistoryAsync(GameData data, CancellationToken ct = default)
    {
        using var lcu = LcuClient.Connect();
        if (lcu is null) return null;

        var seen = new HashSet<string>();
        if (File.Exists(Paths.Matches))
            foreach (var line in File.ReadLines(Paths.Matches))
                try { if (JsonSerializer.Deserialize<MatchRecord>(line) is { } r) seen.Add(r.Id); } catch (JsonException) { }

        using var history = await lcu.GetAsync("/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=20", ct);
        // Tu fila de cada partida (KDA, daño, oro) para el historial de la pantalla de inicio, y tu nombre.
        int mine = MyMatches.SaveFromHistory(history.RootElement.GetProperty("games").GetProperty("games"), data);
        try { using var me = await lcu.GetAsync("/lol-summoner/v1/current-summoner", ct); MyMatches.SaveProfile(me.RootElement); }
        catch (HttpRequestException) { }
        var games = history.RootElement.GetProperty("games").GetProperty("games").EnumerateArray()
            .Where(g => g.GetProperty("queueId").GetInt32() == MayhemQueueId)
            .Select(g => (Id: $"{g.GetProperty("platformId").GetString()}_{g.GetProperty("gameId").GetInt64()}", GameId: g.GetProperty("gameId").GetInt64(),
                          Me: g.GetProperty("participants").GetArrayLength() > 0 && g.GetProperty("participants")[0].TryGetProperty("participantId", out var pid) ? pid.GetInt32() : 0))
            .Where(g => !seen.Contains(g.Id) || !File.Exists(ReportPath(g.Id)))
            .ToList();
        if (games.Count == 0) return mine;

        Directory.CreateDirectory(Paths.DataDir);
        using var output = new StreamWriter(Paths.Matches, append: true);
        int saved = 0;
        foreach (var (id, gameId, me) in games)
        {
            using var game = await lcu.GetAsync($"/lol-match-history/v1/games/{gameId}", ct);
            // La partida completa (los 10 jugadores con todas sus estadísticas) para la pantalla de resumen de la partida.
            if (!File.Exists(ReportPath(id))) SaveReport(id, me, game.RootElement);
            if (seen.Contains(id)) continue;
            var record = ToRecord(id, game.RootElement, data);
            if (record.Players.All(p => p.Augments.Count == 0)) continue;
            await output.WriteLineAsync(JsonSerializer.Serialize(record));
            saved++;
        }
        return Math.Max(saved, mine);
    }

    /// <summary>Carpeta con la partida completa de cada partida tuya (<c>partidas\&lt;id&gt;.json</c>).</summary>
    public static string ReportPath(string id) => Path.Combine(Paths.DataDir, "partidas", id + ".json");

    static void SaveReport(string id, int me, JsonElement game)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath(id))!);
            File.WriteAllText(ReportPath(id), $"{{\"me\":{me},\"game\":{game.GetRawText()}}}");
        }
        catch (IOException) { }
    }

    static MatchRecord ToRecord(string id, JsonElement g, GameData data)
    {
        var aliasById = data.Champions.Values.ToDictionary(c => c.Id, c => c.Alias);
        var record = new MatchRecord { Id = id, Version = g.GetProperty("gameVersion").GetString() ?? "" };

        foreach (var p in g.GetProperty("participants").EnumerateArray())
        {
            var st = p.GetProperty("stats");
            var player = new PlayerRecord
            {
                Champion = aliasById.GetValueOrDefault(p.GetProperty("championId").GetInt32(), p.GetProperty("championId").GetInt32().ToString()),
                Team = p.GetProperty("teamId").GetInt32(),
                Win = st.GetProperty("win").GetBoolean(),
                PhysicalDamage = (int)st.GetProperty("physicalDamageDealtToChampions").GetInt64(),
                MagicDamage = (int)st.GetProperty("magicDamageDealtToChampions").GetInt64(),
                Heal = (int)st.GetProperty("totalHeal").GetInt64(),
            };
            for (int slot = 0; slot <= 5; slot++)
                if (st.GetProperty($"item{slot}").GetInt32() is var item and > 0) player.Items.Add(item);
            // Los aumentos vienen como playerAugment1, playerAugment2, ... (0 = vacío).
            foreach (var prop in st.EnumerateObject())
                if (prop.Name.StartsWith("playerAugment") && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.GetInt32() > 0)
                    player.Augments.Add(prop.Value.GetInt32());
            record.Players.Add(player);
        }
        return record;
    }
}
