using System.Net.Http;
using System.Text.Json;

namespace MayhemAdvisor.Overlay;

/// <summary>Lo que necesitamos saber de la partida en curso.</summary>
public sealed record GameSnapshot(
    string Champion,
    double Gold,
    IReadOnlyList<int> MyItems,
    IReadOnlyList<(string Champion, IReadOnlyList<int> Items)> Enemies,
    IReadOnlyList<int> MyAugmentIds,
    IReadOnlyDictionary<string, int[]> EnemyAugmentIds,
    double GameTime = 0,
    bool IsDead = false,
    double RespawnTimer = 0,
    IReadOnlyDictionary<string, (int Kills, int Deaths)>? EnemyScores = null,
    double CritChance = 0,
    string? UnknownChampion = null,
    int Level = 0);

/// <summary>
/// Habla con la API oficial de Riot que el juego abre en tu PC durante la partida (127.0.0.1:2999).
/// No necesita clave y solo responde mientras estás dentro de una partida.
/// </summary>
public sealed class LiveClient : IDisposable
{
    readonly HttpClient _http = new(new HttpClientHandler
    {
        // El juego usa un certificado propio; solo lo aceptamos para 127.0.0.1.
        ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1",
    })
    {
        // MAYHEM_LIVE_URL: solo para las pruebas con el simulador de partida (sin la variable, el juego de verdad).
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("MAYHEM_LIVE_URL") ?? "https://127.0.0.1:2999/liveclientdata/"),
        Timeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>Por qué la última lectura no devolvió partida (para el registro de diagnóstico).</summary>
    public string LastStatus { get; private set; } = "";

    /// <summary>Devuelve la partida si estás jugando Mayhem; si no, null.</summary>
    public async Task<GameSnapshot?> GetMayhemSnapshotAsync()
    {
        try
        {
            var raw = await _http.GetStringAsync("allgamedata");
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var mode = root.GetProperty("gameData").GetProperty("gameMode").GetString();
            if (mode != "KIWI") { LastStatus = $"modo de juego {mode} (no es Mayhem)"; return null; }

            var active = root.GetProperty("activePlayer");
            var me = Str(active, "riotId") ?? Str(active, "summonerName");
            double gold = active.TryGetProperty("currentGold", out var g) ? g.GetDouble() : 0;

            var players = root.GetProperty("allPlayers").EnumerateArray().ToList();
            var mine = players.FirstOrDefault(p => me is not null && (Str(p, "riotId") == me || Str(p, "summonerName") == me));
            if (mine.ValueKind != JsonValueKind.Object) { LastStatus = $"no encuentro al jugador «{me}» en la lista"; return null; }
            var myTeam = Str(mine, "team");

            var enemies = players.Where(p => Str(p, "team") != myTeam)
                                 .Select(p => (Champion(p), (IReadOnlyList<int>)Items(p)))
                                 .ToList();
            var enemyAugs = players.Where(p => Str(p, "team") != myTeam)
                                   .GroupBy(Champion).ToDictionary(g => g.Key, g => Augments(g.First()));
            var myAugs = Augments(mine).Concat(Augments(active)).Distinct().ToArray();
            // Asesinatos y muertes de cada enemigo: el que va alimentado pesa más en el tipo de daño enemigo.
            var enemyScores = players.Where(p => Str(p, "team") != myTeam).GroupBy(Champion)
                .ToDictionary(g => g.Key, g => (Score(g.First(), "kills"), Score(g.First(), "deaths")));

            // Riot no documenta si aquí vienen los aumentos: guardamos una muestra (cada minuto) para poder revisarlo.
            if (DateTime.UtcNow >= _nextSample)
            {
                _nextSample = DateTime.UtcNow.AddMinutes(1);
                try { File.WriteAllText(Path.Combine(Core.Paths.DataDir, "live-sample.json"), raw); } catch (IOException) { }
            }
            LastStatus = "ok";
            double gameTime = root.GetProperty("gameData").TryGetProperty("gameTime", out var gt) && gt.ValueKind == JsonValueKind.Number ? gt.GetDouble() : 0;
            bool dead = mine.TryGetProperty("isDead", out var dd) && dd.ValueKind == JsonValueKind.True;
            double respawn = mine.TryGetProperty("respawnTimer", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : 0;
            // Tu probabilidad de crítico (0-1): con el 100 % ya no conviene comprar más crítico.
            double crit = active.TryGetProperty("championStats", out var cs) && cs.TryGetProperty("critChance", out var cc) && cc.ValueKind == JsonValueKind.Number
                ? cc.GetDouble() : 0;
            if (crit > 1.5) crit /= 100; // por si viniera en % y no en tanto por uno
            var champion = Champion(mine);
            // Campeón que no reconocemos: los nombres que dio el juego, para el registro (y no se recomienda a ciegas).
            string? unknown = Resolve is { } check && check(champion) is null ? RawNames(mine) : null;
            int level = active.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
            return new GameSnapshot(champion, gold, Items(mine), enemies, myAugs, enemyAugs, gameTime, dead, respawn, enemyScores, crit, unknown, level);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            LastStatus = ex switch
            {
                HttpRequestException => "el juego no responde (no hay partida o está cargando)",
                TaskCanceledException => "el juego tardó más de 2 s en responder",
                _ => $"respuesta incompleta del juego ({ex.GetType().Name})",
            };
            return null; // no hay partida, está cargando, etc.
        }
    }

    DateTime _nextSample = DateTime.MinValue;

    static int Score(JsonElement p, string name) =>
        p.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    /// <summary>Ids de aumento en cualquier campo cuyo nombre contenga "augment" (números, listas u objetos con id).</summary>
    static int[] Augments(JsonElement p)
    {
        var ids = new List<int>();
        if (p.ValueKind == JsonValueKind.Object)
            foreach (var prop in p.EnumerateObject())
                if (prop.Name.Contains("augment", StringComparison.OrdinalIgnoreCase)) Collect(prop.Value, ids);
        return ids.Where(id => id > 0).Distinct().ToArray();

        static void Collect(JsonElement v, List<int> ids)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number when v.TryGetInt32(out var n): ids.Add(n); break;
                case JsonValueKind.Array: foreach (var e in v.EnumerateArray()) Collect(e, ids); break;
                case JsonValueKind.Object:
                    foreach (var name in new[] { "id", "augmentId", "augmentID", "ID" })
                        if (v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n2)) { ids.Add(n2); return; }
                    break;
            }
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Reconoce un nombre de campeón (alias como «MonkeyKing» o nombre visible como «Wukong», «Lee Sin») y devuelve su
    /// alias; null si no es un campeón. Lo pone la app al cargar los datos del juego.
    /// </summary>
    public static Func<string, string?>? Resolve { get; set; }

    // "game_character_displayname_MonkeyKing" -> "MonkeyKing" (mismo nombre que usa la API de partidas). Con alguna skin el
    // juego da otra cosa: Seraphine salió como «Name» (2026-11-02) y se le recomendaron objetos de AD. Entonces vale el
    // nombre de la skin («game_character_skin_displayname_Seraphine_1») o el que se ve en pantalla («Seraphine»).
    static string Champion(JsonElement p)
    {
        var raw = Str(p, "rawChampionName") ?? "";
        var fromRaw = raw[(raw.LastIndexOf('_') + 1)..];
        if (Resolve is not { } resolve) return fromRaw;
        var candidates = raw.Split('_').Reverse()
            .Concat((Str(p, "rawSkinName") ?? "").Split('_').Reverse())
            .Append(Str(p, "championName") ?? "");
        return candidates.Select(c => c.Length > 1 ? resolve(c) : null).FirstOrDefault(a => a is not null) ?? fromRaw;
    }

    /// <summary>Los nombres que dio el juego para tu campeón, para el registro si no se reconoce.</summary>
    public static string RawNames(JsonElement p) =>
        $"rawChampionName «{Str(p, "rawChampionName")}», rawSkinName «{Str(p, "rawSkinName")}», championName «{Str(p, "championName")}»";

    static List<int> Items(JsonElement p) =>
        p.TryGetProperty("items", out var items)
            ? items.EnumerateArray().Select(i => i.GetProperty("itemID").GetInt32()).ToList()
            : [];

    public void Dispose() => _http.Dispose();
}
