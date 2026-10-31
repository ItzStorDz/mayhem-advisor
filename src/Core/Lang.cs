namespace MayhemAdvisor.Core;

/// <summary>
/// Idioma de lo que ve el usuario (Ajustes → Idioma). Los textos se escriben en los dos idiomas donde se generan:
/// <c>L("Mucho daño mágico", "Lots of magic damage")</c>. Los nombres de objetos, campeones y aumentos salen del juego.
/// </summary>
public static class Lang
{
    /// <summary>True = inglés; false = español (por defecto).</summary>
    public static bool English
    {
        get => _english;
        set
        {
            _english = value;
            // Los números con formato (59 % / 59%, 1.007 / 1,007) siguen al idioma elegido, no al de Windows.
            var culture = System.Globalization.CultureInfo.GetCultureInfo(value ? "en-US" : "es-ES");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
    static bool _english;

    public static string Code => English ? "en" : "es";

    public static string L(string es, string en) => English ? en : es;
}
