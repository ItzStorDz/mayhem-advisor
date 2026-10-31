namespace MayhemAdvisor.Core;

/// <summary>
/// Dónde están los archivos de datos: en <c>%LOCALAPPDATA%\MayhemAdvisor</c> o en la carpeta <c>datos\</c>
/// junto al ejecutable (la copia offline que viaja con la app y que actualiza <c>herramientas/actualizar-mayhem.mjs</c>).
/// Si están en los dos sitios gana el más nuevo: una copia vieja en AppData no debe tapar los datos recién bajados.
/// </summary>
public static class DataFiles
{
    public static string Resolve(string name)
    {
        var local = Path.Combine(Paths.DataDir, name);
        var bundled = Path.Combine(AppContext.BaseDirectory, "datos", name);
        bool hasLocal = File.Exists(local), hasBundled = File.Exists(bundled);
        if (hasLocal && hasBundled)
            return File.GetLastWriteTimeUtc(bundled) > File.GetLastWriteTimeUtc(local) ? bundled : local;
        return hasBundled ? bundled : local;
    }
}
