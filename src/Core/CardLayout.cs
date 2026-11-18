namespace MayhemAdvisor.Core;

/// <summary>
/// Dónde pone el juego las cartas de aumento: centradas en su ventana, con los nombres en una misma fila. Las posiciones van
/// en partes del alto de la ventana, contadas desde su centro (−0,34 = a la izquierda del centro un 34 % del alto).
/// - 3 cartas (lo normal): en −0,34, 0 y +0,34 (790/1280/1770 con 2560x1440).
/// - 4 cartas (Headliner «Maestro», parche 26.23): a ±½ y ±1½ veces una separación que aún no hemos medido en el juego, así
///   que se deduce de lo leído (<see cref="FourGap"/>).
/// </summary>
public static class CardLayout
{
    public const double ThreeGap = 0.34;
    /// <summary>Lo que se puede desviar un nombre de su columna (las cartas de verdad, como mucho un 0,6 %).</summary>
    public const double Tolerance = 0.03;
    /// <summary>Separaciones posibles con 4 cartas (si fueran como las de 3, 0,34; más juntas para que quepan, menos).</summary>
    public const double MinFourGap = 0.18, MaxFourGap = 0.40;

    /// <summary>Distancia a la columna más cercana de las 3 cartas de siempre.</summary>
    public static double OffThree(double dx) => Math.Min(Math.Abs(dx), Math.Abs(Math.Abs(dx) - ThreeGap));

    /// <summary>Lo mismo con 4 cartas separadas <paramref name="gap"/>.</summary>
    public static double OffFour(double dx, double gap) => Math.Min(Math.Abs(Math.Abs(dx) - gap / 2), Math.Abs(Math.Abs(dx) - gap * 1.5));

    /// <summary>
    /// ¿Son 4 cartas? Con 3 o más nombres en la fila: la separación de 4 cartas que pone cada uno en una columna distinta, si
    /// alguno queda fuera de las columnas de 3 (un nombre en el centro siempre son 3 cartas). Null si no.
    /// Se prueba la distancia entre cada par de nombres dividida por 1, 2 y 3, y se queda la que mejor encaja.
    /// </summary>
    public static double? FourGap(IReadOnlyList<double> d)
    {
        if (d.Count < 3 || d.All(x => OffThree(x) <= Tolerance)) return null;
        double? best = null; double bestError = double.MaxValue;
        for (int i = 0; i < d.Count; i++)
            for (int j = i + 1; j < d.Count; j++)
                for (int k = 1; k <= 3; k++)
                {
                    double gap = Math.Abs(d[i] - d[j]) / k;
                    if (gap < MinFourGap || gap > MaxFourGap) continue;
                    double[] cols = [-1.5 * gap, -0.5 * gap, 0.5 * gap, 1.5 * gap];
                    var used = new HashSet<int>();
                    double error = 0; bool fits = true;
                    foreach (var x in d)
                    {
                        int c = Enumerable.Range(0, 4).MinBy(n => Math.Abs(x - cols[n]));
                        double off = Math.Abs(x - cols[c]);
                        if (off > Tolerance || !used.Add(c)) { fits = false; break; }
                        error = Math.Max(error, off);
                    }
                    if (fits && error < bestError) (best, bestError) = (gap, error);
                }
        return best;
    }
}
