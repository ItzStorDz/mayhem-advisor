namespace MayhemAdvisor.Core;

/// <summary>Rutas de datos. Todo vive en %LOCALAPPDATA%\MayhemAdvisor.</summary>
public static class Paths
{
    // MAYHEM_DATA_DIR: solo para las pruebas (otra carpeta, para no tocar los datos ni los ajustes de verdad).
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("MAYHEM_DATA_DIR") is { Length: > 0 } test ? test : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MayhemAdvisor");

    public static string Matches => Path.Combine(DataDir, "matches.jsonl");
    // v5: descripciones de Mayhem (kiwi_) antes que las de Arena (cherry_), claves "*_desc" y palabras clave {{ ... }}.
    // v9 (1.3.2): las estadísticas de cada aumento se leen mejor (Mejora de Brillo, Eureka, Sobrecarga…) y entran los
    // aumentos con icono de Mayhem que faltaban (Ult Bot, High Roller…): hay que recalcular la caché.
    // v10 (1.6.1): el enfriamiento propio del aumento ya no lo hace "de habilidades" y los efectos que escalan con AD y AP
    // a la vez cuentan como fuerza adaptable (Rápido y Seguro, El Bum y el Bajo): hay que recalcular las estadísticas.
    public static string CatalogCache => Path.Combine(DataDir, "augments-v10.json");
}
