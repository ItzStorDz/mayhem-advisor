using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MayhemAdvisor.Core;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Datos anónimos para mejorar la app (servidor en <c>cloud\</c>; qué se envía, en PRIVACY.md). Se puede quitar en
/// Ajustes («Compartir datos anónimos»), y no se envía nada hasta que la ventana principal haya enseñado el aviso.
/// <list type="bullet">
/// <item>Cada partida de ARAM: Caos al guardarse: la línea de matches.jsonl (campeones, aumentos, objetos y daño de los
/// 10, sin nombres), cuál eras tú, y del diario las cartas ofrecidas con su nota, las compras recomendadas y las muertes
/// (sin los textos).</item>
/// <item>Cada pocas horas, un resumen de errores del registro (sin rutas ni tu nombre) y contadores de salud.</item>
/// </list>
/// Lo pendiente se guarda en <c>nube\</c> y se reintenta; nunca molesta en partida (son unos pocos KB).
/// </summary>
static class Cloud
{
    /// <summary>El servidor. MAYHEM_CLOUD_URL: solo para las pruebas (el servidor en local).</summary>
    const string ServerUrl = "https://mayhem-advisor.itzstordz.workers.dev";
    static string Url => Environment.GetEnvironmentVariable("MAYHEM_CLOUD_URL") is { Length: > 0 } test ? test.TrimEnd('/') : ServerUrl;

    /// <summary>Los ajustes lo permiten (compartir activado). Se fija al arrancar y al cambiar el ajuste.</summary>
    public static bool Allowed { get; set; }
    /// <summary>Ya se enseñó el aviso de los datos anónimos: hasta entonces se apunta, pero no se envía.</summary>
    public static bool NoticeShown { get; set; }
    static bool CanSend => Allowed && NoticeShown && Url.Length > 0;

    static string Dir => Path.Combine(Paths.DataDir, "nube");
    static string PendingDir => Path.Combine(Dir, "pendientes");
    static string SentFile => Path.Combine(Dir, "enviadas.txt");
    static string ReportFile => Path.Combine(Dir, "errores.json");
    /// <summary>Partidas de la comunidad para las estadísticas (ver <see cref="DownloadCommunityAsync"/>).</summary>
    public static string CommunityFile => Path.Combine(Paths.DataDir, "comunidad.jsonl");

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MayhemAdvisor", Updater.Current.ToString(3)));
        return http;
    }
    static readonly SemaphoreSlim Busy = new(1, 1);

    // ───────── Partidas ─────────

    /// <summary>
    /// Prepara para enviar las partidas guardadas que aún no se enviaron (las de los últimos 30 días, la primera vez
    /// también las de antes de tener esta versión).
    /// </summary>
    public static void QueueNewMatches()
    {
        if (!Allowed) return;
        try
        {
            var done = File.Exists(SentFile) ? File.ReadAllLines(SentFile).ToHashSet() : [];
            Directory.CreateDirectory(PendingDir);
            done.UnionWith(Directory.EnumerateFiles(PendingDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)));
            long since = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
            var mine = MyMatches.Load().Where(m => m.Date >= since && !done.Contains(m.Id)).ToList();
            if (mine.Count == 0 || !File.Exists(Paths.Matches)) return;
            var lines = new Dictionary<string, JsonNode>();
            foreach (var line in File.ReadLines(Paths.Matches))
            {
                try { if (JsonNode.Parse(line) is JsonObject o && o["id"]?.GetValue<string>() is { } id && mine.Any(m => m.Id == id)) lines[id] = o; }
                catch (JsonException) { }
            }
            foreach (var m in mine)
                if (lines.TryGetValue(m.Id, out var game) && Payload(m, game) is { } payload)
                    File.WriteAllText(Path.Combine(PendingDir, m.Id + ".json"), payload.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static JsonObject? Payload(MyMatch m, JsonNode game)
    {
        if (game["p"] is not JsonArray players || players.Count != 10) return null;
        int me = -1;
        for (int i = 0; i < players.Count; i++)
            if (players[i]?["c"]?.GetValue<string>() == m.Champion && players[i]?["w"]?.GetValue<bool>() == m.Win) { me = i; break; }
        if (me < 0) return null;
        var journal = GameJournal.Find(m.Champion, m.Date, m.Duration);
        return new JsonObject
        {
            ["app"] = Updater.Current.ToString(3),
            ["lang"] = Lang.Code,
            ["id"] = m.Id,
            ["patch"] = game["v"]?.GetValue<string>() ?? "",
            ["dur"] = m.Duration,
            ["me"] = me,
            ["p"] = players.DeepClone(),
            // Sin las razones de compra (texto en un idioma): solo qué objeto y cuándo.
            ["journal"] = journal is null ? null : new JsonObject
            {
                ["picks"] = new JsonArray(journal.Picks.Select(p => (JsonNode)new JsonObject
                {
                    ["min"] = p.Minute, ["chosen"] = p.Chosen,
                    ["offered"] = new JsonArray(p.Offered.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["score"] = c.Score, ["grade"] = c.Grade }).ToArray()),
                }).ToArray()),
                ["buys"] = new JsonArray(journal.Buys.Select(b => (JsonNode)new JsonObject { ["min"] = b.Minute, ["item"] = b.ItemId }).ToArray()),
                ["build"] = new JsonArray(journal.FinalBuild.Select(i => (JsonNode)i).ToArray()),
                ["deaths"] = new JsonArray(journal.Deaths.Select(d => (JsonNode)new JsonObject { ["min"] = d.Minute, ["gold"] = d.Gold }).ToArray()),
                ["minutes"] = journal.Minutes,
            },
        };
    }

    /// <summary>
    /// Baja las partidas que comparten los demás (las de los 2 últimos parches, sin nombres) para que las estadísticas
    /// aprendan de todos. Se baja aunque no compartas las tuyas. True si cambió el archivo.
    /// </summary>
    public static async Task<bool> DownloadCommunityAsync(CancellationToken ct)
    {
        if (Url.Length == 0) return false;
        if (File.Exists(CommunityFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CommunityFile) < TimeSpan.FromHours(12)) return false;
        try
        {
            using var resp = await Http.GetAsync(Url + "/v1/community", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var text = await resp.Content.ReadAsStringAsync(ct);
            // Solo líneas de partida bien formadas (el servidor ya las comprobó al recibirlas).
            var lines = text.Split('\n').Where(l => l.StartsWith("{\"id\":")).ToList();
            var tmp = CommunityFile + ".tmp";
            await File.WriteAllLinesAsync(tmp, lines, ct);
            File.Move(tmp, CommunityFile, overwrite: true);
            Log.Write($"Comunidad: {lines.Count} partidas compartidas para las estadísticas");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { return false; }
    }

    // ───────── Errores y salud ─────────

    // Lo que se cuenta de cada línea del registro. Los fallos de red de «buscar actualizaciones» o del cliente de LoL
    // arrancando son avisos (pasan sin internet o al abrir el cliente), no errores.
    static readonly Regex Errors = new(@"^(Error |Fallo al |No pude (?!buscar actualizaciones|copiar el historial)|Overlay Electron no )", RegexOptions.Compiled);
    static readonly Regex Warnings = new(@"^(No pude buscar actualizaciones|No pude copiar el historial|La partida no apareció|Overlay Electron iniciado \(intento [2-9])", RegexOptions.Compiled);
    static readonly Regex Scan = new(@"^Lectura de pantalla: (\d+) ms de media \((\d+) lecturas", RegexOptions.Compiled);

    static readonly object Gate = new();
    static Dictionary<string, int>? _errors;  // "error|mensaje" → veces
    static Dictionary<string, long>? _health; // starts, games, cardSets, picks, scans, scanMs, crashes
    static DateTime _nextSave;

    /// <summary>Cada línea del registro pasa por aquí (ver <see cref="Log"/>): se cuentan errores y datos de salud.</summary>
    public static void Note(string message)
    {
        if (!Allowed) return;
        string? kind = Errors.IsMatch(message) ? "error" : Warnings.IsMatch(message) ? "warn" : null;
        string? counter = message.StartsWith("Mayhem Advisor iniciado") ? "starts"
            : message.StartsWith("Partida de Mayhem detectada") ? "games"
            : message.StartsWith("Cartas en pantalla:") ? "cardSets"
            : message.StartsWith("Aumento elegido:") ? "picks"
            : message.StartsWith("Error no controlado") || message.StartsWith("Error en segundo plano") || message.StartsWith("Error en el bucle") ? "crashes"
            : null;
        var scan = Scan.Match(message);
        if (kind is null && counter is null && !scan.Success) return;
        lock (Gate)
        {
            LoadReport();
            if (kind is not null)
            {
                var key = kind + "|" + Scrub(message);
                if (_errors!.Count < 60 || _errors.ContainsKey(key)) _errors[key] = _errors.GetValueOrDefault(key) + 1;
            }
            if (counter is not null) _health![counter] = _health.GetValueOrDefault(counter) + 1;
            if (scan.Success)
            {
                long n = long.Parse(scan.Groups[2].Value), ms = long.Parse(scan.Groups[1].Value);
                _health!["scans"] = _health.GetValueOrDefault("scans") + n;
                _health["scanMs"] = _health.GetValueOrDefault("scanMs") + ms * n;
            }
            if (DateTime.UtcNow >= _nextSave) { SaveReport(); _nextSave = DateTime.UtcNow.AddSeconds(30); }
        }
    }

    static string? _profileName;
    static readonly Regex UserPath = new(@"([A-Za-z]:|\\\\[^\\]+\\[A-Za-z]\$)\\Users\\[^\\\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Numbers = new(@"(?<![0-9A-Fa-fx])\d{2,}", RegexOptions.Compiled);

    /// <summary>
    /// El mensaje sin nada tuyo: la carpeta de usuario de Windows, tu nombre de invocador y los números (así los mismos
    /// errores se juntan). De una excepción, solo las 3 primeras líneas. Máximo 400 caracteres.
    /// </summary>
    internal static string Scrub(string message)
    {
        var text = string.Join(" ⏎ ", message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
        text = UserPath.Replace(text, @"C:\Users\<user>");
        _profileName ??= MyMatches.LoadProfile()?.Name;
        foreach (var secret in new[] { Environment.UserName, _profileName })
            if (secret is { Length: >= 3 }) text = Regex.Replace(text, Regex.Escape(secret), "<user>", RegexOptions.IgnoreCase);
        text = Numbers.Replace(text, "#");
        return text.Length > 400 ? text[..400] : text;
    }

    static void LoadReport()
    {
        if (_errors is not null) return;
        _errors = [];
        _health = [];
        try
        {
            if (File.Exists(ReportFile) && JsonNode.Parse(File.ReadAllText(ReportFile)) is JsonObject o)
            {
                foreach (var (k, v) in o["errors"]?.AsObject() ?? new JsonObject()) _errors[k] = v!.GetValue<int>();
                foreach (var (k, v) in o["health"]?.AsObject() ?? new JsonObject()) _health[k] = v!.GetValue<long>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
    }

    static void SaveReport()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var o = new JsonObject
            {
                ["errors"] = new JsonObject(_errors!.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)e.Value))),
                ["health"] = new JsonObject(_health!.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)h.Value))),
            };
            File.WriteAllText(ReportFile, o.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ───────── Envío ─────────

    /// <summary>Envía lo pendiente (partidas y el resumen de errores). Sin conexión, se queda para la próxima vez.</summary>
    public static async Task FlushAsync(CancellationToken ct)
    {
        if (!CanSend || !await Busy.WaitAsync(0, ct)) return;
        try
        {
            int sent = 0;
            if (Directory.Exists(PendingDir))
                foreach (var file in Directory.EnumerateFiles(PendingDir, "*.json").Take(40).ToList())
                {
                    var (status, error) = await PostAsync("/v1/match", await File.ReadAllTextAsync(file, ct), ct);
                    if (status is null or 429 or >= 500) break; // sin conexión o el servidor no puede ahora: luego
                    if (status == 200) { File.AppendAllText(SentFile, Path.GetFileNameWithoutExtension(file) + Environment.NewLine); sent++; }
                    else Log.Write($"Nube: partida rechazada ({status}): {error}");
                    File.Delete(file);
                }
            if (sent > 0) Log.Write($"Nube: {sent} partida(s) enviada(s)");
            await SendReportAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { Busy.Release(); }
    }

    static async Task SendReportAsync(CancellationToken ct)
    {
        string body;
        lock (Gate)
        {
            LoadReport();
            if (_errors!.Count == 0 && _health!.Count == 0) return;
            body = new JsonObject
            {
                ["app"] = Updater.Current.ToString(3),
                ["lang"] = Lang.Code,
                ["os"] = $"Windows {Environment.OSVersion.Version.Build}",
                ["errors"] = new JsonArray(_errors.Select(e =>
                {
                    int bar = e.Key.IndexOf('|');
                    return (JsonNode)new JsonObject { ["k"] = e.Key[..bar], ["m"] = e.Key[(bar + 1)..], ["n"] = e.Value };
                }).ToArray()),
                ["health"] = new JsonObject(_health!.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)Math.Min(h.Value, 10_000_000)))),
            }.ToJsonString();
        }
        var (status, error) = await PostAsync("/v1/report", body, ct);
        if (status is null or 429 or >= 500) return;
        if (status != 200) Log.Write($"Nube: resumen de errores rechazado ({status}): {error}");
        lock (Gate)
        {
            // Lo que se apuntó mientras se enviaba se pierde: son contadores, no hace falta exactitud.
            _errors!.Clear();
            _health!.Clear();
            SaveReport();
        }
    }

    /// <returns>El código HTTP (null sin conexión) y el error que da el servidor.</returns>
    static async Task<(int? Status, string? Error)> PostAsync(string path, string json, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsync(Url + path, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            return ((int)resp.StatusCode, resp.IsSuccessStatusCode ? null : await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, null); }
    }
}
