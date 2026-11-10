namespace MayhemAdvisor.Core;

public readonly record struct Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public Box Union(Box o)
    {
        int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }
}

public sealed record OcrLine(string Text, Box Bounds);
public sealed record AugmentHit(Augment Augment, Box Bounds, double Similarity);

/// <summary>
/// Busca nombres de aumentos de Mayhem entre las líneas leídas por el OCR.
/// Tolera pequeños errores de lectura y nombres partidos en dos líneas.
/// </summary>
public sealed class AugmentMatcher
{
    const double MinSimilarity = 0.82;
    readonly List<(Augment Aug, string Name)> _names = [];
    readonly Func<int, bool>? _inMayhem;

    /// <param name="inMayhem">Si el aumento sale en los datos de Mayhem. El catálogo del juego trae también los de Arena y
    /// los de modos antiguos, y algunos se llaman igual: «Icathia's Fall» es el 393 de Arena y el 1361 de Mayhem, y se leía
    /// el de Arena (sin datos ni objetos).</param>
    public AugmentMatcher(AugmentCatalog catalog, Func<int, bool>? inMayhem = null)
    {
        _inMayhem = inMayhem;
        foreach (var a in catalog.Augments)
            foreach (var n in a.Names.Values.Select(AugmentCatalog.Normalize).Distinct())
                if (n.Length >= 4) _names.Add((a, n));
    }

    public List<AugmentHit> Find(IReadOnlyList<OcrLine> lines)
    {
        // Candidatos: cada línea sola y cada línea unida con la de justo debajo (nombres largos que se parten).
        var candidates = new List<(string Text, Box Box)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            candidates.Add((AugmentCatalog.Normalize(a.Text), a.Bounds));
            for (int j = 0; j < lines.Count; j++)
            {
                var b = lines[j];
                bool below = b.Bounds.Y > a.Bounds.Y && b.Bounds.Y - a.Bounds.Bottom < a.Bounds.Height * 1.2;
                bool alignedX = Math.Abs((a.Bounds.X + a.Bounds.Right) / 2 - (b.Bounds.X + b.Bounds.Right) / 2) < a.Bounds.Height * 3;
                if (i != j && below && alignedX)
                    candidates.Add((AugmentCatalog.Normalize(a.Text + b.Text), a.Bounds.Union(b.Bounds)));
            }
        }

        var best = new Dictionary<int, AugmentHit>();
        foreach (var (text, box) in candidates)
        {
            if (text.Length < 4) continue;
            foreach (var (aug, name) in _names)
            {
                if (Math.Abs(text.Length - name.Length) > Math.Max(2, name.Length / 4)) continue;
                double sim = Similarity(text, name);
                if (sim < MinSimilarity) continue;
                if (!best.TryGetValue(aug.Id, out var prev) || sim > prev.Similarity)
                    best[aug.Id] = new AugmentHit(aug, box, sim);
            }
        }

        // Si dos aumentos caen en el mismo sitio, nos quedamos con el más parecido.
        // Con dos igual de parecidos (se llaman igual), el que sale en Mayhem.
        var hits = new List<AugmentHit>();
        foreach (var h in best.Values.OrderByDescending(h => h.Similarity).ThenByDescending(h => _inMayhem?.Invoke(h.Augment.Id) == true))
            if (!hits.Any(o => Overlaps(o.Bounds, h.Bounds))) hits.Add(h);

        // Los nombres de las cartas están todos en la misma fila. Un nombre de aumento que sale en la
        // descripción de otra carta (más abajo) no es una carta: nos quedamos con la fila más poblada.
        if (hits.Count >= 3)
        {
            var row = hits
                .Select(h => hits.Where(o => Math.Abs(o.Bounds.Y - h.Bounds.Y) <= Math.Max(h.Bounds.Height, o.Bounds.Height) * 1.5).ToList())
                .OrderByDescending(g => g.Count).ThenByDescending(g => g.Sum(x => x.Similarity))
                .First();
            if (row.Count >= 2) hits = row;
        }
        // Las cartas de una misma tanda son todas de la misma rareza. Si dos aumentos de Mayhem se llaman igual (en español
        // «¡Estadísticas sobre Estadísticas!» es uno de oro y otro prismático; en portugués «Sobrecarga»), el de la rareza
        // de las otras cartas.
        for (int i = 0; i < hits.Count && hits.Count >= 2; i++)
        {
            var h = hits[i];
            var others = hits.Where(o => o != h).GroupBy(o => o.Augment.Rarity).OrderByDescending(g => g.Count()).ToList();
            if (others.Count != 1 || others[0].Key == h.Augment.Rarity) continue; // las demás no coinciden entre sí, o ya encaja
            bool Known(AugmentHit x) => _inMayhem?.Invoke(x.Augment.Id) == true;
            var twin = best.Values.FirstOrDefault(o => o.Augment.Id != h.Augment.Id && o.Augment.Rarity == others[0].Key
                && Math.Abs(o.Similarity - h.Similarity) < 1e-9 && Overlaps(o.Bounds, h.Bounds) && (Known(o) || !Known(h)));
            if (twin is not null) hits[i] = twin;
        }
        return hits.OrderByDescending(h => h.Similarity).Take(4).OrderBy(h => h.Bounds.X).ToList();
    }

    static bool Overlaps(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>1 = idénticos, 0 = nada que ver (distancia de Levenshtein normalizada).</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
