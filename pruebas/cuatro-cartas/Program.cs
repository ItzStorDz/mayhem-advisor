// ¿Reconoce la app 4 cartas (Headliner «Maestro», parche 26.23) sin confundirlas con las 3 de siempre ni con texto suelto?
// Prueba src/Core/CardLayout.cs con posiciones de mentira (en partes del alto, desde el centro). No necesita datos.
//   dotnet run --project pruebas\cuatro-cartas -c Release
using MayhemAdvisor.Core;

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok     " : "FALLO  ") + what); if (!ok) fails++; }
double[] Four(double gap) => [-1.5 * gap, -0.5 * gap, 0.5 * gap, 1.5 * gap];
var rng = new Random(23);
double Noise() => (rng.NextDouble() * 2 - 1) * 0.008; // las cartas de verdad se desvían como mucho un 0,6 %

Check(CardLayout.FourGap([-0.34, 0, 0.34]) is null, "3 cartas de siempre: no son 4");
Check(CardLayout.FourGap([-0.34, 0.34]) is null && CardLayout.FourGap([-0.34, 0.005, 0.33]) is null, "3 cartas leídas a medias o algo movidas: no son 4");
Check(CardLayout.FourGap([-0.27, 0, 0.27]) is null, "con un nombre en el centro nunca son 4");
Check(CardLayout.FourGap([-0.405, -0.135]) is null, "con solo 2 nombres no se decide (pueden ser de otro overlay)");

foreach (var gap in new[] { 0.19, 0.22, 0.25, 0.27, 0.30, 0.34, 0.38 })
{
    var cols = Four(gap);
    int all = 0, three = 0, tries = 200;
    for (int t = 0; t < tries; t++)
    {
        if (CardLayout.FourGap(cols.Select(c => c + Noise()).ToList()) is { } g && Math.Abs(g - gap) < 0.02) all++;
        // Solo 3 de las 4 leídas (cualquiera puede faltar)
        int skip = t % 4;
        if (CardLayout.FourGap(cols.Where((_, i) => i != skip).Select(c => c + Noise()).ToList()) is { } g3 && Math.Abs(g3 - gap) < 0.03) three++;
    }
    Check(all == tries && three >= tries * 0.95, $"4 cartas separadas {gap:0.00}: las 4 leídas {all}/{tries}, solo 3 leídas {three}/{tries}");
    // Una vez medida la separación, cada carta cae en su columna y ninguna en las de 3 por casualidad... salvo si coinciden.
    Check(cols.All(c => CardLayout.OffFour(c, gap) < 1e-9), $"  las 4 columnas con {gap:0.00} son las esperadas");
}

// Texto suelto en la fila de las cartas (otro overlay, el marcador...): 3 nombres al azar. ¿Cuántas veces pasa por 4 cartas?
int fake = 0, n = 20000;
for (int t = 0; t < n; t++)
{
    var d = Enumerable.Range(0, 3).Select(_ => rng.NextDouble() * 1.4 - 0.7).ToList();
    if (CardLayout.FourGap(d) is not null) fake++;
}
Console.WriteLine($"       3 nombres en sitios al azar que encajan como 4 cartas: {(double)fake / n:P1} (además hace falta que sean nombres de aumentos y en la fila)");
Check((double)fake / n < 0.05, "texto suelto casi nunca pasa por 4 cartas");

Console.WriteLine(fails == 0 ? "TODO BIEN" : $"{fails} FALLOS");
return fails;
