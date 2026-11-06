using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MayhemAdvisor.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrLine = MayhemAdvisor.Core.OcrLine;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Captura la zona central de la ventana del juego (donde aparecen las cartas de aumentos)
/// y la lee con el OCR que ya trae Windows. No usa GPU ni motores externos.
/// Las posiciones devueltas son píxeles reales de la pantalla.
/// </summary>
public sealed class ScreenReader
{
    readonly OcrEngine _ocr;

    ScreenReader(OcrEngine ocr) => _ocr = ocr;

    public string Language => _ocr.RecognizerLanguage.DisplayName;

    public static ScreenReader? Create()
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("es-MX"))
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("es-ES"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        return engine is null ? null : new ScreenReader(engine);
    }

    /// <summary>
    /// Zona de la pantalla que ocupa el juego: el interior de su ventana (sin barra de título), en píxeles de pantalla.
    /// Así en modo ventana leemos solo el juego y no lo que haya alrededor. Si no está abierto, la pantalla principal.
    /// </summary>
    public static Rectangle GameArea()
    {
        var hwnd = FindGameWindow();
        var origin = new POINT(); // la esquina de arriba a la izquierda del interior de la ventana
        RECT rc = default;
        bool found = hwnd != IntPtr.Zero && !IsIconic(hwnd) && GetClientRect(hwnd, out rc)
                     && rc.Right > 0 && rc.Bottom > 0 && ClientToScreen(hwnd, ref origin);
        var area = found ? new Rectangle(origin.X, origin.Y, rc.Right, rc.Bottom) : System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        if (area != _lastArea)
        {
            _lastArea = area;
            Log.Write($"Leyendo cartas en la {(found ? "ventana del juego" : "pantalla principal")}: {area.Width}x{area.Height} en ({area.X},{area.Y})");
        }
        return area;
    }

    static Rectangle _lastArea;

    static IntPtr _gameWindow;

    /// <summary>
    /// La ventana del juego: clase RiotWindowClass del proceso "League of Legends" (el cliente usa otra clase).
    /// Se recuerda mientras siga abierta, para no buscarla cuatro veces por segundo.
    /// </summary>
    static IntPtr FindGameWindow()
    {
        if (_gameWindow != IntPtr.Zero && IsWindow(_gameWindow)) return _gameWindow;
        _gameWindow = SearchGameWindow();
        return _gameWindow;
    }

    static IntPtr SearchGameWindow()
    {
        for (var hwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "RiotWindowClass", null); hwnd != IntPtr.Zero;
             hwnd = FindWindowEx(IntPtr.Zero, hwnd, "RiotWindowClass", null))
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("League of Legends", StringComparison.OrdinalIgnoreCase)) return hwnd;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // el proceso acaba de cerrarse
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

    /// <param name="screen">Zona del juego en píxeles de pantalla (ver <see cref="GameArea"/>).</param>
    public Task<List<OcrLine>> ReadAsync(Rectangle screen) =>
        // Casi todo el ancho (con 4 cartas o en pantallas 16:9 las de los lados se salían del 70 % central y se
        // leían tarde) y solo la franja de los nombres, del 22 % al 60 % del alto: menos texto = lectura más rápida
        // y menos nombres de aumentos sueltos en las descripciones.
        ReadRegionAsync(new Rectangle(
            screen.X + screen.Width * 3 / 100, screen.Y + screen.Height * 22 / 100,
            screen.Width * 94 / 100, screen.Height * 38 / 100), 1);

    /// <summary>
    /// Segunda lectura, más fina: solo la fila de los nombres de las cartas (alrededor de <paramref name="centerY"/>),
    /// ampliada al doble. Con letras más grandes y sin las descripciones, el OCR lee nombres que antes se le escapaban.
    /// </summary>
    public Task<List<OcrLine>> ReadNameRowAsync(Rectangle screen, int centerY, int textHeight)
    {
        int half = Math.Max(24, textHeight * 3);
        return ReadRegionAsync(new Rectangle(screen.X + screen.Width * 3 / 100, centerY - half, screen.Width * 94 / 100, half * 2), 2);
    }

    /// <summary>Lecturas que se ahorraron porque la imagen no cambió (para el registro de rendimiento).</summary>
    public int Reused { get; private set; }
    ulong _lastSignature;
    Rectangle _lastSignatureArea;
    DateTime _lastRealRead;
    List<OcrLine> _lastLines = [];

    /// <summary>Como mucho este tiempo seguido se reutiliza una lectura: luego se lee de verdad (por si un cambio pequeño no movió la huella).</summary>
    static readonly TimeSpan ReuseMax = TimeSpan.FromSeconds(3);

    async Task<List<OcrLine>> ReadRegionAsync(Rectangle area, int scale)
    {
        // Con la ventana del juego diminuta (minimizándose o cargando) la zona sale de 0 px de alto y el Bitmap lanzaba
        // «Parameter is not valid» (2 veces en los resúmenes del 05/06-11): ahí no puede haber cartas.
        if (area.Width < 40 || area.Height < 16) return [];
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size, CopyPixelOperation.SourceCopy);

        // Idea de Hexsight: una "huella" barata de la imagen (0,5-1 ms frente a ~90 ms del OCR). Si es la misma que la de
        // la lectura anterior de la misma zona (las cartas quietas en pantalla, la pantalla gris al morir…), el texto
        // también: se devuelve lo de antes sin pasar el OCR. Solo para la lectura normal (no la ampliada).
        ulong signature = scale == 1 ? Signature(bmp) : 0;
        if (scale == 1 && signature == _lastSignature && area == _lastSignatureArea && DateTime.UtcNow - _lastRealRead < ReuseMax)
        {
            Reused++;
            return [.. _lastLines];
        }

        using var big = scale > 1 ? Scale(bmp, scale) : null;
        using var soft = ToSoftwareBitmap(big ?? bmp);
        var result = await _ocr.RecognizeAsync(soft);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            // Windows a veces junta en una línea los nombres de dos cartas que están a la misma altura:
            // si hay un hueco grande entre palabras, son textos distintos.
            var group = new List<OcrWord>();
            foreach (var word in line.Words)
            {
                if (group.Count > 0)
                {
                    var prev = group[^1].BoundingRect;
                    if (word.BoundingRect.X - (prev.X + prev.Width) > Math.Max(prev.Height, word.BoundingRect.Height) * 2.5)
                    {
                        lines.Add(ToLine(group, area, scale));
                        group.Clear();
                    }
                }
                group.Add(word);
            }
            if (group.Count > 0) lines.Add(ToLine(group, area, scale));
        }
        if (scale == 1) (_lastSignature, _lastSignatureArea, _lastRealRead, _lastLines) = (signature, area, DateTime.UtcNow, [.. lines]);
        return lines;
    }

    /// <summary>
    /// Huella de la imagen: la media de cada casilla de una rejilla de 48x12, sin sus 3 bits bajos (un brillo que
    /// palpita unos niveles no la cambia; un texto distinto, claro sobre fondo oscuro, sí). Se mira 1 de cada 3 píxeles.
    /// </summary>
    static unsafe ulong Signature(Bitmap bmp)
    {
        const int Cols = 48, Rows = 12;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var sums = new long[Cols * Rows];
            var counts = new int[Cols * Rows];
            for (int y = 0; y < data.Height; y += 3)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                int cy = y * Rows / data.Height;
                for (int x = 0; x < data.Width; x += 3)
                {
                    byte* p = row + x * 4;
                    int cell = cy * Cols + x * Cols / data.Width;
                    sums[cell] += (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                    counts[cell]++;
                }
            }
            ulong hash = 14695981039346656037UL; // FNV-1a
            for (int i = 0; i < sums.Length; i++)
            {
                hash ^= (ulong)(counts[i] == 0 ? 0 : sums[i] / counts[i]) >> 3;
                hash *= 1099511628211UL;
            }
            return hash;
        }
        finally { bmp.UnlockBits(data); }
    }

    static Bitmap Scale(Bitmap source, int scale)
    {
        var big = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(big);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, big.Width, big.Height);
        return big;
    }

    /// <param name="scale">Cuánto se amplió la imagen (las posiciones se devuelven en píxeles reales de la pantalla).</param>
    static OcrLine ToLine(List<OcrWord> words, Rectangle area, int scale)
    {
        var r = words.Select(w => w.BoundingRect).Aggregate((a, b) => { a.Union(b); return a; });
        return new OcrLine(string.Join(" ", words.Select(w => w.Text)),
            new Box(area.X + (int)(r.X / scale), area.Y + (int)(r.Y / scale), (int)(r.Width / scale), (int)(r.Height / scale)));
    }

    static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { bmp.UnlockBits(data); }
    }
}
