using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Overlay en Electron: una ventana web transparente que la librería electron-overlay-window
/// pega a la ventana del juego. Este programa sigue leyendo la pantalla y calculando; aquí solo le mandamos
/// los datos en JSON, una línea por mensaje, por una tubería con nombre de Windows (local, solo de tu usuario).
/// La entrada estándar no sirve: en Windows Electron no la lee.
/// Por la misma tubería vuelven los botones del overlay ("me quedo con el item"), también un JSON por línea.
/// </summary>
public sealed class ElectronBridge : IDisposable
{
    const int MaxStarts = 5;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly string _exe, _appDir;
    readonly object _gate = new();
    Process? _process;
    NamedPipeServerStream? _pipe;
    StreamWriter? _writer;
    string? _lastLive; // se manda en cuanto Electron se conecta, para que no empiece en blanco
    DateTime _nextStart = DateTime.MinValue;
    int _starts;

    ElectronBridge(string exe, string appDir) => (_exe, _appDir) = (exe, appDir);

    /// <summary>Un mensaje del overlay (se lanza en un hilo de fondo).</summary>
    public event Action<JsonElement>? MessageReceived;

    /// <summary>True si Electron dejó de arrancar demasiadas veces: entonces se usa el overlay de antes.</summary>
    public bool GaveUp => !_disposed && _starts >= MaxStarts && !Running;
    bool Running
    {
        get
        {
            // Un Process ya cerrado con Dispose lanza «No process is associated with this object» (informe del 05-11).
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    bool _disposed; // la app se está cerrando: no se vuelve a arrancar Electron
    bool _connected; // el Electron de ahora llegó a conectarse por la tubería (para el registro si se cierra)

    /// <summary>Busca la carpeta overlay-electron subiendo desde donde está el programa. Null si no está instalada.</summary>
    public static ElectronBridge? TryCreate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var appDir = Path.Combine(dir.FullName, "overlay-electron");
            var exe = Path.Combine(appDir, "node_modules", "electron", "dist", "electron.exe");
            if (File.Exists(exe) && File.Exists(Path.Combine(appDir, "main.js"))) return new ElectronBridge(exe, appDir);
        }
        return null;
    }

    public void Send(object message)
    {
        var line = JsonSerializer.Serialize(message, Json);
        lock (_gate)
        {
            // Al salir (Quit) el bucle aún puede mandar algo una vez: antes preguntaba al proceso ya cerrado y saltaba
            // «Error en el bucle» (1.8.0 y 1.8.2, informe del 05-11); sin esto, además, podría arrancar otro Electron.
            if (_disposed) return;
            if (line.StartsWith("{\"type\":\"live\"", StringComparison.Ordinal)) _lastLive = line;
            if (!EnsureStarted() || _writer is null) return; // aún conectando: se manda _lastLive al conectar
            Write(line);
        }
    }

    void Write(string line)
    {
        try
        {
            _writer!.WriteLine(line);
            _writer.Flush();
        }
        catch (IOException ex)
        {
            Log.Write($"Overlay Electron: se cortó la comunicación ({ex.Message})");
            ClosePipe();
        }
    }

    bool EnsureStarted()
    {
        if (Running && _pipe is not null) return true;
        if (_starts >= MaxStarts || DateTime.UtcNow < _nextStart) return false;
        // Por qué se cerró el anterior: en el informe del 05-11 a un jugador no le arrancaba (5 intentos y vuelta al overlay
        // de antes) y no se sabía por qué. El código va en hexadecimal (0xC0000005…): el resumen de errores quita los números.
        try
        {
            if (_process is { HasExited: true } old)
                Log.Write($"Overlay Electron se cerró solo (código 0x{old.ExitCode:X8}{old.ExitCode switch
                {
                    // Los de overlay-electron/main.js:
                    17 => ": ya había otro overlay abierto",
                    18 or 19 => ": no pudo conectarse con la app",
                    _ => "",
                }}{(_connected ? "" : ", sin llegar a conectarse")})");
        }
        catch (InvalidOperationException) { }
        _connected = false;
        _starts++;
        _nextStart = DateTime.UtcNow.AddSeconds(10);
        StopProcess();
        ClosePipe();
        try
        {
            var name = $"mayhem-advisor-overlay-{Environment.ProcessId}-{_starts}";
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var pipe = _pipe;
            _ = pipe.WaitForConnectionAsync().ContinueWith(t =>
            {
                lock (_gate)
                {
                    if (t.IsFaulted || pipe != _pipe) return;
                    _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
                    _connected = true;
                    Log.Write("Overlay Electron conectado");
                    if (_lastLive is not null) Write(_lastLive);
                }
                _ = Task.Run(() => ReadMessages(pipe));
            }, TaskScheduler.Default);

            var info = new ProcessStartInfo(_exe) { WorkingDirectory = _appDir, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(_appDir);
            info.ArgumentList.Add($"--mayhem-pipe={name}");
            _process = Process.Start(info);
            Log.Write($"Overlay Electron iniciado (intento {_starts})");
            return Running;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Log.Write($"Overlay Electron no arrancó: {ex.Message}");
            return false;
        }
    }

    void ReadMessages(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    MessageReceived?.Invoke(doc.RootElement.Clone());
                }
                catch (JsonException) { } // línea rota: se ignora
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } // se cerró la tubería
    }

    void ClosePipe()
    {
        try { _writer?.Dispose(); } catch (IOException) { }
        try { _pipe?.Dispose(); } catch (IOException) { }
        _writer = null;
        _pipe = null;
    }

    void StopProcess()
    {
        if (!Running) return;
        try { if (!_process!.WaitForExit(1500)) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_writer is not null) Write("{\"type\":\"quit\"}");
            ClosePipe(); // al cerrarse la tubería, Electron se cierra solo
            StopProcess();
            _process?.Dispose();
        }
    }
}
