// Simulador de partida para probar Mayhem Advisor sin jugar:
//  · ventana a pantalla completa con el título del juego (el overlay de Electron se pega a ella),
//  · servidor falso de la API del juego (MAYHEM_LIVE_URL) con los datos de una partida real,
//  · cartas de aumento dibujadas, clics en los botones del panel y capturas.
// Uso: FakeGame <carpeta de salida> <MayhemAdvisor.exe> [es|en]
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class P
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    static string FrontTitle() { var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString(); }

    /// <summary>Pone el "juego" delante (con el truco de la tecla Alt, que Windows exige para cambiar de ventana activa).</summary>
    static async Task Front()
    {
        if (GetForegroundWindow() == Game.Handle) return;
        keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero);
        SetForegroundWindow(Game.Handle);
        await Task.Delay(600);
        Note($"(ventana delante cambiada a: {FrontTitle()})");
    }

    static string Out = "", LogPath = "";
    static string LiveJson = "", Live = "";
    static volatile bool LiveOff; // la partida acabó: el servidor deja de responder
    /// <summary>Partida en el minuto <paramref name="seconds"/>; muerto o vivo (con 12 s para reaparecer).</summary>
    static void SetLive(double seconds, bool dead)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(LiveJson)!;
        root["gameData"]!["gameTime"] = seconds;
        var me = root["activePlayer"]!["riotId"]?.GetValue<string>();
        foreach (var pl in root["allPlayers"]!.AsArray())
            if (pl!["riotId"]?.GetValue<string>() == me) { pl["isDead"] = dead; pl["respawnTimer"] = dead ? 12.0 : 0.0; }
        Live = root.ToJsonString();
    }
    static readonly StringBuilder Report = new();
    static Form Game = null!;
    static string[]? Cards;
    static bool CardsEn; // nombres de las cartas en inglés (si no, en español como el juego del usuario) // nombres dibujados (null = sin cartas)
    static Image? Showcase; // FG_SHOWCASE: una captura del juego con cartas de verdad (para imágenes de promoción)
    /// <summary>Nombres sueltos en la fila de las cartas pero fuera de sus columnas (desplazados desde el centro, en altos de pantalla),
    /// como el texto de otro overlay que con Sylas (2026-11-02) se leía como «Vulnerabilidad + Destellante».</summary>
    static (string Text, float DxH)[]? Loose;
    /// <summary>Centro de la carta i (0-2): el juego las pone centradas y separadas un 34 % del alto de la pantalla.</summary>
    static int CardX(Rectangle b, int i) => (int)(b.Width / 2.0 + (i - 1) * 0.34 * b.Height);
    static string? Stray;   // un nombre suelto más arriba que la fila de las cartas (animación al elegir, como con Rammus)

    static void Note(string s) { var line = $"{DateTime.Now:HH:mm:ss.fff}  {s}"; Console.WriteLine(line); Report.AppendLine(line); }

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        Out = args[0]; Directory.CreateDirectory(Out);
        string exe = args[1], lang = args.Length > 2 ? args[2] : "es";
        string dataDir = Environment.GetEnvironmentVariable("MAYHEM_DATA_DIR") ?? throw new Exception("falta MAYHEM_DATA_DIR");
        CardsEn = args.Length > 3 && args[3] == "encards";
        LogPath = Path.Combine(dataDir, "overlay.log");
        Directory.CreateDirectory(dataDir);
        // Idioma de la prueba (lo lee la app al arrancar).
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), $$"""{ "Language": "{{lang}}", "ShowPanel": true, "AutoAugmentsTab": true, "AutoAccept": false, "ShopItemSet": false }""");
        File.WriteAllText(LogPath, "");

        // Servidor falso de la API del juego.
        LiveJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "live-sample.json")).TrimStart('﻿');
        {
            var root0 = System.Text.Json.Nodes.JsonNode.Parse(LiveJson)!;
            var me0 = root0["activePlayer"]!["riotId"]?.GetValue<string>();
            var myTeam0 = root0["allPlayers"]!.AsArray().First(pl => pl!["riotId"]?.GetValue<string>() == me0)!["team"]!.GetValue<string>();
            var foe = root0["allPlayers"]!.AsArray().First(pl => pl!["team"]!.GetValue<string>() != myTeam0)!;
            foe["scores"]!["kills"] = 10; foe["scores"]!["deaths"] = 2;
            LiveJson = root0.ToJsonString();
        }
        SetLive(400, false); // minuto 6:40 (inicio de partida), vivo
        var listener = new TcpListener(IPAddress.Loopback, 2998); listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var c = await listener.AcceptTcpClientAsync();
                if (LiveOff) continue; // conexión cerrada sin respuesta, como el juego cerrado
                var st = c.GetStream(); var buf = new byte[4096]; await st.ReadAsync(buf);
                var body = Encoding.UTF8.GetBytes(Live);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await st.WriteAsync(head); await st.WriteAsync(body);
            }
        });

        var bg = Image.FromFile(Path.Combine(AppContext.BaseDirectory, "fondo-partida.png"));
        var screen = Screen.PrimaryScreen!.Bounds;
        Game = new Form { Text = "League of Legends (TM) Client", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = screen, BackColor = Color.Black };
        typeof(Form).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(Game, true);
        Game.Paint += (_, e) =>
        {
            if (Showcase is { } sc)
            {
                // Que llene la pantalla sin deformarse (se recorta lo que sobre por los lados o arriba y abajo).
                float k = Math.Max(screen.Width / (float)sc.Width, screen.Height / (float)sc.Height);
                e.Graphics.DrawImage(sc, (screen.Width - sc.Width * k) / 2, (screen.Height - sc.Height * k) / 2, sc.Width * k, sc.Height * k);
                return;
            }
            e.Graphics.DrawImage(bg, 0, 0, screen.Width, screen.Height);
            if (Stray is not null)
            {
                using var f2 = new Font("Segoe UI", screen.Height * 0.019f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = e.Graphics.MeasureString(Stray, f2);
                e.Graphics.DrawString(Stray, f2, Brushes.White, screen.Width * 0.5f - sz.Width / 2, screen.Height * 0.24f);
            }
            if (Loose is not null)
            {
                using var f3 = new Font("Segoe UI", screen.Height * 0.019f, FontStyle.Bold, GraphicsUnit.Pixel);
                foreach (var (text, dx) in Loose)
                {
                    var sz = e.Graphics.MeasureString(text, f3);
                    e.Graphics.DrawString(text, f3, Brushes.White, screen.Width / 2f + dx * screen.Height - sz.Width / 2, screen.Height * 0.40f);
                }
            }
            if (Cards is null) return;
            // Cartas como las del juego: nombres a ~40 % del alto, centradas y separadas un 34 % del alto (790/1280/1770 en 2560x1440).
            using var font = new Font("Segoe UI", screen.Height * 0.019f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var card = new SolidBrush(Color.FromArgb(240, 22, 18, 40));
            using var border = new Pen(Color.FromArgb(200, 190, 150, 255), 4);
            for (int i = 0; i < Cards.Length; i++)
            {
                float cx = CardX(screen, i), w = screen.Height * 0.32f, top = screen.Height * 0.20f, h = screen.Height * 0.55f;
                e.Graphics.FillRectangle(card, cx - w / 2, top, w, h); e.Graphics.DrawRectangle(border, cx - w / 2, top, w, h);
                var size = e.Graphics.MeasureString(Cards[i], font);
                e.Graphics.DrawString(Cards[i], font, Brushes.White, cx - size.Width / 2, screen.Height * 0.40f);
            }
        };
        Game.Shown += async (_, _) => { try { await Run(exe, lang); } catch (Exception ex) { Note("ERROR del simulador: " + ex); } finally { File.WriteAllText(Path.Combine(Out, "informe.txt"), Report.ToString()); Application.Exit(); } };
        Application.Run(Game);
    }

    static string LogText() { try { using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(f).ReadToEnd(); } catch { return ""; } }

    static async Task<bool> WaitLog(string text, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (LogText().Contains(text)) return true; await Task.Delay(50); }
        return false;
    }

    /// <summary>Espera a que el texto salga en el registro más veces que <paramref name="before"/>.</summary>
    static async Task<bool> WaitCount(string text, int before, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (Count(text) > before) return true; await Task.Delay(50); }
        return false;
    }

    static void Shot(string name, bool full = false)
    {
        var b = Screen.PrimaryScreen!.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        using var small = full ? new Bitmap(bmp) : new Bitmap(bmp, b.Width / 2, b.Height / 2);
        small.Save(Path.Combine(Out, name + ".png"), ImageFormat.Png);
        // El overlay ya no sale en las capturas (a propósito): su propia imagen la guarda Electron en modo prueba.
        try { File.Copy(Path.Combine(Out, "overlay.png"), Path.Combine(Out, name + "-overlay.png"), true); } catch { }
        try { File.Copy(Path.Combine(Out, "overlay.txt"), Path.Combine(Out, name + "-overlay.txt"), true); } catch { }
        Note($"captura {name}.png");
    }

    /// <summary>Último rectángulo publicado para un botón (act, id) según el registro de depuración.</summary>
    static Rectangle? Button(string act, string? id = null)
    {
        var line = LogText().Split('\n').LastOrDefault(l => l.Contains("DEBUG botones:"));
        if (line is null) return null;
        foreach (var part in line[(line.IndexOf("DEBUG botones:") + 14)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.Split('@'); var key = at[0].Split(':');
            if (key[0] != act || (id is not null && key[1] != id)) continue;
            var n = at[1].Split(',').Select(int.Parse).ToArray();
            return new Rectangle(n[0], n[1], n[2], n[3]);
        }
        return null;
    }

    /// <summary>Las builds del selector del panel (ids de los botones "style"), en orden.</summary>
    static List<string> StyleIds()
    {
        var line = LogText().Split('\n').LastOrDefault(l => l.Contains("DEBUG botones:"));
        return line is null ? [] : line[(line.IndexOf("DEBUG botones:") + 14)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('@')[0].Split(':')).Where(k => k[0] == "style" && k.Length > 1).Select(k => k[1]).Distinct().ToList();
    }

    static async Task<bool> Click(string act, string? id = null)
    {
        await Front();
        if (Button(act, id) is not { } r) { Note($"FALLO: no encuentro el botón {act}:{id} (delante: {FrontTitle()})"); return false; }
        int x = r.X + r.Width / 2, y = r.Y + r.Height / 2;
        SetCursorPos(x, y); await Task.Delay(250); // el overlay detecta el ratón encima (cada 50 ms)
        mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
        Note($"clic en {act}:{id} ({x},{y})");
        await Task.Delay(700);
        return true;
    }

    static async Task Run(string exe, string lang)
    {
        await Front();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.Environment["MAYHEM_LIVE_URL"] = "http://127.0.0.1:2998/liveclientdata/";
        psi.Environment["MAYHEM_DEBUG"] = "1";
        psi.Environment["MAYHEM_DATA_DIR"] = Path.GetDirectoryName(LogPath)!;
        psi.Environment["MAYHEM_OVERLAY_SHOT"] = Out;
        var app = Process.Start(psi)!;
        Note($"app arrancada (pid {app.Id}), idioma {lang}");
        try
        {
            if (!await WaitLog("Partida de Mayhem detectada", 180_000)) { Note("FALLO: la app no detectó la partida en 3 min"); return; }
            Note("partida detectada");
            if (!await WaitLog("Overlay Electron conectado", 30_000)) Note("FALLO: Electron no se conectó");
            await Front();
            // La primera lista de botones llega vacía (el panel aún no se dibujó): se espera a la que trae botones.
            if (!await WaitLog(" minimize:@", 20_000)) // (desde el botón ⊟ «compacto», minimize ya no es el primero de la lista) { Note("FALLO: el panel no publicó sus botones"); Shot("00-sin-panel"); return; }
            await Task.Delay(1500);
            if (Environment.GetEnvironmentVariable("FG_SHOWCASE") is { Length: > 0 } showcase)
            {
                // Solo la imagen de promoción: la app lee las cartas de la captura y se guarda todo a tamaño completo.
                int seen0 = Count("Cartas en pantalla");
                Showcase = Image.FromFile(showcase); Game.Invalidate(); Game.Update();
                var tc = Stopwatch.StartNew();
                while (Count("Cartas en pantalla") == seen0 && tc.ElapsedMilliseconds < 15_000) await Task.Delay(50);
                Note(Count("Cartas en pantalla") > seen0 ? "cartas de la captura reconocidas" : "FALLO: no reconoció las cartas de la captura");
                await Task.Delay(2500);
                Shot($"{lang}-showcase", full: true);
                return;
            }
            Shot($"{lang}-1-compra");

            // Selector de build (1.7.0): fijar otra build y soltarla.
            var styleIds = StyleIds();
            if (styleIds.Count < 2) Note($"FALLO: el selector de build tiene {styleIds.Count} opciones");
            else
            {
                int locks = Count("Build fijada");
                await Click("style", styleIds[1]);
                if (!await WaitCount("Build fijada", locks, 5_000)) Note("FALLO: fijar la build no llegó a la app");
                else { await Task.Delay(2500); Note("build fijada: " + styleIds[1]); Shot($"{lang}-1b-build-fijada"); }
                int autos = Count("Build: la elige la app");
                await Click("style", styleIds[1]);
                Note(await WaitCount("Build: la elige la app", autos, 5_000) ? "build suelta: la vuelve a elegir la app" : "FALLO: soltar la build no llegó a la app");
                await Task.Delay(1500);
            }

            // Botones del panel.
            int before = Count("Clic en el botón del panel");
            await Click("next"); Shot($"{lang}-2-otra-opcion");
            await Click("tab", "adaptar"); Shot($"{lang}-3-adaptar");
            await Click("details"); Shot($"{lang}-4-adaptar-mas");
            await Click("scroll", "up"); await Task.Delay(500);
            // Rueda del ratón encima del panel: el programa la ve con su gancho y el panel se desplaza.
            if (Button("panel") is { } pr)
            {
                await Front();
                SetCursorPos(pr.X + pr.Width / 2, pr.Y + pr.Height / 2); await Task.Delay(400);
                var before0 = File.ReadAllBytes(Path.Combine(Out, "overlay.png"));
                for (int k = 0; k < 3; k++) { mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); await Task.Delay(120); }
                await Task.Delay(1200);
                var after0 = File.ReadAllBytes(Path.Combine(Out, "overlay.png"));
                Note(before0.SequenceEqual(after0) ? "FALLO: la rueda no movió el panel" : "rueda del ratón: el panel se desplazó");
                Shot($"{lang}-5-rueda");
            }
            await Click("scroll", "down"); Shot($"{lang}-5-scroll");
            await Click("tab", "compra");
            await Click("minimize"); Shot($"{lang}-6-minimizado");
            // Muerto: el panel se abre solo en «Compra» con el aviso, aunque esté minimizado.
            SetLive(400, true); await Task.Delay(3000); Shot($"{lang}-6b-muerto");
            Note(Count("muerto") > 0 ? "muerte vista en el registro" : "(la app no registra la muerte; mirar la captura)");
            SetLive(400, false); await Task.Delay(2500); Shot($"{lang}-6c-revive-minimizado");
            await Click("restore");
            // Mover el panel arrastrando la cabecera.
            await Task.Delay(800);
            if (Button("panel") is { } pp)
            {
                await Front();
                int sx = pp.X + 50, sy = pp.Y + 16;
                SetCursorPos(sx, sy); await Task.Delay(300);
                mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); await Task.Delay(150);
                for (int k = 1; k <= 20; k++) { SetCursorPos(sx - k * 25, sy + k * 12); await Task.Delay(30); }
                mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); await Task.Delay(1500);
                Shot($"{lang}-6d-arrastrado");
                var moved = Button("panel");
                Note(moved is { } m && Math.Abs(m.X - pp.X) > 200 ? $"panel arrastrado: de ({pp.X},{pp.Y}) a ({m.X},{m.Y})" : $"FALLO: el panel no se movió ({pp.X},{pp.Y}) → {moved}");
                // Doble clic en la cabecera: vuelve a su sitio.
                if (moved is { } mv)
                {
                    SetCursorPos(mv.X + 50, mv.Y + 16); await Task.Delay(300);
                    for (int k = 0; k < 2; k++) { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); await Task.Delay(60); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); await Task.Delay(90); }
                    await Task.Delay(1500);
                    var back = Button("panel");
                    Note(back is { } bk && Math.Abs(bk.X - pp.X) < 10 ? "doble clic: el panel volvió a su sitio" : $"FALLO: el doble clic no lo devolvió ({back})");
                    Shot($"{lang}-6e-devuelto");
                }
            }
            if (Button("resize") is { } gr && Button("panel") is { } p0)
            {
                await Front();
                int sx = gr.X + gr.Width / 2, sy = gr.Y + gr.Height / 2;
                SetCursorPos(sx, sy); await Task.Delay(300);
                mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); await Task.Delay(150);
                for (int k = 1; k <= 20; k++) { SetCursorPos(sx - k * 6, sy + k * 5); await Task.Delay(30); }
                mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); await Task.Delay(1500);
                var p1 = Button("panel");
                Note(p1 is { } q && q.Width > p0.Width + 60 && Math.Abs(q.Right - p0.Right) < 12
                    ? $"panel redimensionado: {p0.Width}x{p0.Height} → {q.Width}x{q.Height} (borde derecho quieto)" : $"FALLO: el panel no cambió de tamaño bien ({p0} → {p1})");
                Shot($"{lang}-6f-grande");
                if (Button("resize") is { } g2)
                {
                    SetCursorPos(g2.X + g2.Width / 2, g2.Y + g2.Height / 2); await Task.Delay(300);
                    for (int k = 0; k < 2; k++) { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); await Task.Delay(60); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); await Task.Delay(90); }
                    await Task.Delay(1500);
                    var p2 = Button("panel");
                    Note(p2 is { } r2 && Math.Abs(r2.Width - p0.Width) < 4 ? "doble clic en la esquina: tamaño normal" : $"FALLO: el doble clic no devolvió el tamaño ({p2})");
                }
            }
            else Note("FALLO: no encuentro la esquina para cambiar el tamaño");
            Note($"clics vistos por el programa (reenvío): {Count("Clic en el botón del panel") - before}");

            // Cartas: cuánto tarda en reconocerlas.
            var cpu0 = app.TotalProcessorTime; var wall = Stopwatch.StartNew();
            await Task.Delay(8000); // en partida sin cartas: consumo en reposo
            app.Refresh(); Note($"CPU de la app sin cartas: {(app.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount:0.0} % del total ({Environment.ProcessorCount} hilos)");

            Cards = CardsEn ? ["Blunt Force", "Goliath", "Pandora's Box"] : ["Fuerza Bruta", "Goliat", "Caja de Pandora"];
            int seen = Count("Cartas en pantalla");
            Game.Invalidate(); Game.Update();
            var t0 = Stopwatch.StartNew();
            while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            Note(Count("Cartas en pantalla") > seen ? $"CARTAS reconocidas en {t0.ElapsedMilliseconds} ms" : "FALLO: las cartas no se reconocieron en 10 s");
            await Task.Delay(1200); Shot($"{lang}-7-cartas");

            // Elegir la carta del medio y quitar las cartas: el panel debe volver a "Compra".
            var b = Screen.PrimaryScreen!.Bounds;
            SetCursorPos(b.Width / 2, (int)(b.Height * 0.55)); await Task.Delay(100);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            Cards = null; Game.Invalidate(); Game.Update();
            Note(await WaitLog("Cartas quitadas", 5000) ? "cartas quitadas tras elegir" : "FALLO: las cartas no se quitaron tras elegir");
            int after = Count("Cartas en pantalla");
            await Task.Delay(8000); Shot($"{lang}-8-despues-de-elegir");
            Note(Count("Cartas en pantalla") == after ? "sin cartas falsas después de elegir" : "FALLO: aparecieron cartas falsas después de elegir");
            // Elegir un aumento y que salga enseguida el yunque (sin pausa sin cartas): la elección no debe perderse.
            Cards = CardsEn ? ["Blunt Force", "Goliath", "Pandora's Box"] : ["Fuerza Bruta", "Goliat", "Caja de Pandora"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            await Task.Delay(800); await Front();
            SetCursorPos((int)(b.Width * 0.30), (int)(b.Height * 0.55)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(150);
            Cards = CardsEn ? ["Health Shard", "Armor Shard", "Tenacity Shard"] : ["Fragmento de vida", "Fragmento de armadura", "Fragmento de tenacidad"];
            Game.Invalidate(); Game.Update();
            Note(await WaitLog("salieron cartas de otro tipo", 8000) ? "yunque justo después del aumento: detectado" : "FALLO: no vi el cambio a yunque");
            Note(await WaitLog("Aumento elegido", 8000) ? "la elección del aumento se guardó" : "FALLO: la elección del aumento se perdió");
            Cards = null; Game.Invalidate(); Game.Update(); await Task.Delay(3000);

            // Una palabra suelta a la altura de los nombres (como "Vulnerabilidad" con Singed): se descarta y no se apunta.
            int picks = Count("Aumento elegido");
            Cards = CardsEn ? ["Vulnerability"] : ["Vulnerabilidad"]; Game.Invalidate(); Game.Update();
            await Task.Delay(1500); await Front();
            SetCursorPos(CardX(b, 0), (int)(b.Height * 0.55)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            Note(await WaitLog("Carta suelta descartada", 8000) ? "carta suelta descartada" : "FALLO: la carta suelta no se descartó");
            int lone = Count("Cartas en pantalla"); await Task.Delay(9000);
            Note(Count("Cartas en pantalla") == lone ? "la carta suelta no vuelve a salir" : "FALLO: la carta suelta volvió a salir");
            Cards = null; Game.Invalidate(); Game.Update(); await Task.Delay(8000);
            Note(Count("Aumento elegido") == picks ? "la carta suelta no se apuntó como aumento" : "FALLO: la carta suelta se apuntó como aumento");

            // Clic en la parte de abajo de la carta (+23 % del alto bajo el nombre, como con Sombrero de Wooglet): sí es elegir.
            Cards = CardsEn ? ["Blunt Force", "Goliath", "Pandora's Box"] : ["Fuerza Bruta", "Goliat", "Caja de Pandora"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            await Task.Delay(800); await Front();
            SetCursorPos(CardX(b, 2), (int)(b.Height * 0.63)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(150); Cards = null; Game.Invalidate(); Game.Update();
            Note(await WaitLog(CardsEn ? "clic en Pandora" : "clic en Caja de Pandora", 5000) ? "clic bajo en la carta: cuenta como elegir" : "FALLO: el clic bajo en la carta no contó");
            await Task.Delay(8000);

            // Partida de Rammus (2026-10-28): eliges la del centro, durante la animación se lee un nombre suelto más arriba
            // en esa columna, y luego haces clics abajo en el centro (subir habilidades). Antes se perdía la elección.
            Cards = CardsEn ? ["Shrink Ray", "Armored Impact", "Long Range"] : ["Rayo Encogedor", "Impacto Blindado", "De Largo Alcance"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            await Task.Delay(800); await Front();
            int picksBefore = Count("Aumento elegido");
            int goliat = Count(CardsEn ? "clic en Armored Impact" : "clic en Impacto Blindado");
            SetCursorPos((int)(b.Width * 0.5), (int)(b.Height * 0.45)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            Stray = CardsEn ? "Locomotive" : "Locomotora"; Game.Invalidate(); Game.Update(); await Task.Delay(700);
            Cards = null; Game.Invalidate(); Game.Update(); await Task.Delay(500);
            Stray = null; Game.Invalidate(); Game.Update();
            for (int k = 0; k < 3; k++)
            {
                SetCursorPos((int)(b.Width * (0.47 + k * 0.02)), (int)(b.Height * 0.89)); await Task.Delay(120);
                mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero); await Task.Delay(300);
            }
            var tg = Stopwatch.StartNew();
            while (Count(CardsEn ? "clic en Armored Impact" : "clic en Impacto Blindado") == goliat && tg.ElapsedMilliseconds < 8000) await Task.Delay(50);
            Note(Count(CardsEn ? "clic en Armored Impact" : "clic en Impacto Blindado") > goliat ? "Rammus: elegir con un nombre suelto en la animación cuenta" : "FALLO: Rammus: la elección se perdió");
            await Task.Delay(8000);
            Note(Count("Aumento elegido") > picksBefore ? "Rammus: el aumento se apuntó" : "FALLO: Rammus: el aumento no se apuntó");

            // Garen (2026-10-28): eliges «Plan de Escape» y al momento salen otras 3 cartas en el mismo sitio (HEAVY/POWER/THRASH).
            Cards = ["Alma Infernal", "Plan de Escape", "Fuego Zorruno"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            await Task.Delay(800); await Front();
            int escape = Count("clic en Plan de Escape");
            SetCursorPos((int)(b.Width * 0.5), (int)(b.Height * 0.45)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(120);
            Cards = ["HEAVY", "POWER", "THRASH"]; Game.Invalidate(); Game.Update();
            var tz = Stopwatch.StartNew(); while (Count("clic en Plan de Escape") == escape && tz.ElapsedMilliseconds < 6000) await Task.Delay(50);
            Note(Count("clic en Plan de Escape") > escape ? "Garen: la elección antes de HEAVY/POWER/THRASH cuenta" : "FALLO: Garen: se perdió «Plan de Escape»");
            await Task.Delay(1500);
            SetCursorPos(CardX(b, 0), (int)(b.Height * 0.45)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(150); Cards = null; Game.Invalidate(); Game.Update();
            await Task.Delay(9000);
            var logNow = LogText();
            Note(logNow.Contains("Aumento elegido: Plan de Escape") ? "Garen: «Plan de Escape» se apuntó" : "FALLO: Garen: «Plan de Escape» no se apuntó");

            // Wakka (2026-11-01): eliges la carta de la izquierda y enseguida haces clic en el centro para moverte (con las
            // cartas aún en pantalla). Cuenta el primer clic: antes se apuntaba la carta del centro.
            Cards = CardsEn ? ["Shrink Ray", "Armored Impact", "Long Range"] : ["Rayo Encogedor", "Impacto Blindado", "De Largo Alcance"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            await Task.Delay(800); await Front();
            string left = CardsEn ? "clic en Shrink Ray" : "clic en Rayo Encogedor", middle = CardsEn ? "clic en Armored Impact" : "clic en Impacto Blindado";
            int leftBefore = Count(left), middleBefore = Count(middle);
            SetCursorPos(CardX(b, 0), (int)(b.Height * 0.45)); await Task.Delay(150);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(150);
            SetCursorPos((int)(b.Width * 0.5), (int)(b.Height * 0.45)); await Task.Delay(60);
            mouse_event(0x02, 0, 0, 0, IntPtr.Zero); await Task.Delay(40); mouse_event(0x04, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(250); Cards = null; Game.Invalidate(); Game.Update();
            var tw = Stopwatch.StartNew();
            while (Count(left) == leftBefore && Count(middle) == middleBefore && tw.ElapsedMilliseconds < 8000) await Task.Delay(50);
            Note(Count(left) > leftBefore ? "Wakka: elegir y mover enseguida apunta la carta elegida"
                : Count(middle) > middleBefore ? "FALLO: Wakka: se apuntó la carta del clic de moverse" : "FALLO: Wakka: no se apuntó nada");
            await Task.Delay(3000);

            // Texto de otro overlay dentro del juego: 3 nombres casi pegados. No deben salir como cartas.
            int before2 = Count("Cartas en pantalla");
            Cards = null; Stray = "Barrera Azul   Sonata   Locomotora"; Game.Invalidate(); Game.Update(); await Task.Delay(3000);
            Stray = null; Game.Invalidate(); Game.Update();
            Note(Count("Cartas en pantalla") == before2 ? "texto de otro overlay: no se toma por cartas" : "FALLO: el texto de otro overlay salió como cartas");

            // Sylas (2026-11-02): dos nombres de aumento en la fila de las cartas pero fuera de sus columnas (otro overlay). Se
            // tomaban por cartas una y otra vez y se apuntaban aumentos que no tenías.
            int before3 = Count("Cartas en pantalla");
            Loose = CardsEn ? [("Vulnerability", -0.54f), ("Dashing", -0.17f)] : [("Vulnerabilidad", -0.54f), ("Destellante", -0.17f)];
            Game.Invalidate(); Game.Update(); await Task.Delay(4000);
            Loose = null; Game.Invalidate(); Game.Update(); await Task.Delay(500);
            Note(Count("Cartas en pantalla") == before3 ? "Sylas: nombres fuera de las columnas de las cartas: no se toman por cartas" : "FALLO: Sylas: nombres fuera de las columnas salieron como cartas");

            // Taric (2026-11-02): las cartas se van sin ningún clic (se acabó el tiempo y el juego eligió por ti). Se apunta
            // «no se vio cuál» para completarlo al acabar con tu historial; antes no quedaba nada.
            Cards = CardsEn ? ["Soul Eater", "Armored Impact", "Goliath"] : ["Devorador de Almas", "Impacto Blindado", "Goliat"];
            seen = Count("Cartas en pantalla"); Game.Invalidate(); Game.Update();
            t0.Restart(); while (Count("Cartas en pantalla") == seen && t0.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            int unseen = Count("no se vio cuál");
            await Task.Delay(1500); Cards = null; Game.Invalidate(); Game.Update();
            Note(await WaitCount("no se vio cuál", unseen, 25_000) ? "Taric: cartas sin clic: se apunta «no se vio cuál»" : "FALLO: Taric: las cartas sin clic no dejaron nada apuntado");

            Cards = null; Game.Invalidate(); Game.Update(); await Task.Delay(1000);
            // Fin de la partida: la app debe guardar la partida que falta en cuanto el cliente la tenga (antes, 90 s fijos).
            if (Environment.GetEnvironmentVariable("FG_REMOVE_ID") is { Length: > 0 } rid)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                foreach (var f in new[] { "my-matches.jsonl", "matches.jsonl" })
                    File.WriteAllLines(Path.Combine(dir, f), File.ReadAllLines(Path.Combine(dir, f)).Where(l => !l.Contains(rid)));
                File.Delete(Path.Combine(dir, "partidas", rid + ".json"));
                Note($"partida {rid} quitada de los datos de prueba");
            }
            LiveOff = true; Cards = null; Game.Invalidate(); Game.Update();
            Note(await WaitLog("Partida terminada", 30_000) ? "fin de partida detectado" : "FALLO: no vio el fin de la partida");
            var ts = Stopwatch.StartNew();
            Note(await WaitLog("Partida guardada", 120_000) ? $"partida guardada {ts.Elapsed.TotalSeconds:0} s después de detectar el fin" : "FALLO: la partida no se guardó en 2 min");
            // 1.8.2: «¿Qué te parece la app?» a los 15 s de acabar (el simulador empieza con unos ajustes sin opinión dada).
            Note(await WaitLog("Sugerencias: sale", 30_000) ? "ventanita «¿Qué te parece la app?» al acabar la partida" : "FALLO: no salió la ventanita de sugerencias al acabar");
            foreach (var l in LogText().Split('\n').Where(l => l.Contains("Cartas") || l.Contains("Aumento elegido") || l.Contains("Lectura de pantalla") || l.Contains("Partida") || l.Contains("historial") || l.Contains("Error") || l.Contains("Fallo")))
                Note("  log> " + l.Trim());
        }
        finally
        {
            try { app.Kill(entireProcessTree: true); } catch { }
            File.Copy(LogPath, Path.Combine(Out, $"overlay-{lang}.log"), true);
        }
    }

    static int Count(string text) => LogText().Split('\n').Count(l => l.Contains(text));
}
