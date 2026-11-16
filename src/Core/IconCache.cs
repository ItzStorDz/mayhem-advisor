using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MayhemAdvisor.Core;

/// <summary>
/// Las imágenes (iconos de campeones, objetos, aumentos y hechizos) guardadas en el PC. Antes la ventana y el panel las
/// pedían cada vez a CommunityDragon: del 12 al 14 de noviembre de 2026 esa web falló a ratos y la app se veía sin imágenes
/// (opiniones 17, 18, 21 y 23). Ahora cada imagen se baja una vez y se sirve del disco; el instalador trae además una copia
/// (<see cref="SeedDir"/>), así que salen desde el primer momento y sin conexión.
/// El nombre del archivo es el SHA-1 de la dirección (ver <see cref="FileName"/>): el panel de Electron
/// (overlay-electron/main.js) usa la misma regla y la misma carpeta.
/// </summary>
public static class IconCache
{
    /// <summary>Carpeta donde la app guarda las imágenes que baja.</summary>
    public static string Dir => Path.Combine(Paths.DataDir, "iconos");
    /// <summary>Copia que trae el instalador (solo lectura), hecha con pruebas/bajar-iconos.</summary>
    public static string SeedDir => Path.Combine(AppContext.BaseDirectory, "datos", "iconos");

    /// <summary>Una imagen guardada hace más que esto se vuelve a bajar cuando se pueda (Riot cambia algún dibujo).</summary>
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);
    const int MaxBytes = 3_000_000;

    static readonly ConcurrentDictionary<string, Task<string?>> Downloads = new();

    /// <summary>"https://…/3031_infinityedge.png" → "5f0c…e1.png" (SHA-1 de la dirección en minúsculas y sin %xx).</summary>
    public static string FileName(string url)
    {
        string key = url;
        try { key = Uri.UnescapeDataString(url); } catch (UriFormatException) { }
        key = key.ToLowerInvariant();
        string path = key.Split('?', '#')[0];
        string ext = Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".webp" ? Path.GetExtension(path) : ".img";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ext;
    }

    public static string Mime(string file) => Path.GetExtension(file) switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png",
    };

    /// <summary>El archivo de esa imagen si ya está en el PC (lo que bajó la app o, si no, lo que trajo el instalador).</summary>
    public static string? Cached(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string name = FileName(url);
        foreach (var dir in new[] { Dir, SeedDir })
        {
            var file = new FileInfo(Path.Combine(dir, name));
            if (file.Exists && file.Length > 0) return file.FullName;
        }
        return null;
    }

    static bool Stale(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge;

    /// <summary>La imagen del PC; si no está, se baja y se guarda. Null si no está y no se pudo bajar.</summary>
    public static async Task<string?> GetAsync(string url, HttpClient http, CancellationToken ct = default) =>
        Cached(url) ?? await DownloadAsync(url, http, ct);

    /// <summary>Baja la imagen y la guarda (aunque ya hubiera una). Null si falla; la que hubiera se queda como estaba.</summary>
    public static Task<string?> DownloadAsync(string url, HttpClient http, CancellationToken ct = default) =>
        Downloads.GetOrAdd(url, _ => DownloadCoreAsync(url, http, ct));

    static async Task<string?> DownloadCoreAsync(string url, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
            if (data.Length > MaxBytes || !LooksLikeImage(data)) return null;
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, FileName(url)), temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(temp, data, ct);
            try { File.Move(temp, file, overwrite: true); }
            catch (IOException) { try { File.Delete(temp); } catch (IOException) { } if (!File.Exists(file)) return null; } // otro proceso la estaba leyendo
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
        finally { Downloads.TryRemove(url, out _); }
    }

    /// <summary>PNG, JPEG o WebP por sus primeros bytes (una página de error de la web no se guarda como imagen).</summary>
    static bool LooksLikeImage(byte[] d) =>
        d.Length > 12 && ((d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') || (d[0] == 0xFF && d[1] == 0xD8)
            || (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P'));

    /// <summary>
    /// Las imágenes que enseña la app: campeones, objetos (todos: en las partidas guardadas salen también los que no están
    /// en la tienda, como la Galleta de Poro), aumentos y hechizos.
    /// </summary>
    public static List<string> AllIcons(GameData data, AugmentCatalog catalog) =>
        data.Champions.Values.Select(c => c.Icon)
            .Concat(data.Items.Values.Select(i => i.Icon))
            .Concat(catalog.Augments.Select(a => a.Icon))
            .Concat(SummonerSpells.AllIcons)
            .Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();

    public sealed record FetchResult(int Wanted, int Downloaded, int Failed);

    /// <summary>
    /// Baja las imágenes de la lista que faltan en el PC (o llevan más de 60 días), de 3 en 3. Para si <paramref name="pause"/>
    /// dice que sí (hay partida) o tras 6 fallos seguidos (la web no responde: se vuelve a intentar otro rato).
    /// </summary>
    public static async Task<FetchResult> FetchMissingAsync(IEnumerable<string> urls, HttpClient http, Func<bool>? pause = null, CancellationToken ct = default)
    {
        var wanted = urls.Where(u => Cached(u) is not { } file || Stale(file)).ToList();
        int downloaded = 0, failed = 0, failedInARow = 0;
        using var gate = new SemaphoreSlim(3);
        var tasks = new List<Task>();
        foreach (var url in wanted)
        {
            if (ct.IsCancellationRequested || pause?.Invoke() == true || Volatile.Read(ref failedInARow) >= 6) break;
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await DownloadAsync(url, http, ct) is not null) { Interlocked.Increment(ref downloaded); Volatile.Write(ref failedInARow, 0); }
                    else { Interlocked.Increment(ref failed); Interlocked.Increment(ref failedInARow); }
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);
        return new FetchResult(wanted.Count, downloaded, failed);
    }
}
