using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MayhemAdvisor.Core;

/// <summary>
/// Los datos de la app (notas de aumentos, builds, % de cada campeón) se actualizan solos, sin sacar una versión nueva:
/// GitHub los prepara una vez al día (herramientas/publicar-datos.mjs, .github/workflows/datos.yml) y los deja en la
/// Release «datos» del repositorio con un manifiesto (fecha, tamaño y SHA-256 de cada archivo). La app mira el manifiesto
/// al abrirse y cada pocas horas; lo que sea más nuevo que lo que tiene lo baja a <see cref="Paths.DataDir"/>, donde
/// <see cref="DataFiles.Resolve"/> ya prefiere el archivo más reciente. No se envía nada: solo se descarga de GitHub.
/// Un archivo solo se cambia si llegó entero (SHA-256), es JSON y trae los campeones que debe; si algo falla, la app
/// sigue con los datos que tenía. Pedido del usuario (2026-11-12): «que se actualice sola la app con la información de
/// las fuentes sin tener que lanzar actualizaciones cada rato».
/// </summary>
public static class DataUpdater
{
    /// <summary>MAYHEM_DATA_URL: solo para las pruebas (otro sitio del que bajar).</summary>
    static readonly string BaseUrl = Environment.GetEnvironmentVariable("MAYHEM_DATA_URL") is { Length: > 0 } test ? test
        : "https://github.com/ItzStorDz/mayhem-advisor/releases/download/datos/";

    /// <summary>El formato de los archivos que entiende esta versión: si cambia, el manifiesto llevará otro número (y otro nombre).</summary>
    const int Schema = 1;
    const string ManifestName = "manifest-v1.json";
    const long MaxBytes = 30_000_000;
    const int MinEntries = 150;

    /// <summary>Los archivos que se actualizan solos y dónde lleva cada uno sus campeones (o aumentos).</summary>
    static readonly Dictionary<string, string> Files = new()
    {
        ["aramhex-augments.json"] = "champions",
        ["aramkit-augments.json"] = "champions",
        ["aramkit-builds.json"] = "champions",
        ["mayhem-champions.json"] = "champions",
        ["mayhem-champion-builds.json"] = "champions",
        ["mayhem-augments.json"] = "augments",
    };

    sealed class Manifest
    {
        [JsonPropertyName("schema")] public int Schema { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, Entry>? Files { get; set; }
    }

    sealed class Entry
    {
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
        [JsonPropertyName("patch")] public string? Patch { get; set; }
    }

    /// <summary>Lo que hizo una comprobación: los archivos que se actualizaron y, si no se pudo mirar, por qué.</summary>
    public sealed record Result(IReadOnlyList<string> Updated, string? Problem);

    /// <summary>Mira si hay datos más nuevos y los baja. Nunca lanza: sin internet o con algo raro, devuelve el motivo.</summary>
    public static async Task<Result> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        var updated = new List<string>();
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await http.GetStringAsync(BaseUrl + ManifestName, ct));
            if (manifest?.Files is null || manifest.Schema != Schema) return new(updated, "el manifiesto es de otro formato");
            Directory.CreateDirectory(Paths.DataDir);
            foreach (var (name, key) in Files)
            {
                if (!manifest.Files.TryGetValue(name, out var entry) || entry.Sha256 is not { Length: 64 } sha || entry.Size <= 0 || entry.Size > MaxBytes) continue;
                var stamp = entry.UpdatedAt.ToUniversalTime();
                var current = DataFiles.Resolve(name);
                // Lo que ya tiene es igual de nuevo o más (p. ej. una versión recién instalada con datos de hoy): nada que hacer.
                if (File.Exists(current) && File.GetLastWriteTimeUtc(current) >= stamp) continue;
                if (File.Exists(current) && Hash(await File.ReadAllBytesAsync(current, ct)) == sha)
                {
                    // Los mismos datos con fecha más vieja: si es la copia de AppData, se le pone la fecha para no volver a mirarla.
                    if (IsLocal(current)) File.SetLastWriteTimeUtc(current, stamp);
                    continue;
                }
                var bytes = await http.GetByteArrayAsync(BaseUrl + name, ct);
                if (bytes.LongLength != entry.Size || Hash(bytes) != sha || !LooksRight(bytes, key)) continue; // a medias o raro: la próxima vez
                var target = Path.Combine(Paths.DataDir, name);
                var temp = target + ".nuevo";
                await File.WriteAllBytesAsync(temp, bytes, ct);
                File.SetLastWriteTimeUtc(temp, stamp);
                File.Move(temp, target, overwrite: true);
                updated.Add(entry.Patch is { Length: > 0 } patch ? $"{name} ({patch}, {stamp:yyyy-MM-dd})" : name);
            }
            return new(updated, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new(updated, ex.Message);
        }
    }

    static bool IsLocal(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Paths.DataDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>¿Es JSON y trae al menos <see cref="MinEntries"/> campeones (o aumentos)? Un archivo vacío o cortado no entra.</summary>
    static bool LooksRight(byte[] bytes, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var list)) return false;
            int n = list.ValueKind switch
            {
                JsonValueKind.Array => list.GetArrayLength(),
                JsonValueKind.Object => list.EnumerateObject().Count(),
                _ => 0,
            };
            return n >= MinEntries;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
