namespace MayhemAdvisor.Core;

/// <summary>Un idioma de la app: código, nombre en ese idioma, cultura de los números y carpeta de CommunityDragon.</summary>
public sealed record AppLanguage(string Code, string Name, string Culture, string Locale);

/// <summary>
/// Idioma de lo que ve el usuario (Ajustes → Idioma). Desde la 1.8.5 hay 6: español, inglés, portugués (Brasil), francés,
/// alemán y vietnamita (un jugador lo pidió el 2026-11-08). La interfaz (ventana, panel, selección de campeón, bandeja) va en
/// los 6, y los nombres de objetos, campeones y aumentos salen del juego en cada idioma. Los textos que genera la app (los
/// porqués, los consejos) se escriben en español y en inglés donde se generan, <c>L("Mucho daño mágico", "Lots of magic
/// damage")</c>: con portugués, francés, alemán o vietnamita salen en inglés.
/// </summary>
public static class Lang
{
    public static readonly IReadOnlyList<AppLanguage> All =
    [
        new("es", "Español", "es-ES", "es_mx"),
        new("en", "English", "en-US", "default"),
        new("pt", "Português (Brasil)", "pt-BR", "pt_br"),
        new("fr", "Français", "fr-FR", "fr_fr"),
        new("de", "Deutsch", "de-DE", "de_de"),
        new("vi", "Tiếng Việt", "vi-VN", "vi_vn"),
    ];

    /// <summary>El idioma elegido ("es", "en", "pt", "fr", "de", "vi").</summary>
    public static string Code { get; private set; } = "es";

    /// <summary>Cambia el idioma (un código que no conocemos pasa a inglés).</summary>
    public static void Set(string? code)
    {
        Code = Normalize(code);
        // Los números con formato (59 % / 59%, 1.007 / 1,007) siguen al idioma elegido, no al de Windows.
        var culture = System.Globalization.CultureInfo.GetCultureInfo(Current.Culture);
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
    }

    public static string Normalize(string? code) => All.Any(l => l.Code == code) ? code! : "en";

    public static AppLanguage Current => All.First(l => l.Code == Code);

    /// <summary>
    /// True si los textos que genera la app van en inglés (todos los idiomas menos el español). Las pruebas lo siguen usando
    /// para elegir entre español e inglés.
    /// </summary>
    public static bool English
    {
        get => Code != "es";
        set => Set(value ? "en" : "es");
    }

    /// <summary>El idioma de los textos que genera la app: "es" o "en" (para el diario y el instalador, que solo tienen esos dos).</summary>
    public static string TextCode => English ? "en" : "es";

    /// <summary>
    /// Nombres de objetos y campeones del juego en el idioma elegido (portugués, francés, alemán, vietnamita); null en español
    /// e inglés, que van ya en <see cref="ItemInfo"/> y <see cref="ChampionInfo"/>. Lo carga la app al arrancar y al cambiar.
    /// </summary>
    public static LocalizedNames? Names { get; set; }

    public static string L(string es, string en) => English ? en : es;

    /// <summary>Un texto fijo en los 6 idiomas (para el menú de la bandeja y poco más); sin traducción, el inglés.</summary>
    public static string T(string es, string en, string pt, string fr, string de, string vi) => Code switch
    {
        "es" => es, "pt" => pt, "fr" => fr, "de" => de, "vi" => vi, _ => en,
    };
}
