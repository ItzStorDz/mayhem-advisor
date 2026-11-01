using static MayhemAdvisor.Core.Lang;

namespace MayhemAdvisor.Core;

/// <summary>Hacia dónde empuja la build: daño físico, mágico, aguante o una mezcla.</summary>
public enum BuildKind { AD, AP, Tank, HybridAdAp, HybridApTank, HybridAdTank }

/// <summary>Un objeto recomendado para la dirección, con su % de victorias con tu campeón (null = sin datos).</summary>
public sealed record DirectionItem(ItemInfo Item, double? WinRate, int Games);

/// <summary>
/// "Tus aumentos apuntan a…": la dirección de build que piden tus aumentos (y tus objetos), por qué, 3 objetos para
/// seguirla y un aviso si choca con lo normal del campeón (p. ej. un aumento de AP con un tirador).
/// </summary>
public sealed record BuildDirection(BuildKind Kind, string Label, string Reason, string? Warning, IReadOnlyList<DirectionItem> Items);

public static class BuildDirections
{
    /// <summary>Cuánto empuja un aumento hacia AP, AD y tanque (por lo que da y con qué escala: &lt;scaleAP&gt;, &lt;scaleHealth&gt;…).</summary>
    public static (double Ap, double Ad, double Tank) Lean(Augment a, bool championIsMagic)
    {
        double ap = 0, ad = 0, tank = 0;
        // Reduce armadura y resistencia mágica a la vez (Erosión): sirve a cualquier daño, cuenta como el del campeón.
        bool bothPen = a.StatList.Contains("Lethality") && a.StatList.Contains("MagicPen");
        foreach (var t in a.StatList)
            switch (t)
            {
                case "MagicPen" or "Lethality" when bothPen: if (t == "MagicPen") { if (championIsMagic) ap += 0.8; else ad += 0.8; } break;
                case "AP": case "MagicPen": ap += 1; break;
                case "AD": case "Lethality": case "Crit": ad += 1; break;
                case "AS": ad += 0.6; break;
                case "OnHit": ad += 0.4; ap += 0.3; break;
                case "Adaptive": if (championIsMagic) ap += 0.8; else ad += 0.8; break;
                case "Health": case "Armor": case "MR": tank += 1; break;
                // Un escudo solo (De Vuelta a lo Básico, Protección: escudos para aliados) no hace tanque a nadie.
                case "Shield": tank += 0.4; break;
                case "Tenacity": tank += 0.5; break;
            }
        // Un aumento cuenta como mucho 1 hacia tanque (Agallas del Forajido da armadura Y resist. mágica, pero es un aumento).
        // Solo vida o escudo (Locomotora, Plan de Escape, De Mente a Materia) empuja menos que armadura / resist. mágica.
        bool resists = a.StatList.Any(t => t is "Armor" or "MR" or "Tenacity");
        tank = Math.Min(tank, 1) * (resists ? 1 : 0.85);
        return (ap, ad, tank);
    }

    /// <summary>"→ AP", "→ AD", "→ TANQUE" o "→ HÍBRIDO" para la carta de un aumento (null si no se decanta).</summary>
    public static string? CardLabel(Augment a, bool championIsMagic)
    {
        var (ap, ad, tank) = Lean(a, championIsMagic);
        var kinds = new[] { (K: "AP", V: ap), (K: "AD", V: ad), (K: L("TANQUE", "TANK"), V: tank) }.Where(x => x.V > 0).OrderByDescending(x => x.V).ToList();
        if (kinds.Count == 0 || kinds[0].V < 0.5) return null; // apenas se decanta (un escudo solo, p. ej.)
        // Mezcla: las dos direcciones (p. ej. Goliat con Nasus: vida + fuerza adaptable → "AD/TANQUE").
        return kinds.Count > 1 && kinds[1].V >= kinds[0].V * 0.6 ? $"{kinds[0].K}/{kinds[1].K}" : kinds[0].K;
    }

    public static string Name(BuildKind k) => k switch
    {
        BuildKind.AP => "AP", BuildKind.AD => "AD", BuildKind.Tank => L("TANQUE", "TANK"),
        BuildKind.HybridAdAp => L("HÍBRIDO AD/AP", "HYBRID AD/AP"), BuildKind.HybridApTank => L("AP TANQUE", "AP TANK"),
        _ => L("LUCHADOR (AD/TANQUE)", "BRUISER (AD/TANK)"),
    };

    /// <summary>¿Encaja el objeto con la dirección? (híbridos: objetos que dan las dos cosas, o cualquiera de ellas).</summary>
    public static bool Fits(ItemInfo i, BuildKind k)
    {
        bool ap = i.Has("SpellDamage"), ad = i.Has("Damage") || i.Has("CriticalStrike") || i.Has("AttackSpeed"), tank = i.IsDefensive && !i.IsOffensive;
        bool sturdy = i.Has("Health") || i.Has("Armor") || i.Has("SpellBlock");
        return k switch
        {
            BuildKind.AP => ap, BuildKind.AD => ad, BuildKind.Tank => tank,
            BuildKind.HybridAdAp => ap && ad || i.Has("OnHit"),
            BuildKind.HybridApTank => ap && sturdy || tank,
            _ => ad && sturdy || tank,
        };
    }
}

public sealed partial class Advisor
{
    /// <summary>
    /// La dirección que piden tus aumentos (cada uno cuenta según lo que da) y, si ya llevas objetos, tu build actual.
    /// Null si aún no hay nada que la decida (sin aumentos que se decanten ni objetos).
    /// </summary>
    public BuildDirection? Direction(string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine)
    {
        bool magic = PlaysMagic(champion, myItems, mine);
        SetQuestItems(mine);
        double ap = 0, ad = 0, tank = 0;
        var why = new List<string>();
        foreach (var a in mine)
        {
            var (x, y, z) = BuildDirections.Lean(a, magic);
            if (x + y + z < 0.5) continue; // apenas se decanta: no cuenta (ni sale en el porqué)
            ap += x; ad += y; tank += z;
            var main = new[] { (L("AP", "AP"), x), (L("AD", "AD"), y), (L("aguante", "tankiness"), z) }.MaxBy(t => t.Item2).Item1;
            why.Add($"{a.DisplayName} ({main})");
        }
        // Tus objetos (si ya llevas algo) cuentan más cuantos más llevas: con uno, como un aumento y medio (no te decimos que
        // cambies todo de golpe); con 4 o 5 ya mandan ellos. Antes siempre 1,5: una Vayne con Filo del Infinito, Matakrakens
        // y 100 % de crítico salía «híbrido AD/AP» por sus aumentos (Wakka, 2026-10-31).
        int gold = myItems.Select(Data.Item).OfType<ItemInfo>().Where(i => !i.IsBoots).Sum(i => i.PriceTotal);
        if (gold >= 1000)
        {
            var (apShare, _, _) = BuildProfile(champion, myItems, []);
            // Rylai, Liandry o el Bastón de las Eras dan vida, pero son de mago: cuentan como daño, no como tanque.
            double tankShare = TankShare(champion, myItems, [], apHybridIsDamage: true);
            double weight = Math.Clamp(gold / 2500.0, 1.5, 6);
            tank += weight * tankShare;
            ap += weight * (1 - tankShare) * apShare;
            ad += weight * (1 - tankShare) * (1 - apShare);
            why.Add(L("tus objetos", "your items"));
        }
        if (ap + ad + tank < 0.5) return null;

        var ranked = new[] { (K: "AP", V: ap), (K: "AD", V: ad), (K: "T", V: tank) }.OrderByDescending(x => x.V).ToList();
        // Magos y tiradores casi nunca van de tanque: para llamarlo mezcla con tanque, el aguante tiene que pesar bastante más.
        bool squishy = Data.Champion(champion)?.Roles.FirstOrDefault() is "mage" or "marksman";
        bool hybrid = ranked[1].V >= ranked[0].V * (squishy && ranked[1].K == "T" ? 0.8 : 0.6);
        var kind = !hybrid
            ? ranked[0].K switch { "AP" => BuildKind.AP, "AD" => BuildKind.AD, _ => BuildKind.Tank }
            : (ranked[0].K + ranked[1].K) switch
            {
                "APAD" or "ADAP" => BuildKind.HybridAdAp,
                "APT" or "TAP" => BuildKind.HybridApTank,
                _ => BuildKind.HybridAdTank,
            };

        // Aviso si choca con lo normal del campeón (lo que se compra con él, sin contar tus objetos ni aumentos).
        var (usualAp, _, _) = BuildProfile(champion, [], []);
        string name = Data.ChampionName(champion);
        string? warning = kind switch
        {
            BuildKind.AP when usualAp < 0.3 => L($"Ojo: {name} suele ir de AD; este camino es poco habitual.", $"Heads up: {name} usually goes AD; this path is unusual."),
            BuildKind.AD when usualAp > 0.7 => L($"Ojo: {name} suele ir de AP; este camino es poco habitual.", $"Heads up: {name} usually goes AP; this path is unusual."),
            // Tanques (Alistar, Leona, Sejuani…): un aumento de daño no los convierte en AD/AP sin avisar.
            BuildKind.AD or BuildKind.AP or BuildKind.HybridAdAp when TankShare(champion, [], []) >= 0.6 =>
                L($"Ojo: {name} suele ir de tanque; este camino es poco habitual.", $"Heads up: {name} usually goes tank; this path is unusual."),
            _ => null,
        };

        // 3 objetos para esa dirección: los que mejor ganan con tu campeón (mayhemmeta.com); sin datos, los mejor puntuados.
        var profile = new EnemyProfile(0.5, DamageMix.Mixed, 0, 0, 0, 0);
        double baseRate = stats.ChampionBase(champion);
        int champGames = stats.Global.For(champion)?.Games ?? 0;
        var items = Data.Items.Values
            .Where(i => i.IsFinal && i.InStore && i.Id < 10000 && !i.IsBoots && !Owns(myItems, i.Id) && stats.SeenInMayhem(i.Id)
                        && !stats.Links.IsReward(i.Id) && !OffRoleSupportItem(i, champion) && !ConflictsWithOwned(i, myItems) && BuildDirections.Fits(i, kind))
            .Select(i => (Item: i, G: stats.GlobalItem(champion, i.Id)))
            .OrderByDescending(x => x.G?.WinRate is not null && stats.Global.For(champion) is { } b && b.GamesWith(x.G) >= 100)
            .ThenByDescending(x => x.G?.WinRate ?? 0)
            .ThenByDescending(x => Score(x.Item, baseRate, 0, champion, profile, mine, magic).Score)
            .Aggregate(new List<(ItemInfo Item, MayhemItemStat? G)>(), (list, x) =>
            {
                // 3 objetos que se puedan llevar juntos (no dos del mismo grupo único).
                if (list.Count < 3 && !list.Any(o => Clash(o.Item.Id, x.Item.Id))) list.Add(x);
                return list;
            })
            .Select(x => new DirectionItem(x.Item, x.G?.WinRate / 100, x.G?.PickRate is { } pick ? (int)Math.Round(champGames * pick / 100) : 0))
            .ToList();

        string reason = why.Count > 0 ? string.Join(" + ", why) : "";
        return new BuildDirection(kind, BuildDirections.Name(kind), reason, warning, items);
    }
}
