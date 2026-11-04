using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using MayhemAdvisor.Core;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Overlay;

/// <summary>Tu última partida, para adjuntarla a una sugerencia (sin nombres; lo mismo que «Compartir datos anónimos»).</summary>
/// <param name="AppBuild">La build completa que te recomendaba la app (para entender un «me recomendó algo raro»).</param>
sealed record FeedbackGame(string Champion, bool? Win, IReadOnlyList<int> Augments, IReadOnlyList<int> Items, double Minutes,
    IReadOnlyList<int> AppBuild, DateTime StartedUtc)
{
    static readonly Regex Alias = new("^[A-Za-z]{2,20}$");

    /// <summary>Lo que se envía (lo mismo que comprueba el servidor); null con un campeón que no reconocemos.</summary>
    public JsonObject? ToJson()
    {
        if (!Alias.IsMatch(Champion)) return null;
        static JsonArray Ids(IEnumerable<int> ids) => new(ids.Where(id => id is > 0 and <= 999999).Take(8).Select(id => (JsonNode)id).ToArray());
        return new JsonObject
        {
            ["c"] = Champion, ["a"] = Ids(Augments), ["i"] = Ids(Items), ["r"] = Ids(AppBuild),
            ["w"] = Win, ["min"] = Math.Round(Math.Clamp(Minutes, 0, 90), 1),
        };
    }
}

/// <summary>
/// Sugerencias (1.8.2). Al acabar una partida sale una ventanita «¿Qué te parece Mayhem Advisor?»: como mucho una vez al
/// día, en partidas de 5 minutos o más y solo hasta que envíes tu opinión (o pulses «No volver a preguntar»). Después queda
/// la pestaña «Sugerencias» de la ventana principal (también en la bandeja) para ideas, fallos o lo que sea. Lo enviado
/// llega al informe diario del creador (servidor en cloud\, ver <see cref="Cloud.SendFeedbackAsync"/>).
/// </summary>
public partial class App
{
    const double FeedbackMinMinutes = 5;   // las partidas repetidas (remake) no cuentan
    const int FeedbackDelayMs = 15_000;    // que ya estés en la pantalla de resultados del cliente

    DashboardWindow? _feedbackWindow;
    /// <summary>La partida que acaba de ver la app (aún sin guardar en tu historial al salir la ventanita).</summary>
    FeedbackGame? _lastGameSeen;
    IReadOnlyList<int> _lastMyItems = [];
    volatile bool _inGame, _inChampSelect;

    static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    bool ShouldAskFeedback(double minutes) =>
        !_settings.FeedbackSent && !_settings.FeedbackNeverAsk && _settings.FeedbackAskedOn != Today
        && minutes >= FeedbackMinMinutes && _feedbackWindow is null;

    /// <summary>Al acabar la partida (ver EndGame): espera unos segundos y, si toca hoy, pregunta.</summary>
    async Task AskFeedbackAfterGameAsync(double minutes, CancellationToken ct)
    {
        if (!ShouldAskFeedback(minutes)) return;
        await Delay(FeedbackDelayMs, ct);
        // Ya en otra partida (o reconectando) o en la selección de campeón: mejor tras la próxima.
        if (ct.IsCancellationRequested || _inGame || _inChampSelect || !ShouldAskFeedback(minutes)) return;
        _settings.FeedbackAskedOn = Today;
        _settings.Save();
        Log.Write("Sugerencias: sale «¿Qué te parece la app?» al acabar la partida");
        OpenFeedbackPrompt();
    }

    /// <summary>
    /// La ventanita, abajo a la derecha y por encima del cliente, sin quitarte el foco (si estás escribiendo en el chat,
    /// sigues escribiendo). Se cierra sola si empieza otra partida.
    /// </summary>
    void OpenFeedbackPrompt()
    {
        var window = new DashboardWindow(OnFeedbackMessage, "postgame", "sugerencias.html")
        {
            Title = L("Mayhem Advisor · Tu opinión", "Mayhem Advisor · Your feedback"),
            Width = 440, Height = 610, MinWidth = 0, MinHeight = 0,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 16;
        window.Top = Math.Max(area.Top, area.Bottom - window.Height - 16);
        window.Closed += (_, _) => _feedbackWindow = null;
        _feedbackWindow = window;
        window.Show();
    }

    /// <summary>Empieza otra partida: fuera la ventanita (no puede quedarse por encima del juego).</summary>
    void CloseFeedbackPrompt()
    {
        if (_feedbackWindow is { } window)
        {
            Log.Write("Sugerencias: la ventanita se cierra (empieza otra partida)");
            window.Close();
        }
    }

    /// <summary>En la selección de campeón la ventanita deja de estar por encima (no tapa el cliente ni el panel).</summary>
    void LowerFeedbackPrompt()
    {
        if (_feedbackWindow is { Topmost: true } window) window.Topmost = false;
    }

    /// <summary>Lo que pide la ventanita (sugerencias.html).</summary>
    async Task<object?> OnFeedbackMessage(JsonElement msg)
    {
        switch (msg.GetProperty("type").GetString())
        {
            case "ready":
            case "feedbackInfo":
                return FeedbackInfo("postgame");
            case "feedback":
                return await SubmitFeedbackAsync(msg);
            case "close":
                CloseFeedbackSoon();
                return null;
            case "never":
                _settings.FeedbackNeverAsk = true;
                _settings.Save();
                Log.Write("Sugerencias: «No volver a preguntar»");
                CloseFeedbackSoon();
                return null;
        }
        return null;
    }

    // Se cierra después de contestar a la página (cerrarla dentro de su propio aviso libera WebView2 mientras lo usa).
    void CloseFeedbackSoon() => _ = Dispatcher.InvokeAsync(() => _feedbackWindow?.Close(), System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Idioma y tu última partida (para «Adjuntar mi última partida»), para el formulario.</summary>
    object FeedbackInfo(string source)
    {
        var game = LastGameForFeedback();
        return new
        {
            type = "feedbackInfo", lang = Lang.Code, source,
            // El nombre en el idioma de ahora (la página lo vuelve a pedir al cambiar de idioma); «victoria» lo pone la página.
            lastGame = game?.ToJson() is null ? null : new { name = _data.ChampionName(game.Champion), win = game.Win },
        };
    }

    /// <summary>
    /// Tu última partida: la de tu historial (con victoria o derrota) o, si aún no se guardó, la que acaba de ver la app.
    /// </summary>
    FeedbackGame? LastGameForFeedback()
    {
        MyMatch? saved = null;
        try { saved = MyMatches.Load().FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        // La del historial empieza un poco antes de que la app la vea (pantalla de carga): con 10 min de margen es la misma.
        if (saved is not null && (_lastGameSeen is not { } seen || DateTimeOffset.FromUnixTimeMilliseconds(saved.Date).UtcDateTime >= seen.StartedUtc.AddMinutes(-10)))
        {
            var journal = GameJournal.Find(saved.Champion, saved.Date, saved.Duration);
            return new FeedbackGame(saved.Champion, saved.Win, saved.Augments, saved.Items, saved.Duration / 60.0, journal?.FinalBuild ?? [],
                DateTimeOffset.FromUnixTimeMilliseconds(saved.Date).UtcDateTime);
        }
        return _lastGameSeen;
    }

    /// <summary>Envía lo que escribiste (desde la ventanita o desde la pestaña «Sugerencias»).</summary>
    async Task<object> SubmitFeedbackAsync(JsonElement msg)
    {
        string kind = msg.TryGetProperty("kind", out var k) && k.GetString() is "idea" or "bug" or "other" ? k.GetString()! : "other";
        int? rating = msg.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n) && n is >= 1 and <= 5 ? n : null;
        string text = msg.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
        if (text.Length > 2000) text = text[..2000];
        string source = msg.TryGetProperty("source", out var s) && s.GetString() == "postgame" ? "postgame" : "app";
        bool attach = msg.TryGetProperty("attach", out var a) && a.ValueKind == JsonValueKind.True;
        if (text.Length == 0 && rating is null) return new { type = "feedbackResult", ok = false, sent = false };

        var body = new JsonObject { ["kind"] = kind, ["rating"] = rating, ["text"] = text, ["source"] = source };
        if (attach && LastGameForFeedback()?.ToJson() is { } game) body["context"] = game;
        bool sent = await Cloud.SendFeedbackAsync(body, _cts.Token);
        // Ya opinó: no se le vuelve a preguntar al acabar las partidas (le queda la pestaña «Sugerencias»).
        _settings.FeedbackSent = true;
        _settings.Save();
        // Sin el texto en el registro: solo el tipo, la nota y cuánto ocupa.
        Log.Write($"Sugerencias: {kind}{(rating is { } stars ? $" {stars}★" : "")}, {text.Length} letras, desde {(source == "postgame" ? "la ventanita" : "la pestaña")}"
                  + $"{(body["context"] is null ? "" : ", con la última partida")}: {(sent ? "enviada" : "guardada para enviar")}");
        return new { type = "feedbackResult", ok = true, sent };
    }
}
