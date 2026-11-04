namespace MayhemAdvisor.Overlay;

/// <summary>Datos anónimos (ver <see cref="Cloud"/>): al abrir la app, cada 3 horas y al guardarse cada partida.</summary>
public partial class App
{
    async Task CloudLoopAsync(CancellationToken ct)
    {
        await Delay(30_000, ct); // primero, que cargue lo demás y se copie el historial
        while (!ct.IsCancellationRequested)
        {
            Cloud.QueueNewMatches();
            await Cloud.FlushAsync(ct);
            await Cloud.FlushFeedbackAsync(ct); // las sugerencias van aunque no se compartan datos
            if (await Cloud.DownloadCommunityAsync(ct)) ReloadStats();
            await Delay(3 * 3600 * 1000, ct);
        }
    }

    /// <summary>Al volver a activar «Compartir datos anónimos» o aceptar el aviso.</summary>
    async Task CloudAfterChangeAsync()
    {
        Cloud.QueueNewMatches();
        await Cloud.FlushAsync(_cts.Token);
    }
}
