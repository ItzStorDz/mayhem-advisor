using System.Management;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MayhemAdvisor.Core;

/// <summary>
/// Habla con el cliente de LoL abierto en tu PC (la "LCU"). La dirección y la contraseña
/// cambian cada vez que abres el cliente; las leemos de la línea de comandos de LeagueClientUx.exe.
/// </summary>
public sealed class LcuClient : IDisposable
{
    readonly HttpClient _http;

    LcuClient(HttpClient http, string? installDir) => (_http, InstallDir) = (http, installDir);

    /// <summary>Carpeta de League of Legends (donde está LeagueClientUx.exe), o null si no se pudo leer.</summary>
    public string? InstallDir { get; }

    /// <summary>Devuelve null si el cliente de LoL no está abierto.</summary>
    public static LcuClient? Connect()
    {
        using var search = new ManagementObjectSearcher("SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE Name = 'LeagueClientUx.exe'");
        var proc = search.Get().Cast<ManagementObject>().FirstOrDefault(o => o["CommandLine"] is string);
        if (proc?["CommandLine"] is not string cmd) return null;
        var exe = proc["ExecutablePath"] as string;
        // La carpeta del ejecutable (LeagueClientUx.exe está en la raíz de League of Legends).
        var installDir = exe is null ? null : Path.GetDirectoryName(exe);

        var port = Arg(cmd, "--app-port=");
        var token = Arg(cmd, "--remoting-auth-token=");
        if (port is null || token is null) return null;

        var http = new HttpClient(new HttpClientHandler
        {
            // El cliente usa un certificado propio; solo lo aceptamos para 127.0.0.1.
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1",
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(45),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{token}")));
        return new LcuClient(http, installDir is not null && Directory.Exists(installDir) ? installDir : null);
    }

    static string? Arg(string cmd, string name)
    {
        int i = cmd.IndexOf(name, StringComparison.Ordinal);
        if (i < 0) return null;
        i += name.Length;
        int end = cmd.IndexOfAny([' ', '"'], i);
        return end < 0 ? cmd[i..] : cmd[i..end];
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>POST sin cuerpo (p. ej. aceptar la partida). True si el cliente respondió bien.</summary>
    public async Task<bool> PostAsync(string path, CancellationToken ct = default)
    {
        using var res = await _http.PostAsync(path, null, ct);
        return res.IsSuccessStatusCode;
    }

    /// <summary>PATCH con un cuerpo JSON. True si el cliente respondió bien.</summary>
    public async Task<bool> PatchJsonAsync(string path, string json, CancellationToken ct = default)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var res = await _http.PatchAsync(path, body, ct);
        return res.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
