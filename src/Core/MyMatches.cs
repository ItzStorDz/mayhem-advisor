using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>Una partida tuya de ARAM: Caos (para el historial y las estadísticas de la pantalla de inicio).</summary>
/// <param name="Date">Inicio de la partida (ms desde 1970, UTC).</param>
/// <param name="Duration">Duración en segundos.</param>
public sealed record MyMatch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("date")] long Date,
    [property: JsonPropertyName("dur")] int Duration,
    [property: JsonPropertyName("c")] string Champion,
    [property: JsonPropertyName("w")] bool Win,
    [property: JsonPropertyName("k")] int Kills,
    [property: JsonPropertyName("d")] int Deaths,
    [property: JsonPropertyName("a")] int Assists,
    [property: JsonPropertyName("dmg")] int Damage,
    [property: JsonPropertyName("gold")] int Gold,
    [property: JsonPropertyName("i")] int[] Items,
    [property: JsonPropertyName("aug")] int[] Augments)
{
    public double Kda => (Kills + Assists) / (double)Math.Max(1, Deaths);
}

/// <summary>Tu nombre de invocador (lo da el cliente).</summary>
public sealed record MyProfile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("icon")] int Icon);

/// <summary>
/// Tus partidas en my-matches.jsonl. matches.jsonl guarda los 10 jugadores para las estadísticas, pero no dice
/// cuál eres tú; el historial del cliente sí (viene solo tu fila, con KDA, daño y oro).
/// </summary>
public static class MyMatches
{
    static string FilePath => Path.Combine(Paths.DataDir, "my-matches.jsonl");
    static string ProfilePath => Path.Combine(Paths.DataDir, "my-profile.json");

    /// <summary>Tus partidas, la más reciente primero.</summary>
    /// <param name="includeRemakes">
    /// Partidas rehechas (menos de 5 min, 0/0/0): no cuentan para tu historial ni tus estadísticas (una de 1:49 salía como
    /// victoria). Solo se incluyen para saber qué partidas ya están guardadas.
    /// </param>
    public static List<MyMatch> Load(bool includeRemakes = false)
    {
        var list = new List<MyMatch>();
        if (!File.Exists(FilePath)) return list;
        try
        {
            foreach (var line in File.ReadLines(FilePath))
                try { if (JsonSerializer.Deserialize<MyMatch>(line) is { } m) list.Add(m); } catch (JsonException) { }
        }
        catch (IOException) { }
        return list.DistinctBy(m => m.Id).Where(m => includeRemakes || m.Duration >= 300).OrderByDescending(m => m.Date).ToList();
    }

    public static MyProfile? LoadProfile()
    {
        try { return File.Exists(ProfilePath) ? JsonSerializer.Deserialize<MyProfile>(File.ReadAllText(ProfilePath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>
    /// Guarda las partidas de ARAM: Caos del historial del cliente (/lol-match-history/.../matches) que aún no estén.
    /// Devuelve cuántas añadió.
    /// </summary>
    public static int SaveFromHistory(JsonElement games, GameData data)
    {
        var known = Load(includeRemakes: true).Select(m => m.Id).ToHashSet();
        var aliasById = data.Champions.Values.ToDictionary(c => c.Id, c => c.Alias);
        var fresh = new List<MyMatch>();
        foreach (var g in games.EnumerateArray())
        {
            if (g.GetProperty("queueId").GetInt32() != MatchSync.MayhemQueueId) continue;
            string id = $"{g.GetProperty("platformId").GetString()}_{g.GetProperty("gameId").GetInt64()}";
            if (known.Contains(id) || g.GetProperty("participants").GetArrayLength() == 0) continue;
            var p = g.GetProperty("participants")[0];
            var st = p.GetProperty("stats");
            int Int(string name) => st.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
            int champId = p.GetProperty("championId").GetInt32();
            fresh.Add(new MyMatch(id, g.GetProperty("gameCreation").GetInt64(), g.GetProperty("gameDuration").GetInt32(),
                aliasById.GetValueOrDefault(champId, champId.ToString()), st.GetProperty("win").GetBoolean(),
                Int("kills"), Int("deaths"), Int("assists"), Int("totalDamageDealtToChampions"), Int("goldEarned"),
                Enumerable.Range(0, 6).Select(i => Int($"item{i}")).Where(i => i > 0).ToArray(),
                Enumerable.Range(1, 6).Select(i => Int($"playerAugment{i}")).Where(i => i > 0).ToArray()));
        }
        if (fresh.Count == 0) return 0;
        Directory.CreateDirectory(Paths.DataDir);
        File.AppendAllLines(FilePath, fresh.Select(m => JsonSerializer.Serialize(m)));
        return fresh.Count;
    }

    public static void SaveProfile(JsonElement summoner)
    {
        try
        {
            string Str(string n) => summoner.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            int Num(string n) => summoner.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var profile = new MyProfile(Str("gameName") is { Length: > 0 } g ? g : Str("displayName"), Str("tagLine"), Num("summonerLevel"), Num("profileIconId"));
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }
}
