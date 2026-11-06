using static MayhemAdvisor.Core.Lang;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MayhemAdvisor.Core;
using Forms = System.Windows.Forms;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Bucle principal:
///  • Selección de campeón (ARAM: Caos): muestra el % de victorias de tu campeón y los del banco.
///  • En partida: lee la pantalla cada 0,25 s; cuando aparecen cartas de aumento, las puntúa y dice
///    con qué objetos van bien. Detecta cuál eliges (por dónde haces clic) y ajusta las compras.
///  • Fuera de partida casi no gasta nada (comprueba cada pocos segundos).
/// </summary>
public partial class App : System.Windows.Application
{
    const int ScanIntervalMs = 100; // lectura de pantalla ~10 veces por segundo en partida (cada lectura tarda 5-30 ms)
    static readonly TimeSpan GameLostGrace = TimeSpan.FromSeconds(8);
    string? _lastScanError;
    DateTime _nextSingleHitLog;
    int _lastSingleHitId;
    // Cuánto tiempo sin ver las cartas antes de quitarlas: depende de si hiciste clic en una (= elegiste).
    // Sin clic sobre una carta se esperaba hasta 60 s y las tarjetas se quedaban en pantalla con la partida ya en marcha
    // (Wakka, 2026-11-01). Si las cartas vuelven (las ocultaste para mirar el mapa), se leen y se enseñan otra vez.
    static readonly TimeSpan HideAfterPick = TimeSpan.FromSeconds(0.8),
        HideAfterOtherClick = TimeSpan.FromSeconds(2.5),
        HideWithoutClick = TimeSpan.FromSeconds(8);
    // Una carta que sigue sola tras este rato no es una carta: las de verdad salen de 3 en 3 y se leen en <1 s.
    // (Con Singed, una palabra del juego leída como "Vulnerabilidad" salió sola y se apuntó como aumento elegido.)
    static readonly TimeSpan LoneCardTimeout = TimeSpan.FromSeconds(3);
    DateTime _cardsLastSeen, _cardsShownAt;
    (int Id, DateTime Until) _ignoredLone; // la carta suelta descartada: no se vuelve a mostrar sola durante un rato
    System.Drawing.Rectangle _gameArea = Forms.Screen.PrimaryScreen!.Bounds; // zona del juego de la última lectura

    MainWindow _overlay = null!;
    DashboardWindow? _dashboard;
    Forms.NotifyIcon _tray = null!;
    readonly CancellationTokenSource _cts = new();

    AugmentCatalog _catalog = null!;
    AugmentMatcher _matcher = null!;
    GameData _data = new();
    StatsStore _stats = new();
    DateTime _statsLoadedFrom;
    MayhemChampions _mayhem = new(); // % de todos los jugadores (herramientas/actualizar-mayhem.mjs)
    MayhemBuilds _builds = new();    // sus objetos y aumentos por campeón (el mismo script)
    AugmentItemLinks _links = new(); // qué objetos pide o regala cada aumento
    MayhemAugments _augmentTable = new(); // % de cada campeón con cada aumento (todos los jugadores)
    ExternalAugments _aramhex = new(), _aramkit = new(); // lo mismo según AramHex y aramkit.com (punto de partida de las notas)
    AugmentBuilds _aramkitBuilds = new(); // estilo de build (apoyo, AP…) con cada aumento, de aramkit.com
    ChampionKits _kits = new(); // para qué sirve el kit de cada campeón (herramientas/actualizar-kits.mjs)
    DateTime _mayhemLoadedFrom, _buildsLoadedFrom, _augmentTextsLoadedFrom, _aramhexLoadedFrom, _aramkitLoadedFrom, _aramkitBuildsLoadedFrom, _kitsLoadedFrom;
    readonly Settings _settings = Settings.Load();
    bool _panelEnabled = true;

    // Estado de la partida actual
    readonly List<Augment> _myAugments = [];
    List<AugmentCard> _cards = [];
    readonly List<Click> _clicks = [];
    /// <summary>Un clic izquierdo mientras se ven las cartas: dónde (en la pantalla) y cuándo.</summary>
    readonly record struct Click(int X, int Y, DateTime At);
    // Los clics se vigilan en un hilo aparte: el hilo principal se ocupa de leer la pantalla y a veces tarda,
    // y con un temporizador en él se perdían clics rápidos (justo el de elegir carta).
    readonly System.Collections.Concurrent.ConcurrentQueue<Click> _clickQueue = new();
    volatile bool _trackClicks;
    object? _lastLive;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Números en español en todos los textos (47,3 %), igual que en el overlay, aunque Windows esté en inglés.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        _overlay = new MainWindow();
        _overlay.Show();
        // Overlay nuevo en Electron (pegado a la ventana del juego). Si no está instalado, el de siempre.
        _bridge = ElectronBridge.TryCreate();
        if (_bridge is not null) _bridge.MessageReceived += msg => Dispatcher.InvokeAsync(() => OnOverlayMessage(msg));
        _overlay.Suppressed = _bridge is not null;
        _panelEnabled = _settings.ShowPanel;
        Lang.English = _settings.Language == "en";
        Cloud.Allowed = _settings.ShareData;
        Cloud.NoticeShown = _settings.DataNoticeShown;
        CreateTray();
        new Thread(ClickWatcher) { IsBackground = true, Name = "Clics", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(WheelWatcher) { IsBackground = true, Name = "Rueda", Priority = ThreadPriority.AboveNormal }.Start();
        // Un error inesperado se apunta en el registro en vez de cerrar el programa.
        DispatcherUnhandledException += (_, args) => { Log.Write($"Error no controlado: {args.Exception}"); args.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Write($"Error en segundo plano: {args.Exception}"); args.SetObserved(); };
        // En cuanto cambia la ventana activa (p. ej. el juego en modo ventana toma el foco), volvemos a ponernos encima.
        _foregroundHook = (_, _, _, _, _, _, _) => _overlay.KeepOnTop();
        _foregroundHookHandle = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundHook, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log.Write("Mayhem Advisor iniciado");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _tray.Text = "Mayhem Advisor (cargando datos…)";
            _catalog = await AugmentCatalog.LoadAsync(http);
            _matcher = new AugmentMatcher(_catalog);
            _data = await GameData.LoadAsync(http);
            // Para reconocer tu campeón aunque el juego dé un nombre raro (Seraphine salió como «Name», 2026-11-02): alias o
            // nombre visible, en español o en inglés, también sin espacios ni apóstrofos («Kog'Maw», «Lee Sin»).
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Compact(string n) => new(n.Where(char.IsLetterOrDigit).ToArray());
            foreach (var c in _data.Champions.Values)
                foreach (var n in new[] { c.Alias, c.Name, c.NameEn }.OfType<string>().Where(n => n.Length > 1))
                {
                    byName.TryAdd(n, c.Alias);
                    byName.TryAdd(Compact(n), c.Alias);
                }
            LiveClient.Resolve = n => byName.GetValueOrDefault(n) ?? byName.GetValueOrDefault(Compact(n));
            await SyncHistoryAsync();
            ReloadStats();
            _tray.Text = "Mayhem Advisor";
            // Los archivos de datos descargados ya no hacen falta en memoria: devolvemos esa RAM a Windows.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            Log.Write($"No pude cargar los datos del juego: {ex}");
            Forms.MessageBox.Show(L($"No pude cargar los datos del juego:\n{ex.Message}", $"Couldn't load the game data:\n{ex.Message}"), "Mayhem Advisor");
            Quit();
            return;
        }

        var reader = ScreenReader.Create();
        if (reader is null)
        {
            Forms.MessageBox.Show(L("Windows no tiene ningún idioma de OCR instalado.\nAñade Español o Inglés en Configuración > Hora e idioma > Idioma.",
                "Windows has no OCR language installed.\nAdd Spanish or English in Settings > Time & language > Language."), "Mayhem Advisor");
            Quit();
            return;
        }

        // Si ya estás jugando, no abrimos la ventana principal para no sacarte del juego.
        // MAYHEM_OPEN: solo para las pruebas (abre la ventana en una pestaña, p. ej. "partida:LA1_123").
        if (System.Diagnostics.Process.GetProcessesByName("League of Legends").Length == 0) OpenDashboard(Environment.GetEnvironmentVariable("MAYHEM_OPEN"));
        _ = UpdateLoopAsync(_cts.Token);
        _ = CloudLoopAsync(_cts.Token);
        await RunLoopAsync(reader, _cts.Token);
    }

    // ───────────────────────── Aceptar partida automáticamente ─────────────────────────

    /// <summary>
    /// Si el cliente muestra "¡Partida encontrada!" y aún no respondiste, pulsa Aceptar (API local del cliente,
    /// la misma que usa su botón). Con "solo ARAM: Caos", las demás colas se dejan para que las aceptes tú.
    /// </summary>
    async Task TryAutoAcceptAsync(LcuClient lcu, CancellationToken ct)
    {
        try
        {
            using var check = await lcu.GetAsync("/lol-matchmaking/v1/ready-check", ct);
            var root = check.RootElement;
            if (root.GetProperty("state").GetString() != "InProgress" || root.GetProperty("playerResponse").GetString() != "None") return;
            if (_settings.AutoAcceptOnlyMayhem)
            {
                using var session = await lcu.GetAsync("/lol-gameflow/v1/session", ct);
                int queue = session.RootElement.GetProperty("gameData").GetProperty("queue").GetProperty("id").GetInt32();
                if (queue != MatchSync.MayhemQueueId) return;
            }
            if (await lcu.PostAsync("/lol-matchmaking/v1/ready-check/accept", ct)) Log.Write("Partida aceptada automáticamente");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Sin búsqueda en marcha el cliente responde 404: no hay nada que aceptar.
        }
    }

    // ───────────────────────── Bucle principal ─────────────────────────

    async Task RunLoopAsync(ScreenReader reader, CancellationToken ct)
    {
        using var live = new LiveClient();
        LcuClient? lcu = null;
        DateTime nextLcuTry = DateTime.MinValue;
        string? lastChampion = null, lastContext = null;
        Advice? advice = null;
        DateTime nextAdvice = DateTime.MinValue, nextGamePoll = DateTime.MinValue, nextSlowTick = DateTime.MinValue;
        GameSnapshot? game = null, lastGoodGame = null;
        Task<GameSnapshot?>? gamePoll = null;
        DateTime lastGoodAt = DateTime.MinValue;
        string lastStatus = "";

        while (!ct.IsCancellationRequested)
        {
          try
          {
            // Los datos de la partida (oro, objetos) cambian despacio: basta con pedirlos cada segundo.
            // La pantalla, en cambio, se lee cada 0,25 s para que las cartas salgan enseguida.
            // En partida la consulta va en paralelo: si el juego tarda en responder (hasta 2 s), la pantalla se sigue
            // leyendo y las cartas no se retrasan. Fuera de partida se espera, porque no hay nada más que hacer.
            if (game is not null && gamePoll is null && DateTime.UtcNow >= nextGamePoll) gamePoll = live.GetMayhemSnapshotAsync();
            if (game is null || gamePoll is { IsCompleted: true })
            {
                var fresh = gamePoll is not null ? await gamePoll : await live.GetMayhemSnapshotAsync();
                gamePoll = null;
                nextGamePoll = DateTime.UtcNow.AddSeconds(1);
                if (live.LastStatus != lastStatus) Log.Write($"Juego: {lastStatus = live.LastStatus}");

                if (fresh is not null) { game = lastGoodGame = fresh; lastGoodAt = DateTime.UtcNow; }
                // Un tirón o una pantalla de carga no es el final de la partida: seguimos con la última lectura buena un rato.
                else if (lastChampion is not null && lastGoodGame is not null && DateTime.UtcNow - lastGoodAt < GameLostGrace) game = lastGoodGame;
                else game = null;
            }

            // ── Fuera de partida: ¿selección de campeón? ──
            if (game is null)
            {
                if (lastChampion is not null) EndGame(ct);
                lastChampion = lastContext = null;
                advice = null;

                if (lcu is null && DateTime.UtcNow >= nextLcuTry)
                {
                    lcu = LcuClient.Connect();
                    nextLcuTry = DateTime.UtcNow.AddSeconds(10);
                }

                ChampSelectState? select = null;
                if (lcu is not null)
                {
                    try { select = await ChampSelect.ReadAsync(lcu, ct); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        lcu.Dispose(); lcu = null; // el cliente se cerró o se reinició
                    }
                }

                _inChampSelect = select is { QueueId: MatchSync.MayhemQueueId } && lcu is not null;
                if (select is { QueueId: MatchSync.MayhemQueueId } && lcu is not null)
                {
                    LowerFeedbackPrompt(); // la ventanita de sugerencias ya no va por encima del cliente
                    await ChampSelectExtrasAsync(lcu, select, ct);
                    ReloadStats(); // también recarga los datos de mayhemmeta.com si cambiaron
                    var options = ChampSelect.Rank(select, _stats, _mayhem);
                    if (_panelEnabled) _overlay.ShowChampSelect(options); else _overlay.HideSidePanel();
                    _overlay.KeepOnTop();
                    PushLive("champselect", L("Selección de campeón", "Champion select"), champSelect: options);
                    await Delay(1000, ct);
                }
                else
                {
                    _overlay.HideSidePanel();
                    _lcuActions.Clear(); // un botón pulsado justo al salir de la selección ya no vale
                    _trades = [];
                    PushLive("idle", lcu is null ? L("Cliente de LoL cerrado", "League client closed") : L("Esperando partida de ARAM: Caos", "Waiting for an ARAM: Mayhem game"));
                    if (lcu is not null && _settings.AutoAccept) await TryAutoAcceptAsync(lcu, ct);
                    // Con "aceptar partida" activado se mira cada segundo (el aviso dura ~10 s); si no, cada 3 s.
                    await Delay(_settings.AutoAccept ? 1000 : 3000, ct);
                }
                continue;
            }

            // ── En partida ──
            _inGame = true;
            _inChampSelect = false;
            if (game.Champion != lastChampion)
            {
                Log.Write($"Partida de Mayhem detectada: {game.Champion}");
                CloseFeedbackPrompt(); // nunca por encima del juego
                if (game.UnknownChampion is { } rawNames)
                    Log.Write($"No reconozco el campeón (no se recomiendan objetos): {rawNames}");
                lastChampion = game.Champion;
                _journal = new GameJournal { StartedUtc = DateTime.UtcNow, Lang = Lang.Code, Champion = _data.Champion(game.Champion)?.Alias ?? game.Champion };
                lastContext = null;
                _myAugments.Clear();
                _keepItems.Clear();
                _lockedStyle = null;
                _nameRowRel = null;
                _pendingPick = null;
                ReloadStats(); // por si se añadieron partidas desde la última vez
                ShowStatus($"Mayhem Advisor · {_data.ChampionName(game.Champion)} · " + L($"{_stats.MatchCount} partidas{(_stats.Patch is null ? "" : $" (parche {_stats.Patch})")}",
                    $"{_stats.MatchCount} games{(_stats.Patch is null ? "" : $" (patch {_stats.Patch})")}"));
            }

            // Si el juego nos dice tus aumentos, son más fiables que adivinarlos por el clic.
            foreach (var id in game.MyAugmentIds)
                if (_catalog.Get(id) is { } own && !_myAugments.Any(a => a.Id == id)) _myAugments.Add(own);

            // Consejos de compra: se recalculan cuando cambian objetos (tuyos o enemigos), oro (de 100 en 100) o aumentos (como mucho cada 2 s).
            var context = $"{string.Join(";", game.MyItems)}|{(int)game.Gold / 100}|{string.Join(";", _myAugments.Select(a => a.Id))}|" +
                          string.Join(";", game.Enemies.Select(en => en.Champion + ":" + string.Join(",", en.Items) + ":" +
                              string.Join(",", game.EnemyAugmentIds.GetValueOrDefault(en.Champion) ?? [])));
            context += "|" + string.Join(";", _keepItems) + "|" + _lockedStyle;
            // Fase de la partida (14 / 25 min) y muerte: al cambiar de fase o al morir (es cuando compras)
            // se recalcula al momento, sin esperar los 2 s.
            var gamePhase = GamePhases.From(game.GameTime);
            context += "|" + gamePhase + "|" + game.IsDead;
            if (gamePhase != _lastPhase || (game.IsDead && !_wasDead)) nextAdvice = DateTime.MinValue;
            // Para revisar la partida después (lo que viste y cuándo): cambios de fase y muertes.
            if (gamePhase != _lastPhase && game.GameTime > 0) Log.Write($"Fase: {GamePhases.Label(gamePhase)} (min {game.GameTime / 60:0})");
            if (game.IsDead && !_wasDead)
            {
                Log.Write($"Muerto (min {game.GameTime / 60:0}, reaparece en {game.RespawnTimer:0} s, oro {game.Gold:0})");
                _journal?.Deaths.Add(new JournalDeath(Math.Round(game.GameTime / 60, 1), (int)game.Gold));
            }
            // Al reaparecer: el oro que te quedó sin gastar (muerto se compra; el oro al morir no dice nada de eso).
            if (!game.IsDead && _wasDead && _journal is { Deaths.Count: > 0 } jd && jd.Deaths[^1] is { Left: null } lastDeath)
                jd.Deaths[^1] = lastDeath with { Left = (int)game.Gold };
            _gameMinutes = game.GameTime / 60;
            _lastMyItems = game.MyItems;
            _lastPhase = gamePhase;
            _wasDead = game.IsDead;
            if (_forceAdvice) { _forceAdvice = false; lastContext = null; nextAdvice = DateTime.MinValue; }
            // Con un campeón que no reconocemos no se recomienda nada: antes salía una build genérica de AD (Seraphine).
            if (game.UnknownChampion is null && context != lastContext && (DateTime.UtcNow >= nextAdvice || _keepChanged))
            {
                _keepChanged = false;
                lastContext = context;
                nextAdvice = DateTime.UtcNow.AddSeconds(2);
                var enemyAugs = game.EnemyAugmentIds.ToDictionary(kv => kv.Key,
                    kv => (IReadOnlyList<Augment>)kv.Value.Select(_catalog.Get).OfType<Augment>().ToList());
                advice = new Advisor(_stats).Build(game.Champion, game.MyItems, game.Enemies, _myAugments, game.Gold, enemyAugs, _keepItems, gamePhase, game.EnemyScores, game.CritChance, _lockedStyle);
                // Lo que recomendaba en cada momento (solo cuando cambia), con tus objetos y el dato enemigo.
                var shown = $"{advice.Next?.Item.DisplayName ?? "-"} | build: {string.Join(", ", (advice.FullBuild ?? []).Select(b => b.Item.Item.DisplayName))}";
                if (shown != _lastAdviceLog)
                {
                    _lastAdviceLog = shown;
                    if (advice.Next is { } nb && _journal is { } jr && jr.Buys.LastOrDefault()?.ItemId != nb.Item.Id)
                        jr.Buys.Add(new JournalBuy(Math.Round(game.GameTime / 60, 1), nb.Item.Id,
                            advice.Items.Concat(advice.Boots is { } b2 ? [b2] : []).FirstOrDefault(i => i.Item.Id == nb.Item.Id)?.Reason ?? ""));
                    if (_journal is not null) _journal.FinalBuild = (advice.FullBuild ?? []).Select(b => b.Item.Item.Id).ToList();
                    var why = advice.Items.Concat(advice.Boots is { } bb ? [bb] : []).FirstOrDefault(i => i.Item.Id == advice.Next?.Item.Id)?.Reason;
                    Log.Write($"Compra recomendada (min {game.GameTime / 60:0}): {advice.Next?.Item.DisplayName ?? "nada"}{(why is { Length: > 0 } ? $" ({why})" : "")}" +
                              $" · build: {string.Join(", ", (advice.FullBuild ?? []).Select(b => (b.Owned ? "✓" : "") + b.Item.Item.DisplayName))}" +
                              $" · tienes: {string.Join(", ", game.MyItems.Select(_data.Item).OfType<ItemInfo>().Where(i => i.IsFinal).Select(i => i.DisplayName))}" +
                              (advice.Enemies.Fact() is { } fact ? $" · {fact}" : ""));
                }
                if (_panelEnabled) _overlay.ShowAdvice(advice, game.Gold); else _overlay.HideSidePanel();
            }

            bool cardsChanged = false;
            try
            {
                // Solo la ventana del juego (en modo ventana no ocupa toda la pantalla); si no se encuentra, la pantalla principal.
                var screen = _gameArea = ScreenReader.GameArea();
                var scanWatch = System.Diagnostics.Stopwatch.StartNew();
                var lines = await reader.ReadAsync(screen);
                _scanMs += scanWatch.Elapsed.TotalMilliseconds; _scans++;
                // Media móvil de lo que tarda una lectura: en PCs lentos se lee algo menos a menudo (ver ScanDelay).
                _scanEma = _scanEma <= 0 ? scanWatch.Elapsed.TotalMilliseconds : _scanEma * 0.9 + scanWatch.Elapsed.TotalMilliseconds * 0.1;
                if (DateTime.UtcNow >= _nextScanLog && _scans >= 50)
                {
                    // Para diagnosticar PCs lentos: cuánto tarda de media cada lectura de pantalla.
                    Log.Write($"Lectura de pantalla: {_scanMs / _scans:0} ms de media ({_scans} lecturas, {reader.Reused - _reusedAtLog} sin OCR porque no cambió nada)");
                    _reusedAtLog = reader.Reused;
                    _scanMs = 0; _scans = 0; _nextScanLog = DateTime.UtcNow.AddMinutes(3);
                }
                var hits = _matcher.Find(lines).Where(h => OnCardColumn(h.Bounds)).ToList();
                // Se leyeron 1 o 2 cartas (suelen ser 3): leemos otra vez solo la fila de los nombres, ampliada.
                if (hits.Count is 1 or 2 && _cards.Count < 3)
                {
                    var row = hits[0].Bounds;
                    var more = _matcher.Find(await reader.ReadNameRowAsync(screen, row.Y + row.Height / 2, row.Height));
                    foreach (var h in more.Where(h => OnCardColumn(h.Bounds)))
                        if (!hits.Any(o => o.Augment.Id == h.Augment.Id || Math.Abs(Center(o.Bounds) - Center(h.Bounds)) < o.Bounds.Width))
                            hits.Add(h);
                    hits = hits.OrderBy(h => h.Bounds.X).ToList();
                }
                // Yunque de estadísticas o aumentos: nunca salen a la vez, nos quedamos con el tipo mayoritario.
                bool anvil = hits.Count(h => h.Augment.IsAnvil) * 2 > hits.Count;
                hits = hits.Where(h => h.Augment.IsAnvil == anvil).ToList();

                var now = DateTime.UtcNow;
                DrainClicks();
                cardsChanged = UpdateCards(hits, game, advice, screen.Width, now);
                // Para diagnosticar cartas que tardan en salir: solo se reconoció una (hacen falta 2 para mostrarlas).
                // La misma palabra suelta en el mismo sitio (p. ej. "Danse Macabre" en el marcador) se apunta una vez por minuto.
                if (_cards.Count == 0 && hits.Count == 1 && (now >= _nextSingleHitLog || hits[0].Augment.Id != _lastSingleHitId))
                {
                    _nextSingleHitLog = now.AddSeconds(60);
                    _lastSingleHitId = hits[0].Augment.Id;
                    Log.Write($"Solo se reconoce 1 carta: {hits[0].Augment.Name} en ({hits[0].Bounds.X},{hits[0].Bounds.Y})");
                }

                // Las cartas solo se quitan cuando ya no están en pantalla Y elegiste una (clic encima).
                // Si no hiciste clic en ninguna, se quedan; solo se quitan solas tras mucho rato.
                if (_cards.Count == 1 && now - _cardsShownAt > LoneCardTimeout)
                {
                    Log.Write($"Carta suelta descartada (no salieron más cartas): {_cards[0].Hit.Augment.Name}");
                    _ignoredLone = (_cards[0].Hit.Augment.Id, now.AddSeconds(60));
                    _overlay.ClearCards();
                    _cards = [];
                    _trackClicks = false;
                    cardsChanged = true;
                }
                if (_cards.Count > 0)
                {
                    var limit = ClickedCard() is not null ? HideAfterPick : _clicks.Count > 0 ? HideAfterOtherClick : HideWithoutClick;
                    if (now - _cardsLastSeen > limit)
                    {
                        var picked = ClickedCard();
                        Log.Write($"Cartas quitadas ({(picked is not null ? $"clic en {picked.Hit.Augment.Name}" : _clicks.Count > 0 ? "clic fuera de las cartas" : "sin clic, tiempo agotado")})");
                        // Dónde estaban las cartas y los clics, para poder ajustarlo (también si hubo varios clics).
                        if (_clicks.Count > (picked is null ? 0 : 1))
                            Log.Write($"   cartas (nombre x,y): {string.Join(" | ", _cards.Select(c => $"{c.Hit.Augment.Name} {c.Hit.Bounds.X + c.Hit.Bounds.Width / 2},{c.Hit.Bounds.Y}"))}" +
                                      $" · juego {_gameArea.X},{_gameArea.Y} {_gameArea.Width}x{_gameArea.Height} · clics: {string.Join(" ", _clicks.TakeLast(6).Select(c => $"{c.X},{c.Y}"))}");
                        RegisterPick();
                        _overlay.ClearCards();
                        _cards = [];
                        _trackClicks = false;
                        cardsChanged = true;
                    }
                }

                // Si las cartas no han vuelto en unos segundos, la elección era de verdad.
                if (_pendingPick is { } p && _cards.Count == 0 && now - p.At > PickConfirmDelay) ConfirmPick();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una captura fallida (p. ej. al cambiar de ventana) no debe tumbar el programa. Se apunta una vez por tipo de error.
                if (ex.GetType().Name + ex.Message != _lastScanError) Log.Write($"Fallo al leer la pantalla: {ex.GetType().Name}: {ex.Message}");
                _lastScanError = ex.GetType().Name + ex.Message;
            }

            // El juego en modo ventana puede ponerse encima: lo comprobamos en cada vuelta (es muy barato).
            _overlay.KeepOnTop();
            if (cardsChanged || DateTime.UtcNow >= nextSlowTick)
            {
                nextSlowTick = DateTime.UtcNow.AddSeconds(1);
                if (advice is not null) PushLive("ingame", L($"En partida · {_myAugments.Count} aumento(s)", $"In game · {_myAugments.Count} augment(s)"), game: (game, advice));
            }
            await Delay(ScanDelay, ct);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
            // Pase lo que pase en una vuelta, el bucle sigue: si no, el overlay desaparecería hasta reiniciar.
            Log.Write($"Error en el bucle: {ex}");
            await Delay(1000, ct);
          }
        }
        lcu?.Dispose();
    }

    /// <summary>
    /// Actualiza las cartas con lo que leyó el OCR. Una lectura parcial (p. ej. una carta agrandada al pasar
    /// el ratón) no borra las demás; una carta nueva en el sitio de otra es un refresco de esa carta.
    /// Devuelve true si cambió lo que se muestra.
    /// </summary>
    AugmentHit? _singleHit; // la carta de la lectura anterior cuando solo se leyó una
    double _scanMs; int _scans; DateTime _nextScanLog; int _reusedAtLog;
    double _scanEma;

    /// <summary>
    /// Pausa entre lecturas de pantalla: 0,1 s si el PC lee rápido; en PCs lentos tanto como tarda una lectura
    /// (hasta 0,35 s), para no quitarle más de ~la mitad de un núcleo al juego. Con cartas en pantalla, lo mínimo.
    /// </summary>
    int ScanDelay => _cards.Count > 0 ? ScanIntervalMs : (int)Math.Clamp(_scanEma, ScanIntervalMs, 350);

    /// <summary>Altura de la fila de nombres (fracción del alto del juego) medida al ver 2+ cartas en esta partida.</summary>
    double? _nameRowRel;

    double RelY(Box b) => (b.Y - _gameArea.Y) / (double)Math.Max(1, _gameArea.Height);

    /// <summary>
    /// ¿Está justo a la altura de los nombres de las cartas? Si ya vimos cartas en esta partida, a la misma altura
    /// (±1,5 %); si no, en la franja en la que salen (37-44 % del alto). Así una palabra suelta en otro sitio de la
    /// pantalla (p. ej. "Goliat" en la 1.0.1) no se toma por una carta.
    /// </summary>
    bool InNameRow(Box b) =>
        _nameRowRel is { } row ? Math.Abs(RelY(b) - row) < 0.015 : RelY(b) is > 0.37 and < 0.44;

    /// <summary>
    /// Se queda con los nombres que pueden ser cartas de verdad: las del juego están en una misma fila y separadas (centros
    /// a ~21 % del ancho). Primero la fila con más nombres; luego, de dos nombres a menos del 12 % del ancho, se queda el
    /// que está más a la altura de esa fila.
    /// </summary>
    List<AugmentHit> PlausibleSubset(List<AugmentHit> hits)
    {
        int tol = (int)(hits.Max(h => h.Bounds.Height) * 1.5);
        var row = hits.Select(a => hits.Where(b => Math.Abs(b.Bounds.Y - a.Bounds.Y) <= tol).ToList())
                      .OrderByDescending(r => r.Count).ThenBy(r => _nameRowRel is { } nr ? Math.Abs(RelY(r[0].Bounds) - nr) : 0).First();
        double y = row.Select(h => (double)h.Bounds.Y).OrderBy(v => v).ElementAt(row.Count / 2);
        double minGap = Math.Max(1, _gameArea.Width) * 0.12;
        var kept = row.OrderBy(h => Center(h.Bounds)).ToList();
        for (int i = 1; i < kept.Count;)
        {
            if (Center(kept[i].Bounds) - Center(kept[i - 1].Bounds) >= minGap) { i++; continue; }
            kept.RemoveAt(Math.Abs(kept[i].Bounds.Y - y) > Math.Abs(kept[i - 1].Bounds.Y - y) ? i : i - 1);
        }
        return kept;
    }

    static bool SameRow(Box b, List<AugmentHit> row) =>
        row.Any(m => Math.Abs(m.Bounds.Y - b.Y) <= Math.Max(m.Bounds.Height, b.Height) * 1.5);

    bool UpdateCards(List<AugmentHit> hits, GameSnapshot game, Advice? advice, int screenWidth, DateTime now)
    {
        // Nombres que no pueden ser cartas: casi pegados o a distinta altura. Son el texto que otros overlays dibujan dentro
        // del juego (listas de aumentos): «Barrera Azul, Sonata, Locomotora» a 15 px unos de otros (Garen, 2026-10-28).
        if (hits.Count >= 2)
        {
            var kept = PlausibleSubset(hits);
            if (kept.Count < hits.Count)
            {
                var dropped = string.Join(", ", hits.Except(kept).Select(h => h.Augment.Name));
                if (dropped != _lastDropped) Log.Write($"Descartado (no son cartas): {string.Join(", ", hits.Except(kept).Select(h => $"{h.Augment.Name} {Center(h.Bounds)},{h.Bounds.Y}"))}");
                _lastDropped = dropped;
            }
            hits = kept;
            if (hits.Count == 0) return false;
        }
        var ids = hits.Select(h => h.Augment.Id).ToHashSet();

        // Todas (2+) las cartas cambiaron a la vez: no es un re-roll (cambia una sola), son cartas nuevas. Pasa al elegir
        // un aumento cuando el juego enseña enseguida otra elección en el mismo sitio (HEAVY / POWER / THRASH): antes se
        // tomaba por re-roll de las tres y el aumento elegido («Plan de Escape», Garen 2026-10-28) se perdía.
        bool allNew = _cards.Count >= 2 && hits.Count >= 2 && hits.All(h => _cards.All(c => c.Hit.Augment.Id != h.Augment.Id))
                      && hits.All(h => SameRow(h.Bounds, _cards.Select(c => c.Hit).ToList()));

        // Cartas nuevas (o de otro tipo: yunque ↔ aumentos).
        if (_cards.Count == 0 || allNew || (hits.Count >= 2 && hits[0].Augment.IsAnvil != _cards[0].Hit.Augment.IsAnvil))
        {
            // Elegiste un aumento y enseguida salieron otras cartas (yunque, o HEAVY/POWER/THRASH): las de antes no llegaron
            // a quitarse, así que la elección se apunta aquí (antes se perdía).
            if (_cards.Count > 0)
            {
                var picked = ClickedCard();
                Log.Write($"Cartas quitadas (salieron cartas {(hits.Count >= 2 && hits[0].Augment.IsAnvil != _cards[0].Hit.Augment.IsAnvil ? "de otro tipo" : "nuevas")}{(picked is not null ? $"; clic en {picked.Hit.Augment.Name}" : "")})");
                // Sin carta elegida: dónde estaban las cartas y los clics (así se perdió Sobrecarga con Singed).
                if (picked is null)
                    Log.Write($"   cartas (nombre x,y): {string.Join(" | ", _cards.Select(c => $"{c.Hit.Augment.Name} {c.Hit.Bounds.X + c.Hit.Bounds.Width / 2},{c.Hit.Bounds.Y}"))}" +
                              $" · juego {_gameArea.X},{_gameArea.Y} {_gameArea.Width}x{_gameArea.Height} · clics: {string.Join(" ", _clicks.TakeLast(6).Select(c => $"{c.X},{c.Y}"))}");
                RegisterPick();
            }
            if (hits.Count < 2)
            {
                // Una sola carta leída: se muestra si sale igual en dos lecturas seguidas y a la altura de los nombres
                // (antes hacían falta 2 y a veces tardaban varios segundos). Las demás se añaden en cuanto se lean.
                var one = hits.Count == 1 ? hits[0] : null;
                bool stable = one is not null && _singleHit is { } prev && prev.Augment.Id == one.Augment.Id
                              && Math.Abs(prev.Bounds.Y - one.Bounds.Y) < 25 && InNameRow(one.Bounds)
                              && !(one.Augment.Id == _ignoredLone.Id && now < _ignoredLone.Until);
                _singleHit = one;
                if (!stable) return false;
            }
            _singleHit = null;
            if (hits.Count >= 2) _nameRowRel = hits.Average(h => RelY(h.Bounds));
            // ¿Volvieron las mismas cartas poco después? Entonces no elegiste: fue un refresco o una animación.
            if (_pendingPick is { } pending)
            {
                if (pending.Shown.Overlaps(ids)) _pendingPick = null;
                else ConfirmPick();
            }
            _clicks.Clear();
            _clickQueue.Clear();
            _trackClicks = true;
            SetCards(hits, game, advice);
            _cardsLastSeen = _cardsShownAt = now;
            Log.Write($"Cartas en pantalla: {string.Join(", ", hits.Select(h => h.Augment.Name))}");
            return true;
        }

        if (hits.Count == 0 || hits[0].Augment.IsAnvil != _cards[0].Hit.Augment.IsAnvil) return false;

        int column = Math.Max(120, screenWidth / 12);
        var merged = _cards.Select(c => c.Hit).ToList();
        bool seen = false, moved = false, replaced = false;
        foreach (var h in hits)
        {
            int i = merged.FindIndex(m => m.Augment.Id == h.Augment.Id);
            if (i >= 0)
            {
                seen = true;
                // La carta se movió (animación de entrada): recolocamos la tarjeta.
                if (Math.Abs(merged[i].Bounds.X - h.Bounds.X) > 25 || Math.Abs(merged[i].Bounds.Y - h.Bounds.Y) > 25) { merged[i] = h; moved = true; }
                continue;
            }
            // Un nombre fuera de la fila de las cartas no es una carta nueva: al elegir, la carta se anima y se llegó a leer
            // «Locomotora» bastante más arriba (partida de Rammus, 2026-10-28). Se tomaba por un re-roll de la carta del
            // centro, se borraba el clic de elegir y el aumento («Impacto Blindado») se perdía.
            if (!SameRow(h.Bounds, merged)) continue;
            int col = merged.FindIndex(m => Math.Abs(Center(m.Bounds) - Center(h.Bounds)) < column);
            // Las cartas salen de 3 en 3: con 3 ya leídas, una nueva es el refresco de la más cercana aunque se leyera algo
            // desplazada (animación). Antes entraba como cuarta y el clic de elegir podía irse a la carta vieja: en la nube,
            // con 4 cartas la app apuntó mal 6 de 20 elecciones; con 3, 6 de 136.
            if (col < 0 && merged.Count >= 3)
                col = merged.Select((m, i) => (i, d: Math.Abs(Center(m.Bounds) - Center(h.Bounds)))).MinBy(x => x.d).i;
            if (col >= 0)
            {
                // Refresco de esa carta (re-roll): los clics en esa columna eran sobre la carta de antes o su botón.
                Log.Write($"Carta cambiada: {merged[col].Augment.Name} → {h.Augment.Name}");
                int x0 = Center(merged[col].Bounds);
                _clicks.RemoveAll(c => Math.Abs(c.X - x0) < column);
                merged[col] = h; seen = replaced = true;
            }
            else { merged.Add(h); seen = replaced = true; } // una que no se leyó al principio (con menos de 3)
        }

        if (seen) _cardsLastSeen = now;
        if (!moved && !replaced) return false;
        SetCards(merged.OrderBy(h => h.Bounds.X).ToList(), game, advice);
        return true;
    }

    static int Center(Box b) => b.X + b.Width / 2;

    /// <summary>
    /// ¿Está en una de las columnas de las cartas? El juego las pone centradas en su ventana y separadas un 34 % de su alto
    /// (790/1280/1770 con 2560x1440). En tu registro, las de verdad se desvían como mucho un 0,6 % del alto; lo que se leía
    /// como cartas sin serlo (otro overlay: «Vulnerabilidad + Destellante» una y otra vez con Sylas, 2026-11-02, apuntando
    /// aumentos que no tenías; «Barrera Azul, Sonata, Locomotora») entre un 4 y un 26 %.
    /// </summary>
    bool OnCardColumn(Box b)
    {
        double center = _gameArea.X + _gameArea.Width / 2.0, gap = CardSpacing * _gameArea.Height;
        double off = new[] { center - gap, center, center + gap }.Min(col => Math.Abs(Center(b) - col));
        return off <= ColumnTolerance * _gameArea.Height;
    }
    const double CardSpacing = 0.34, ColumnTolerance = 0.03;

    void SetCards(List<AugmentHit> hits, GameSnapshot game, Advice? advice)
    {
        if (hits[0].Augment.IsAnvil)
        {
            var ranking = Anvil.Rank(hits.Select(h => h.Augment), game.Champion, _stats,
                advice?.Enemies ?? EnemyProfile.Build(game.Enemies, _stats, scores: game.EnemyScores), _myAugments, game.MyItems, game.CritChance);
            _cards = hits.Select(h =>
            {
                var r = ranking.First(x => x.Fragment.Id == h.Augment.Id);
                int rank = ranking.IndexOf(r);
                var tier = rank == 0 ? "S" : r.Score >= 3 ? "A" : r.Score >= 1 ? "B" : r.Score >= 0 ? "C" : "D";
                // Los yunques no tienen % de victorias: la nota sale de su puntuación por reglas.
                double score = Math.Clamp(Math.Round(55 + r.Score * 8, 1), 5, rank == 0 ? 99 : 94);
                return new AugmentCard(h, new Recommendation(h.Augment.Id, 0, 0, 0, tier), [], r, Score: score, Grade: tier);
            }).ToList();
        }
        else
        {
            var mix = advice?.Enemies.Mix ?? DamageMix.Mixed;
            var advisor = new Advisor(_stats);
            _cards = hits.Select(h => (_cards.FirstOrDefault(c => c.Hit.Augment.Id == h.Augment.Id) is { } old
                ? old with { Hit = h }
                : new AugmentCard(h, _stats.Rate(game.Champion, h.Augment.Id, mix), advisor.ItemsForAugment(h.Augment, game.Champion, game.MyItems)))
                with { Synergy = advisor.SynergyWith(h.Augment, _myAugments, game.Champion, game.MyItems, game.CritChance, _lockedStyle) })
                .Select(c =>
                {
                    double score = ScaleFor(game.Champion, mix).Score(c.SortScore, AugmentScale.IsReliable(c.Rec));
                    return c with { Score = score, Grade = AugmentScale.Grade(score) };
                }).ToList();
        }
        _overlay.ShowCards(_cards, _gameArea.Height);
    }

    /// <summary>Escala de notas 0-100 del campeón (se calcula una vez por campeón y tipo de equipo enemigo).</summary>
    AugmentScale ScaleFor(string champion, DamageMix mix)
    {
        var key = $"{champion}|{mix}|{_stats.MatchCount}";
        if (_scale is null || _scaleKey != key) (_scale, _scaleKey) = (AugmentScale.For(champion, mix, _stats, _catalog), key);
        return _scale;
    }

    AugmentScale? _scale;
    string? _scaleKey;
    ElectronBridge? _bridge;
    /// <summary>Objetos con los que dijiste "me quedo con el item" en el overlay (solo esta partida).</summary>
    readonly HashSet<int> _keepItems = [];
    bool _keepChanged;
    /// <summary>La build que fijaste en el selector del panel ("on_hit", "crit"…) esta partida; null = la elige la app.</summary>
    string? _lockedStyle;
    volatile bool _forceAdvice;
    GamePhase _lastPhase = GamePhase.Early;
    string? _lastAdviceLog;
    GameJournal? _journal;
    string? _balloonGame; // la partida del último aviso («Pulsa aquí para ver el resumen»)
    GameReports Reports() => new(_data, _catalog, _stats);
    double _gameMinutes;
    string? _lastDropped;
    bool _wasDead;

    /// <summary>
    /// Botones del overlay en píxeles de pantalla. Al empezar la partida el juego a veces se queda el ratón y los clics
    /// no llegan al overlay (hasta cambiar el modo de ventana): por eso los clics se vigilan también desde aquí
    /// y se le reenvían al overlay (él descarta el repetido si también le llegó).
    /// </summary>
    volatile OverlayButton[] _overlayButtons = [];
    sealed record OverlayButton(System.Drawing.Rectangle Rect, string Act, string? Id);

    /// <summary>Lo que pide el overlay de Electron (sus botones). Llega en el hilo de la interfaz.</summary>
    void OnOverlayMessage(JsonElement msg)
    {
        if (msg.TryGetProperty("type", out var type) && type.GetString() == "buttons" && msg.TryGetProperty("rects", out var rects))
        {
            _overlayButtons = rects.EnumerateArray().Select(r => new OverlayButton(
                new System.Drawing.Rectangle(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32()),
                r.GetProperty("act").GetString() ?? "", r.TryGetProperty("id", out var bid) && bid.ValueKind == JsonValueKind.String ? bid.GetString() : null)).ToArray();
            // MAYHEM_DEBUG: el simulador de partida lee de aquí dónde pulsar.
            if (Environment.GetEnvironmentVariable("MAYHEM_DEBUG") == "1")
                Log.Write("DEBUG botones: " + string.Join(" ", _overlayButtons.Select(b => $"{b.Act}:{b.Id}@{b.Rect.X},{b.Rect.Y},{b.Rect.Width},{b.Rect.Height}")));
            return;
        }
        if (msg.TryGetProperty("type", out var ct2) && ct2.GetString() is "benchswap" or "trade" or "borderless")
        {
            OnChampSelectButton(ct2.GetString()!, msg);
            return;
        }
        if (msg.TryGetProperty("type", out var t) && t.GetString() == "keep" && msg.TryGetProperty("itemId", out var id) && id.TryGetInt32(out var itemId))
        {
            if (!_keepItems.Add(itemId)) return; // el mismo botón pulsado otra vez
            _keepChanged = true;
            Log.Write($"Me quedo con el objeto: {_data.Item(itemId)?.Name ?? itemId.ToString()}");
        }
        // Selector de build del panel: fija esa build, o la suelta si ya estaba fijada (vuelve a elegir la app).
        if (msg.TryGetProperty("type", out var st) && st.GetString() == "style" && msg.TryGetProperty("key", out var sk) && sk.GetString() is { Length: > 0 } key)
        {
            _lockedStyle = _lockedStyle == key ? null : key;
            _keepChanged = true; // recalcula ya, sin esperar los 2 s
            Log.Write(_lockedStyle is null ? "Build: la elige la app" : $"Build fijada: {key}");
        }
    }

    void ShowStatus(string text)
    {
        _overlay.ShowStatus(text);
        _bridge?.Send(new { type = "toast", text });
    }

    void EndGame(CancellationToken ct)
    {
        Log.Write($"Partida terminada · aumentos que vio la app: {(_myAugments.Count > 0 ? string.Join(", ", _myAugments.Select(a => a.Name)) : "ninguno")}");
        ConfirmPick(); // una elección aún sin confirmar (6 s) también cuenta
        // Para «Adjuntar mi última partida» en la ventanita de sugerencias (tu historial tarda unos segundos en tenerla).
        if (_journal is { } seen)
            _lastGameSeen = new FeedbackGame(seen.Champion, null, _myAugments.Select(a => a.Id).ToList(), _lastMyItems, _gameMinutes, seen.FinalBuild.ToList(), seen.StartedUtc);
        double minutes = _gameMinutes;
        if (_journal is { } journal) { journal.Minutes = Math.Round(_gameMinutes, 1); journal.Save(); }
        _journal = null;
        _lastAdviceLog = null;
        _lastPhase = GamePhase.Early;
        _wasDead = false;
        _overlay.ClearCards();
        _overlay.HideSidePanel();
        _cards = [];
        _trackClicks = false;
        // La partida acabó: en cuanto aparezca en tu historial, la guardamos.
        _ = Dispatcher.InvokeAsync(() => SyncAfterGameAsync(DateTime.UtcNow, ct));
        _inGame = false;
        // «¿Qué te parece la app?», una vez al día como mucho y hasta que opines (App.Feedback.cs). En el hilo de la ventana.
        _ = Dispatcher.InvokeAsync(() => AskFeedbackAfterGameAsync(minutes, ct));
    }

    /// <summary>
    /// El cliente pone la partida en tu historial unos segundos después de acabar (con la pantalla de resultados).
    /// Se mira cada pocos segundos hasta que aparece (antes se esperaban 90 s fijos y tardaba en salir en la app),
    /// como mucho unos 4 minutos.
    /// </summary>
    async Task SyncAfterGameAsync(DateTime endedUtc, CancellationToken ct)
    {
        int[] waits = [3, 3, 4, 4, 5, 5, 5, 8, 8, 10, 10, 15, 15, 20, 30, 30, 30];
        foreach (var seconds in waits)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); } catch (TaskCanceledException) { return; }
            if (await SyncHistoryAsync(quiet: true) is > 0)
            {
                Log.Write($"Partida guardada {(DateTime.UtcNow - endedUtc).TotalSeconds:0} s después de acabar");
                Cloud.QueueNewMatches();
                await Cloud.FlushAsync(ct);
                return;
            }
        }
        Log.Write("La partida no apareció en el historial del cliente (se guardará al abrir la app o con «Actualizar»)");
    }

    // ───────────────────────── Detección del aumento elegido ─────────────────────────

    /// <summary>
    /// Hilo aparte: mientras hay cartas en pantalla, mira el botón izquierdo cada 5 ms y apunta dónde haces clic
    /// (solo la posición, nada más). Fuera de las cartas solo comprueba si hace falta, cada 50 ms.
    /// </summary>
    void ClickWatcher()
    {
        bool wasDown = false;
        // Arrastrar el panel por su cabecera ("drag"): el juego se queda el ratón, así que el movimiento también se ve
        // desde aquí y se le pasa al overlay en píxeles de pantalla desde donde se pulsó. Doble clic: vuelve a su sitio.
        // La esquina de abajo a la derecha ("resize") funciona igual, pero cambia el tamaño del panel.
        POINT? dragFrom = null;
        int lastDx = 0, lastDy = 0;
        long lastDragDown = 0;
        string dragKind = "drag", lastDragKind = "";
        while (true)
        {
            var buttons = _overlayButtons;
            if (!_trackClicks && buttons.Length == 0) { wasDown = false; dragFrom = null; Thread.Sleep(50); continue; }
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !wasDown && GetCursorPos(out var p))
            {
                // ¿Un botón del panel del overlay? Se le avisa (por si el juego se quedó el clic).
                if (buttons.FirstOrDefault(b => b.Rect.Contains(p.X, p.Y)) is { } hit)
                {
                    if (hit.Act is "drag" or "resize")
                    {
                        long now = Environment.TickCount64;
                        if (now - lastDragDown < 400 && hit.Act == lastDragKind) { _bridge?.Send(new { type = hit.Act + "reset" }); lastDragDown = 0; }
                        else
                        {
                            dragFrom = p; dragKind = lastDragKind = hit.Act; lastDx = lastDy = 0; lastDragDown = now;
                            _bridge?.Send(new { type = dragKind + "start" });
                        }
                    }
                    else
                    {
                        _bridge?.Send(new { type = "click", act = hit.Act, id = hit.Id });
                        if (hit.Act != "panel") Log.Write($"Clic en el botón del panel: {hit.Act}");
                    }
                }
                else if (_trackClicks) _clickQueue.Enqueue(new Click(p.X, p.Y, DateTime.UtcNow));
            }
            if (dragFrom is { } from)
            {
                if (down && GetCursorPos(out var m) && (m.X - from.X != lastDx || m.Y - from.Y != lastDy))
                {
                    lastDx = m.X - from.X; lastDy = m.Y - from.Y;
                    _bridge?.Send(new { type = dragKind + "move", dx = lastDx, dy = lastDy });
                }
                if (!down)
                {
                    dragFrom = null;
                    _bridge?.Send(new { type = dragKind + "end" });
                    if (Math.Abs(lastDx) + Math.Abs(lastDy) > 3) Log.Write($"{(dragKind == "drag" ? "Panel movido" : "Panel redimensionado")} ({lastDx}, {lastDy})");
                }
            }
            wasDown = down;
            Thread.Sleep(dragFrom is null ? 5 : 15);
        }
    }

    /// <summary>
    /// Hilo aparte: la rueda del ratón encima del panel lo desplaza. El juego se queda la rueda (y la usaría para el
    /// zoom de la cámara), así que la vemos desde aquí con un gancho de ratón de Windows, se la pasamos al overlay
    /// y el juego no la recibe. Fuera del panel la rueda sigue yendo al juego como siempre.
    /// </summary>
    void WheelWatcher()
    {
        _wheelHook = (code, wParam, lParam) =>
        {
            if (code >= 0 && wParam == WM_MOUSEWHEEL && _overlayButtons.LastOrDefault() is { Act: "panel" } panel)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (panel.Rect.Contains(info.Pt.X, info.Pt.Y))
                {
                    int delta = (short)(info.MouseData >> 16); // 120 por "clic" de rueda; positivo = hacia arriba
                    // Fuera del gancho: Windows lo quita si tarda (y el ratón iría a tirones), así que se envía aparte.
                    ThreadPool.QueueUserWorkItem(_ => _bridge?.Send(new { type = "wheel", delta }));
                    return 1;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        if (SetWindowsHookEx(WH_MOUSE_LL, _wheelHook, GetModuleHandle(null), 0) == IntPtr.Zero)
        {
            Log.Write($"No pude vigilar la rueda del ratón (error {Marshal.GetLastWin32Error()}): quedan los botones ▲▼");
            return;
        }
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // el gancho necesita que este hilo atienda mensajes
    }

    delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    LowLevelMouseProc? _wheelHook; // en un campo, igual que _foregroundHook
    const int WH_MOUSE_LL = 14;
    static readonly IntPtr WM_MOUSEWHEEL = 0x020A;
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public POINT Pt; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    /// <summary>Pasa los clics que vio el hilo de vigilancia a la lista que usa la detección (en el hilo principal).</summary>
    void DrainClicks()
    {
        while (_clickQueue.TryDequeue(out var click)) _clicks.Add(click);
    }

    /// <param name="Augment">La carta elegida; null si las cartas se fueron sin ver tu clic (se sabrá al acabar, con tu historial).</param>
    sealed record PendingPick(Augment? Augment, HashSet<int> Shown, DateTime At, List<JournalCard>? Offered = null);
    PendingPick? _pendingPick;
    static readonly TimeSpan PickConfirmDelay = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Al desaparecer las cartas, el último clic sobre una de ellas indica cuál elegiste.
    /// No se da por buena enseguida: si las cartas vuelven a salir, era un refresco (ver el bucle).
    /// </summary>
    void RegisterPick()
    {
        if (_cards.Any(c => c.Anvil is not null)) return; // los yunques no son aumentos
        if (_cards.Count < 2) return; // una carta que nunca tuvo compañeras no era una elección de verdad
        // Sin clic sobre una carta también se apunta, como «no vi cuál»: al acabar, tu historial dice cuál de esas cartas
        // tienes (GameJournal.WithRealPicks). Taric, 2026-11-02: las cartas se fueron sin ver ningún clic (seguramente se
        // acabó el tiempo y el juego eligió por ti) y en el resumen y la nube faltaba Impacto Blindado.
        _pendingPick = new PendingPick(ClickedCard()?.Hit.Augment, _cards.Select(c => c.Hit.Augment.Id).ToHashSet(), DateTime.UtcNow,
            _cards.Select(c => new JournalCard(c.Hit.Augment.Id, c.Score, c.Grade)).ToList());
    }

    /// <summary>La carta sobre la que hiciste el último clic (null si no tocaste ninguna).</summary>
    AugmentCard? ClickedCard()
    {
        if (_clicks.Count == 0 || _cards.Count == 0) return null;
        var screenH = _gameArea.Height; // alto de la ventana del juego: las cartas se escalan con ella
        var centers = _cards.Select(c => (Card: c, X: c.Hit.Bounds.X + c.Hit.Bounds.Width / 2, Y: c.Hit.Bounds.Y)).ToList();
        double halfGap = centers.Count > 1 ? (centers.Max(c => c.X) - centers.Min(c => c.X)) / (2.0 * (centers.Count - 1)) : screenH * 0.15;
        // Nunca más ancho que media carta (las cartas miden ~0,29 del alto del juego y van separadas ~0,34). Si no se leyó el
        // nombre de una carta y la elegías, el clic caía en la de al lado: con Archimaga y X leídas y Crueldad sin leer en
        // medio, elegir Crueldad apuntaba Archimaga (Wakka, 2026-10-31). Ahora ese clic no apunta nada.
        halfGap = Math.Min(halfGap, screenH * CardHalfWidth);

        // El PRIMER clic sobre una carta es la elección: en Mayhem un clic en una carta ya la elige. Los de después son de
        // volver a jugar (mucha gente mueve o ataca con el clic izquierdo mientras las cartas aún se van) y caían en la columna
        // de otra carta. Con el último clic, la nube (1.5.0-1.7.0) mostraba la carta de al lado en el 8-9 % de las elecciones;
        // y cortar por tiempo (1.7.0) dejaba sin apuntar el 21 %: el clic de elegir suele llegar después de la última
        // lectura que aún veía las cartas.
        foreach (var click in _clicks)
        {
            // Solo el cuerpo de la carta: desde el icono hasta la descripción. Los botones de abajo (refrescar) no cuentan.
            // Medido en tus partidas: un clic de elegir a +20 % bajo el nombre (Sombrero de Wooglet), refrescar a +30 % o más.
            var hit = centers.Where(c => Math.Abs(click.X - c.X) <= halfGap && click.Y > c.Y - screenH * 0.28 && click.Y < c.Y + screenH * 0.26)
                             .OrderBy(c => Math.Abs(click.X - c.X)).FirstOrDefault();
            if (hit.Card is not null) return hit.Card;
        }
        return null;
    }

    /// <summary>Media carta de ancho, en partes del alto de la ventana del juego (medido: 409 px entre cartas con 1200 de alto).</summary>
    const double CardHalfWidth = 0.17;

    void ConfirmPick()
    {
        if (_pendingPick is not { } p) return;
        _pendingPick = null;
        if (p.Augment is null)
        {
            Log.Write("Aumento elegido: no se vio cuál (se sabrá al acabar, con tu historial)");
            _journal?.Picks.Add(new JournalPick(Math.Round(_gameMinutes, 1), p.Offered ?? [], 0));
            return;
        }
        if (_myAugments.Any(a => a.Id == p.Augment.Id)) return;
        _myAugments.Add(p.Augment); // al cambiar la lista, las compras se recalculan en la siguiente vuelta
        Log.Write($"Aumento elegido: {p.Augment.Name}");
        _journal?.Picks.Add(new JournalPick(Math.Round(_gameMinutes, 1), p.Offered ?? [], p.Augment.Id));
        ShowStatus(L($"Aumento elegido: {p.Augment.DisplayName}", $"Augment picked: {p.Augment.DisplayName}"));
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    WinEventProc? _foregroundHook; // en un campo: si lo recogiera el recolector de basura, Windows llamaría a memoria liberada
    IntPtr _foregroundHookHandle;
    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    // ───────────────────────── Ventana principal ─────────────────────────

    /// <param name="page">Pestaña que se abre (p. ej. "acerca"); null = la de siempre.</param>
    void OpenDashboard(string? page = null)
    {
        if (_dashboard is not null)
        {
            if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
            _dashboard.Activate();
            if (page is not null) _dashboard.Push(new { type = "goto", page });
            return;
        }
        _dashboard = new DashboardWindow(OnDashboardMessage, page);
        _dashboard.Closed += (_, _) => _dashboard = null; // se libera su memoria; el overlay sigue
        _dashboard.Show();
    }

    async Task<object?> OnDashboardMessage(JsonElement msg)
    {
        switch (msg.GetProperty("type").GetString())
        {
            case "settings":
                return SettingsMessage();
            case "home":
                return HomeMessage();
            case "champion":
                return ChampionMessage(msg.GetProperty("champion").GetString() ?? "");
            case "setLanguage":
                SetLanguage(msg.GetProperty("value").GetString() ?? "es");
                return null;
            case "setSetting":
                SetSetting(msg.GetProperty("key").GetString() ?? "", msg.GetProperty("value").GetBoolean());
                return null;
            case "games":
                return new { type = "games", data = Reports().List() };
            case "game":
                var gid = msg.GetProperty("id").GetString() ?? "";
                return new { type = "game", id = gid, data = Reports().Detail(gid) };
            case "capture":
                if (_dashboard is not null)
                {
                    var r = msg.GetProperty("rect");
                    var saved = await _dashboard.CaptureAsync(r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                        r.GetProperty("w").GetDouble(), r.GetProperty("h").GetDouble(), r.TryGetProperty("vw", out var vw) ? vw.GetDouble() : 0,
                        msg.GetProperty("name").GetString() ?? "partida",
                        msg.TryGetProperty("copy", out var cp) && cp.GetBoolean());
                    return new { type = "captured", path = saved };
                }
                return null;
            case "openFile":
                if (msg.GetProperty("path").GetString() is { Length: > 0 } fp && File.Exists(fp))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fp}\"");
                return null;
            case "openData":
                System.Diagnostics.Process.Start("explorer.exe", Paths.DataDir);
                return null;
            case "feedbackInfo": // pestaña «Sugerencias» (App.Feedback.cs)
                return FeedbackInfo("app");
            case "feedback":
                return await SubmitFeedbackAsync(msg);
            case "ready":
                _dashboard?.Push(SettingsMessage());
                _dashboard?.Push(HomeMessage());
                if (_lastLive is not null) _dashboard?.Push(_lastLive);
                _dashboard?.Push(UpdateMessage());
                return InitMessage();
            case "sync":
                await SyncHistoryAsync();
                ReloadStats();
                _dashboard?.Push(HomeMessage());
                return InitMessage();
            case "augments":
                var champ = msg.GetProperty("champion").GetString() ?? "";
                var advisor = new Advisor(_stats);
                var scale = AugmentScale.For(champ, DamageMix.Mixed, _stats, _catalog);
                var list = _stats.TopAugments(champ, 40)
                    .Select(r => (Rec: r, Aug: _catalog.Get(r.AugmentId)))
                    .Where(x => x.Aug is not null)
                    .Select((x, i) =>
                    {
                        double score = scale.Score(AugmentScale.Evidence(x.Rec), AugmentScale.IsReliable(x.Rec));
                        return AugmentDto(x.Aug!, x.Rec, advisor.ItemsForAugment(x.Aug!, champ, []), best: i == 0, // vienen de mejor a peor
                            score: score, grade: AugmentScale.Grade(score));
                    });
                return new { type = "augments", champion = champ, list };
            case "checkUpdate":
                await CheckUpdateAsync(manual: true);
                return null;
            case "installUpdate":
                await InstallUpdateAsync();
                return null;
            case "dataNotice":
                // El aviso de los datos anónimos: «Vale» (se queda activado) o «Desactivar».
                _settings.DataNoticeShown = Cloud.NoticeShown = true;
                if (msg.GetProperty("value").GetString() == "off") SetSetting("shareData", false);
                else { _settings.Save(); _dashboard?.Push(SettingsMessage()); _ = CloudAfterChangeAsync(); }
                Log.Write($"Aviso de datos anónimos: {(_settings.ShareData ? "activado" : "desactivado")}");
                return null;
            case "privacy":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/ItzStorDz/mayhem-advisor/blob/main/PRIVACY.md") { UseShellExecute = true });
                return null;
            case "releaseNotes":
                OpenReleasePage();
                return null;
            case "opgg":
                // Abre la página del campeón en OP.GG en tu navegador (no descargamos sus datos).
                if (_data.Champion(msg.GetProperty("champion").GetString() ?? "") is { } c)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://op.gg/es/lol/modes/aram-mayhem/{c.Alias.ToLowerInvariant()}/build") { UseShellExecute = true });
                return null;
            default:
                return null;
        }
    }

    object InitMessage()
    {
        ReloadMayhem();
        // Tus campeones y los de mayhemmeta.com, aunque no los hayas jugado nunca.
        var own = MyMatches.Load().GroupBy(m => m.Champion, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Games: g.Count(), WinRate: g.Count(m => m.Win) / (double)g.Count()), StringComparer.OrdinalIgnoreCase);
        var aliases = own.Keys.Concat(_mayhem.Champions.Select(m => m.Alias)).Distinct(StringComparer.OrdinalIgnoreCase);
        return new
        {
            type = "init",
            version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3), lang = Lang.Code,
            matchCount = _stats.OwnMatchCount,
            patch = _stats.Patch,
            mayhemPatch = _mayhem.IsEmpty ? null : _mayhem.Patch,
            mayhemDate = _mayhem.IsEmpty ? null : _mayhem.FetchedAt.ToLocalTime().ToString("d MMM"),
            champions = aliases
                .Select(a => (Info: _data.Champion(a), Own: own.GetValueOrDefault(a), Mayhem: _mayhem.For(a)))
                .Where(c => c.Info is not null)
                .Select(c => new
                {
                    alias = c.Info!.Alias, name = c.Info.DisplayName, icon = c.Info.Icon,
                    winRate = c.Own.Games > 0 ? c.Own.WinRate : (double?)null, games = c.Own.Games,
                    tier = c.Mayhem?.Tier, mayhemWinRate = c.Mayhem?.WinRate / 100, mayhemGames = c.Mayhem?.Games,
                }),
        };
    }

    static object AugmentDto(Augment a, Recommendation r, IReadOnlyList<ItemAdvice> goodItems, bool best, string? note = null,
                             AugmentSynergy? synergy = null, double? score = null, string? grade = null, Box? box = null, string? lean = null,
                             int? popularity = null) => new
    {
        id = a.Id, score, grade = grade ?? r.Tier, lean,
        popularity, popularityLabel = popularity is { } pop ? MayhemAugments.PopularityLabel(pop) : null,
        scoreLabel = score is { } sc ? AugmentScale.Label(sc) + (note is null && !AugmentScale.IsReliable(r) ? L(" · pocos datos", " · little data") : "") : null,
        box = box is { } bx ? new { x = bx.X, y = bx.Y, w = bx.Width, h = bx.Height } : null,
        // Sin % de victorias del aumento: Riot no deja enseñarlo en apps ni overlays («Products cannot display win rates for
        // Augments»). Van la nota 0-100, el rango, las partidas y la fiabilidad (1-3).
        name = a.DisplayName, icon = a.Icon, tier = r.Tier, games = r.Global ? r.EvidenceGames : r.Games, global = r.Global, average = r.FromAverage,
        confidence = note is null ? AugmentScale.Confidence(r) : (int?)null, desc = a.DisplayDescription,
        stats = a.StatList.Select(Anvil.Label), best, note,
        synergy = synergy is null ? null : new { good = synergy.Bonus >= 0, reason = synergy.Reason },
        goodItems = goodItems.Select(i => new { name = i.Item.DisplayName, icon = i.Item.Icon }),
    };

    /// <summary>
    /// Con cartas de aumento en pantalla: los 5 aumentos de esa rareza con mejor nota para tu campeón (datos de todos
    /// los jugadores, mínimo 50 partidas) y si alguno está entre tus cartas. Null sin cartas o con yunques.
    /// </summary>
    object? BestAugments(string champion)
    {
        if (_cards.Count == 0 || _cards[0].Hit.Augment.IsAnvil) return null;
        var rarity = _cards[0].Hit.Augment.Rarity;
        var alias = _data.Champion(champion)?.Alias ?? champion;
        var onScreen = _cards.Select(c => c.Hit.Augment.Id).ToHashSet();
        var scale = AugmentScale.FromTable(alias, _augmentTable, _catalog);
        var list = _catalog.Augments
            .Where(a => a.Rarity == rarity && !a.IsAnvil)
            .Select(a => (Aug: a, Row: _augmentTable.For(alias, a.Id)))
            .Where(x => x.Row is { Games: >= 50 })
            .OrderByDescending(x => x.Row!.Delta).Take(5)
            .Select(x =>
            {
                double score = scale.Score(x.Row!.Delta);
                return new { name = x.Aug.DisplayName, icon = x.Aug.Icon, score, grade = AugmentScale.Grade(score), games = x.Row.Games,
                             confidence = AugmentScale.Confidence(x.Row.Games), onScreen = onScreen.Contains(x.Aug.Id) };
            })
            .ToList();
        if (list.Count == 0) return null;
        string label = rarity switch
        {
            "kPrismatic" => L("PRISMÁTICOS", "PRISMATIC"), "kGold" => L("DE ORO", "GOLD"), "kSilver" => L("DE PLATA", "SILVER"), _ => "",
        };
        return new { rarity = label, champion = _data.ChampionName(champion), list };
    }

    /// <summary>Lo elegido que es el aumento con tu campeón frente a los de su rareza (0-4, ver <see cref="MayhemAugments.Popularity"/>).</summary>
    int? Popularity(string champion, Augment augment)
    {
        var alias = _data.Champion(champion)?.Alias ?? champion;
        return _augmentTable.Popularity(alias, augment.Id,
            _catalog.Augments.Where(a => a.Rarity == augment.Rarity && !a.IsAnvil).Select(a => a.Id));
    }

    static object ItemDto(ItemAdvice i) => new { id = i.Item.Id, name = i.Item.DisplayName, icon = i.Item.Icon, reason = i.Reason, inProgress = i.InProgress, badges = ItemBadges.For(i.Item) };

    /// <summary>
    /// Las opciones de "siguiente compra" por orden, para el botón "Siguiente sugerencia" del overlay:
    /// la recomendada, luego botas (si faltan) y el resto de objetos recomendados.
    /// </summary>
    IEnumerable<object> NextOptions(GameSnapshot game, Advice advice)
    {
        var all = advice.Items.Concat(advice.Boots is { } b ? [b] : []).ToList();
        var order = new List<ItemInfo>();
        if (advice.Next is { } n) order.Add(n.Item);
        if (advice.Boots is { } boots && !order.Contains(boots.Item)) order.Add(boots.Item);
        order.AddRange(advice.Items.Select(i => i.Item).Where(i => !order.Contains(i)));
        foreach (var item in order)
        {
            int cost = _data.RemainingCost(item, game.MyItems);
            // Sin oro para el objeto entero: la pieza más cara de su receta que ya puedes comprar.
            var part = cost > game.Gold ? _data.AffordablePart(item, game.MyItems, (int)game.Gold) : null;
            yield return new
            {
                id = item.Id, name = item.DisplayName, icon = item.Icon, badges = ItemBadges.For(item),
                reason = all.FirstOrDefault(a => a.Item.Id == item.Id)?.Reason,
                missing = Math.Max(0, cost - (int)game.Gold),
                part = part is null ? null : new { name = part.DisplayName, icon = part.Icon, cost = _data.RemainingCost(part, game.MyItems) },
                progress = Math.Min(1, game.Gold / Math.Max(1, cost)),
            };
        }
    }

    /// <summary>
    /// Con 2+ aumentos de ataques u on-hit y un campeón que también se juega on-hit (sale en el selector de build sin estar
    /// activa), un aviso en el panel: «puedes fijar on-hit». No cambia la build sola: cambiar antes de estilo acierta menos
    /// lo que compra la gente (pruebas/evaluar-objetos, 2026-11-06: 45,2 → 45,0 %, on-hit 43,0 → 42,5 %). Lo pidió un jugador
    /// con Yunara («con aumentos on-hit la build debería adaptarse más»). Null si no toca.
    /// </summary>
    object? OnHitHint(Advice advice)
    {
        if (advice.Styles?.FirstOrDefault(s => s.Key == "on_hit") is not { Active: false }) return null;
        var augs = _myAugments.Where(a => !a.IsAnvil && a.StatList.Contains("OnHit")).ToList();
        return augs.Count < 2 ? null : new { names = augs.Take(3).Select(a => a.DisplayName).ToList() };
    }

    void PushLive(string phase, string status, IReadOnlyList<ChampionOption>? champSelect = null, (GameSnapshot Game, Advice Advice)? game = null)
    {
        object? gameDto = null;
        if (game is { } g)
        {
            var bestId = _cards.Count > 0 ? _cards.MaxBy(c => c.Score)!.Rec.AugmentId : -1;
            bool magic = new Advisor(_stats).PlaysMagic(g.Game.Champion, g.Game.MyItems, _myAugments);
            gameDto = new
            {
                championName = _data.ChampionName(g.Game.Champion),
                champion = _data.Champion(g.Game.Champion)?.Alias ?? g.Game.Champion,
                enemies = g.Advice.Enemies.Describe(),
                enemyFact = g.Advice.Enemies.Fact(),
                gamePhase = GamePhases.Label(g.Advice.Phase),
                dead = g.Game.IsDead,
                respawn = (int)Math.Ceiling(g.Game.RespawnTimer),
                next = g.Advice.Next is { } n ? new { id = n.Item.Id, name = n.Item.DisplayName, icon = n.Item.Icon, missing = n.GoldMissing, progress = Math.Min(1, g.Game.Gold / Math.Max(1, n.Cost)), badges = ItemBadges.For(n.Item) } : null,
                boots = g.Advice.Boots is { } b ? ItemDto(b) : null,
                swap = g.Advice.Swap is { } sw ? new
                {
                    sell = new { id = sw.Sell.Id, name = sw.Sell.DisplayName, icon = sw.Sell.Icon }, sellGold = sw.SellGold, sellReason = sw.SellReason,
                    buy = ItemDto(sw.Buy), missing = sw.GoldMissing,
                } : null,
                items = g.Advice.Items.Select(ItemDto),
                nextOptions = NextOptions(g.Game, g.Advice).ToList(),
                building = (g.Advice.Building ?? []).Select(ItemDto),
                kept = _keepItems.ToList(),
                bestAugments = BestAugments(g.Game.Champion),
                direction = g.Advice.Direction is { } dir ? new
                {
                    kind = dir.Kind.ToString(), label = dir.Label, reason = dir.Reason, warning = dir.Warning,
                    items = dir.Items.Select(i => new { name = i.Item.DisplayName, icon = i.Item.Icon, winRate = i.WinRate, games = i.Games }),
                } : null,
                plan = g.Advice.Plan is { } pl ? new
                {
                    magicShare = pl.MagicShare,
                    enemies = pl.Enemies.Select(e => new { name = e.Name, magic = e.MagicShare }),
                    recommended = (g.Advice.FullBuild ?? []).Select(b => new { name = b.Item.Item.DisplayName, icon = b.Item.Item.Icon, owned = b.Owned }),
                    situational = pl.Situational.Select(b => new { name = b.Item.Item.DisplayName, icon = b.Item.Item.Icon, owned = b.Owned,
                        changed = pl.Changed.Contains(b.Item.Item.Id), reason = pl.Changed.Contains(b.Item.Item.Id) ? b.Item.Reason : null }),
                    categories = pl.Categories.Select(c => new
                    {
                        title = c.Title, why = c.Why,
                        items = c.Items.Select(i => new { name = i.Item.DisplayName, icon = i.Item.Icon, winRate = i.WinRate, games = i.Games, augment = i.Augment, owned = i.Owned }),
                    }),
                    discards = pl.Discards.Select(d => new { name = d.Item.DisplayName, icon = d.Item.Icon, reason = d.Reason }),
                } : null,
                inventoryFull = g.Advice.InventoryFull,
                styles = (g.Advice.Styles ?? []).Select(s => new
                {
                    key = s.Key, name = s.Name, share = s.Share, winRate = s.WinRate / 100, active = s.Active, locked = s.Locked,
                    items = s.Items.Select(i => new { name = i.DisplayName, icon = i.Icon }),
                }),
                styleSource = g.Advice.StyleSource,
                fullBuild = (g.Advice.FullBuild ?? []).Select(b => new { item = ItemDto(b.Item), owned = b.Owned }),
                augments = _myAugments.Select(a => new { name = a.DisplayName, icon = a.Icon }),
                cards = _cards.Select(c => AugmentDto(c.Hit.Augment, c.Rec, c.GoodItems, c.Rec.AugmentId == bestId, c.Anvil?.Reason, c.Synergy,
                    c.Score, c.Grade, c.Hit.Bounds, c.Anvil is null ? BuildDirections.CardLabel(c.Hit.Augment, magic) : null,
                    c.Anvil is null ? Popularity(g.Game.Champion, c.Hit.Augment) : null)),
                skills = g.Advice.Skills is { } sk ? new { order = sk.MaxOrder, winRate = sk.WinRate, games = sk.Games, share = sk.Share } : null,
                onHitHint = OnHitHint(g.Advice),
            };
        }

        _lastLive = new
        {
            type = "live", phase, status, matchCount = _stats.MatchCount, lang = Lang.Code,
            champSelect = (champSelect ?? []).Select(o => new { championId = o.Champion.Id, alias = o.Champion.Alias, name = o.Champion.DisplayName, icon = o.Champion.Icon, winRate = o.WinRate, games = o.Games, isCurrent = o.IsCurrent,
                damage = _kits.For(o.Champion.Alias)?.DamageType, // "physical" / "magic" / "mixed": para ver qué le falta a tu equipo
                tier = o.Mayhem?.Tier, mayhemWinRate = o.Mayhem?.WinRate / 100, mayhemGames = o.Mayhem?.Games,
                // El orden de habilidades más jugado con ese campeón (aramkit), para el tuyo y los del banco.
                skills = new Advisor(_stats).CommonSkills(o.Champion.Alias) is { } csk ? new { order = csk.MaxOrder, share = csk.Share } : null,
                spells = o.Spells is { } sp
                    ? new { names = sp.Names, icons = sp.Icons, winRate = sp.WinRate, pickRate = sp.PickRate, games = sp.Games, reason = sp.Reason } : null }),
            champSelectInfo = phase == "champselect" ? ChampSelectInfo() : null,
            game = gameDto,
            // Para el overlay de Electron: zona del juego en píxeles de pantalla (las cartas vienen en las mismas unidades).
            area = new { x = _gameArea.X, y = _gameArea.Y, w = _gameArea.Width, h = _gameArea.Height },
            panel = _panelEnabled, autoAugmentsTab = _settings.AutoAugmentsTab,
        };
        _dashboard?.Push(_lastLive);
        if (_bridge is not null)
        {
            _bridge.Send(_lastLive);
            if (_bridge.GaveUp && _overlay.Suppressed)
            {
                Log.Write("Overlay Electron no funciona: vuelvo al overlay de siempre");
                _overlay.Suppressed = false;
            }
        }
    }

    // ───────────────────────── Datos ─────────────────────────

    /// <summary>Copia tus partidas de Mayhem nuevas desde el historial del cliente (si está abierto).</summary>
    /// <param name="quiet">Tras una partida se reintenta varias veces: los fallos del cliente no se apuntan cada vez.</param>
    /// <returns>Partidas nuevas guardadas, o null si el cliente no está abierto o falló.</returns>
    async Task<int?> SyncHistoryAsync(bool quiet = false)
    {
        try
        {
            var synced = await MatchSync.SyncOwnHistoryAsync(_data);
            if (synced is > 0 and var n)
            {
                // Aviso con la última partida: al pulsarlo se abre su resumen.
                if (MyMatches.Load().FirstOrDefault() is { } last)
                {
                    _balloonGame = last.Id;
                    _balloonUpdate = false;
                    _tray.ShowBalloonTip(5000, "Mayhem Advisor",
                        L($"{(last.Win ? "Victoria" : "Derrota")} con {_data.ChampionName(last.Champion)} ({last.Kills}/{last.Deaths}/{last.Assists}). Pulsa aquí para ver el resumen.",
                          $"{(last.Win ? "Victory" : "Defeat")} with {_data.ChampionName(last.Champion)} ({last.Kills}/{last.Deaths}/{last.Assists}). Click to see the summary."),
                        Forms.ToolTipIcon.Info);
                }
                _dashboard?.Push(new { type = "games", data = Reports().List() });
                ReloadStats();
                _dashboard?.Push(InitMessage());
            }
            // Tu historial (my-matches) se guarda aunque no haya partidas nuevas para las estadísticas: la pantalla de
            // inicio se refresca siempre (antes se quedaba vacía si la ventana ya estaba abierta).
            if (synced is not null) _dashboard?.Push(HomeMessage());
            return synced;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or IOException or System.Management.ManagementException)
        {
            // El cliente está arrancando o respondió algo raro; lo intentaremos tras la próxima partida.
            // (Antes, una respuesta rara al abrir la app la cerraba con «No pude cargar los datos del juego».)
            if (!quiet) Log.Write($"No pude copiar el historial: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    void ReloadStats()
    {
        // Tus partidas y las de la comunidad (Cloud.cs las baja cada 12 horas): se recarga si cambia cualquiera.
        DateTime Stamp(string p) => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
        var stamp = new[] { Stamp(Paths.Matches), Stamp(Cloud.CommunityFile) }.Max();
        if (stamp != _statsLoadedFrom || _stats.Data != _data)
        {
            _stats = StatsStore.Load(Paths.Matches, _data, Cloud.CommunityFile);
            _statsLoadedFrom = stamp;
        }
        ReloadMayhem();
        _stats.Global = _builds;
        _stats.Links = _links;
        _stats.AugmentTable = _augmentTable;
        _stats.Aramhex = _aramhex;
        _stats.Aramkit = _aramkit;
        _stats.AramkitBuilds = _aramkitBuilds;
        _stats.Kits = _kits;
    }

    /// <summary>
    /// Vuelve a leer los archivos de mayhemmeta.com si el script los cambió (tier list ~35 KB, builds ~900 KB).
    /// Si un archivo está pero no se puede leer (el script lo está escribiendo), se quedan los datos que
    /// teníamos y se reintenta la próxima vez.
    /// </summary>
    void ReloadMayhem()
    {
        if (ReloadIfChanged("mayhem-champions.json", ref _mayhemLoadedFrom, MayhemChampions.Load, c => c.IsEmpty) is { } champions)
        {
            _mayhem = champions;
            Log.Write(_mayhem.IsEmpty ? "Sin datos de mayhemmeta.com (ejecuta herramientas/actualizar-mayhem.mjs)"
                : $"Datos de mayhemmeta.com: {_mayhem.Champions.Count} campeones, parche {_mayhem.Patch}");
        }
        bool linksStale = false;
        if (ReloadIfChanged("mayhem-champion-builds.json", ref _buildsLoadedFrom, MayhemBuilds.Load, b => b.IsEmpty) is { } builds)
        {
            _builds = builds;
            _stats.Global = _builds;
            linksStale = true; // los nombres en inglés de los objetos salen de aquí
            Log.Write(_builds.IsEmpty ? "Sin builds de mayhemmeta.com (ejecuta herramientas/actualizar-mayhem.mjs sin --stats)"
                : $"Builds de mayhemmeta.com: {_builds.Champions.Count} campeones, parche {_builds.Patch}");
        }
        // Descripciones de mayhemmeta.com para los aumentos que el juego deja sin texto: así se sabe qué objetos piden.
        if (ReloadIfChanged("mayhem-augments.json", ref _augmentTextsLoadedFrom, MayhemAugments.Load, d => d.IsEmpty) is { } augments)
        {
            _augmentTable = augments;
            _stats.AugmentTable = augments;
            _catalog = _catalog.WithExtraDescriptions(augments.Descriptions);
            _matcher = new AugmentMatcher(_catalog);
            linksStale = true;
            Log.Write($"Aumentos de mayhemmeta.com: {augments.Descriptions.Count} descripciones, {augments.PairCount} combinaciones");
        }
        if (ReloadIfChanged("aramhex-augments.json", ref _aramhexLoadedFrom, p => ExternalAugments.Load(p!), a => a.IsEmpty) is { } aramhex)
        {
            _aramhex = aramhex;
            _stats.Aramhex = aramhex;
            Log.Write(aramhex.IsEmpty ? "Sin datos de AramHex (ejecuta herramientas/actualizar-aramhex.mjs)"
                : $"Datos de AramHex (servidor chino): {aramhex.ChampionCount} campeones, {aramhex.RowCount} filas, parche {aramhex.Patch}");
        }
        if (ReloadIfChanged("aramkit-augments.json", ref _aramkitLoadedFrom, p => ExternalAugments.Load(p!), a => a.IsEmpty) is { } aramkit)
        {
            _aramkit = aramkit;
            _stats.Aramkit = aramkit;
            Log.Write(aramkit.IsEmpty ? "Sin datos de aramkit.com (ejecuta herramientas/actualizar-aramkit.mjs)"
                : $"Datos de aramkit.com: {aramkit.ChampionCount} campeones, {aramkit.RowCount} filas, parche {aramkit.Patch}");
        }
        if (ReloadIfChanged("aramkit-builds.json", ref _aramkitBuildsLoadedFrom, p => AugmentBuilds.Load(p!), b => b.IsEmpty) is { } aramkitBuilds)
        {
            _aramkitBuilds = aramkitBuilds;
            _stats.AramkitBuilds = aramkitBuilds;
            Log.Write(aramkitBuilds.IsEmpty ? "Sin builds por aumento de aramkit.com (ejecuta herramientas/actualizar-aramkit.mjs)"
                : $"Builds por aumento de aramkit.com: {aramkitBuilds.ChampionCount} campeones, {aramkitBuilds.RowCount} campeón+aumento, parche {aramkitBuilds.Patch}");
        }
        if (ReloadIfChanged("campeones-kit.json", ref _kitsLoadedFrom, ChampionKits.Load, k => k.IsEmpty) is { } kits)
        {
            _kits = kits;
            _stats.Kits = kits;
            Log.Write(kits.IsEmpty ? "Sin kits de campeones (ejecuta herramientas/actualizar-kits.mjs)" : $"Kits de campeones: {kits.Count}, versión {kits.Version}");
        }
        if (linksStale || _links.IsEmpty)
        {
            _links = new AugmentItemLinks(_catalog.Augments, _data, _builds.ItemNames);
            _stats.Links = _links;
        }
    }

    /// <summary>
    /// Lo vuelve a cargar si cambió la fecha del archivo; null si no hay nada nuevo que usar.
    /// El archivo puede estar en AppData o en la carpeta "datos" junto al .exe (ver <see cref="DataFiles"/>).
    /// </summary>
    static T? ReloadIfChanged<T>(string file, ref DateTime loadedFrom, Func<string?, T> load, Func<T, bool> isEmpty) where T : class
    {
        var path = DataFiles.Resolve(file);
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (stamp == loadedFrom) return null;
        var loaded = load(path);
        if (isEmpty(loaded) && stamp != DateTime.MinValue) return null; // a medio escribir: lo reintentamos luego
        loadedFrom = stamp;
        return loaded;
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.ForTray(),
            Text = "Mayhem Advisor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenDashboard();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonUpdate) OpenDashboard("actualizar");
            else if (_balloonGame is { } id) OpenDashboard("partida:" + id);
        };

        menu.Opening += (_, _) => SyncTray();
        BuildTrayMenu();
    }

    /// <summary>Opciones del menú de la bandeja (las mismas que en Ajustes), en el idioma elegido.</summary>
    void BuildTrayMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(new Forms.ToolStripLabel("Mayhem Advisor") { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_update is { } update && _updateState is "available" or "ingame" or "error")
            menu.Items.Add(new Forms.ToolStripMenuItem(L($"Instalar la versión {update.Version.ToString(3)}", $"Install version {update.Version.ToString(3)}"),
                null, (_, _) => OpenDashboard("actualizar")) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(L("Abrir Mayhem Advisor", "Open Mayhem Advisor"), null, (_, _) => OpenDashboard());
        menu.Items.Add(L("Ajustes", "Settings"), null, (_, _) => OpenDashboard("ajustes"));
        menu.Items.Add(L("Sugerencias y fallos", "Suggestions & bugs"), null, (_, _) => OpenDashboard("sugerencias"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayPanel = new Forms.ToolStripMenuItem(L("Mostrar panel en la partida", "Show in-game panel")) { CheckOnClick = true };
        _trayPanel.Click += (_, _) => SetSetting("showPanel", _trayPanel.Checked);
        _trayAccept = new Forms.ToolStripMenuItem(L("Aceptar partida automáticamente", "Auto-accept matches")) { CheckOnClick = true };
        _trayAccept.Click += (_, _) => SetSetting("autoAccept", _trayAccept.Checked);
        _trayStartup = new Forms.ToolStripMenuItem(L("Iniciar con Windows", "Start with Windows")) { CheckOnClick = true };
        _trayStartup.Click += (_, _) => SetSetting("startup", _trayStartup.Checked);
        var language = new Forms.ToolStripMenuItem(L("Idioma: Español", "Language: English"));
        language.DropDownItems.Add("Español", null, (_, _) => SetLanguage("es"));
        language.DropDownItems.Add("English", null, (_, _) => SetLanguage("en"));
        menu.Items.AddRange([_trayPanel, _trayAccept, _trayStartup, language]);
        SyncTray();
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L("Salir", "Quit"), null, (_, _) => Quit());
    }

    Forms.ToolStripMenuItem _trayPanel = null!, _trayAccept = null!, _trayStartup = null!;

    void SyncTray()
    {
        _trayPanel.Checked = _settings.ShowPanel;
        _trayAccept.Checked = _settings.AutoAccept;
        _trayStartup.Checked = WindowsStartup.Enabled;
    }

    /// <summary>Lo que la pestaña Ajustes muestra.</summary>
    object SettingsMessage() => new
    {
        type = "settings",
        showPanel = _settings.ShowPanel, autoAccept = _settings.AutoAccept, autoAcceptOnlyMayhem = _settings.AutoAcceptOnlyMayhem,
        autoAugmentsTab = _settings.AutoAugmentsTab, shopItemSet = _settings.ShopItemSet, startup = WindowsStartup.Enabled, language = Lang.Code,
        shareData = _settings.ShareData, dataNotice = !_settings.DataNoticeShown,
        // Opciones de administrador (carpeta de datos): ajuste "Admin" o la cuenta de LoL del creador.
        admin = _settings.Admin || string.Equals(MyMatches.LoadProfile()?.Name, "ITzStorDz", StringComparison.OrdinalIgnoreCase),
        matchCount = _stats.OwnMatchCount,
    };

    /// <summary>Cambia el idioma de todo (ventana principal, panel del juego y bandeja) y lo guarda.</summary>
    void SetLanguage(string code)
    {
        _settings.Language = code == "en" ? "en" : "es";
        Lang.English = _settings.Language == "en";
        _settings.Save();
        Log.Write($"Idioma: {_settings.Language}");
        BuildTrayMenu();
        _scale = null; // las etiquetas de las notas van en el idioma
        _forceAdvice = true; // recalcular los consejos con los textos en el idioma nuevo
        _dashboard?.Push(SettingsMessage());
        _dashboard?.Push(InitMessage());
        _dashboard?.Push(HomeMessage());
    }

    /// <summary>Cambia un ajuste (desde la bandeja o la pestaña Ajustes), lo guarda y lo aplica.</summary>
    void SetSetting(string key, bool value)
    {
        switch (key)
        {
            case "showPanel":
                _settings.ShowPanel = _panelEnabled = value;
                if (!value) _overlay.HideSidePanel();
                break;
            case "autoAccept": _settings.AutoAccept = value; break;
            case "autoAcceptOnlyMayhem": _settings.AutoAcceptOnlyMayhem = value; break;
            case "autoAugmentsTab": _settings.AutoAugmentsTab = value; break;
            case "shopItemSet":
                _settings.ShopItemSet = value;
                _itemSetFor = null;
                // Al apagarlo se borran nuestros item sets (si sabemos dónde está League).
                if (!value && _leagueDir is { } dir) ItemSets.DeleteAll(dir);
                break;
            case "startup": WindowsStartup.Enabled = value; break;
            case "shareData":
                _settings.ShareData = Cloud.Allowed = value;
                if (value) _ = CloudAfterChangeAsync();
                break;
            default: return;
        }
        _settings.Save();
        Log.Write($"Ajuste cambiado: {key} = {value}");
        _dashboard?.Push(SettingsMessage());
    }

    static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    void Quit()
    {
        _cts.Cancel();
        _bridge?.Dispose();
        if (_foregroundHookHandle != IntPtr.Zero) UnhookWinEvent(_foregroundHookHandle);
        _dashboard?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }
}
