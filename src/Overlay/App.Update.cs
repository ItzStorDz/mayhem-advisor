using System.Net.Http;
using System.Text.Json;
using MayhemAdvisor.Core;
using static MayhemAdvisor.Core.Lang;
using Forms = System.Windows.Forms;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Actualizaciones (ver <see cref="Updater"/>): al abrir la app y luego cada 6 horas mira si hay una versión nueva en
/// GitHub. Si la hay, avisa una vez en la bandeja y lo enseña en la ventana principal; se instala solo cuando pulsas
/// «Instalar», y nunca con el juego abierto.
/// </summary>
public partial class App
{
    Updater.Release? _update;
    /// <summary>idle · checking · uptodate · available · downloading · installing · ingame · error</summary>
    string _updateState = "idle";
    double _updatePct;
    string? _updateError;
    Version? _updateNotified; // la versión de la que ya avisó la bandeja
    bool _balloonUpdate;      // el último aviso de la bandeja es el de la versión nueva

    static bool GameRunning() => System.Diagnostics.Process.GetProcessesByName("League of Legends").Length > 0;

    async Task UpdateLoopAsync(CancellationToken ct)
    {
        Updater.CleanUp();
        await Delay(15_000, ct); // primero, que cargue lo demás
        while (!ct.IsCancellationRequested)
        {
            await CheckUpdateAsync(manual: false);
            await Delay(6 * 3600 * 1000, ct);
        }
    }

    async Task CheckUpdateAsync(bool manual)
    {
        if (_updateState is "checking" or "downloading" or "installing") return;
        _updateState = "checking";
        PushUpdate();
        try
        {
            _update = await Updater.CheckAsync(_cts.Token);
            _updateState = _update is null ? "uptodate" : "available";
            _updateError = null;
            if (_update is { } u)
            {
                Log.Write($"Hay una versión nueva: {u.Version.ToString(3)} (tienes la {Updater.Current.ToString(3)})");
                // Un aviso por versión, y no en partida (sale cuando se vuelva a mirar, ya fuera del juego).
                if (u.Version != _updateNotified && !GameRunning())
                {
                    _updateNotified = u.Version;
                    _balloonUpdate = true;
                    _tray.ShowBalloonTip(8000, "Mayhem Advisor",
                        L($"Hay una versión nueva: {u.Version.ToString(3)}. Pulsa aquí para instalarla.",
                          $"A new version is available: {u.Version.ToString(3)}. Click here to install it."), Forms.ToolTipIcon.Info);
                }
            }
            else if (manual) Log.Write($"Buscar actualizaciones: tienes la última versión ({Updater.Current.ToString(3)})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            // Sin internet o GitHub no responde: se vuelve a mirar dentro de 6 horas (o con «Buscar ahora»).
            _updateState = "error";
            _updateError = ex.Message;
            Log.Write($"No pude buscar actualizaciones: {ex.GetType().Name}: {ex.Message}");
        }
        BuildTrayMenu();
        PushUpdate();
    }

    async Task InstallUpdateAsync()
    {
        if (_update is null || _updateState is "downloading" or "installing") return;
        if (GameRunning()) { _updateState = "ingame"; PushUpdate(); return; }
        var u = _update;
        try
        {
            // Se vuelve a preguntar a GitHub: tras un fallo («Reintentar») la Release puede haber cambiado.
            _update = await Updater.CheckAsync(_cts.Token);
            if (_update is null) { _updateState = "uptodate"; BuildTrayMenu(); PushUpdate(); return; }
            u = _update;
            _updateState = "downloading";
            _updatePct = 0;
            PushUpdate();
            var lastPush = DateTime.MinValue;
            var progress = new Progress<double>(p =>
            {
                _updatePct = p;
                if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 300) return;
                lastPush = DateTime.UtcNow;
                PushUpdate();
            });
            Log.Write($"Bajando la versión {u.Version.ToString(3)} ({u.Size / 1048576.0:0} MB)…");
            var file = await Updater.DownloadAsync(u, progress, _cts.Token);
            // Si mientras bajaba empezó una partida, se espera a que la cierres y pulses otra vez.
            if (GameRunning()) { _updateState = "ingame"; PushUpdate(); return; }
            _updateState = "installing";
            PushUpdate();
            Log.Write($"Instalando la versión {u.Version.ToString(3)}: la app se cierra y se vuelve a abrir sola");
            Updater.Install(file, Lang.TextCode); // el instalador solo tiene español e inglés
            await Task.Delay(1500); // que la ventana vea «Instalando…»
            Quit();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            _updateState = "error";
            _updateError = ex.Message;
            Log.Write($"No pude instalar la versión {u.Version.ToString(3)}: {ex.GetType().Name}: {ex.Message}");
            PushUpdate();
        }
    }

    void PushUpdate() => _dashboard?.Push(UpdateMessage());

    object UpdateMessage() => new
    {
        type = "update",
        state = _updateState,
        current = Updater.Current.ToString(3),
        latest = _update?.Version.ToString(3),
        sizeMb = _update is null ? 0 : Math.Round(_update.Size / 1048576.0),
        notes = _update?.Notes,
        pct = _updatePct,
        error = _updateError,
    };

    /// <summary>La página de la Release en el navegador (solo enlaces de GitHub de este repositorio).</summary>
    void OpenReleasePage()
    {
        if (_update?.PageUrl is { } url && url.StartsWith("https://github.com/ItzStorDz/mayhem-advisor/", StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
