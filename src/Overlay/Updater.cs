using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// Versiones nuevas: las Releases de github.com/ItzStorDz/mayhem-advisor. Cada una lleva el instalador
/// (MayhemAdvisor-Instalador-X.Y.Z.exe) y GitHub da su huella SHA-256 («digest»): el instalador bajado se comprueba con
/// ella antes de abrirlo, así que uno cambiado por el camino no se instala nunca. Solo se pide la última Release
/// (una petición; GitHub deja 60 por hora sin cuenta).
/// Si no se llega a api.github.com (un jugador se quedó en la 1.5.1 con «No such host is known» y sus partidas sí llegaban
/// al servidor de la app), se pregunta al servidor de la app, que copia lo mismo de GitHub (/v1/latest) y, si tampoco se
/// llega a github.com, da el instalador tal cual (/v1/download). La huella SHA-256 se comprueba igual.
/// </summary>
static class Updater
{
    const string Repo = "ItzStorDz/mayhem-advisor";
    const string Mirror = "https://mayhem-advisor.itzstordz.workers.dev/v1";

    /// <param name="MirrorUrl">El mismo instalador desde el servidor de la app (si no se llega a github.com).</param>
    public sealed record Release(Version Version, string PageUrl, string AssetUrl, string AssetName, long Size, string Sha256, string Notes,
                                 string? MirrorUrl = null);

    /// <summary>Esta versión (la del .csproj), con tres números: 1.4.0.</summary>
    public static Version Current { get; } = Three(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        // Sin tiempo máximo para la descarga (150 MB con una conexión lenta); la consulta lleva el suyo.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MayhemAdvisor", Current.ToString(3)));
        return http;
    }

    /// <summary>Carpeta donde se baja el instalador; se vacía al abrir la versión nueva.</summary>
    static string Folder => Path.Combine(Path.GetTempPath(), "MayhemAdvisor-actualizacion");

    /// <returns>La Release más nueva si es posterior a esta versión; si no (o aún no hay ninguna), null.</returns>
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        // MAYHEM_UPDATE_URL: solo para las pruebas (una Release falsa servida en este PC).
        var test = Environment.GetEnvironmentVariable("MAYHEM_UPDATE_URL") is { Length: > 0 } t ? t : null;
        if (test is not null) return await CheckAtAsync(test, test, ct);
        try { return await CheckAtAsync($"https://api.github.com/repos/{Repo}/releases/latest", null, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Sin acceso a GitHub: lo mismo desde el servidor de la app.
            try { return await CheckAtAsync($"{Mirror}/latest", null, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { throw ex; } // el error que importa es el de GitHub
        }
    }

    static async Task<Release?> CheckAtAsync(string address, string? test, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, address);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await Http.SendAsync(req, timeout.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        if (!Version.TryParse((root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'), out var parsed)) return null;
        var version = Three(parsed);
        if (version <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.StartsWith("MayhemAdvisor-Instalador-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            // Solo instaladores de este repositorio (con la prueba, del servidor de prueba).
            if (test is null && !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L("La versión nueva no trae su huella SHA-256.", "The new version has no SHA-256 fingerprint."));
            return new Release(version, root.GetProperty("html_url").GetString() ?? "", url, Path.GetFileName(name),
                asset.GetProperty("size").GetInt64(), digest[7..].ToLowerInvariant(),
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                test is null ? $"{Mirror}/download/v{version.ToString(3)}/{Path.GetFileName(name)}" : null);
        }
        return null; // Release sin instalador todavía (se está subiendo)
    }

    /// <summary>Baja el instalador y comprueba su huella. Devuelve la ruta del .exe ya comprobado.</summary>
    /// <exception cref="InvalidDataException">El archivo bajado no es el que publica GitHub.</exception>
    public static async Task<string> DownloadAsync(Release release, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, release.AssetName);
        if (File.Exists(file) && await HashAsync(file, ct) == release.Sha256) return file; // ya bajado antes
        var part = file + ".part";
        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) when (release.MirrorUrl is not null)
        {
            // Sin acceso a github.com: el mismo instalador desde el servidor de la app (la huella se comprueba abajo).
            resp = await Http.GetAsync(release.MirrorUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? release.Size, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(1, done / (double)total));
            }
        }
        if (await HashAsync(part, ct) != release.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException(L("El instalador bajado no coincide con el de GitHub: no se instala.",
                                             "The downloaded installer doesn't match the one on GitHub: it won't be installed."));
        }
        File.Move(part, file, overwrite: true);
        return file;
    }

    static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>
    /// Abre el instalador sin preguntas (solo se ve su barra de progreso). Se instala encima, en la misma carpeta y con las
    /// mismas opciones; tus partidas y ajustes no se tocan. /UPDATE=1 le dice que vuelva a abrir la app al acabar.
    /// </summary>
    public static void Install(string file, string lang) =>
        Process.Start(new ProcessStartInfo(file, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 /LANG={lang}") { UseShellExecute = true });

    /// <summary>Borra el instalador de la última actualización (ya no hace falta).</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* en uso: la próxima vez */ }
    }
}
