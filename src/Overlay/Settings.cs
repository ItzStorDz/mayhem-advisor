using System.Text.Json;
using MayhemAdvisor.Core;

namespace MayhemAdvisor.Overlay;

/// <summary>Ajustes del usuario (pestaña Ajustes de la ventana principal y menú de la bandeja), en settings.json.</summary>
public sealed class Settings
{
    /// <summary>Pulsar "Aceptar" solo cuando sale "¡Partida encontrada!".</summary>
    public bool AutoAccept { get; set; }
    /// <summary>Con AutoAccept: solo en colas de ARAM: Caos (las demás se dejan para que las aceptes tú).</summary>
    public bool AutoAcceptOnlyMayhem { get; set; } = true;
    /// <summary>Idioma de la app y del panel: "es" o "en". La primera vez, el de Windows (español si es español; si no, inglés).</summary>
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
    /// <summary>Al salir las cartas, el panel salta solo a la pestaña "Aumentos".</summary>
    public bool AutoAugmentsTab { get; set; } = true;
    /// <summary>Opciones de administrador (p. ej. la carpeta de datos con el registro): solo en el PC del creador.</summary>
    public bool Admin { get; set; }
    /// <summary>Mostrar el panel lateral en la partida.</summary>
    public bool ShowPanel { get; set; } = true;
    /// <summary>En la selección de campeón, escribir la build de la app en la tienda del juego (item set).</summary>
    public bool ShopItemSet { get; set; } = true;
    /// <summary>Enviar partidas y errores anónimos para mejorar la app (ver Cloud.cs y PRIVACY.md). Se puede quitar.</summary>
    public bool ShareData { get; set; } = true;
    /// <summary>Ya se enseñó el aviso de los datos anónimos (hasta entonces no se envía nada).</summary>
    public bool DataNoticeShown { get; set; }

    static string FilePath => Path.Combine(Paths.DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { Log.Write($"No pude leer los ajustes: {ex.Message}"); return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException ex) { Log.Write($"No pude guardar los ajustes: {ex.Message}"); }
    }
}
