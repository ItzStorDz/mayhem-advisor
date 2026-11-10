using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>Hechizos de invocador recomendados para un campeón, con datos de todos los jugadores (mayhemmeta.com).</summary>
/// <param name="WinRate">% de victorias con esa pareja (0-1).</param>
/// <param name="PickRate">% de jugadores de ese campeón que la llevan (0-1).</param>
/// <param name="Keys">Los dos hechizos con su nombre en inglés de los datos ("Flash", "Mark"), para compararlos.</param>
public sealed record SpellAdvice(IReadOnlyList<string> Names, IReadOnlyList<string> Icons, double WinRate, double PickRate, int Games, string Reason,
    IReadOnlyList<string>? Keys = null);

public static class SummonerSpells
{
    /// <summary>Nombre en inglés (como viene en los datos) → nombre en español e icono del juego.</summary>
    static readonly Dictionary<string, (string Name, string Icon)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flash"] = ("Destello", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_flash.png"),
        ["Mark"] = ("Marca", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_Mark.png"),
        ["Ghost"] = ("Fantasma", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_haste.png"),
        ["Ignite"] = ("Ignición", "/lol-game-data/assets/DATA/Spells/Icons2D/SummonerIgnite.png"),
        ["Barrier"] = ("Barrera", "/lol-game-data/assets/DATA/Spells/Icons2D/SummonerBarrier.png"),
        ["Heal"] = ("Curación", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_heal.png"),
        ["Clarity"] = ("Claridad", "/lol-game-data/assets/DATA/Spells/Icons2D/SummonerMana.png"),
        ["Cleanse"] = ("Purificación", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_boost.png"),
        ["Exhaust"] = ("Extenuación", "/lol-game-data/assets/DATA/Spells/Icons2D/Summoner_exhaust.png"),
    };

    /// <summary>Partidas "virtuales" con el % de la pareja más usada: con pocas partidas, otra no gana por una racha.</summary>
    const double Shrink = 1000;
    const int MinGames = 100;
    /// <summary>Lo que tiene que ganar (ya encogido) una pareja para recomendarla en vez de la más usada.</summary>
    const double MinGain = 0.008;

    /// <summary>La pareja con más % de victorias (con suficientes partidas para fiarse), o null sin datos.</summary>
    public static SpellAdvice? Recommend(MayhemChampionBuild? build)
    {
        if (build is not { Games: > 0 } || build.Spells is not { Count: > 0 }) return null;

        var combos = build.Spells
            .Where(c => c.WinRate.HasValue && c.PickRate.HasValue && c.Spells.Count == 2)
            .Select(c => (Combo: c, Games: build.Games.Value * c.PickRate!.Value / 100, Rate: c.WinRate!.Value / 100))
            .Where(c => c.Games >= MinGames)
            .ToList();
        if (combos.Count == 0) return null;

        // Lo normal es la pareja que lleva casi todo el mundo; otra solo si gana claramente, contando que con
        // pocas partidas su % se acerca al de la más usada (no nos fiamos de una racha).
        var popular = combos.MaxBy(c => c.Games);
        double Trust((MayhemSpellCombo Combo, double Games, double Rate) c) => (c.Rate * c.Games + popular.Rate * Shrink) / (c.Games + Shrink);
        var best = combos.MaxBy(Trust);
        if (Trust(best) - popular.Rate < MinGain) best = popular;
        string reason = best.Combo == popular.Combo
            ? LF($"la que lleva casi todo el mundo ({best.Combo.PickRate:0} %); ninguna otra gana claramente más", $"what almost everyone takes ({best.Combo.PickRate:0}%); nothing else clearly wins more")
            : LF($"gana {best.Rate - popular.Rate:+0.0%;-0.0%} más que {Pair(popular.Combo)}, la más usada ({popular.Combo.PickRate:0} %)", $"wins {best.Rate - popular.Rate:+0.0%;-0.0%} more than {Pair(popular.Combo)}, the most used ({popular.Combo.PickRate:0}%)");

        return new SpellAdvice(
            best.Combo.Spells.Select(SpellName).ToList(),
            best.Combo.Spells.Select(s => Known.TryGetValue(s, out var k) ? GameData.IconUrl(k.Icon) : "").ToList(),
            best.Rate, best.Combo.PickRate!.Value / 100, (int)best.Games, reason, best.Combo.Spells);
    }

    static string Pair(MayhemSpellCombo c) => string.Join(" + ", c.Spells.Select(SpellName));

    /// <summary>"Destello + Marca" en el idioma elegido, a partir de los nombres en inglés de los datos ("Flash", "Mark").</summary>
    public static string PairName(IEnumerable<string> spells) => string.Join(" + ", spells.Select(SpellName));
    /// <summary>Los datos traen el nombre en inglés ("Flash"); en español, el del juego ("Destello").</summary>
    static string SpellName(string s) => English ? s : Known.GetValueOrDefault(s).Name ?? s;
}
