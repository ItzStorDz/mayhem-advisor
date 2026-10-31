using MayhemAdvisor.Core;

// Recolector: copia tus partidas de Mayhem del historial del cliente de LoL (tiene que estar abierto).
// El overlay ya lo hace solo al terminar cada partida; esto sirve para hacerlo a mano.

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Carpeta de datos: {Paths.DataDir}");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var data = await GameData.LoadAsync(http);

try
{
    var saved = await MatchSync.SyncOwnHistoryAsync(data);
    if (saved is null)
    {
        Console.WriteLine("Abre el cliente de LoL e inicia sesión, y vuelve a intentarlo.");
        return 1;
    }
    var stats = StatsStore.Load(Paths.Matches, data);
    Console.WriteLine($"Partidas nuevas: {saved}. Total guardadas: {stats.MatchCount}.");
    return 0;
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    Console.WriteLine($"El cliente de LoL no respondió bien: {ex.Message}");
    return 1;
}
