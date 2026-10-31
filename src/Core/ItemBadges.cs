using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>
/// Etiquetas cortas de lo que da un objeto (Penetración, Armadura, Vida…), sacadas de las
/// categorías que Riot pone a cada objeto. Primero las que más dicen de para qué sirve; como mucho 3.
/// </summary>
public static class ItemBadges
{
    static readonly (Func<ItemInfo, bool> Has, string Es, string En)[] Order =
    [
        (i => i.Has("ArmorPenetration") || i.Has("MagicPenetration"), "Penetración", "Penetration"),
        (i => i.Has("Armor"), "Armadura", "Armor"),
        (i => i.Has("SpellBlock") || i.Has("MagicResist"), "Resist. mágica", "Magic resist"),
        (i => i.Has("Tenacity"), "Tenacidad", "Tenacity"),
        (i => i.Has("LifeSteal") || i.Has("SpellVamp"), "Curación", "Healing"),
        (i => i.Has("Damage") || i.Has("SpellDamage") || i.Has("CriticalStrike") || i.Has("OnHit"), "Daño", "Damage"),
        (i => i.Has("Health"), "Vida", "Health"),
        (i => i.Has("AbilityHaste") || i.Has("CooldownReduction"), "Aceleración", "Ability haste"),
        (i => i.Has("NonbootsMovement") || i.IsBoots, "Movilidad", "Mobility"),
    ];

    public static IReadOnlyList<string> For(ItemInfo item) =>
        Order.Where(b => b.Has(item)).Take(3).Select(b => L(b.Es, b.En)).ToList();
}
