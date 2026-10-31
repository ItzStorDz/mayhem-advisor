using static MayhemAdvisor.Core.Lang;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MayhemAdvisor.Core;

/// <param name="Description">Resumen en español, sin etiquetas (vacío si Riot no publica texto para ese aumento).</param>
/// <param name="Stats">Estadísticas que potencia, sacadas de su descripción (ver <see cref="StatTags"/>).</param>
/// <param name="ExtraDescription">Descripción de mayhemmeta.com (inglés), si la hay: sirve para saber qué objetos pide.</param>
public sealed record Augment(int Id, string ApiName, string Rarity, Dictionary<string, string> Names,
    string Description = "", string[]? Stats = null, string Icon = "", string ExtraDescription = "")
{
    /// <summary>True si no es un aumento sino un fragmento de Yunque de Estadísticas.</summary>
    public bool IsAnvil => Rarity == Anvil.Rarity;
    public string Name => RealName("es_mx") ?? RealName("default") ?? Fallback;
    /// <summary>El nombre en el idioma elegido: "default" es el inglés del juego.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Lang.English ? RealName("default") ?? Name : Name;
    /// <summary>Descripción en el idioma elegido (en inglés, la de mayhemmeta.com si la hay).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayDescription => Lang.English && ExtraDescription != "" ? ExtraDescription : Description;
    public string[] StatList => Stats ?? [];
    /// <summary>True si sella tu definitiva (De Vuelta a lo Básico).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SealsUltimate => ApiName.Equals("ARAM_BacktoBasics", StringComparison.OrdinalIgnoreCase);
    /// <summary>True si su efecto sale de lanzar la definitiva o de su enfriamiento: con la definitiva sellada no hace nada.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsUltimate => UltimateAugments.Contains(ApiName);

    // Sacados de las descripciones que hablan de la definitiva. No están Francotirador Hábil (es para las otras
    // habilidades), A Girar (habilidades giratorias) ni Reina del Baile (te da la definitiva de Rakan).
    static readonly HashSet<string> UltimateAugments = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARAM_CantTouchThis", "ARAM_UltimateRevolution", "ARAM_UltimateUnstoppable", "ARAM_FeyMagic", "ARAM_ItsKillingTime",
        "FinalForm", "UltimateAwakening", "ARAM_StuckInHereWithMe", "SkipTheBasics", "UltimateRecursion", "ArcaneAxiom",
    };

    // Algún aumento de evento viene sin nombre (en blanco o "GoH_PentakillAug_Generic_BlankName"): se usa el del evento.
    string? RealName(string locale) =>
        Names.GetValueOrDefault(locale) is { } n && !string.IsNullOrWhiteSpace(n) && !n.EndsWith("_BlankName", StringComparison.OrdinalIgnoreCase) ? n : null;
    string Fallback => ApiName.StartsWith("GoH_PentakillAug", StringComparison.OrdinalIgnoreCase) ? "Pentakill" : ApiName;
}

/// <summary>
/// Lista de aumentos de Mayhem (modo KIWI) con nombres en inglés y español, descripción e icono,
/// descargada de CommunityDragon (datos públicos del juego) y guardada en caché 3 días.
/// </summary>
public sealed class AugmentCatalog
{
    const string Base = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global";
    const string StringTable = "https://raw.communitydragon.org/latest/game/es_mx/data/menu/en_us/lol.stringtable.json";
    static readonly string[] Locales = ["default", "es_mx", "es_es"];

    public IReadOnlyList<Augment> Augments { get; }
    readonly Dictionary<int, Augment> _byId;

    AugmentCatalog(List<Augment> augments)
    {
        Augments = augments;
        _byId = augments.ToDictionary(a => a.Id);
    }

    public Augment? Get(int id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Completa con las descripciones de mayhemmeta.com (en inglés) los aumentos que el juego deja sin texto
    /// o sin estadísticas reconocidas, para que también se sepa con qué objetos van. Las que ya hay no se tocan.
    /// </summary>
    public AugmentCatalog WithExtraDescriptions(IReadOnlyDictionary<int, string> extra)
    {
        if (extra.Count == 0) return this;
        return new AugmentCatalog(Augments.Select(a =>
        {
            if (a.IsAnvil || !extra.TryGetValue(a.Id, out var text) || text == "") return a;
            var stats = a.StatList.Union(StatTags.FromDescription(text)).ToArray();
            return a with { Description = a.Description != "" ? FillValues(a.Description, text) : text, Stats = stats, ExtraDescription = text };
        }).ToList());
    }

    /// <summary>
    /// El texto en español trae "X" donde el juego pone un número; la descripción en inglés de mayhemmeta.com trae
    /// los números de verdad. Si hay tantos números como "X", se ponen en orden ("Obtienes 60 de aceleración + 3 por…").
    /// Si no coinciden, se deja la "X" para no poner un número en el sitio equivocado.
    /// </summary>
    static string FillValues(string spanish, string english)
    {
        var slots = Regex.Matches(spanish, @"\bX\b");
        var numbers = Regex.Matches(english, @"\d+(?:\.\d+)?");
        if (slots.Count == 0 || slots.Count != numbers.Count) return spanish;
        int i = 0;
        return Regex.Replace(spanish, @"\bX\b", _ => numbers[i++].Value.Replace('.', ','));
    }

    public static async Task<AugmentCatalog> LoadAsync(HttpClient http, bool forceRefresh = false)
    {
        var cache = Paths.CatalogCache;
        if (!forceRefresh && File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-3))
        {
            var cached = JsonSerializer.Deserialize<List<Augment>>(await File.ReadAllTextAsync(cache));
            if (cached is { Count: > 0 }) return new AugmentCatalog(cached);
        }

        try
        {
            var list = await DownloadAsync(http);
            Directory.CreateDirectory(Paths.DataDir);
            await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(list));
            return new AugmentCatalog(list);
        }
        catch when (OfflineCache(cache) is not null)
        {
            // Sin internet: usamos la caché vieja aunque esté caducada (o la de la versión anterior de la app, recién actualizada).
            return new AugmentCatalog(JsonSerializer.Deserialize<List<Augment>>(await File.ReadAllTextAsync(OfflineCache(cache)!))!);
        }
    }

    /// <summary>La caché de aumentos que haya: la actual o, si aún no existe, la más nueva de versiones anteriores (augments-v6…).</summary>
    static string? OfflineCache(string current) =>
        File.Exists(current) ? current
        : Directory.Exists(Paths.DataDir) ? Directory.GetFiles(Paths.DataDir, "augments-v*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    static async Task<List<Augment>> DownloadAsync(HttpClient http)
    {
        // Qué aumentos pertenecen a Mayhem (modo "KIWI").
        using var lists = JsonDocument.Parse(await http.GetStringAsync($"{Base}/default/v1/augment-lists.json"));
        var kiwiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists.RootElement.EnumerateArray())
        {
            if (!list.GetProperty("modeName").GetString()!.StartsWith("KIWI", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var path in list.GetProperty("augmentList").EnumerateArray())
                kiwiNames.Add(path.GetString()!.Split('/').Last());
        }

        var result = new Dictionary<int, Augment>();
        foreach (var locale in Locales)
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{Base}/{locale}/v1/cherry-augments.json"));
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                var apiName = a.GetProperty("augmentNameId").GetString() ?? "";
                // "GoH_" = aumentos de evento (p. ej. mejoras de Q/W/E/R "HEAVY", "POWER"...) que salen en Mayhem
                // aunque Riot no los incluya en la lista oficial del modo. Igual los que tienen icono de Mayhem (".../Kiwi/..."):
                // Ult Bot, High Roller, Crit 'n Cast o Poro Stampede se juegan en Mayhem y faltaban.
                bool kiwiIcon = (a.GetProperty("augmentSmallIconPath").GetString() ?? "").Contains("/Kiwi/", StringComparison.OrdinalIgnoreCase);
                if (!kiwiNames.Contains(apiName) && !kiwiIcon && !apiName.StartsWith("GoH_", StringComparison.OrdinalIgnoreCase)) continue;
                var id = a.GetProperty("id").GetInt32();
                var name = a.GetProperty("nameTRA").GetString() ?? apiName;
                if (!result.TryGetValue(id, out var aug))
                    result[id] = aug = new Augment(id, apiName, a.GetProperty("rarity").GetString() ?? "", [],
                        Icon: GameData.IconUrl(a.GetProperty("augmentSmallIconPath").GetString() ?? ""));
                aug.Names[locale] = name;
            }
        }

        // Descripciones: vienen en la tabla de textos del juego (es grande, ~35 MB, por eso la caché dura 3 días).
        var (texts, anvils) = await LoadTextsAsync(http);
        var augments = result.Values
            .Select(a =>
            {
                var raw = FindDescription(texts, a.ApiName) is { } found ? ResolveKeywords(found, texts) : null;
                return a with { Description = raw is null ? "" : Clean(raw), Stats = raw is null ? [] : StatTags.FromDescription(raw) };
            })
            .OrderBy(a => a.Id);

        // Fragmentos de los Yunques de Estadísticas: se tratan como "aumentos" especiales (id negativo, rareza "anvil").
        var fragments = anvils.OrderBy(kv => kv.Key).Select((kv, i) => new Augment(
            -1000 - i, "Anvil_" + kv.Key, Anvil.Rarity,
            // También en inglés: con el juego en inglés, si no, las cartas del yunque no se reconocían.
            Anvil.EnglishName(kv.Key) is { } en ? new() { ["es_mx"] = kv.Value.Name, ["default"] = en } : new() { ["es_mx"] = kv.Value.Name },
            Description: Clean(kv.Value.Tooltip), Stats: Anvil.TagsFor(kv.Key)));
        return augments.Concat(fragments).ToList();
    }

    static async Task<(Dictionary<string, string> Texts, Dictionary<string, (string Name, string Tooltip)> Anvils)> LoadTextsAsync(HttpClient http)
    {
        var texts = new Dictionary<string, string>();
        var ranks = new Dictionary<string, int>();
        // Mayhem ("kiwi_") antes que Arena ("cherry_"): algunos aumentos existen en los dos modos con valores distintos.
        static int TextRank(string mode, string kind) => (mode == "kiwi" ? 0 : 3) + kind switch { "summary" => 0, "desc" => 1, _ => 2 };
        var anvilNames = new Dictionary<string, string>();
        var anvilTips = new Dictionary<string, string>();
        try
        {
            using var stream = await http.GetStreamAsync(StringTable);
            using var doc = await JsonDocument.ParseAsync(stream);
            // kiwi_aram_augment_terror_desc, cherry_mysticpunch_summary, kiwi_aram_omnisoul_tooltip...
            var pattern = new Regex(@"^(kiwi|cherry)_(?:aram_)?(?:augment_)?(.+?)_(summary|desc|tooltip)$");
            var anvil = new Regex(@"^cherry_(?:gold|pris)?statanvil_([a-z0-9]+?)(_name|_tooltip)?$");
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateObject())
            {
                var value = e.Value.GetString() ?? "";
                if (anvil.Match(e.Name) is { Success: true } am)
                {
                    // "armorpen" no tiene sufijo _name: el texto sin sufijo es el nombre.
                    if (am.Groups[2].Value == "_tooltip") anvilTips.TryAdd(am.Groups[1].Value, value);
                    else anvilNames.TryAdd(am.Groups[1].Value, value);
                    continue;
                }
                // Palabras clave que salen dentro de las descripciones: {{ Item_Keyword_OnHit }} = "al impacto".
                if (e.Name.StartsWith("item_keyword_", StringComparison.Ordinal)) { texts[e.Name] = value; continue; }
                var m = pattern.Match(e.Name);
                if (!m.Success) continue;
                // Preferimos el resumen ("summary"), luego la descripción corta ("desc") y por último el texto largo ("tooltip").
                var key = m.Groups[2].Value;
                int rank = TextRank(m.Groups[1].Value, m.Groups[3].Value);
                if (!ranks.TryGetValue(key, out var old) || rank < old) { texts[key] = value; ranks[key] = rank; }
            }
        }
        catch (HttpRequestException) { /* sin descripciones: el resto sigue funcionando */ }

        var anvils = anvilNames.Where(kv => kv.Value.Trim().Length >= 4)
            .ToDictionary(kv => kv.Key, kv => (Clean(kv.Value), anvilTips.GetValueOrDefault(kv.Key, "")));
        return (texts, anvils);
    }

    static string? FindDescription(Dictionary<string, string> texts, string apiName)
    {
        var key = apiName.ToLowerInvariant();
        var shortKey = key.StartsWith("aram_") ? key[5..] : key;
        return texts.GetValueOrDefault(shortKey) ?? texts.GetValueOrDefault(key) ?? texts.GetValueOrDefault("aram_" + shortKey);
    }

    /// <summary>Cambia {{ Item_Keyword_OnHit }} por su texto ("al impacto"); las que no conocemos se quitan.</summary>
    static string ResolveKeywords(string raw, Dictionary<string, string> texts) =>
        Regex.Replace(raw, @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", m => texts.GetValueOrDefault(m.Groups[1].Value.ToLowerInvariant()) ?? "");

    /// <summary>Quita etiquetas y cambia los valores variables (@Algo@) por "X" para mostrarlo.</summary>
    static string Clean(string raw)
    {
        var s = Regex.Replace(raw, "<br\\s*/?>", " ");
        s = Regex.Replace(s, "<[^>]+>", "");
        s = Regex.Replace(s, "@[^@]+@", "X");
        s = Regex.Replace(s, "%i:[^%]+%", ""); // iconos en línea ("%i:OnHit%")
        s = Regex.Replace(s, "\\{\\{[^}]+\\}\\}", "");
        return Regex.Replace(s, "\\s+", " ").Trim();
    }

    /// <summary>Pasa un texto a minúsculas, sin acentos ni signos, para comparar con lo que lee el OCR.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Estadísticas que menciona la descripción de un aumento y qué categorías de objeto las dan.
/// Así podemos decir "este aumento va bien con estos objetos" usando solo datos del juego.
/// </summary>
public static class StatTags
{
    public static readonly Dictionary<string, (string Label, string[] ItemCategories)> All = new()
    {
        ["AD"] = ("daño de ataque", ["Damage"]),
        ["AP"] = ("poder de habilidad", ["SpellDamage"]),
        ["AS"] = ("velocidad de ataque", ["AttackSpeed"]),
        ["Crit"] = ("crítico", ["CriticalStrike"]),
        ["Health"] = ("vida", ["Health"]),
        ["Armor"] = ("armadura", ["Armor"]),
        ["MR"] = ("resistencia mágica", ["SpellBlock"]),
        ["Haste"] = ("prisa de habilidad", ["AbilityHaste", "CooldownReduction"]),
        ["Lethality"] = ("letalidad", ["ArmorPenetration"]),
        ["MagicPen"] = ("penetración mágica", ["MagicPenetration"]),
        ["Sustain"] = ("curación", ["LifeSteal", "SpellVamp"]),
        ["Mana"] = ("maná", ["Mana"]),
        ["OnHit"] = ("efectos al impactar", ["OnHit", "AttackSpeed"]),
        ["MoveSpeed"] = ("velocidad de movimiento", ["NonbootsMovement"]),
        ["Shield"] = ("escudos", ["Health"]),
        ["Tenacity"] = ("tenacidad", ["Tenacity"]),
        ["ItemHaste"] = ("objetos con activa", ["Active"]),
        ["Slow"] = ("ralentizaciones", ["Slow"]),
        // Aumentos de habilidades: prisa + el daño del campeón (AP o AD, ver ItemCategories).
        ["Abilities"] = ("habilidades", ["AbilityHaste", "CooldownReduction"]),
    };

    // Textos en minúsculas y sin acentos. Español (juego) e inglés (descripciones de mayhemmeta.com).
    static readonly (string Tag, string[] Patterns)[] Rules =
    [
        ("AD", ["<scalead", "dano de ataque", "attack damage", "bonus ad", " ad ", " ad)", " ad,"]),
        ("AP", ["<scaleap", "poder de habilidad", "ability power", "bonus ap", " ap ", " ap)", " ap,"]),
        ("AS", ["velocidad de ataque", "attack speed"]),
        ("Crit", ["critic"]),
        ("Health", ["<scalehealth", "vida maxima", "vida adicional", "de tu vida", "maximum health", "max health", "bonus health"]),
        ("Armor", ["<scalearmor", "armadura", "armor"]),
        ("MR", ["<scalemr", "resistencia magica", "magic resist"]),
        ("Haste", ["<scaleabilityhaste", "prisa de habilidad", "aceleracion de habilidad", "ability haste"]),
        ("Lethality", ["<scalelethality", "letalidad", "penetracion de armadura", "lethality", "armor penetration"]),
        ("MagicPen", ["penetracion magica", "magic penetration"]),
        ("Sustain", ["robo de vida", "omnivampirismo", "curacion", "te curas", "cura ", "life steal", "lifesteal", "omnivamp", "heals", "healing", "heal "]),
        ("Mana", ["<scalemana", " mana"]),
        ("OnHit", ["al impactar", "al impacto", "ataques basicos", "tus ataques", "on-hit", "on hit", "basic attack", "your attacks"]),
        ("MoveSpeed", ["velocidad de movimiento", "movement speed", "move speed"]),
        ("Shield", ["escudo", "shield"]),
        ("Tenacity", ["tenacidad", "tenacity"]),
        ("ItemHaste", ["aceleracion de objetos", "item haste", "item active"]),
        ("Slow", ["ralentiz", "slow"]),
        ("Abilities", ["tus habilidades", "lanzar una habilidad", "lanzas una habilidad", "tu definitiva", "enfriamiento", "your abilities",
                       "casting an ability", "cast an ability", "ultimate", "cooldown"]),
    ];

    public static string[] FromDescription(string raw)
    {
        // Minúsculas y sin acentos, pero conservando espacios y "<" para reconocer etiquetas como <scaleAP>.
        var text = new string(raw.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant).ToArray());
        // "Penetración de armadura" es letalidad, no armadura: se cambia antes para que no cuente como "armadura".
        text = text.Replace("armor penetration", "lethality").Replace("penetracion de armadura", "letalidad");
        // Reducir la armadura / resistencia mágica del enemigo es como penetración, no aguante (p. ej. Erosión).
        // W(): entre palabra y palabra puede haber etiquetas del juego ("reducción de <scaleArmor>Armadura").
        const string reduce = "(reduccion|reducen?|reducir|reduciendo|reduces|reduce|reducing|shreds?)( de| la| su| sus| a la| their| enemy| the target's)?";
        text = Regex.Replace(text, W(reduce + " armadura( y (de )?(la )?resistencia magica)?"), m => " letalidad" + (m.Groups[3].Success ? " y penetracion magica " : " "));
        text = Regex.Replace(text, W(reduce + " resistencia magica"), " penetracion magica ");
        text = Regex.Replace(text, W(reduce + " armor and magic resist(ance)?") + "|" + W("armor and magic resist(ance)? reduction"), " lethality and magic penetration ");
        text = Regex.Replace(text, W(reduce + " armor") + "|" + W("armor reduction"), " lethality ");
        text = Regex.Replace(text, W(reduce + " magic resist(ance)?") + "|" + W("magic resist(ance)? reduction"), " magic penetration ");
        // Daño según la vida máxima DEL ENEMIGO no pide vida (Espada de Fuego, Misil Mágico…); según la tuya, sí.
        text = Regex.Replace(text, W("(de su|infliges) vida maxima") + "|" + W("vida maxima (del enemigo|de los enemigos|del objetivo|de su objetivo)")
            + "|(?<!tu )" + W("vida maxima como dano"), " dano por vida ");
        text = Regex.Replace(text, W("(their|target's|enemy's|enemies'|the target's) (max|maximum) health") + "|(?<!your )" + W("(max|maximum) health (as|damage)"), " health damage ");
        // Una curación según tu vida máxima (Mejora de Brillo: "heal you for 3.5% of your maximum health") es curación, no pide vida.
        text = Regex.Replace(text, @"(heal(s|ing)?|cura(s|n)?)\b(?:[^.<]|\.\d){0,30}?(of your|de tu)(\s|<[^>]*>)+(max|maximum)?(\s|<[^>]*>)*(health|vida maxima)", " healing ");
        // Lo que el aumento te DA a cambio de otra estadística no pide objetos de eso: Eureka ("obtienes aceleración de
        // habilidad equivalente a un 30% de tu poder de habilidad") pide poder de habilidad, no aceleración.
        text = Regex.Replace(text, @"\b(obtienes|gain|gains|grants)(?:[a-z' ]|<[^>]*>){1,60}?\b(equivalente a|equal to)\b", "$1 $2");
        // Más potencia a tus curaciones y escudos (Sobrecarga, De Vuelta a lo Básico) no pide vida ni robo de vida.
        // (El juego los envuelve en etiquetas <healing>…</healing>, <shield>…</shield>, que también se quitan.)
        text = Regex.Replace(text, @"(?:<[^>]*>)*(?:" + W("healing and shield(ing|s)") + "|" + @"curaci(on|ones)(?:<[^>]*>)*," + W(" escudos") + @")(?:<[^>]*>)*",
            " potencia de sanacion ");
        var tags = Rules.Where(r => r.Patterns.Any(text.Contains)).Select(r => r.Tag).ToList();
        // "Fuerza adaptable" sirve a daño de ataque o a poder de habilidad, según el campeón.
        if (text.Contains("<scaleaf") || text.Contains("fuerza adaptable")) tags.Add("Adaptive");
        return tags.Distinct().ToArray();
    }

    /// <summary>Patrón de varias palabras que admite espacios y etiquetas (&lt;scaleArmor&gt;…) entre ellas.</summary>
    static string W(string words) => words.Replace(" ", @"(?:\s|<[^>]*>)+");

    public static string Label(string tag) => English
        ? (tag == "Adaptive" ? "adaptive force" : EnglishLabels.GetValueOrDefault(tag) ?? tag)
        : (tag == "Adaptive" ? "fuerza adaptable" : All.GetValueOrDefault(tag).Label ?? tag);

    static readonly Dictionary<string, string> EnglishLabels = new()
    {
        ["AD"] = "attack damage", ["AP"] = "ability power", ["AS"] = "attack speed", ["Crit"] = "crit", ["Health"] = "health",
        ["Armor"] = "armor", ["MR"] = "magic resist", ["Haste"] = "ability haste", ["Lethality"] = "lethality", ["MagicPen"] = "magic penetration",
        ["Sustain"] = "healing", ["Mana"] = "mana", ["OnHit"] = "on-hit effects", ["MoveSpeed"] = "move speed", ["Shield"] = "shields",
        ["Tenacity"] = "tenacity", ["ItemHaste"] = "active items", ["Slow"] = "slows", ["Abilities"] = "abilities",
    };

    /// <summary>Categorías de objeto que encajan con un aumento; la fuerza adaptable depende de si el campeón es AP o AD.</summary>
    public static IEnumerable<string> ItemCategories(IEnumerable<string> tags, bool championIsMagic) =>
        tags.SelectMany(t => t switch
        {
            "Adaptive" => [championIsMagic ? "SpellDamage" : "Damage"],
            "Abilities" => [.. All["Abilities"].ItemCategories, championIsMagic ? "SpellDamage" : "Damage"],
            _ => All.GetValueOrDefault(t).ItemCategories ?? [],
        });
}
