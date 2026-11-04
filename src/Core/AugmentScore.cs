using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>
/// Nota de 0 a 100 de un aumento: el percentil de lo que sube tu % de victorias comparado con todos los
/// aumentos para tu campeón (98 = mejor que el 98 % de los aumentos). Con ella sale el rango (OP, S, A…).
/// </summary>
public sealed class AugmentScale
{
    readonly double[] _sorted;

    public AugmentScale(IEnumerable<double> values) => _sorted = values.Order().ToArray();

    /// <summary>Partidas propias a partir de las que una estimación sin datos globales se toma en serio.</summary>
    public const int ReliableGames = 20;
    /// <summary>Sin datos fiables la nota nunca pasa de A: no decimos "OP" de algo que casi no hemos visto.</summary>
    const double UnreliableCap = 87.9;

    public static bool IsReliable(Recommendation r) => r.Global || r.Games >= ReliableGames;

    /// <summary>
    /// Fiabilidad de la nota en 1-3 rayitas, por las partidas que la respaldan (como el medidor de Hexsight):
    /// 3 con 1.500+ partidas, 2 con 300+, 1 con menos o si es una estimación (media del aumento o pocas partidas tuyas).
    /// </summary>
    public static int Confidence(Recommendation r) => Confidence(r.Global && !r.FromAverage ? r.EvidenceGames : r.Games >= ReliableGames ? Math.Min(r.Games, 299) : 0);

    public static int Confidence(int games) => games >= 1500 ? 3 : games >= 300 ? 2 : 1;

    /// <summary>
    /// Escala de un campeón con los datos de todos los jugadores (la tabla de Mayhem): lo que sube o baja su % cada
    /// aumento con datos (30+ partidas). Para las páginas de la ventana principal, donde no hay equipo enemigo.
    /// </summary>
    public static AugmentScale FromTable(string alias, MayhemAugments table, AugmentCatalog catalog) =>
        new(catalog.Augments.Where(a => !a.IsAnvil).Select(a => table.For(alias, a.Id)).OfType<MayhemAugmentChampion>()
            .Where(r => r.Games >= 30).Select(r => r.Delta));

    /// <summary>
    /// Lo que cuenta la subida de % de un aumento para la nota: entera con datos de verdad; si es una estimación,
    /// según cuántas partidas tuyas la respaldan (sin partidas queda en la media). Si sale de la media del aumento con
    /// todos los campeones (mayhemmeta), ya está bien estimada: cuenta entera (pero sigue sin ser "fiable": no pasa de A).
    /// </summary>
    public static double Evidence(Recommendation r) => IsReliable(r) || r.FromAverage ? r.Delta : r.Delta * r.Games / (r.Games + ReliableGames);

    /// <summary>
    /// Escala para un campeón: lo que sube o baja su % cada aumento de Mayhem. Solo con aumentos que tienen
    /// datos de verdad para él (si hay bastantes), para que las estimaciones a ciegas no muevan la escala.
    /// </summary>
    public static AugmentScale For(string champion, DamageMix mix, StatsStore stats, AugmentCatalog catalog)
    {
        var all = catalog.Augments.Where(a => !a.IsAnvil).Select(a => stats.Rate(champion, a.Id, mix)).ToList();
        var reliable = all.Where(IsReliable).ToList();
        return new((reliable.Count >= 20 ? reliable : all).Select(r => r.Delta));
    }

    /// <param name="reliable">False si es una estimación sin datos del campeón: cuenta menos y no pasa de A.</param>
    public double Score(double value, bool reliable = true)
    {
        if (!reliable) return Math.Min(UnreliableCap, Score(value));
        if (_sorted.Length == 0) return 50;
        int below = LowerBound(value), upTo = UpperBound(value);
        double rank = below + (upTo - below) / 2.0;
        return Math.Clamp(Math.Round(100 * rank / _sorted.Length, 1), 0.1, 99.9);
    }

    int LowerBound(double v) { int lo = 0, hi = _sorted.Length; while (lo < hi) { int m = (lo + hi) / 2; if (_sorted[m] < v) lo = m + 1; else hi = m; } return lo; }
    int UpperBound(double v) { int lo = 0, hi = _sorted.Length; while (lo < hi) { int m = (lo + hi) / 2; if (_sorted[m] <= v) lo = m + 1; else hi = m; } return lo; }

    public static string Grade(double score) => score switch
    {
        >= 96 => "OP",
        >= 88 => "S",
        >= 70 => "A",
        >= 45 => "B",
        >= 25 => "C",
        _ => "D",
    };

    public static string Label(double score) => score switch
    {
        >= 96 => L("Rendimiento excepcional", "Exceptional"),
        >= 88 => L("Rendimiento muy alto", "Very strong"),
        >= 70 => L("Rendimiento alto", "Strong"),
        >= 45 => L("Rendimiento medio", "Average"),
        >= 25 => L("Rendimiento bajo", "Weak"),
        _ => L("Rendimiento muy bajo", "Very weak"),
    };
}
