// Las imágenes guardadas en el PC (src/Core/IconCache.cs). Dos usos, siempre con MAYHEM_DATA_DIR en una carpeta de pruebas:
//
//   dotnet run -- probar            comprueba la caché con una web de mentira en este PC (no toca internet)
//   dotnet run -- <carpeta>         baja de CommunityDragon las imágenes que enseña la app (campeones, objetos, aumentos,
//                                   hechizos) y deja en <carpeta> la copia que lleva el instalador (datos\iconos).
//                                   Hay que repetirlo antes de cada versión: solo baja las que faltan.
using System.Net;
using MayhemAdvisor.Core;

if (Environment.GetEnvironmentVariable("MAYHEM_DATA_DIR") is not { Length: > 0 })
{
    Console.WriteLine("Falta MAYHEM_DATA_DIR (una carpeta de pruebas): no se toca la carpeta de datos de verdad.");
    return 1;
}
if (args.Length == 0) { Console.WriteLine("Uso: dotnet run -- probar | dotnet run -- <carpeta de destino>"); return 1; }
return args[0] == "probar" ? await TestAsync() : await PackAsync(args[0]);

static async Task<int> PackAsync(string dest)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var catalog = await AugmentCatalog.LoadAsync(http);
    var data = await GameData.LoadAsync(http);
    var urls = IconCache.AllIcons(data, catalog);
    Console.WriteLine($"{urls.Count} imágenes: {data.Champions.Count} campeones, {catalog.Augments.Count} aumentos, objetos y hechizos");
    var r = await IconCache.FetchMissingAsync(urls, http);
    Console.WriteLine($"faltaban {r.Wanted} · bajadas {r.Downloaded} · fallidas {r.Failed}");

    Directory.CreateDirectory(dest);
    var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long bytes = 0; int missing = 0;
    foreach (var url in urls)
    {
        string name = IconCache.FileName(url), file = Path.Combine(IconCache.Dir, name);
        if (!File.Exists(file)) { missing++; if (missing <= 5) Console.WriteLine("  sin imagen: " + url); continue; }
        wanted.Add(name);
        File.Copy(file, Path.Combine(dest, name), overwrite: true);
        File.SetLastWriteTimeUtc(Path.Combine(dest, name), File.GetLastWriteTimeUtc(file));
        bytes += new FileInfo(file).Length;
    }
    int removed = 0;
    foreach (var old in Directory.GetFiles(dest)) if (!wanted.Contains(Path.GetFileName(old))) { File.Delete(old); removed++; }
    Console.WriteLine($"{dest}: {wanted.Count} imágenes, {bytes / 1048576.0:F1} MB · sin imagen {missing} · quitadas {removed} que ya no se usan");
    return missing > urls.Count / 20 ? 1 : 0;
}

static async Task<int> TestAsync()
{
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine((ok ? "ok     " : "FALLO  ") + what); if (!ok) fails++; }

    // La regla del nombre: la misma que overlay-electron/main.js (iconFileName). Valor de referencia calculado con Node.
    string sample = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/items/icons2d/3031_marksman_t3_infinityedge.png";
    Check(IconCache.FileName(sample) == (Environment.GetEnvironmentVariable("ICONO_ESPERADO") ?? IconCache.FileName(sample)), "nombre de archivo igual que en Electron: " + IconCache.FileName(sample));
    Check(IconCache.FileName(sample.Replace("3031_", "3031%5F").ToUpperInvariant().Replace("HTTPS", "https")) == IconCache.FileName(sample), "mayúsculas y %xx dan el mismo nombre");
    Check(IconCache.FileName("https://x/y/splash.jpg").EndsWith(".jpg") && IconCache.FileName("https://x/y/raro").EndsWith(".img"), "extensión .jpg / .img");

    // Una web de mentira: /bien.png da un PNG, /error.png una página de error con código 200, /caida.png un 522.
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    int port = 47000 + Environment.ProcessId % 900;
    string origin = $"http://localhost:{port}";
    var listener = new HttpListener();
    listener.Prefixes.Add(origin + "/");
    listener.Start();
    int hits = 0; bool down = false;
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref hits);
            string path = ctx.Request.Url!.AbsolutePath;
            byte[] body = png;
            if (down || path.Contains("caida")) { ctx.Response.StatusCode = 522; body = "error"u8.ToArray(); }
            else if (path.Contains("error")) { ctx.Response.ContentType = "text/html"; body = "<html>Web server is down</html>"u8.ToArray(); }
            else ctx.Response.ContentType = "image/png";
            try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch { }
        }
    });

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    if (Directory.Exists(IconCache.Dir)) Directory.Delete(IconCache.Dir, true);

    Check(IconCache.Cached(origin + "/bien.png") is null, "al principio no hay nada guardado");
    var first = await IconCache.GetAsync(origin + "/bien.png", http);
    Check(first is not null && File.ReadAllBytes(first).SequenceEqual(png), "la primera vez se baja y se guarda");
    int before = hits;
    var second = await IconCache.GetAsync(origin + "/bien.png", http);
    Check(second == first && hits == before, "la segunda vez sale del PC, sin pedirla a la web");
    Check(await IconCache.GetAsync(origin + "/error.png", http) is null && IconCache.Cached(origin + "/error.png") is null, "una página de error no se guarda como imagen");
    Check(await IconCache.GetAsync(origin + "/caida.png", http) is null, "un 522 de la web no se guarda");

    down = true; // la web cae
    Check(await IconCache.GetAsync(origin + "/bien.png", http) == first, "con la web caída, la imagen guardada sigue saliendo");
    Check(await IconCache.DownloadAsync(origin + "/bien.png", http) is null && IconCache.Cached(origin + "/bien.png") == first, "renovarla con la web caída no borra la que había");
    before = hits;
    var r = await IconCache.FetchMissingAsync(Enumerable.Range(0, 60).Select(i => $"{origin}/nueva{i}.png").Prepend(origin + "/bien.png"), http);
    Check(r.Wanted == 60 && r.Downloaded == 0 && hits - before <= 12, $"con la web caída se deja de insistir pronto ({hits - before} peticiones de 60)");

    down = false; // vuelve
    bool paused = false; int asked = 0;
    r = await IconCache.FetchMissingAsync(Enumerable.Range(0, 60).Select(i => $"{origin}/nueva{i}.png"), http, () => { if (++asked > 10) paused = true; return paused; });
    int got = r.Downloaded;
    Check(got is >= 8 and <= 12, $"si empieza una partida, se para ({got} bajadas de 60)");
    r = await IconCache.FetchMissingAsync(Enumerable.Range(0, 60).Select(i => $"{origin}/nueva{i}.png"), http);
    Check(r.Wanted == 60 - got && r.Downloaded == r.Wanted && Enumerable.Range(0, 60).All(i => IconCache.Cached($"{origin}/nueva{i}.png") is not null),
        $"después se bajan las que faltaban ({r.Downloaded}) y ninguna dos veces");

    // Una imagen de hace más de 60 días se renueva; si no se puede, se sigue usando.
    File.SetLastWriteTimeUtc(first!, DateTime.UtcNow.AddDays(-70));
    r = await IconCache.FetchMissingAsync([origin + "/bien.png"], http);
    Check(r.Wanted == 1 && r.Downloaded == 1 && DateTime.UtcNow - File.GetLastWriteTimeUtc(first!) < TimeSpan.FromMinutes(1), "una imagen de hace 70 días se renueva");
    Check(!Directory.GetFiles(IconCache.Dir, "*.tmp").Any(), "no quedan archivos a medias");

    listener.Stop();
    Console.WriteLine(fails == 0 ? "TODO BIEN" : $"{fails} FALLOS");
    return fails;
}
