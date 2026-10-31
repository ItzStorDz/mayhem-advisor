namespace MayhemAdvisor.Core;

/// <summary>
/// Qué objetos pide o regala cada aumento, según su descripción:
///  • "Endurece tu Corazón" necesita Corazón de Acero; "Sombrero de Wooglet", Rabadon y Zhonya;
///    "Mejorar Filo del Infinito", el Filo del Infinito… → hay que comprarlos cuanto antes.
///  • "Campeón de Urf" regala la Espátula Dorada → no se compra (no está a la venta).
/// Se buscan los nombres de los objetos en la descripción del juego (español) y en la de mayhemmeta.com (inglés).
/// </summary>
public sealed class AugmentItemLinks
{
    readonly Dictionary<int, int[]> _required = [];
    readonly HashSet<int> _rewards = [];

    public AugmentItemLinks() { }

    /// <param name="englishNames">Nombres en inglés de los objetos (de las builds de mayhemmeta.com), para sus descripciones.</param>
    public AugmentItemLinks(IEnumerable<Augment> augments, GameData data, IEnumerable<(int Id, string Name)> englishNames)
    {
        // Objetos que se pueden comprar, por nombre; los nombres largos primero ("Reloj de Arena de Zhonya" antes que trozos).
        var buyable = data.Items.Values
            .Where(i => i.IsFinal && i.InStore && i.PriceTotal > 0 && !IsUnbuyableId(i.Id))
            .Select(i => (i.Id, i.Name))
            .Concat(englishNames.Where(n => data.Item(n.Id) is { IsFinal: true } && !IsUnbuyableId(n.Id)))
            .Where(n => n.Name.Length >= 4)
            .DistinctBy(n => n.Name)
            .OrderByDescending(n => n.Name.Length)
            .ToList();
        // Para las recompensas vale cualquier objeto con ese nombre, en español o en inglés (también los que no están a la venta).
        var anyByName = data.Items.Values.Select(i => (i.Id, i.Name)).Concat(englishNames)
            .GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Id).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        var rewardNames = anyByName.Keys.Where(k => k.Length >= 4).Select(k => (0, k)).ToList();

        var rewardCandidates = new HashSet<int>();
        foreach (var a in augments.Where(a => !a.IsAnvil))
        {
            var required = new List<int>();
            foreach (var text in new[] { a.Description, a.ExtraDescription }.Where(t => t != "").Select(WithoutConditions))
            {
                // "... Requisito: tener X. Recompensa: obtienes Y" → X se compra, Y te lo dan.
                int cut = IndexOfReward(text);
                var before = cut < 0 ? text : text[..cut];
                var after = cut < 0 ? "" : text[cut..];
                var asked = FindNames(before, buyable);
                required.AddRange(asked.Select(n => n.Id));
                // Lo que sale en la recompensa y no en el requisito (si sale en los dos, como el Corazón de Acero
                // de "Endurece tu Corazón", es que la recompensa lo mejora: hay que comprarlo, no te lo dan).
                // ("Obtienes la Espátula Dorada" es justo cómo se dice que te la dan: aquí no se salta.)
                foreach (var (_, name) in FindNames(after, rewardNames, reward: true))
                    if (!asked.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        foreach (var id in anyByName.GetValueOrDefault(name) ?? []) rewardCandidates.Add(id);
            }
            // "Mejora de Brillo" mejora todos los objetos con Brillo ("Spellblade" / "Espada Encantada"), sin nombrarlos.
            if (new[] { a.Description, a.ExtraDescription }.Any(t => t.Contains("Spellblade", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("efectos de Espada Encantada", StringComparison.OrdinalIgnoreCase)))
                required.AddRange(data.Items.Values.Where(i => i.IsFinal && i.InStore && i.PriceTotal > 0 && !IsUnbuyableId(i.Id) && BuildsFrom(data, i, SheenId)).Select(i => i.Id));
            if (required.Count > 0) _required[a.Id] = required.Distinct().ToArray();
        }
        // Un objeto que algún aumento pide comprar nunca es "solo recompensa".
        var askedAnywhere = _required.Values.SelectMany(ids => ids).ToHashSet();
        _rewards.UnionWith(rewardCandidates.Where(id => !askedAnywhere.Contains(id)));
    }

    public bool IsEmpty => _required.Count == 0 && _rewards.Count == 0;

    /// <summary>Objetos que pide el aumento (vacío si no pide ninguno concreto).</summary>
    public IReadOnlyList<int> RequiredBy(int augmentId) => _required.GetValueOrDefault(augmentId) ?? [];

    /// <summary>
    /// True si el objeto no se compra en Mayhem: es la recompensa de un aumento (Espátula Dorada, Sombrero de Wooglet…)
    /// o es una copia de Arena/eventos (ids 22xxxx y 99xxxx) que aparece en las estadísticas de quien la consiguió.
    /// </summary>
    public bool IsReward(int itemId) => IsUnbuyableId(itemId) || _rewards.Contains(itemId);

    static bool IsUnbuyableId(int id) => id / 10000 is 22 or 99;

    const int SheenId = 3057;

    /// <summary>¿Se fabrica el objeto a partir de ese componente (directamente o a través de otros)?</summary>
    static bool BuildsFrom(GameData data, ItemInfo item, int component, int depth = 0) =>
        depth < 4 && (item.From ?? []).Any(c => c == component || (data.Item(c) is { } part && BuildsFrom(data, part, component, depth + 1)));

    /// <summary>
    /// Quita las frases que hablan de cuándo sale el aumento, no de qué pide:
    /// "This augment is not offered if the champion has built Runaan's Hurricane."
    /// </summary>
    static string WithoutConditions(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text,
            @"[^.]*(not offered|is only available|no se ofrece|solo (?:est[aá] )?disponible)[^.]*\.?", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static int IndexOfReward(string text)
    {
        int es = text.IndexOf("recompensa", StringComparison.OrdinalIgnoreCase);
        int en = text.IndexOf("reward", StringComparison.OrdinalIgnoreCase);
        return es < 0 ? en : en < 0 ? es : Math.Min(es, en);
    }

    /// <summary>
    /// Nombres de objeto que salen en el texto, sin contar los que son trozo de otro más largo ya encontrado
    /// ni los que van justo después de "excepto"/"excluding" ("…Hidra (excepto Rompepuertas)").
    /// </summary>
    static List<(int Id, string Name)> FindNames(string text, List<(int Id, string Name)> names, bool reward = false)
    {
        var found = new List<(int Id, string Name)>();
        foreach (var n in names.OrderByDescending(n => n.Name.Length))
        {
            int at = text.IndexOf(n.Name, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || found.Any(f => f.Name.Contains(n.Name, StringComparison.OrdinalIgnoreCase))) continue;
            // "Obtienes Promesa Empírea como hechizo" / "Replace a summoner spell with …": te lo dan, no lo pide.
            var lead = text[Math.Max(0, at - 18)..at].ToLowerInvariant();
            if (!reward && new[] { "except", "excluding", "salvo", "obtien", "gain ", "receive", "spell with" }.Any(lead.Contains)) continue;
            found.Add(n);
        }
        return found;
    }
}
