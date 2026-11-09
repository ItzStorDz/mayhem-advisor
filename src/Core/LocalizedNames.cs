using System.Text.Json;

namespace MayhemAdvisor.Core;

/// <summary>
/// Nombres de objetos y campeones del juego en otro idioma (portugués, francés, alemán, vietnamita), de CommunityDragon
/// (los mismos archivos que usa el cliente). Los de los aumentos van en el catálogo (ver <see cref="AugmentCatalog"/>), que
/// también los necesita para reconocer las cartas. Caché de 3 días en <c>nombres-&lt;locale&gt;.json</c>.
/// </summary>
public sealed class LocalizedNames
{
    const string Base = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global";

    public string Locale { get; init; } = "";
    public Dictionary<int, string> Items { get; init; } = [];
    public Dictionary<int, string> Champions { get; init; } = [];

    public string? Item(int id) => Items.GetValueOrDefault(id);
    public string? Champion(int id) => Champions.GetValueOrDefault(id);

    sealed record Cache(Dictionary<int, string> Items, Dictionary<int, string> Champions);

    /// <summary>Los nombres de ese idioma; sin internet ni caché, null (se quedan los de inglés).</summary>
    public static async Task<LocalizedNames?> LoadAsync(HttpClient http, string locale)
    {
        var path = Path.Combine(Paths.DataDir, $"nombres-{locale}.json");
        Cache? cache = null;
        static async Task<Cache?> Read(string file)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(file)) is { Items.Count: > 0 } c ? c : null : null; }
            catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        }
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-3)) cache = await Read(path);
        if (cache is null)
        {
            try
            {
                using var items = JsonDocument.Parse(await http.GetStringAsync($"{Base}/{locale}/v1/items.json"));
                using var champs = JsonDocument.Parse(await http.GetStringAsync($"{Base}/{locale}/v1/champion-summary.json"));
                cache = new Cache(
                    items.RootElement.EnumerateArray().Where(i => i.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 })
                        .GroupBy(i => i.GetProperty("id").GetInt32()).ToDictionary(g => g.Key, g => g.First().GetProperty("name").GetString()!.Trim()),
                    champs.RootElement.EnumerateArray().Where(c => c.GetProperty("id").GetInt32() > 0)
                        .ToDictionary(c => c.GetProperty("id").GetInt32(), c => c.GetProperty("name").GetString() ?? ""));
                Directory.CreateDirectory(Paths.DataDir);
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(cache));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException)
            {
                cache = await Read(path); // caducada, pero mejor que nada
            }
        }
        return cache is null ? null : new LocalizedNames { Locale = locale, Items = cache.Items, Champions = cache.Champions };
    }
}
