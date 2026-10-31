using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using MayhemAdvisor.Core;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Extras de la selección de campeón (1.3.8, ideas de Hexsight): la build en la tienda del juego, el aviso de pantalla
/// completa con su botón, coger un campeón del banco con un clic y aceptar o rechazar los cambios que te piden.
/// Nada se hace solo: los botones actúan cuando los pulsas, y la build de la tienda se puede apagar en Ajustes.
/// </summary>
public partial class App
{
    /// <summary>Lo que piden los botones del panel; se hace en el bucle, que es quien tiene la conexión con el cliente.</summary>
    readonly ConcurrentQueue<Func<LcuClient, CancellationToken, Task>> _lcuActions = new();
    string? _leagueDir;
    int? _itemSetFor;
    string? _itemSetName;
    int? _windowMode;
    DateTime _nextWindowModeCheck;
    IReadOnlyList<TradeOffer> _trades = [];
    string? _csNotice;
    DateTime _csNoticeUntil;

    async Task ChampSelectExtrasAsync(LcuClient lcu, ChampSelectState select, CancellationToken ct)
    {
        _leagueDir = lcu.InstallDir;
        _trades = select.Trades ?? [];
        while (_lcuActions.TryDequeue(out var action))
        {
            try { await action(lcu, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { Notice(L("El cliente de LoL no respondió", "The League client didn't answer")); }
        }

        if (DateTime.UtcNow >= _nextWindowModeCheck)
        {
            _nextWindowModeCheck = DateTime.UtcNow.AddSeconds(20);
            try
            {
                var mode = await ChampSelect.WindowModeAsync(lcu, ct);
                if (mode != _windowMode && mode is not null) Log.Write($"Modo de ventana del juego: {mode switch { 0 => "pantalla completa", 1 => "ventana", _ => "sin bordes" }}");
                _windowMode = mode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        }

        if (_settings.ShopItemSet && select.MyChampionId > 0 && select.MyChampionId != _itemSetFor && _leagueDir is { } dir
            && _data.ChampionsById.GetValueOrDefault(select.MyChampionId) is { } champ)
        {
            _itemSetFor = select.MyChampionId;
            try
            {
                var path = await Task.Run(() => ItemSets.Write(dir, champ, _stats), ct);
                _itemSetName = path is null ? null : champ.DisplayName;
                Log.Write(path is null ? $"Sin build para la tienda de {champ.DisplayName}" : $"Build enviada a la tienda del juego: {champ.DisplayName} ({path})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _itemSetName = null;
                Log.Write($"No pude escribir la build en la tienda: {ex.Message}");
            }
        }
    }

    void Notice(string text)
    {
        _csNotice = text;
        _csNoticeUntil = DateTime.UtcNow.AddSeconds(8);
        Log.Write("Selección de campeón: " + text);
    }

    /// <summary>Lo que el panel de selección de campeón enseña además de los campeones.</summary>
    object ChampSelectInfo() => new
    {
        trades = _trades.Select(t => _data.ChampionsById.GetValueOrDefault(t.ChampionId) is { } c
            ? new { id = t.Id, name = c.DisplayName, icon = c.Icon, tier = _mayhem.For(c.Alias)?.Tier, winRate = _mayhem.For(c.Alias)?.WinRate / 100 }
            : null).OfType<object>(),
        fullscreen = _windowMode == 0,
        itemSet = _settings.ShopItemSet ? _itemSetName : null,
        notice = DateTime.UtcNow < _csNoticeUntil ? _csNotice : null,
        client = ClientWindowRect() is { } r ? new { x = r.Left, y = r.Top, w = r.Right - r.Left, h = r.Bottom - r.Top } : null,
    };

    /// <summary>Un botón del panel de selección de campeón (Coger, Aceptar/Rechazar, Pasar a sin bordes).</summary>
    void OnChampSelectButton(string type, JsonElement msg)
    {
        switch (type)
        {
            case "benchswap" when msg.TryGetProperty("championId", out var cid) && cid.TryGetInt32(out var championId):
                var name = _data.ChampionsById.GetValueOrDefault(championId)?.DisplayName ?? championId.ToString();
                _lcuActions.Enqueue(async (lcu, ct) =>
                    Notice(await ChampSelect.SwapWithBenchAsync(lcu, championId, ct)
                        ? L($"Has cogido a {name}", $"You took {name}")
                        : L($"No se pudo coger a {name} (puede que el banco aún esté bloqueado)", $"Couldn't take {name} (the bench may still be locked)")));
                break;
            case "trade" when msg.TryGetProperty("id", out var tid) && tid.TryGetInt64(out var tradeId):
                bool accept = msg.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True;
                _lcuActions.Enqueue(async (lcu, ct) =>
                    Notice(await ChampSelect.AnswerTradeAsync(lcu, tradeId, accept, ct)
                        ? (accept ? L("Cambio aceptado", "Trade accepted") : L("Cambio rechazado", "Trade declined"))
                        : L("El cambio ya no está disponible", "That trade is no longer available")));
                break;
            case "borderless":
                _lcuActions.Enqueue(async (lcu, ct) =>
                {
                    bool ok = await ChampSelect.SetBorderlessAsync(lcu, ct);
                    if (ok) { _windowMode = 2; _nextWindowModeCheck = DateTime.UtcNow.AddSeconds(20); }
                    Notice(ok ? L("Listo: el juego se abrirá sin bordes", "Done: the game will open borderless")
                              : L("No se pudo cambiar; hazlo en Ajustes › Vídeo › Modo de ventana", "Couldn't change it; use Settings › Video › Window mode"));
                });
                break;
        }
    }

    /// <summary>Rectángulo de la ventana del cliente de LoL en píxeles de pantalla, para no tapar sus textos con el panel.</summary>
    static RECT? ClientWindowRect()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("LeagueClientUx"))
        {
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero && !IsIconic(p.MainWindowHandle) && GetWindowRect(p.MainWindowHandle, out var r) && r.Right - r.Left > 200)
                    return r;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
}
