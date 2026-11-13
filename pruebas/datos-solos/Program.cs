// ¿Se bajan solos los datos? Llama a DataUpdater como la app, en una carpeta de prueba, y dice qué archivos actualizó.
// Uso: MAYHEM_DATA_DIR=<carpeta vacía o con datos viejos> dotnet run --project pruebas\datos-solos -c Release
// La segunda vez seguida no debe bajar nada. Con MAYHEM_DATA_URL=<otra dirección> se prueba contra otro sitio.
using MayhemAdvisor.Core;

if (Environment.GetEnvironmentVariable("MAYHEM_DATA_DIR") is not { Length: > 0 }) { Console.WriteLine("Falta MAYHEM_DATA_DIR (no se toca la carpeta de verdad)."); return 1; }
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("MayhemAdvisor-pruebas/1.0");
for (int round = 1; round <= 2; round++)
{
    var result = await DataUpdater.CheckAsync(http);
    Console.WriteLine($"vuelta {round}: {result.Updated.Count} archivos · {(result.Problem is null ? "sin problemas" : "PROBLEMA: " + result.Problem)}");
    foreach (var name in result.Updated) Console.WriteLine("   " + name);
}
foreach (var file in Directory.GetFiles(Paths.DataDir, "*.json").Order())
    Console.WriteLine($"{Path.GetFileName(file),-30} {new FileInfo(file).Length,9} bytes  {File.GetLastWriteTimeUtc(file):yyyy-MM-dd HH:mm} UTC");
// Lo que carga la app con esos archivos.
Console.WriteLine($"AramHex: {ExternalAugments.Load(DataFiles.Resolve("aramhex-augments.json")).ChampionCount} campeones · builds: {MayhemBuilds.Load(DataFiles.Resolve("mayhem-champion-builds.json")).Champions.Count}");
return 0;
