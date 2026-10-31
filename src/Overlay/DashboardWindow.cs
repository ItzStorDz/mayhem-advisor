using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MayhemAdvisor.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Ventana principal: la interfaz es HTML/CSS (carpeta wwwroot) dibujada con WebView2,
/// el motor de Edge que ya trae Windows. Al cerrarla se libera toda su memoria; el overlay sigue funcionando.
/// </summary>
public sealed class DashboardWindow : Window
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 15, 20) };
    readonly Func<JsonElement, Task<object?>> _onMessage;
    readonly string? _startPage;
    bool _ready;

    /// <param name="onMessage">Responde a lo que pide la página (devuelve el mensaje de respuesta o null).</param>
    /// <param name="startPage">Pestaña con la que se abre (p. ej. "acerca"); null = la primera.</param>
    public DashboardWindow(Func<JsonElement, Task<object?>> onMessage, string? startPage = null)
    {
        _onMessage = onMessage;
        _startPage = startPage;
        Title = "Mayhem Advisor";
        Icon = AppIcon.ForWindow();
        Width = 1120;
        Height = 740;
        MinWidth = 860;
        MinHeight = 560;
        Background = new SolidColorBrush(Color.FromRgb(11, 15, 20));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _web;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => _web.Dispose();
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.DataDir, "webview2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(Lang.L("Falta el componente WebView2 de Microsoft Edge. Instálalo desde la web de Microsoft y vuelve a abrir.",
                "Microsoft Edge WebView2 is missing. Install it from Microsoft's website and open the app again."), "Mayhem Advisor");
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping("mayhem.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += async (_, e) =>
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready") _ready = true;
            if (await _onMessage(doc.RootElement) is { } reply) Push(reply);
        };
        core.Navigate("https://mayhem.local/index.html" + (_startPage is null ? "" : "#" + _startPage));
        // MAYHEM_TEST_CAPTURE: solo para las pruebas (pulsa «Copiar imagen» del resumen de partida al cargar).
        if (Environment.GetEnvironmentVariable("MAYHEM_TEST_CAPTURE") is not null)
            core.NavigationCompleted += async (_, _) =>
            {
                await Task.Delay(6000);
                var state = await core.ExecuteScriptAsync("(document.querySelector('#gamesBody')?.innerText ?? '').slice(0, 160)");
                Log.Write($"Prueba de captura: {state}");
                await core.ExecuteScriptAsync("document.querySelector('[data-capture=copy]')?.click()");
            };
    }

    // Barra de título oscura a juego con la página (si no, Windows la pinta con el color de acento, p. ej. rojo).
    // DWMWA_USE_IMMERSIVE_DARK_MODE = 20; en Windows 11 también el color del título y del borde (34, 35, 36).
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1, caption = 0x00140F0B /* #0b0f14 en formato 0x00BBGGRR */, border = 0x00304A5C, text = 0x00D8E4EC;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
    }

    /// <summary>
    /// Guarda como imagen un trozo de la página (la tarjeta de la partida): x, y, ancho y alto en píxeles CSS. Se guarda en
    /// Imágenes\Mayhem Advisor y, si <paramref name="copy"/>, también se copia al portapapeles para pegarlo en Discord o WhatsApp.
    /// Devuelve la ruta del archivo (o null si falló).
    /// </summary>
    /// <param name="viewWidth">Ancho de la página en píxeles CSS (window.innerWidth): con él sale la escala de la captura.</param>
    public async Task<string?> CaptureAsync(double x, double y, double w, double h, double viewWidth, string name, bool copy)
    {
        if (_web.CoreWebView2 is null) return null;
        try
        {
            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            using var full = new System.Drawing.Bitmap(ms);
            // La captura sale en píxeles reales: la escala de Windows (125 %, 150 %…) multiplica las medidas CSS.
            double scale = full.Width / Math.Max(1.0, viewWidth > 0 ? viewWidth : _web.ActualWidth);
            var rect = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(0, 0, full.Width, full.Height),
                new System.Drawing.Rectangle((int)Math.Round(x * scale), (int)Math.Round(y * scale), (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
            if (rect.Width < 10 || rect.Height < 10) return null;
            using var part = full.Clone(rect, full.PixelFormat);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mayhem Advisor");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            part.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            Log.Write($"Imagen de la partida guardada: {file} ({rect.Width}x{rect.Height})");
            if (copy)
            {
                using var png = new MemoryStream();
                part.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                png.Position = 0;
                var image = System.Windows.Media.Imaging.BitmapFrame.Create(png, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                System.Windows.Clipboard.SetImage(image);
            }
            return file;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Log.Write($"No pude guardar la imagen de la partida: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Envía datos a la página (si ya cargó).</summary>
    public void Push(object message)
    {
        if (_ready && _web.CoreWebView2 is not null)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }
}
