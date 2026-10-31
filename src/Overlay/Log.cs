using MayhemAdvisor.Core;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Registro de diagnóstico en %LOCALAPPDATA%\MayhemAdvisor\overlay.log (solo cambios de estado y errores).
/// Al pasar de 1 MB se guarda como overlay.old.log y se empieza de nuevo.
/// </summary>
public static class Log
{
    static readonly string FilePath = Path.Combine(Paths.DataDir, "overlay.log");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, Path.Combine(Paths.DataDir, "overlay.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // nunca tumbar el programa por el registro
        }
    }
}
