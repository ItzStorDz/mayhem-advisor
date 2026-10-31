using static MayhemAdvisor.Core.Lang;
namespace MayhemAdvisor.Core;

/// <summary>El enemigo que va alimentado (el de más asesinatos menos muertes), con la parte mágica de su daño (0-1).</summary>
public sealed record FedEnemy(string Name, int Kills, int Deaths, double MagicShare);

/// <summary>
/// Cómo es el equipo enemigo: tipo de daño, tanques, curación, resistencias, críticos, velocidad de ataque,
/// escudos y vida que van comprando (y sus aumentos, si el juego nos los da).
/// </summary>
public sealed record EnemyProfile(double MagicShare, DamageMix Mix, int Tanks, int Healers, int ArmorItems, int MagicResistItems,
    int CritItems = 0, int AttackSpeedItems = 0, int Shields = 0, int HealthItems = 0, int AugmentsSeen = 0,
    int MagicChampions = 0, int PhysicalChampions = 0, FedEnemy? Fed = null)
{
    /// <summary>
    /// Mezcla lo que sabemos del campeón (sus partidas) con lo que lleva comprado ahora mismo y sus aumentos.
    /// Se usa igual al calcular estadísticas y en vivo, para que ambas cosas sean comparables.
    /// </summary>
    /// <param name="scores">Asesinatos y muertes de cada enemigo (en vivo): el que va alimentado cuenta más.</param>
    public static EnemyProfile Build(IReadOnlyList<(string Champion, IReadOnlyList<int> Items)> enemies, StatsStore stats,
        IReadOnlyDictionary<string, IReadOnlyList<Augment>>? augments = null, IReadOnlyDictionary<string, (int Kills, int Deaths)>? scores = null)
    {
        if (enemies.Count == 0) return new EnemyProfile(0.5, DamageMix.Mixed, 0, 0, 0, 0);

        double magicSum = 0, weightSum = 0;
        FedEnemy? fed = null;
        int tanks = 0, healers = 0, armor = 0, mr = 0, crit = 0, attackSpeed = 0, shields = 0, health = 0, augsSeen = 0, apChamps = 0, adChamps = 0;
        foreach (var (champ, itemIds) in enemies)
        {
            var roles = stats.Data.Champion(champ)?.Roles ?? [];
            var items = itemIds.Select(stats.Data.Item).OfType<ItemInfo>().Where(i => i.IsFinal).ToList();
            var augs = augments?.GetValueOrDefault(champ) ?? [];
            augsSeen += augs.Count;
            bool AugGives(string tag) => augs.Any(a => a.StatList.Contains(tag));

            double share = ChampionMagicShare(champ, itemIds, stats, augs);
            var score = scores?.GetValueOrDefault(champ);
            double weight = ThreatWeight(items, score);
            if (score is { } sc && IsFed(sc) && (fed is null || sc.Kills - sc.Deaths > fed.Kills - fed.Deaths))
                fed = new FedEnemy(stats.Data.ChampionName(champ), sc.Kills, sc.Deaths, share);
            magicSum += share * weight;
            weightSum += weight;
            if (share >= 0.6) apChamps++; else if (share <= 0.4) adChamps++;

            int tankAugs = augs.Count(a => a.StatList.Any(t => t is "Health" or "Armor" or "MR"));
            if (roles.Contains("tank") || items.Count(i => i.IsDefensive && !i.IsOffensive) + tankAugs >= 2) tanks++;
            if (stats.IsHealer(champ) || items.Any(i => i.Has("LifeSteal") || i.Has("SpellVamp")) || AugGives("Sustain")) healers++;
            armor += items.Count(i => i.Has("Armor")) + augs.Count(a => a.StatList.Contains("Armor"));
            mr += items.Count(i => i.Has("SpellBlock")) + augs.Count(a => a.StatList.Contains("MR"));
            crit += items.Count(i => i.Has("CriticalStrike")) + augs.Count(a => a.StatList.Contains("Crit"));
            attackSpeed += items.Count(i => i.Has("AttackSpeed")) + augs.Count(a => a.StatList.Contains("AS"));
            shields += items.Count(i => i.Is(ItemCounters.GivesShield)) + augs.Count(a => a.Description.Contains("escudo", StringComparison.OrdinalIgnoreCase));
            health += items.Count(i => i.Has("Health")) + augs.Count(a => a.StatList.Contains("Health"));
        }

        double magic = magicSum / weightSum;
        var mix = magic < 0.45 ? DamageMix.Physical : magic > 0.55 ? DamageMix.Magic : DamageMix.Mixed;
        return new EnemyProfile(magic, mix, tanks, healers, armor, mr, crit, attackSpeed, shields, health, augsSeen, apChamps, adChamps, fed);
    }

    /// <summary>
    /// Cuánto pesa un enemigo en el tipo de daño del equipo: más cuanto más oro lleva en objetos terminados y ×1,5 si va
    /// alimentado (6+ asesinatos y 5 más que muertes, como Hexsight). Con tus 136 partidas, el % de daño mágico enemigo
    /// sale más cerca del real: 6,96 → 6,69 puntos de error solo con el oro; 8,10 → 6,78 con oro y alimentado
    /// (las 27 partidas con asesinatos guardados).
    /// </summary>
    public static double ThreatWeight(IReadOnlyList<ItemInfo> finalItems, (int Kills, int Deaths)? score)
    {
        double weight = 0.5 + finalItems.Sum(i => i.PriceTotal) / 10000.0;
        if (score is { } s && IsFed(s)) weight *= 1.5;
        return weight;
    }

    static bool IsFed((int Kills, int Deaths) s) => s.Kills >= 6 && s.Kills - s.Deaths >= 5;

    /// <summary>Qué parte del daño de un enemigo es mágica: lo que hace el campeón en sus partidas, ajustado por lo que lleva.</summary>
    public static double ChampionMagicShare(string champ, IReadOnlyList<int> itemIds, StatsStore stats, IReadOnlyList<Augment>? augs = null)
    {
        augs ??= [];
        var items = itemIds.Select(stats.Data.Item).OfType<ItemInfo>().Where(i => i.IsFinal).ToList();
        double share = stats.MagicShare(champ) ?? RoleMagicShare(stats.Data.Champion(champ)?.Roles ?? []);
        int ap = items.Count(i => i.Has("SpellDamage")) + augs.Count(a => a.StatList.Contains("AP")),
            ad = items.Count(i => i.Has("Damage") || i.Has("CriticalStrike")) + augs.Count(a => a.StatList.Contains("AD") || a.StatList.Contains("Crit"));
        if (ap + ad > 0) share = 0.5 * share + 0.5 * ap / (ap + ad);
        return share;
    }

    public static double RoleMagicShare(string[] roles) =>
        roles.FirstOrDefault() switch { "mage" => 0.85, "support" => 0.7, "marksman" => 0.1, "assassin" => 0.35, "fighter" => 0.2, "tank" => 0.5, _ => 0.5 };

    /// <summary>
    /// Un dato corto del equipo enemigo para el panel, («3 campeones AP en el equipo enemigo»):
    /// lo que más condiciona la build. Null si no hay nada que destaque.
    /// </summary>
    public string? Fact() =>
        Fed is { } f ? (f.MagicShare >= 0.6 ? L($"{f.Name} va alimentado ({f.Kills}/{f.Deaths}): hace daño mágico", $"{f.Name} is fed ({f.Kills}/{f.Deaths}): magic damage")
                      : f.MagicShare <= 0.4 ? L($"{f.Name} va alimentado ({f.Kills}/{f.Deaths}): hace daño físico", $"{f.Name} is fed ({f.Kills}/{f.Deaths}): physical damage")
                      : L($"{f.Name} va alimentado ({f.Kills}/{f.Deaths})", $"{f.Name} is fed ({f.Kills}/{f.Deaths})"))
        : MagicChampions >= 3 ? L($"{MagicChampions} campeones AP enfrente", $"{MagicChampions} AP champions on the enemy team")
        : PhysicalChampions >= 3 ? L($"{PhysicalChampions} campeones AD enfrente", $"{PhysicalChampions} AD champions on the enemy team")
        : Tanks >= 2 ? L($"{Tanks} tanques enfrente", $"{Tanks} tanks on the enemy team")
        : Healers >= 3 ? L($"{Healers} enemigos con curación", $"{Healers} enemies with healing")
        : null;

    public string Describe()
    {
        var parts = new List<string>
        {
            Mix switch
            {
                DamageMix.Magic => L($"Mucho daño mágico ({MagicShare:P0})", $"Lots of magic damage ({MagicShare:P0})"),
                DamageMix.Physical => L($"Mucho daño físico ({1 - MagicShare:P0})", $"Lots of physical damage ({1 - MagicShare:P0})"),
                _ => L("Daño mixto", "Mixed damage"),
            },
        };
        if (Tanks >= 2) parts.Add(L($"{Tanks} tanques", $"{Tanks} tanks"));
        if (Healers >= 2) parts.Add(L($"{Healers} con curación", $"{Healers} healers"));
        if (ArmorItems >= 3) parts.Add(L($"compran armadura ({ArmorItems})", $"buying armor ({ArmorItems})"));
        if (MagicResistItems >= 3) parts.Add(L($"compran resist. mágica ({MagicResistItems})", $"buying magic resist ({MagicResistItems})"));
        if (CritItems >= 3) parts.Add(L($"van de crítico ({CritItems})", $"going crit ({CritItems})"));
        if (AttackSpeedItems >= 3) parts.Add(L($"mucha velocidad de ataque ({AttackSpeedItems})", $"lots of attack speed ({AttackSpeedItems})"));
        if (Shields >= 2) parts.Add(L($"escudos ({Shields})", $"shields ({Shields})"));
        if (HealthItems >= 6) parts.Add(L($"mucha vida ({HealthItems})", $"lots of health ({HealthItems})"));
        if (AugmentsSeen > 0) parts.Add(L($"{AugmentsSeen} aumentos enemigos vistos", $"{AugmentsSeen} enemy augments seen"));
        return string.Join(" · ", parts);
    }
}

/// <param name="InProgress">Ya llevas alguno de sus componentes (lo estás haciendo).</param>
/// <param name="Bonus">Cuánto suma (o resta) a la puntuación del aumento, en las mismas unidades que el % de victorias.</param>
/// <param name="Reason">Frase corta para la carta, p. ej. «Va con «Eureka»: los dos dan poder de habilidad».</param>
public sealed record AugmentSynergy(double Bonus, string Reason);

public sealed record ItemAdvice(ItemInfo Item, double Score, int Games, string Reason, bool InProgress = false);
/// <summary>Una casilla de la build completa recomendada; <paramref name="Owned"/> si ya lo tienes.</summary>
public sealed record BuildSlot(ItemAdvice Item, bool Owned);
public sealed record SkillAdvice(string MaxOrder, string Start, double WinRate, int Games);
/// <param name="Cost">Lo que te cuesta ya descontando los componentes que llevas.</param>
public sealed record NextBuy(ItemInfo Item, int Cost, int GoldMissing);
/// <param name="SellGold">Lo que te devuelve la tienda al venderlo (~70 % del precio).</param>
/// <param name="Cost">Precio del objeto nuevo descontando los componentes que conservas.</param>
public sealed record SwapAdvice(ItemInfo Sell, int SellGold, string SellReason, ItemAdvice Buy, int Cost, int GoldMissing);

public sealed record Advice(
    EnemyProfile Enemies,
    IReadOnlyList<ItemAdvice> Items,
    ItemAdvice? Boots,
    NextBuy? Next,
    SkillAdvice? Skills,
    IReadOnlyList<Augment> MyAugments,
    SwapAdvice? Swap = null,
    bool InventoryFull = false,
    IReadOnlyList<BuildSlot>? FullBuild = null,
    IReadOnlyList<ItemAdvice>? Building = null,
    CounterPlan? Plan = null,
    BuildDirection? Direction = null,
    GamePhase Phase = GamePhase.Mid);

/// <summary>Un objeto de la build habitual que conviene saltarse, y por qué.</summary>
public sealed record Discard(ItemInfo Item, string Reason);
/// <summary>Quién del equipo enemigo hace qué daño (0 = todo físico, 1 = todo mágico).</summary>
public sealed record EnemyThreat(string Name, double MagicShare);

/// <summary>Un objeto de una categoría de "Adaptar build": su % de victorias con tu campeón (null = sin datos).</summary>
/// <param name="Augment">Nombre del aumento tuyo con el que encaja, si encaja con alguno.</param>
public sealed record CategoryItem(ItemInfo Item, double? WinRate, int Games, string? Augment, bool Owned);
/// <summary>Lo que pide la partida (p. ej. "RESISTENCIA MÁGICA"), por qué, y sus objetos por % de victorias.</summary>
public sealed record CounterCategory(string Title, string Why, IReadOnlyList<CategoryItem> Items);

/// <summary>
/// "Adaptar build": cómo es el daño enemigo, una build situacional de 6 objetos para comparar con la recomendada,
/// las categorías de objetos que pide la partida (ordenados por % de victorias con tu campeón) y qué objetos de la
/// build habitual te puedes saltar. Son sugerencias.
/// </summary>
/// <param name="Changed">Objetos de la build situacional que no están en la recomendada.</param>
public sealed record CounterPlan(double MagicShare, IReadOnlyList<EnemyThreat> Enemies, IReadOnlyList<BuildSlot> Situational,
    IReadOnlySet<int> Changed, IReadOnlyList<CounterCategory> Categories, IReadOnlyList<Discard> Discards);

/// <summary>Junta estadísticas, datos del juego y el contexto de la partida para decir qué comprar.</summary>
public sealed partial class Advisor(StatsStore stats)
{
    /// <summary>
    /// Sin límite propio de penetración de armadura: la tienda solo limita el grupo de Recordatorio Mortal (UniqueGroups).
    /// Antes era 2, y a Pyke (Arco Axiomático + Arrogancia + Filo de la Noche + Youmuu en la página) le quitaba su build;
    /// en las partidas guardadas, 82 de 1.210 jugadores llevan 3 o más.
    /// </summary>
    public const int MaxArmorPenItems = 6;

    GameData Data => stats.Data;

    int ArmorPenSlotsLeft(IReadOnlyList<int> myItems) =>
        MaxArmorPenItems - myItems.Count(id => Data.Item(id) is { IsFinal: true } i && i.Has("ArmorPenetration"));

    /// <summary>
    /// Grupos de objetos de los que el juego solo deja llevar uno (mItemGroups con mMaxGroupOwnable = 1 en los datos del
    /// juego, items.cdtb.bin.json de CommunityDragon). Antes la app podía recomendar Recuerdos de Lord Dominik con
    /// Recordatorio Mortal, o Fauces de Malmortius con Sterak, y la tienda no deja comprarlos juntos.
    /// </summary>
    static readonly Dictionary<string, int[]> UniqueGroups = new()
    {
        ["LastWhisper"] = [3033, 3035, 3036, 3071, 3302, 4015, 6694],   // Recordatorio Mortal, Lord Dominik, Cuchilla Oscura, Terminus, Serylda…
        ["Cleave"] = [3074, 3077, 3748, 6631, 6698],                   // Hidras, Tiamat, Rompezancadas
        ["Lifeline"] = [2525, 3003, 3040, 3053, 3155, 3156, 6673],      // Arnés de Protoplasma, Arcángel/Serafín, Sterak, Fauces, Arco Escudo
        ["Tear"] = [2526, 3003, 3004, 3070, 3119],                      // Arcángel, Manamune, Llegada del Invierno
        ["VoidPen"] = [3135, 3137, 3302, 4010, 4015, 4630, 8010],      // Báculo del Vacío, Criptorretoño, Terminus
        ["Immolate"] = [3068, 6660, 6664],                              // Égida de Fuego Solar, Resplandor Vacío
        ["Eternity"] = [3803, 4402, 6657],                              // Bastón de las Eras
        ["Quicksilver"] = [3139, 3140, 6035],                           // Cimitarra Mercurial
        ["Glory"] = [1082, 3041],                                       // Mejai
        // Grupos sin nombre en los datos del juego (solo un código):
        ["Spellblade"] = [2510, 3078, 3100, 3508, 6662],                // Brillo: Ocaso y Alba, Fuerza de Trinidad, Liche, Saqueador, Hijo del Hielo
        ["Annul"] = [3102, 3814],                                       // Velo de la Banshee, Filo de la Noche
        ["Momentum"] = [3002, 3742],                                    // Pionero, Placa del Hombre Muerto
    };
    static readonly Dictionary<int, string[]> GroupsOf = UniqueGroups
        .SelectMany(g => g.Value.Select(id => (Id: id, Group: g.Key)))
        .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Select(x => x.Group).ToArray());

    /// <summary>¿Son dos objetos distintos del mismo grupo único? (Serafín cuenta como Arcángel, Muramana como Manamune…)</summary>
    public static bool SameGroup(int a, int b)
    {
        a = TransformedFrom.GetValueOrDefault(a, a); b = TransformedFrom.GetValueOrDefault(b, b);
        return a != b && GroupsOf.TryGetValue(a, out var ga) && GroupsOf.TryGetValue(b, out var gb) && ga.Intersect(gb).Any();
    }

    /// <summary>
    /// Objetos que pide una misión de tus aumentos: esos sí se pueden llevar juntos (la misión Ultra Hydra pide las tres
    /// hidras a la vez). Se rellena en <see cref="Build"/> y <see cref="Direction"/>.
    /// </summary>
    HashSet<int> _questItems = [];

    // Solo las misiones (Ultrahidra pide las tres hidras); Mejora de Brillo pide "un objeto con Brillo", no todos.
    void SetQuestItems(IReadOnlyList<Augment> mine) =>
        _questItems = mine.Where(a => a.ApiName.Contains("Quest", StringComparison.OrdinalIgnoreCase)).SelectMany(a => stats.Links.RequiredBy(a.Id)).Select(id => TransformedFrom.GetValueOrDefault(id, id)).ToHashSet();

    /// <summary>¿No se pueden llevar juntos? (mismo grupo único y no los pide a los dos una misión tuya)</summary>
    bool Clash(int a, int b) =>
        SameGroup(a, b) && !(_questItems.Contains(TransformedFrom.GetValueOrDefault(a, a)) && _questItems.Contains(TransformedFrom.GetValueOrDefault(b, b)));

    /// <summary>¿Choca con algún objeto TERMINADO que ya llevas? (Los componentes no: la Lágrima se convierte en Manamune).</summary>
    bool ConflictsWithOwned(ItemInfo item, IReadOnlyList<int> myItems) =>
        myItems.Any(id => Data.Item(TransformedFrom.GetValueOrDefault(id, id)) is { IsFinal: true } own && Clash(own.Id, item.Id));

    /// <summary>Los mejores <paramref name="count"/> sin pasarse del límite de penetración de armadura ni repetir grupo único.</summary>
    List<ItemAdvice> TakeWithinLimit(IEnumerable<ItemAdvice> ordered, int count, int penSlots)
    {
        var picked = new List<ItemAdvice>();
        foreach (var a in ordered)
        {
            if (picked.Any(p => Clash(p.Item.Id, a.Item.Id))) continue;
            if (a.Item.Has("ArmorPenetration"))
            {
                if (penSlots <= 0) continue;
                penSlots--;
            }
            picked.Add(a);
            if (picked.Count == count) break;
        }
        return picked;
    }

    // Para TU build manda lo que se compra con el campeón en la página (miles de partidas); si no hay, el daño que hace.
    public bool IsMagic(string champion) =>
        (stats.PageApShare(champion) ?? stats.MagicShare(champion) ?? EnemyProfile.RoleMagicShare(Data.Champion(champion)?.Roles ?? [])) > 0.5;

    /// <summary>
    /// ¿Haces daño mágico en ESTA partida? Campeones que van de las dos maneras (Shaco, Nidalee, Kai'Sa…): si tus
    /// objetos ya lo deciden, manda tu build; si no, lo habitual del campeón (<see cref="IsMagic"/>).
    /// </summary>
    public bool PlaysMagic(string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine)
    {
        var (ap, _, fromItems) = BuildProfile(champion, myItems, mine);
        // Con objetos comprados manda lo que más llevas (antes, entre 25 % y 75 % se volvía a lo habitual del campeón:
        // un Shaco con 2 objetos AD y un aumento mixto pasaba a recibir objetos de AP).
        return fromItems ? ap > 0.5 : IsMagic(champion);
    }

    public Advice Build(string myChampion, IReadOnlyList<int> myItems, IReadOnlyList<(string Champion, IReadOnlyList<int> Items)> enemies,
                        IReadOnlyList<Augment> myAugments, double gold, IReadOnlyDictionary<string, IReadOnlyList<Augment>>? enemyAugments = null,
                        IReadOnlySet<int>? keep = null, GamePhase phase = GamePhase.Mid,
                        IReadOnlyDictionary<string, (int Kills, int Deaths)>? enemyScores = null)
    {
        keep ??= new HashSet<int>();
        var profile = EnemyProfile.Build(enemies, stats, enemyAugments, enemyScores);
        SetQuestItems(myAugments);
        _tankBuild = TankShare(myChampion, myItems, myAugments) >= 0.6;
        (_apShare, _, _fromItems) = BuildProfile(myChampion, myItems, myAugments);
        _style = StyleFor(myChampion, myAugments);
        bool iAmMagic = PlaysMagic(myChampion, myItems, myAugments);
        // Otro estilo por tus aumentos: de él sale si vas de daño mágico, físico o de tanque. Si tus objetos ya decidieron
        // el daño contrario (Ezreal con 2 objetos de AD y un aumento "de AP"), manda tu build y el estilo no se usa.
        bool physicalStyle = _style?.Key is "ad" or "crit" or "lethality" or "on_hit";
        if (_fromItems && ((_style?.Key == "ap" && _apShare < 0.35) || (physicalStyle && _apShare > 0.65))) _style = null;
        if (_style is not null && !_fromItems)
        {
            if (_style.Key == "ap") iAmMagic = true;
            else if (physicalStyle) iAmMagic = false;
            if (_style.Key == "tank") _tankBuild = true;
            else if (_style.Key == "ap" || physicalStyle) _tankBuild = false;
        }
        bool ownBoots = myItems.Any(i => Data.Item(i)?.IsBoots == true);

        var pool = CandidatePool(myChampion, profile.Mix).Where(c => !Owns(myItems, c.Item.Id) && !ConflictsWithOwned(c.Item, myItems)).ToList();
        // Lo que pide un aumento tuyo (Corazón de Acero para "Endurece tu Corazón"…) entra aunque nadie lo compre con tu campeón.
        foreach (var id in myAugments.SelectMany(a => stats.Links.RequiredBy(a.Id)).Distinct())
            // …salvo que no se pueda llevar con lo tuyo (Relicario Enervante con el Bastón de las Eras) o sea del daño
            // contrario a tu build (Filo del Infinito para Ahri con «Mejorar Filo del Infinito»).
            if (!myItems.Contains(id) && pool.All(p => p.Item.Id != id) && Data.Item(id) is { } wanted
                && !ConflictsWithOwned(wanted, myItems) && !WrongDamageFor(wanted, iAmMagic))
                pool.Add((wanted, stats.ChampionBase(myChampion), 0));
        // Con otro estilo de build (Sona de AP con Mago Tirador), lo que se compra en ese estilo entra aunque no salga en las
        // tablas de mayhemmeta del campeón (allí casi todas las Sona van de apoyo).
        if (_style is not null)
            foreach (var (id, share) in _style.Items)
                if (share >= StyleItemMin && pool.All(p => p.Item.Id != id) && !Owns(myItems, id) && Data.Item(id) is { IsFinal: true, InStore: true } styleItem
                    && !styleItem.IsBoots && !ConflictsWithOwned(styleItem, myItems) && !stats.Links.IsReward(id))
                    pool.Add((styleItem, stats.ChampionBase(myChampion), 0));
        pool.AddRange(CounterCandidates(myChampion, profile, iAmMagic, pool.Select(c => c.Item.Id).Concat(myItems).ToHashSet()));
        var scored = pool.Select(c => Score(c.Item, c.Rate, c.Games, myChampion, profile, myAugments, iAmMagic)).ToList();

        // Los que ya estás haciendo (tienes parte de sus componentes) suben un poco según lo avanzados que van.
        scored = scored.Select(a => InBuilding(a, myItems)).ToList();
        var ranked = scored.Where(a => !a.Item.IsBoots).OrderByDescending(a => a.Score).ToList();

        // Con las 6 casillas llenas solo puedes terminar lo que ya estás haciendo (los componentes dejan sitio) o vender.
        int usedSlots = myItems.Count(id => Data.Item(id) is { PriceTotal: > 0 } i && !i.Has("Trinket"));
        bool full = usedSlots >= InventorySlots;
        var buyable = full ? ranked.Where(a => a.InProgress).ToList() : ranked;
        // Build ya clara (Shaco con 2 objetos AD): los de daño contrario no entran ni como "otra opción"; si faltan opciones,
        // se completan abajo con objetos de la tienda de tu tipo de daño.
        if (_fromItems && _apShare is < 0.35 or > 0.65)
            buyable = buyable.Where(a => a.InProgress || !WrongDamageFor(a.Item, iAmMagic)).ToList();

        var items = TakeWithinLimit(buyable, 3, ArmorPenSlotsLeft(myItems));
        // Se acabaron los objetos de sus datos (Pyke: todo letalidad y ya lleva 2): objetos de la tienda que le sirvan.
        if (!full && items.Count < 3)
        {
            var known = pool.Select(c => c.Item.Id).ToHashSet();
            var extra = Data.Items.Values
                .Where(i => i.IsFinal && i.InStore && i.Id < 10000 && !i.IsBoots && !i.Has("Lane") && stats.SeenInMayhem(i.Id) && !stats.Links.IsReward(i.Id)
                            && !known.Contains(i.Id) && !Owns(myItems, i.Id) && !ConflictsWithOwned(i, myItems) && !OffRoleSupportItem(i, myChampion)
                            && Fits(i, myChampion, iAmMagic) && !WrongDamageFor(i, iAmMagic))
                .Select(i => Score(i, stats.ChampionBase(myChampion), 0, myChampion, profile, myAugments, iAmMagic))
                .OrderByDescending(a => a.Score);
            items = TakeWithinLimit(items.Concat(extra), 3, ArmorPenSlotsLeft(myItems));
            ranked = ranked.Concat(items.Where(i => !ranked.Contains(i))).ToList();
        }
        if (!full) EnsureCounter(items, ranked, profile, ArmorPenSlotsLeft(myItems), myItems);
        if (phase == GamePhase.Early && !full) CheaperFirst(items, myItems);

        // Si vas avanzado con un objeto que también es buena opción, se termina antes de cambiar de idea.
        // Si dijiste "me quedo con este" (botón del overlay), se termina ese pase lo que pase.
        var commit = buyable.Where(a => a.InProgress && keep.Contains(a.Item.Id)).MaxBy(a => a.Score) ?? Committed(buyable, myItems);
        if (commit is not null)
        {
            items.Remove(commit);
            items.Insert(0, commit);
            if (items.Count > 3) items.RemoveAt(items.Count - 1);
        }

        var boots = ownBoots ? null : BestBoots(myChampion, profile, myAugments, iAmMagic, myItems);
        if (full && boots is { InProgress: false }) boots = null; // no caben unas botas nuevas

        var swap = full ? BestSwap(myChampion, myItems, ranked, profile, myAugments, iAmMagic, gold, keep) : null;

        // Lo próximo que conviene comprar: lo que ya estás terminando; si no, botas si aún no tienes; si no, el mejor objeto.
        // Lleno: terminar lo empezado o, si no hay nada, el cambio.
        var nextItem = commit?.Item
                       ?? (boots is { InProgress: true } ? boots.Item : null)
                       ?? (full ? items.FirstOrDefault()?.Item : boots?.Item ?? items.FirstOrDefault()?.Item);
        NextBuy? next = nextItem is not null
            ? new NextBuy(nextItem, Data.RemainingCost(nextItem, myItems), Math.Max(0, Data.RemainingCost(nextItem, myItems) - (int)gold))
            : swap is not null ? new NextBuy(swap.Buy.Item, swap.Cost - swap.SellGold, swap.GoldMissing) : null;

        var fullBuild = FullBuild(myChampion, myItems, ranked, boots, profile, myAugments, iAmMagic, swap?.Sell);
        var skills = stats.BestSkills(myChampion) is { } s ? new SkillAdvice(s.MaxOrder, s.Start, s.Rate, s.Games) : null;
        // Objetos empezados que no son la siguiente compra: el overlay ofrece "me quedo con este".
        var building = ranked.Where(a => a.InProgress && a.Item.Id != next?.Item.Id).Take(2).ToList();
        var plan = Plan(myChampion, myItems, enemies, profile, myAugments, iAmMagic, ranked, fullBuild);
        return new Advice(profile, items, boots, next, skills, myAugments, swap, full, fullBuild, building, plan,
            Direction(myChampion, myItems, myAugments), phase);
    }

    /// <summary>En el inicio de partida, dos objetos casi igual de buenos se desempatan por el más barato.</summary>
    const double EarlyTieMargin = 0.005;
    /// <summary>Cuánto más barato tiene que ser para adelantarlo.</summary>
    const int EarlyCheaperBy = 400;

    /// <summary>
    /// Inicio de partida (antes de 14 min): si el segundo objeto es casi igual de bueno (medio punto) y te
    /// cuesta al menos 400 de oro menos, va primero: tienes su fuerza antes. Lo empezado no se toca (eso lo decide Committed).
    /// </summary>
    void CheaperFirst(List<ItemAdvice> items, IReadOnlyList<int> myItems)
    {
        if (items.Count < 2 || items[0].InProgress) return;
        int firstCost = Data.RemainingCost(items[0].Item, myItems);
        var cheaper = items.Skip(1)
            .Where(a => a.Score >= items[0].Score - EarlyTieMargin && Data.RemainingCost(a.Item, myItems) <= firstCost - EarlyCheaperBy)
            .MinBy(a => Data.RemainingCost(a.Item, myItems));
        if (cheaper is null) return;
        items.Remove(cheaper);
        var why = L("inicio de partida: casi igual de bueno y más barato", "early game: almost as good and cheaper");
        items.Insert(0, cheaper with { Reason = string.IsNullOrEmpty(cheaper.Reason) ? why : cheaper.Reason + " · " + why });
    }

    /// <summary>Arma el panel "Adaptar build" (ver <see cref="CounterPlan"/>).</summary>
    CounterPlan Plan(string champion, IReadOnlyList<int> myItems, IReadOnlyList<(string Champion, IReadOnlyList<int> Items)> enemies,
                     EnemyProfile profile, IReadOnlyList<Augment> myAugments, bool iAmMagic, List<ItemAdvice> ranked, List<BuildSlot> recommended)
    {
        var threats = enemies.Select(e => new EnemyThreat(Data.ChampionName(e.Champion), EnemyProfile.ChampionMagicShare(e.Champion, e.Items, stats)))
                             .OrderByDescending(t => t.MagicShare).ToList();
        string Names(Func<EnemyThreat, bool> which) => string.Join(", ", threats.Where(which).Select(t => t.Name).Take(3));
        string With(string names) => names == "" ? "" : $" ({names})";
        int apCount = threats.Count(t => t.MagicShare >= 0.6), adCount = threats.Count(t => t.MagicShare <= 0.4);

        // Lo que pide la partida, de más a menos importante.
        var needs = new List<(string Title, string Why, double Weight, Func<ItemInfo, bool> Counters)>();
        double lean = profile.MagicShare - 0.5;
        if (lean > 0.03) needs.Add((L("RESISTENCIA MÁGICA", "MAGIC RESIST"),
            L($"{(apCount >= 2 ? $"{apCount} hacen daño mágico" : "Mucho daño mágico")}: el {profile.MagicShare:P0} del daño", $"{(apCount >= 2 ? $"{apCount} deal magic damage" : "Lots of magic damage")}: {profile.MagicShare:P0} of the damage") + With(Names(t => t.MagicShare >= 0.6)), lean, i => i.Has("SpellBlock")));
        if (lean < -0.03) needs.Add((L("ARMADURA", "ARMOR"),
            L($"{(adCount >= 2 ? $"{adCount} hacen daño físico" : "Mucho daño físico")}: el {1 - profile.MagicShare:P0} del daño", $"{(adCount >= 2 ? $"{adCount} deal physical damage" : "Lots of physical damage")}: {1 - profile.MagicShare:P0} of the damage") + With(Names(t => t.MagicShare <= 0.4)), -lean, i => i.Has("Armor")));
        if (profile.Healers >= 2) needs.Add((L("HERIDAS GRAVES", "GRIEVOUS WOUNDS"), L($"{profile.Healers} enemigos se curan mucho", $"{profile.Healers} enemies heal a lot"), 0.03 * profile.Healers, i => i.AppliesWounds));
        if (profile.Shields >= 2) needs.Add((L("ANTIESCUDOS", "ANTI-SHIELD"), L($"{profile.Shields} escudos en el equipo enemigo", $"{profile.Shields} shields on the enemy team"), 0.025 * profile.Shields, i => i.Is(ItemCounters.AntiShield)));
        if (profile.CritItems >= 3) needs.Add((L("ANTICRÍTICO", "ANTI-CRIT"), L($"Llevan {profile.CritItems} objetos de crítico", $"They have {profile.CritItems} crit items"), 0.02 * profile.CritItems, i => i.Is(ItemCounters.AntiCrit)));
        if (profile.AttackSpeedItems >= 3) needs.Add((L("CONTRA VELOCIDAD DE ATAQUE", "ANTI-ATTACK SPEED"), L($"Llevan {profile.AttackSpeedItems} objetos de velocidad de ataque", $"They have {profile.AttackSpeedItems} attack speed items"), 0.02 * profile.AttackSpeedItems, i => i.Is(ItemCounters.AntiAttackSpeed)));
        if (profile.Tanks >= 2 || profile.HealthItems >= 6) needs.Add((L("DAÑO POR % DE VIDA", "% HEALTH DAMAGE"), profile.Tanks >= 2 ? L($"{profile.Tanks} tanques", $"{profile.Tanks} tanks") : L("Compran mucha vida", "They stack health"), 0.06, i => i.Is(ItemCounters.PercentHealth)));
        if (!_tankBuild && !iAmMagic && (profile.Tanks >= 2 || profile.ArmorItems >= 3)) needs.Add((L("PENETRACIÓN DE ARMADURA", "ARMOR PENETRATION"), L($"Compran armadura ({profile.ArmorItems} objetos)", $"They buy armor ({profile.ArmorItems} items)"), 0.05, i => i.Has("ArmorPenetration")));
        if (!_tankBuild && iAmMagic && (profile.Tanks >= 2 || profile.MagicResistItems >= 3)) needs.Add((L("PENETRACIÓN MÁGICA", "MAGIC PENETRATION"), L($"Compran resist. mágica ({profile.MagicResistItems} objetos)", $"They buy magic resist ({profile.MagicResistItems} items)"), 0.05, i => i.Has("MagicPenetration")));
        needs = needs.OrderByDescending(n => n.Weight).ToList();

        // Cada categoría: objetos que la cubren y le sirven a tu campeón, por % de victorias con él (mayhemmeta.com).
        int champGames = stats.Global.For(champion)?.Games ?? 0;
        string? AugmentFor(ItemInfo item) =>
            myAugments.FirstOrDefault(aug => stats.Links.RequiredBy(aug.Id).Contains(item.Id) || StatTags.ItemCategories(aug.StatList, iAmMagic).Any(item.Has))?.Name;
        CategoryItem Describe(ItemInfo item)
        {
            var g = stats.GlobalItem(champion, item.Id);
            return new CategoryItem(item, g?.WinRate / 100, g?.PickRate is { } pick ? (int)Math.Round(champGames * pick / 100) : 0, AugmentFor(item), Owns(myItems, item.Id));
        }
        var store = Data.Items.Values
            .Where(i => i.IsFinal && i.InStore && i.Id < 10000 && stats.SeenInMayhem(i.Id) && !stats.Links.IsReward(i.Id) && !OffRoleSupportItem(i, champion)
                        && !ConflictsWithOwned(i, myItems)
                        && (Fits(i, champion, iAmMagic) || (i.IsBoots && i.PriceTotal <= 1300) || stats.GlobalItem(champion, i.Id) is not null))
            .ToList();
        var categories = needs.Select(n => new CounterCategory(n.Title, n.Why, store.Where(n.Counters).Select(Describe)
                .OrderByDescending(c => c.WinRate.HasValue).ThenByDescending(c => c.WinRate ?? 0).ThenByDescending(c => c.Augment is not null)
                .Take(5).ToList()))
            .Where(c => c.Items.Count > 0).ToList();

        // Build situacional: la recomendada, cambiando lo que haga falta para cubrir las categorías (como mucho 3 cambios).
        var build = recommended.ToList();
        var changed = new HashSet<int>();
        foreach (var (need, category) in needs.Zip(needs.Select(n => categories.FirstOrDefault(c => c.Title == n.Title))))
        {
            if (category is null || changed.Count >= 3) continue;
            // De los de arriba de la lista (±1,5 puntos), el que encaje con tus aumentos.
            var options = category.Items.Where(c => !c.Owned).ToList();
            double bestRate = options.FirstOrDefault()?.WinRate ?? 0;
            var pick = options.FirstOrDefault(c => c.Augment is not null && (c.WinRate ?? 0) >= bestRate - 0.015) ?? options.FirstOrDefault();
            if (pick is null || build.Any(slot => slot.Item.Item.Id == pick.Item.Id)) continue; // ya está
            double RateOf(ItemInfo item) => category.Items.FirstOrDefault(c => c.Item.Id == item.Id)?.WinRate ?? stats.GlobalItem(champion, item.Id)?.WinRate / 100 ?? 0;
            // Si la build ya cubre esto con un objeto peor de la lista (o sin datos), se cambia por el mejor;
            // si no lo cubre nada, entra en lugar del objeto que menos aporta. Lo que ya tienes no se toca.
            int replace = pick.Item.IsBoots
                ? build.FindIndex(slot => slot.Item.Item.IsBoots && !slot.Owned)
                : build.FindLastIndex(slot => !slot.Item.Item.IsBoots && !slot.Owned && !changed.Contains(slot.Item.Item.Id)
                                              && need.Counters(slot.Item.Item) && RateOf(slot.Item.Item) < (pick.WinRate ?? 0) - 0.005);
            if (replace < 0 && !pick.Item.IsBoots && !build.Any(slot => need.Counters(slot.Item.Item)))
                replace = build.FindLastIndex(slot => !slot.Item.Item.IsBoots && !slot.Owned && !changed.Contains(slot.Item.Item.Id));
            if (replace < 0) continue;
            // No puede chocar con lo que se queda en la build (p. ej. Recordatorio Mortal con Lord Dominik).
            if (build.Where((slot, i) => i != replace).Any(slot => Clash(slot.Item.Item.Id, pick.Item.Id))) continue;
            string reason = L($"cubre {category.Title.ToLowerInvariant()}", $"covers {category.Title.ToLowerInvariant()}")
                            + (pick.WinRate is { } wr ? L($" ({wr:P1} de victorias con tu campeón)", $" ({wr:P1} win rate with your champion)") : "")
                            + (pick.Augment is not null ? L($" · va con tu aumento «{pick.Augment}»", $" · fits your augment “{pick.Augment}”") : "");
            build[replace] = new BuildSlot(new ItemAdvice(pick.Item, 0, pick.Games, reason), false);
            changed.Add(pick.Item.Id);
        }

        // Sáltate de la build habitual: lo más comprado con el campeón que no encaja con tus aumentos o con este equipo.
        var (apShare, abilityShare, _) = BuildProfile(champion, myItems, myAugments);
        var usual = stats.ItemRates(champion, DamageMix.Mixed)
            .Select(r => (Item: Data.Item(TransformedFrom.GetValueOrDefault(r.Item, r.Item)), r.Games))
            .Where(r => r.Item is { IsFinal: true, InStore: true } && !stats.Links.IsReward(r.Item.Id))
            .OrderByDescending(r => r.Games).Select(r => r.Item!).DistinctBy(i => i.Id).Take(7).ToList();
        double top = ranked.FirstOrDefault()?.Score ?? 0;
        var discards = new List<Discard>();
        foreach (var item in usual)
        {
            // Lo que contrarresta algo del enemigo (Cota de Espinas da heridas graves…) o ya llevas no se manda evitar.
            if (needs.Any(n => n.Counters(item)) || Owns(myItems, item.Id)) continue;
            string? why = null;
            bool armorOnly = item.Has("Armor") && !item.Has("SpellBlock"), mrOnly = item.Has("SpellBlock") && !item.Has("Armor");
            bool apItem = ApItem.Any(item.Has) && !AdItem.Any(item.Has), adItem = AdItem.Any(item.Has) && !ApItem.Any(item.Has);
            bool attackItem = AttackItem.Any(item.Has) && !AbilityItem.Any(item.Has);
            if (item.IsBoots && armorOnly && lean > 0.05) why = L($"botas de armadura y el {profile.MagicShare:P0} del daño enemigo es mágico: mejor Botas de Mercurio", $"armor boots but {profile.MagicShare:P0} of enemy damage is magic: get Mercury's Treads");
            else if (armorOnly && lean > 0.07) why = L($"da armadura y el {profile.MagicShare:P0} del daño enemigo es mágico", $"gives armor but {profile.MagicShare:P0} of enemy damage is magic");
            else if (mrOnly && lean < -0.07) why = L($"da resist. mágica y el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"gives magic resist but {1 - profile.MagicShare:P0} of enemy damage is physical");
            else if (apItem && apShare < 0.3) why = L("es de AP y tu build (objetos y aumentos) va de AD", "it is AP and your build (items and augments) is AD");
            else if (adItem && apShare > 0.7) why = L("es de AD y tu build (objetos y aumentos) va de AP", "it is AD and your build (items and augments) is AP");
            else if (attackItem && abilityShare > 0.75) why = L("es de ataques básicos y tu build va de habilidades", "it is for basic attacks and your build uses abilities");
            else if (!item.IsBoots && ranked.FirstOrDefault(a => a.Item.Id == item.Id) is { } scored && scored.Score < top - 0.08)
                why = L("rinde poco con tus aumentos y contra este equipo", "performs poorly with your augments and against this team");
            if (why is not null) discards.Add(new Discard(item, why));
            if (discards.Count == 3) break;
        }

        return new CounterPlan(profile.MagicShare, threats, build, changed, categories, discards);
    }

    /// <summary>A partir de qué parte hecha (en oro) un objeto se termina antes de cambiar a otro.</summary>
    const double CommitProgress = 0.25;
    /// <summary>Cuánto peor que el mejor puede ser el objeto empezado para que aun así se termine.</summary>
    const double CommitMargin = 0.08;
    /// <summary>Empujón a un objeto según lo hecho que lo llevas (con 2 de 3 componentes pesa más que con 1).</summary>
    const double ProgressBonus = 0.12;

    /// <summary>Qué parte del objeto llevas ya hecha (0 = nada, 1 = completo), por el oro de sus componentes.</summary>
    double Progress(ItemInfo item, IReadOnlyList<int> myItems) =>
        item.PriceTotal <= 0 ? 0 : 1 - (double)Data.RemainingCost(item, myItems) / item.PriceTotal;

    /// <summary>Marca los objetos que ya estás haciendo y les da un empujón según lo avanzados que van.</summary>
    ItemAdvice InBuilding(ItemAdvice a, IReadOnlyList<int> myItems)
    {
        double progress = Progress(a.Item, myItems);
        if (progress <= 0) return a;
        int missing = Data.RemainingCost(a.Item, myItems);
        return a with
        {
            Score = a.Score + progress * ProgressBonus,
            InProgress = true,
            Reason = L($"ya lo estás haciendo (te faltan {missing} de oro)", $"you are already building it ({missing} gold to go)") + $" · {a.Reason}",
        };
    }

    /// <summary>
    /// El objeto empezado que toca terminar. Un componente sirve para varios objetos: de los que lo usan, el mejor
    /// (su puntuación ya incluye lo avanzado que va, así que el que tiene más piezas tuyas sale ganando).
    /// </summary>
    ItemAdvice? Committed(List<ItemAdvice> ranked, IReadOnlyList<int> myItems)
    {
        double top = ranked.FirstOrDefault()?.Score ?? 0;
        bool penLeft = ArmorPenSlotsLeft(myItems) > 0;
        return ranked.Where(a => a.InProgress && a.Score >= top - CommitMargin && Progress(a.Item, myItems) >= CommitProgress
                                 && (penLeft || !a.Item.Has("ArmorPenetration")))
                     .MaxBy(a => a.Score);
    }

    /// <summary>¿Se usa <paramref name="componentId"/> (directamente o dentro de otra pieza) para hacer el objeto?</summary>
    bool BuildsInto(ItemInfo item, int componentId) =>
        (item.From ?? []).Any(id => id == componentId || (Data.Item(id) is { } part && BuildsInto(part, componentId)));

    /// <summary>
    /// La build de 6 objetos a la que apuntar: botas + los 5 mejores contando tus aumentos, lo que lleva el enemigo
    /// y lo que ya tienes (lo tuyo solo se cambia si hay algo claramente mejor). Respeta el límite de penetración.
    /// </summary>
    List<BuildSlot> FullBuild(string champion, IReadOnlyList<int> myItems, List<ItemAdvice> ranked, ItemAdvice? boots,
                              EnemyProfile profile, IReadOnlyList<Augment> myAugments, bool iAmMagic, ItemInfo? sell)
    {
        var rates = new Dictionary<int, (double Rate, int Games)>();
        foreach (var r in stats.ItemRates(champion, profile.Mix)) rates.TryAdd(r.Item, (r.Rate, r.Games));
        double baseRate = stats.ChampionBase(champion);
        ItemAdvice ScoreOwned(ItemInfo i)
        {
            var (rate, games) = rates.GetValueOrDefault(i.Id, (baseRate, 0));
            var a = Score(i, rate, games, champion, profile, myAugments, iAmMagic);
            return a with { Score = a.Score + SwapMargin, Reason = L("ya lo tienes", "you already have it") + " · " + a.Reason };
        }

        var owned = myItems.Distinct().Select(Data.Item).OfType<ItemInfo>()
            .Where(i => i.IsFinal && i.PriceTotal > 0 && !i.Has("Trinket") && !i.Has("Consumable"))
            .ToList();
        var ownedIds = owned.Select(i => i.Id).ToHashSet();

        // Lo que ya tienes terminado se queda (venderlo pierde un 30 % del oro); solo sale el que diga "Vender" (BestSwap).
        // Antes se cambiaba por cualquier cosa 0,03 mejor: tu Liandry (lo más comprado con Teemo) por un Arcángel 0,001 mejor.
        var kept = owned.Where(i => !i.IsBoots && i.Id != sell?.Id).Select(ScoreOwned).OrderByDescending(a => a.Score).ToList();
        var candidates = kept
            .Concat(ranked.Where(a => !ownedIds.Contains(a.Item.Id)).OrderByDescending(a => a.Score)).ToList();
        var picks = TakeWithinLimit(candidates, InventorySlots - 1, MaxArmorPenItems);
        if (kept.Count < InventorySlots - 1)
            EnsureCounter(picks, candidates, profile, MaxArmorPenItems, []); // aquí tus objetos ya van entre los candidatos (y primero)

        var slots = new List<BuildSlot>();
        if (owned.FirstOrDefault(i => i.IsBoots) is { } myBoots) slots.Add(new BuildSlot(ScoreOwned(myBoots), true));
        else if (boots is not null) slots.Add(new BuildSlot(boots, false));
        slots.AddRange(picks.OrderByDescending(a => ownedIds.Contains(a.Item.Id)).ThenByDescending(a => a.Score)
                            .Select(a => new BuildSlot(a, ownedIds.Contains(a.Item.Id))));
        return slots;
    }

    /// <summary>
    /// Qué tal combina un aumento que te ofrecen con los que ya elegiste. Mira tres cosas:
    /// cómo ganan juntos (todos los jugadores de mayhemmeta.com, o si no tus partidas guardadas), si piden el mismo objeto, y si potencian lo mismo
    /// (solo estadísticas que le sirven a tu campeón: el poder de habilidad no cuenta para un tirador).
    /// Además mira si el aumento encaja con tu build (ver <see cref="BuildFit"/>). Null si no hay nada que decir.
    /// </summary>
    const double MaxGuessBonus = 0.015;

    public AugmentSynergy? SynergyWith(Augment candidate, IReadOnlyList<Augment> mine, string champion, IReadOnlyList<int>? myItems = null)
    {
        if (candidate.IsAnvil) return null;
        // De Vuelta a lo Básico sella la definitiva: con un aumento que vive de ella (Axioma Arcano, Forma Final…) uno de
        // los dos no hace nada. Casi nadie los junta, así que no hay partidas que lo midan: va siempre al fondo.
        if (mine.FirstOrDefault(m => candidate.SealsUltimate ? m.NeedsUltimate : candidate.NeedsUltimate && m.SealsUltimate) is { } clash)
            return new AugmentSynergy(-0.12, candidate.SealsUltimate
                ? L($"Sella tu definitiva, y «{clash.DisplayName}» la necesita", $"Seals your ultimate, which “{clash.DisplayName}” needs")
                : L($"Necesita tu definitiva, y «{clash.DisplayName}» la sella", $"Needs your ultimate, which “{clash.DisplayName}” seals"));
        bool magic = PlaysMagic(champion, myItems ?? [], mine);
        var useful = UsefulTags(candidate, magic);
        var needs = stats.Links.RequiredBy(candidate.Id);

        double total = 0, bestWeight = 0, guessed = 0;
        string? reason = null;
        void Add(double amount, string why)
        {
            total += amount;
            if (Math.Abs(amount) > bestWeight) { bestWeight = Math.Abs(amount); reason = why; }
        }
        // Lo que se suma solo porque "potencian lo mismo" o "encaja con tu build" (sin partidas que lo midan) suma como mucho
        // +1,5 puntos en total: el % del aumento con tu campeón ya cuenta su build habitual, y si no, un aumento flojo
        // (Gigantobola de Nieve con Teemo, −0,6) acababa con nota S por encima de otro que gana mucho más.
        void Guess(double amount, string why)
        {
            double room = Math.Max(0, MaxGuessBonus - guessed);
            if (Math.Min(amount, room) <= 0) { if (reason is null) reason = why; return; }
            guessed += Math.Min(amount, room);
            Add(Math.Min(amount, room), why);
        }

        foreach (var m in mine.Where(m => !m.IsAnvil && m.Id != candidate.Id))
        {
            // Primero los datos de todos los jugadores (miles de partidas); si no hay, tus partidas guardadas.
            if (stats.AugmentTable.Pair(candidate.Id, m.Id) is { Games: >= 50 } global)
            {
                double lift = global.ShrunkSynergy / 100;
                if (Math.Abs(lift) >= 0.01)
                    Add(Math.Clamp(lift, -0.06, 0.06), lift > 0
                        ? L($"{(lift >= 0.03 ? "combina muy bien" : "combina bien")} con «{m.DisplayName}» (~{global.Games:N0} partidas en Mayhem)", $"{(lift >= 0.03 ? "great combo" : "good combo")} with “{m.DisplayName}” (~{global.Games:N0} Mayhem games)")
                        : L($"no combina bien con «{m.DisplayName}» (~{global.Games:N0} partidas en Mayhem)", $"bad combo with “{m.DisplayName}” (~{global.Games:N0} Mayhem games)"));
            }
            else if (stats.AugmentPairLift(candidate.Id, m.Id) is { } pair && Math.Abs(pair.Lift) >= 0.02)
                Add(Math.Clamp(pair.Lift, -0.05, 0.05), pair.Lift > 0
                    ? L($"combina bien con «{m.DisplayName}» en tus partidas ({pair.Games})", $"good combo with “{m.DisplayName}” in your games ({pair.Games})")
                    : L($"no combina bien con «{m.DisplayName}» en tus partidas ({pair.Games})", $"bad combo with “{m.DisplayName}” in your games ({pair.Games})"));

            var sharedItems = needs.Intersect(stats.Links.RequiredBy(m.Id)).Select(Data.Item).OfType<ItemInfo>().ToList();
            if (sharedItems.Count > 0) Add(0.04, L($"va con «{m.DisplayName}»: los dos piden {sharedItems[0].DisplayName}", $"fits “{m.DisplayName}”: both want {sharedItems[0].DisplayName}"));

            // Las estadísticas muy comunes (habilidades, prisa) cuentan la mitad: casi todos los aumentos las tienen.
            var shared = useful.Intersect(UsefulTags(m, magic)).OrderBy(t => t is "Abilities" or "Haste").ToList();
            double tagBonus = shared.Take(3).Sum(t => t is "Abilities" or "Haste" ? 0.0075 : 0.015);
            if (tagBonus > 0)
                Guess(tagBonus, L($"va con «{m.DisplayName}»: los dos potencian {string.Join(" y ", shared.Take(2).Select(StatTags.Label))}", $"fits “{m.DisplayName}”: both boost {string.Join(" and ", shared.Take(2).Select(StatTags.Label))}"));
        }

        if (BuildFit(candidate, champion, myItems ?? [], mine) is { } fit)
        {
            if (fit.Bonus > 0) Guess(fit.Bonus, fit.Reason); else Add(fit.Bonus, fit.Reason);
        }

        // Aumentos que mejoran o piden un objeto («Mejorar Filo del Infinito», «Descarga Tormentosa Mejorada»…): si ese
        // objeto no es para tu build, o no se puede llevar con lo que ya tienes, el aumento casi no te sirve.
        var needed = needs.Select(Data.Item).OfType<ItemInfo>().Where(i => i.IsFinal).ToList();
        if (needed.Count > 0 && needed.All(i => WrongDamageFor(i, magic)))
            Add(-0.08, L($"Pide {needed[0].DisplayName}, que no es para tu build de {(magic ? "AP" : "AD")}", $"Wants {needed[0].DisplayName}, which doesn't fit your {(magic ? "AP" : "AD")} build"));
        else if (needed.Count > 0 && myItems is not null && needed.All(i => !myItems.Contains(i.Id) && ConflictsWithOwned(i, myItems)))
            Add(-0.08, L($"Pide {needed[0].DisplayName}, que no se puede llevar con lo que ya tienes", $"Wants {needed[0].DisplayName}, which can't be combined with your items"));

        if (reason is null) return null;
        return new AugmentSynergy(Math.Clamp(total, -0.12, 0.10), reason);
    }

    // ───────── ¿Encaja el aumento con tu build? ─────────
    // El % de victorias de un aumento mezcla todas las builds del campeón: si los MF de AP ganan mucho con un aumento
    // de habilidades, sale alto también para el MF de AD, al que no le sirve. Se mira tu build en dos ejes:
    // daño físico ↔ mágico y ataques básicos ↔ habilidades, y se baja la nota de lo que pide la build contraria.

    static readonly string[] AdItem = ["Damage", "CriticalStrike", "AttackSpeed", "ArmorPenetration", "OnHit"];

    /// <summary>¿Objeto solo de daño del tipo contrario? (de AD con build de AP o al revés; los mixtos y defensivos no).</summary>
    static bool WrongDamageFor(ItemInfo i, bool magic) => i.IsOffensive && !i.IsDefensive &&
        (magic ? AdItem.Any(i.Has) && !ApItem.Any(i.Has) : ApItem.Any(i.Has) && !AdItem.Any(i.Has));
    static readonly string[] ApItem = ["SpellDamage", "MagicPenetration"];
    static readonly string[] AttackItem = ["AttackSpeed", "CriticalStrike", "OnHit"];
    static readonly string[] AbilityItem = ["SpellDamage", "AbilityHaste", "CooldownReduction", "Mana"];

    /// <summary>Peso de "lo normal con este campeón" (sus 6 objetos más comprados) frente a tus objetos, en oro.</summary>
    const double PriorGold = 2500;
    /// <summary>Lo que pesa cada aumento que ya elegiste al decidir hacia dónde va tu build, en oro.</summary>
    const double AugmentGold = 600;

    /// <summary>
    /// Hacia dónde va tu build: Ap = parte mágica (0 = todo AD, 1 = todo AP) y Abilities = parte de habilidades
    /// (0 = todo ataques básicos, 1 = todo habilidades). Mezcla lo que se suele comprar con el campeón, tus objetos
    /// (pesan según su precio, así que mandan en cuanto tienes un par) y los aumentos que ya elegiste.
    /// </summary>
    public (double Ap, double Abilities, bool FromItems) BuildProfile(string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine)
    {
        double ad = 0, ap = 0, attack = 0, ability = 0, ownGold = 0;
        void AddItem(ItemInfo item, double weight)
        {
            bool isAd = AdItem.Any(item.Has), isAp = ApItem.Any(item.Has);
            if (isAd) ad += isAp ? weight / 2 : weight;
            if (isAp) ap += isAd ? weight / 2 : weight;
            bool isAttack = AttackItem.Any(item.Has), isAbility = AbilityItem.Any(item.Has);
            if (isAttack) attack += isAbility ? weight / 2 : weight;
            if (isAbility) ability += isAttack ? weight / 2 : weight;
        }

        var common = stats.ItemRates(champion, DamageMix.Mixed)
            .Select(r => (Item: Data.Item(r.Item), r.Games))
            .Where(r => r.Item is { IsBoots: false, IsFinal: true })
            .OrderByDescending(r => r.Games).Take(6).ToList();
        foreach (var (item, _) in common) AddItem(item!, PriorGold / common.Count);

        foreach (var item in myItems.Select(Data.Item).OfType<ItemInfo>().Where(i => i.PriceTotal > 0 && !i.Has("Trinket") && !i.Has("Consumable")))
        {
            AddItem(item, item.PriceTotal);
            ownGold += item.PriceTotal;
        }

        foreach (var tags in mine.Where(m => !m.IsAnvil).Select(m => m.StatList))
        {
            if (tags.Contains("AP") || tags.Contains("MagicPen")) ap += AugmentGold;
            if (tags.Contains("AD") || tags.Contains("Lethality")) ad += AugmentGold;
            if (tags.Contains("Abilities")) ability += AugmentGold;
            if (tags.Contains("AS") || tags.Contains("Crit") || tags.Contains("OnHit")) attack += AugmentGold;
        }

        double Share(double a, double b) => a + b <= 0 ? 0.5 : a / (a + b);
        return (Share(ap, ad), Share(ability, attack), ownGold >= 1000);
    }

    /// <summary>
    /// Qué parte de tu build es de tanque (objetos solo defensivos, por su precio) frente a daño, contando tus aumentos
    /// (vida/armadura/resist. mágica/escudos → tanque) y, al principio, lo que se suele comprar con el campeón.
    /// Amumu o Nasus pueden ir de AP/AD o de tanque: con 0,6 o más se trata como build de tanque.
    /// </summary>
    /// <param name="apHybridIsDamage">Objetos de AP con vida/resistencias (Rylai, Liandry…) como daño, no mitad tanque.</param>
    public double TankShare(string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine, bool apHybridIsDamage = false)
    {
        double tank = 0, damage = 0;
        void AddItem(ItemInfo item, double weight)
        {
            if (item.IsBoots) return;
            if (item.IsDefensive && !item.IsOffensive) tank += weight;
            else if (item.IsOffensive && !item.IsDefensive) damage += weight;
            else if (apHybridIsDamage && item.Has("SpellDamage")) damage += weight;
            else { tank += weight / 2; damage += weight / 2; }
        }
        var common = stats.ItemRates(champion, DamageMix.Mixed).Select(r => (Item: Data.Item(r.Item), r.Games))
            .Where(r => r.Item is { IsBoots: false, IsFinal: true }).OrderByDescending(r => r.Games).Take(6).ToList();
        foreach (var (item, _) in common) AddItem(item!, 1500.0 / common.Count);
        foreach (var item in myItems.Select(Data.Item).OfType<ItemInfo>().Where(i => i.PriceTotal > 0 && !i.Has("Trinket") && !i.Has("Consumable")))
            AddItem(item, item.PriceTotal * 1.5); // lo que compras manda más que la media
        foreach (var tags in mine.Where(m => !m.IsAnvil).Select(m => m.StatList).Concat(mine.Where(m => m.IsAnvil).Select(m => m.StatList)))
        {
            if (tags.Any(t => t is "Health" or "Armor" or "MR" or "Shield" or "Tenacity")) tank += AugmentGold;
            if (tags.Any(t => t is "AP" or "AD" or "Crit" or "AS" or "Lethality" or "MagicPen")) damage += AugmentGold;
        }
        return tank + damage <= 0 ? 0 : tank / (tank + damage);
    }

    /// <summary>True durante <see cref="Build"/> si tu build es de tanque (ver <see cref="TankShare"/>).</summary>
    bool _tankBuild;
    /// <summary>Durante <see cref="Build"/>: el estilo de build que más gana con tus aumentos si no es el habitual (ver <see cref="StyleFor"/>).</summary>
    StylePick? _style;

    /// <summary>
    /// Con tus aumentos, ¿gana más otro estilo de build que el habitual del campeón? Según aramkit.com: Sona con Mago Tirador
    /// va de AP el 38 % y gana el 50,3 % frente al 48,5 % de apoyo. Null si no hay datos o si conviene lo de siempre.
    /// </summary>
    public StylePick? StyleFor(string champion, IReadOnlyList<Augment> mine) =>
        stats.AramkitBuilds.Pick(champion, mine.Where(a => !a.IsAnvil).Select(a => a.Id)) is { Changed: true } style ? style : null;

    /// <summary>Nombre del estilo de aramkit para los textos ("utility" → "apoyo").</summary>
    public static string StyleName(string key) => key switch
    {
        "utility" => L("apoyo", "support"), "ap" => "AP", "ad" => "AD", "tank" => L("tanque", "tank"),
        _ => key.ToUpperInvariant(),
    };
    /// <summary>Durante <see cref="Build"/>: parte AP de tu build (ver <see cref="BuildProfile"/>) y si ya la deciden tus objetos.</summary>
    double _apShare = 0.5;
    bool _fromItems;

    /// <summary>Objetos de support (Convergencia de Zeke, Redención, Relicario…): en Mayhem casi solo los compran los supports.</summary>
    static readonly HashSet<int> SupportItems = [3050, 3190, 3107, 3109, 2065, 6617, 3504, 3222, 6620, 6621, 6616, 4005];

    /// <summary>
    /// ¿Es un objeto de support que no le toca a este campeón? Vale si lo compra al menos el 3 % de los que lo juegan
    /// (Taric con el Relicario, Sona con la Piedra Lunar); sin datos del campeón, solo si su rol principal es support.
    /// Antes salían Convergencia de Zeke a Renekton o Redención a Shaco.
    /// </summary>
    public bool OffRoleSupportItem(ItemInfo item, string champion) =>
        SupportItems.Contains(item.Id) && (stats.Global.For(champion) is { } build
            ? !(build.Item(item.Id)?.PickRate >= 3)
            : Data.Champion(champion)?.Roles.FirstOrDefault() != "support");

    /// <summary>
    /// Ajuste por build: si el aumento pide claramente la build contraria a la tuya (p. ej. un aumento de habilidades
    /// en un MF de crítico) baja hasta 0,10; si encaja de lleno sube un poco. Null si el aumento no se decanta.
    /// </summary>
    public AugmentSynergy? BuildFit(Augment candidate, string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine)
    {
        var tags = candidate.StatList.ToHashSet();
        bool needsAp = tags.Overlaps(["AP", "MagicPen"]), needsAd = tags.Overlaps(["AD", "Crit", "Lethality"]);
        // Los aumentos de la definitiva (Axioma Arcano, Recursión Definitiva…) sirven a cualquiera que la use, también a un
        // Yi o un Twitch de ataques básicos: no piden build de habilidades.
        bool needsAbilities = tags.Contains("Abilities") && !candidate.NeedsUltimate, needsAttacks = tags.Overlaps(["AS", "Crit", "OnHit"]);
        if (needsAp == needsAd && needsAbilities == needsAttacks) return null; // pide de todo o nada concreto

        var (apShare, abilityShare, fromItems) = BuildProfile(champion, myItems, mine);
        var checks = new List<(double Fit, string Wants, string Have, string Fits)>();
        if (needsAp && !needsAd) checks.Add((apShare, L("build de AP", "an AP build"), L("de AD", "AD"), L("de AP", "AP")));
        if (needsAd && !needsAp) checks.Add((1 - apShare, L("build de AD", "an AD build"), L("de AP", "AP"), L("de AD", "AD")));
        if (needsAbilities && !needsAttacks) checks.Add((abilityShare, L("habilidades", "abilities"), L("de ataques básicos", "basic attacks"), L("de habilidades", "abilities")));
        if (needsAttacks && !needsAbilities) checks.Add((1 - abilityShare, L("ataques básicos", "basic attacks"), L("de habilidades", "abilities"), L("de ataques básicos", "basic attacks")));
        var worst = checks.MinBy(c => c.Fit);

        string whose = fromItems ? L("tu build va", "your build is") : L($"con {Data.ChampionName(champion)} se suele ir", $"{Data.ChampionName(champion)} usually goes");
        if (worst.Fit < 0.5)
        {
            double penalty = (worst.Fit - 0.5) * 0.2; // hasta −0,10 si no encaja nada
            return penalty <= -0.02 ? new AugmentSynergy(penalty, L($"Pide {worst.Wants}, y {whose} {worst.Have}", $"Wants {worst.Wants}, and {whose} {worst.Have}")) : null;
        }
        return worst.Fit >= 0.75
            ? new AugmentSynergy(0.02, fromItems ? L($"Encaja con tu build {worst.Fits}", $"Fits your {worst.Fits} build") : L($"Encaja con la build habitual de {Data.ChampionName(champion)} ({worst.Fits})", $"Fits the usual {Data.ChampionName(champion)} build ({worst.Fits})"))
            : null;
    }

    /// <summary>Estadísticas de un aumento que le sirven a este campeón (la fuerza adaptable cuenta como AP o AD).</summary>
    static HashSet<string> UsefulTags(Augment a, bool magic)
    {
        var tags = a.StatList.Select(t => t == "Adaptive" ? (magic ? "AP" : "AD") : t).ToHashSet();
        if (magic) tags.ExceptWith(["AD", "Crit", "Lethality"]);
        else tags.ExceptWith(["AP", "MagicPen"]);
        return tags;
    }

    /// <summary>Objetos que van con un aumento para este campeón (para la tarjeta del aumento).</summary>
    public IReadOnlyList<ItemAdvice> ItemsForAugment(Augment augment, string champion, IReadOnlyList<int> myItems)
    {
        bool iAmMagic = PlaysMagic(champion, myItems, []);
        var wanted = StatTags.ItemCategories(augment.StatList, iAmMagic).ToHashSet();

        var required = stats.Links.RequiredBy(augment.Id);
        var pool = CandidatePool(champion, null).ToList();
        foreach (var id in required)
            if (pool.All(p => p.Item.Id != id) && Data.Item(id) is { } need) pool.Add((need, stats.ChampionBase(champion), 0));

        // Objetos solo de daño del tipo contrario a tu build (Filo del Infinito a Karthus, Descarga Tormentosa a Shaco AD):
        // fuera, salvo que el aumento pida justo ese tipo de daño o los necesite.
        bool wantsAd = augment.StatList.Any(t => t is "AD" or "Crit" or "AS" or "OnHit" or "Lethality");
        bool wantsAp = augment.StatList.Any(t => t is "AP" or "MagicPen");
        bool WrongType(ItemInfo i) => i.IsOffensive && !i.IsDefensive &&
            (iAmMagic ? !wantsAd && AdItem.Any(i.Has) && !ApItem.Any(i.Has) : !wantsAp && ApItem.Any(i.Has) && !AdItem.Any(i.Has));
        // Si pide varios a elegir (Mejora de Brillo: todos los objetos con Brillo), solo los de tu tipo de daño.
        bool anyRightRequired = required.Any(id => Data.Item(id) is { } r && !WrongType(r));
        bool WrongDamage(ItemInfo i) => (!required.Contains(i.Id) || anyRightRequired) && WrongType(i);
        var ordered = pool
            .Where(c => !Owns(myItems, c.Item.Id) && !c.Item.IsBoots && !WrongDamage(c.Item) && !ConflictsWithOwned(c.Item, myItems))
            .Select(c =>
            {
                var match = c.Item.Categories.Count(wanted.Contains);
                var lift = stats.AugmentItemLift(augment.Id, c.Item.Id);
                bool needed = required.Contains(c.Item.Id);
                double score = (needed ? 1 : 0) + match * 0.03 + (lift?.Lift ?? 0) + (c.Rate - 0.5) * 0.5;
                string reason = needed ? L("lo necesita este aumento", "this augment needs it")
                              : lift is { Lift: > 0.02 } l ? L($"rinde mejor con este aumento ({l.Games} partidas)", $"does better with this augment ({l.Games} games)")
                              : match > 0 ? L("potencia lo mismo que el aumento", "boosts the same as the augment") : "";
                return new ItemAdvice(c.Item, score, c.Games, reason);
            })
            .Where(a => a.Reason != "")
            .OrderByDescending(a => a.Score);
        return TakeWithinLimit(ordered, 3, ArmorPenSlotsLeft(myItems));
    }

    /// <summary>Objetos candidatos: los que se ven con el campeón; si hay pocos datos, los que encajan con su tipo de daño.</summary>
    /// <summary>Objetos que no se compran: aparecen al cargar del todo otro objeto (lágrima) → el que sí se compra.</summary>
    static readonly Dictionary<int, int> TransformedFrom = new()
    {
        [3040] = 3003, // Abrazo del Serafín ← Báculo del Arcángel
        [3042] = 3004, // Muramana ← Manamune
        [3121] = 3119, // Invierno Nórdico ← Umbral del Invierno
    };

    /// <summary>¿Lo llevas ya (o ya se transformó, p. ej. el Arcángel en Serafín)?</summary>
    static bool Owns(IReadOnlyList<int> myItems, int id) =>
        myItems.Contains(id) || TransformedFrom.Any(kv => kv.Value == id && myItems.Contains(kv.Key));

    IEnumerable<(ItemInfo Item, double Rate, int Games)> CandidatePool(string champion, DamageMix? mix)
    {
        bool magic = IsMagic(champion);
        var fromStats = stats.ItemRates(champion, mix ?? DamageMix.Mixed)
            // Abrazo del Serafín, Muramana… salen en las estadísticas pero no se compran: se recomienda el objeto que se transforma en ellos.
            .Select(r => (Item: Data.Item(TransformedFrom.GetValueOrDefault(r.Item, r.Item)), r.Rate, r.Games))
            .DistinctBy(r => r.Item?.Id)
            // Lo que compran miles de jugadores con este campeón vale aunque no encaje con su rol "de libro".
            // La Espátula Dorada y demás recompensas de aumentos salen en las estadísticas, pero no se pueden comprar;
            // tampoco lo que no está en la tienda (p. ej. la Diadema Musical).
            // Los de inicio (Orbe del Guardián, Doran…) tampoco: no son un objeto de la build.
            .Where(r => r.Item is not null && r.Item.InStore && !r.Item.Has("Lane") && !stats.Links.IsReward(r.Item.Id) && !OffRoleSupportItem(r.Item, champion))
            .Where(r => r.Item!.IsBoots || Fits(r.Item, champion, magic) || stats.GlobalItem(champion, r.Item.Id) is not null)
            .Select(r => (r.Item!, r.Rate, r.Games))
            .ToList();
        if (fromStats.Count(r => !r.Item1.IsBoots) >= 6) return fromStats;

        // Sin datos suficientes: objetos de la tienda que encajan con el campeón.
        var known = fromStats.Select(r => r.Item1.Id).ToHashSet();
        var fallback = Data.Items.Values
            .Where(i => i.IsFinal && i.InStore && i.Id < 10000 && !known.Contains(i.Id) && !i.Has("Boots") && stats.SeenInMayhem(i.Id) && !stats.Links.IsReward(i.Id))
            .Where(i => Fits(i, champion, magic) && !OffRoleSupportItem(i, champion))
            .Select(i => (i, stats.ChampionBase(champion), 0));
        return fromStats.Concat(fallback);
    }

    /// <summary>
    /// ¿Le sirve este objeto al campeón? Magos: poder de habilidad. Tiradores: daño, crítico o velocidad de ataque.
    /// Asesinos y demás físicos: daño o crítico. Tanques y luchadores además pueden ir con objetos defensivos.
    /// </summary>
    bool Fits(ItemInfo i, string champion, bool magic)
    {
        var role = Data.Champion(champion)?.Roles.FirstOrDefault();
        bool frontline = role is "tank" or "fighter";
        if (frontline && i.IsDefensive) return true;
        return magic ? i.Has("SpellDamage")
             : i.Has("Damage") || i.Has("CriticalStrike") || (role == "marksman" && i.Has("AttackSpeed"));
    }

    const int InventorySlots = 6;
    /// <summary>Por cada estadística que pide un aumento tuyo y da el objeto (hasta 2 por aumento).</summary>
    const double AugmentMatchBonus = 0.04;
    /// <summary>Lo que suma el objeto que compra la mayoría con tu campeón (el 60 % o más de las partidas: todo).</summary>
    const double MaxPopularBonus = 0.06;
    const double OffMetaPenalty = -0.05;

    /// <summary>
    /// Cuanto más se compra con tu campeón en Mayhem, más suma: la build habitual de la página manda salvo que haya un
    /// motivo claro (un aumento, el equipo enemigo). Antes un objeto raro con buen % en pocas partidas le ganaba.
    /// Con otro estilo de build por tus aumentos (<see cref="_style"/>), cuenta lo que se compra en ese estilo.
    /// </summary>
    double PopularBonus(string champion, int itemId) =>
        _style is not null ? MaxPopularBonus * Math.Min(_style.Items.GetValueOrDefault(itemId), 0.6) / 0.6
        : stats.GlobalItem(champion, itemId) is { PickRate: { } pick } ? MaxPopularBonus * Math.Min(pick, 60) / 60 : 0;
    /// <summary>Con otro estilo de build, un objeto que sale en menos de esta parte de sus partidas no entra como candidato extra.</summary>
    const double StyleItemMin = 0.1;
    /// <summary>Tope de todo lo que suman tus aumentos a un objeto.</summary>
    const double MaxAugmentBonus = 0.14;
    const double ManaScalingBonus = 0.05;

    /// <summary>¿El efecto del aumento crece con tu maná máximo? ("+0.5% per 100 maximum mana", "según tu maná máximo").</summary>
    static bool ScalesWithMana(Augment a) => !a.IsAnvil && System.Text.RegularExpressions.Regex.IsMatch(a.Description + " " + a.ExtraDescription,
        @"(per\W+100|of\W+your)\W+(maximum\W+|max\W+)?mana|\d%\W+(maximum|max)\W+mana|(seg[uú]n|de)\W+tu\W+man[aá]\W+m[aá]ximo", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Objeto que un aumento tuyo necesita para funcionar: por encima de cualquier otra cosa.</summary>
    const double RequiredBonus = 0.25;
    const double SellRatio = 0.7;
    /// <summary>Cuánto mejor tiene que ser el objeto nuevo para que valga la pena vender (evita cambios por nada).</summary>
    const double SwapMargin = 0.03;
    const double SellFinishedMargin = 0.06;

    /// <summary>
    /// Con el inventario lleno: el objeto tuyo que menos aporta contra este equipo y el mejor que lo sustituye.
    /// Nunca vende botas ni objetos que acumulan cargas, y respeta el límite de penetración de armadura.
    /// </summary>
    SwapAdvice? BestSwap(string champion, IReadOnlyList<int> myItems, List<ItemAdvice> ranked, EnemyProfile profile,
                         IReadOnlyList<Augment> myAugments, bool iAmMagic, double gold, IReadOnlySet<int> keep)
    {
        var rates = new Dictionary<int, (double Rate, int Games)>();
        foreach (var r in stats.ItemRates(champion, profile.Mix)) rates.TryAdd(r.Item, (r.Rate, r.Games));
        double baseRate = stats.ChampionBase(champion);

        // Un componente con el que estás haciendo un objeto bueno no se vende: se termina.
        // Tampoco lo que dijiste que te quedas (botón "me quedo con el item"), ni sus piezas.
        var building = ranked.Where(r => r.InProgress).Take(3).Concat(ranked.Where(r => keep.Contains(r.Item.Id))).ToList();
        var owned = myItems.Distinct().Select(Data.Item).OfType<ItemInfo>()
            .Where(i => i.PriceTotal > 0 && !i.Has("Trinket") && !i.Has("Consumable") && !i.IsBoots && !i.Is(ItemCounters.Stacks))
            .Where(i => !keep.Contains(i.Id))
            .Where(i => i.IsFinal || !building.Any(b => BuildsInto(b.Item, i.Id)))
            .Select(i =>
            {
                var (rate, games) = rates.GetValueOrDefault(i.Id, (baseRate, 0));
                var a = Score(i, rate, games, champion, profile, myAugments, iAmMagic);
                // Un componente suelto ocupando casilla es lo primero que sobra.
                return (Advice: i.IsFinal ? a : a with { Score = a.Score - 0.05 }, Rate: rate, Games: games);
            })
            .OrderBy(o => o.Advice.Score)
            .ToList();

        int penOwned = myItems.Count(id => Data.Item(id) is { IsFinal: true } i && i.Has("ArmorPenetration"));
        foreach (var (sell, rate, games) in owned)
        {
            var rest = myItems.ToList();
            rest.Remove(sell.Item.Id);
            int penAfterSale = penOwned - (sell.Item.IsFinal && sell.Item.Has("ArmorPenetration") ? 1 : 0);
            var buy = ranked.FirstOrDefault(c => penAfterSale + (c.Item.Has("ArmorPenetration") ? 1 : 0) <= MaxArmorPenItems
                                                && !(c.Item.From ?? []).Contains(sell.Item.Id)); // no vender una pieza del propio objeto
            // Un objeto terminado se vende con pérdida (30 % del oro): solo si el nuevo es bastante mejor.
            if (buy is null || buy.Score - sell.Score < (sell.Item.IsFinal ? SellFinishedMargin : SwapMargin)) continue;

            int sellGold = (int)(sell.Item.PriceTotal * SellRatio);
            int cost = Data.RemainingCost(buy.Item, rest);
            string why =
                !sell.Item.IsFinal ? L("es un componente suelto", "it is a loose component")
                : profile.Mix == DamageMix.Physical && sell.Item.Has("SpellBlock") && !sell.Item.Has("Armor") ? L("resist. mágica contra un equipo de daño físico", "magic resist against a physical team")
                : profile.Mix == DamageMix.Magic && sell.Item.Has("Armor") && !sell.Item.Has("SpellBlock") ? L("armadura contra un equipo de daño mágico", "armor against a magic team")
                // Con datos de todos los jugadores, su % real; si no, el de tus partidas.
                : stats.GlobalItem(champion, sell.Item.Id) is { WinRate: { } globalRate } ? L($"rinde poco con tu campeón: {globalRate:0.#} % de victorias (~{games:N0} partidas en Mayhem)", $"weak on your champion: {globalRate:0.#}% win rate (~{games:N0} Mayhem games)")
                : games >= 5 ? L($"solo {rate:P0} de victorias en tus partidas ({games})", $"only {rate:P0} win rate in your games ({games})")
                : L("es el que menos te aporta en esta partida", "it helps you the least this game");
            return new SwapAdvice(sell.Item, sellGold, why, buy, cost, Math.Max(0, cost - sellGold - (int)gold));
        }
        return null;
    }

    /// <summary>
    /// Si el enemigo tiene algo muy marcado (mucha curación, escudos, críticos o velocidad de ataque) y ninguno
    /// de los 3 objetos lo contrarresta, el tercero se cambia por el mejor objeto que sí lo hace.
    /// </summary>
    void EnsureCounter(List<ItemAdvice> items, List<ItemAdvice> ranked, EnemyProfile profile, int penSlots, IReadOnlyList<int> myItems)
    {
        (bool Strong, Func<ItemInfo, bool> Counters)[] needs =
        [
            (profile.Healers >= 3, i => i.AppliesWounds),
            (profile.Shields >= 3, i => i.Is(ItemCounters.AntiShield)),
            (profile.CritItems >= 4, i => i.Is(ItemCounters.AntiCrit)),
            (profile.AttackSpeedItems >= 4, i => i.Is(ItemCounters.AntiAttackSpeed)),
        ];
        foreach (var (strong, counters) in needs)
        {
            // Si ya llevas uno (Recordatorio Mortal contra curaciones…), no hace falta otro.
            if (!strong || items.Any(a => counters(a.Item)) || myItems.Any(id => Data.Item(id) is { } own && counters(own))) continue;
            var best = ranked.FirstOrDefault(a => counters(a.Item) && !items.Contains(a));
            if (best is null) continue;
            int bestPen = best.Item.Has("ArmorPenetration") ? 1 : 0;
            if (bestPen > penSlots) continue;
            // Los mejores que quepan junto al counter sin pasarse de 2 objetos de penetración de armadura.
            var kept = TakeWithinLimit(ranked.Where(a => a != best && !Clash(a.Item.Id, best.Item.Id)), items.Count - 1, penSlots - bestPen);
            items.Clear();
            items.AddRange(kept);
            items.Add(best);
            return; // como mucho un cambio: los primeros siguen siendo los que mejor te funcionan
        }
    }

    /// <summary>
    /// Objetos que contrarrestan lo que lleva el enemigo (anticríticos, antiescudos, % de vida…) aunque no salgan en
    /// tus partidas con este campeón; solo si encajan con su tipo de daño (o son defensivos y eres tanque/luchador).
    /// </summary>
    IEnumerable<(ItemInfo Item, double Rate, int Games)> CounterCandidates(string champion, EnemyProfile profile, bool iAmMagic, HashSet<int> exclude)
    {
        var needed = new List<string>();
        if (profile.CritItems >= 3) needed.Add(ItemCounters.AntiCrit);
        if (profile.AttackSpeedItems >= 3) needed.Add(ItemCounters.AntiAttackSpeed);
        if (profile.Shields >= 2) needed.Add(ItemCounters.AntiShield);
        if (profile.Tanks >= 2 || profile.HealthItems >= 6) needed.Add(ItemCounters.PercentHealth);
        bool wantsWounds = profile.Healers >= 2;
        bool wantsMr = profile.MagicShare > 0.53, wantsArmor = profile.MagicShare < 0.47;
        if (needed.Count == 0 && !wantsWounds && !wantsMr && !wantsArmor) yield break;

        double baseRate = stats.ChampionBase(champion);
        foreach (var i in Data.Items.Values)
        {
            // Tampoco las recompensas de aumentos (la Espátula Dorada da armadura y resist. mágica, pero no se compra).
            if (!i.IsFinal || !i.InStore || i.Id >= 10000 || i.IsBoots || exclude.Contains(i.Id) || !stats.SeenInMayhem(i.Id) || stats.Links.IsReward(i.Id)) continue;
            if (!needed.Any(i.Is) && !(wantsWounds && i.AppliesWounds) && !(wantsMr && i.Has("SpellBlock")) && !(wantsArmor && i.Has("Armor"))) continue;
            if (Fits(i, champion, iAmMagic) && !OffRoleSupportItem(i, champion) && !exclude.Any(e => Clash(e, i.Id))) yield return (i, baseRate, 0);
        }
    }

    /// <summary>"62 % de victorias en Mayhem (25.800 partidas)" con datos de todos; con los tuyos, tu %; sin datos, null.</summary>
    string? WinRateReason(string champion, int itemId, double rate, int games) =>
        stats.GlobalItem(champion, itemId) is { WinRate: { } wr } ? L($"{wr / 100:P0} de victorias en Mayhem ({games:N0} partidas)", $"{wr / 100:P0} win rate in Mayhem ({games:N0} games)")
        : games > 0 ? L($"{rate:P0} de victorias en {games} partidas", $"{rate:P0} win rate in {games} games") : null;

    ItemAdvice Score(ItemInfo item, double rate, int games, string champion, EnemyProfile profile, IReadOnlyList<Augment> myAugments, bool iAmMagic)
    {
        double score = rate - stats.ChampionBase(champion);
        string? reason = null;
        double reasonWeight = 0;
        // El motivo que se muestra es el del bonus más grande.
        void Bonus(bool when, double amount, string why)
        {
            if (!when) return;
            score += amount;
            if (why != "" && amount > reasonWeight) { reason = why; reasonWeight = amount; }
        }

        // La build de la página (lo que compra la mayoría con tu campeón en mayhemmeta.com) es el punto de partida.
        if (_style is not null)
        {
            // Otro estilo de build por tus aumentos: manda lo que compra la gente en ese estilo, no la build habitual.
            double share = _style.Items.GetValueOrDefault(item.Id);
            string? aug = myAugments.FirstOrDefault(a => a.Id == _style.AugmentId)?.DisplayName;
            Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : L(
                $"con «{aug}» el {_style.Share:P0} va de {StyleName(_style.Key)} y gana más ({_style.WinRate:0.#} % frente a {_style.UsualWinRate:0.#} % de {StyleName(_style.Usual)}): lo compra el {share:P0}",
                $"with “{aug}” {_style.Share:P0} go {StyleName(_style.Key)} and win more ({_style.WinRate:0.#}% vs {_style.UsualWinRate:0.#}% {StyleName(_style.Usual)}): {share:P0} buy it"));
            Bonus(share < 0.05, OffMetaPenalty, "");
        }
        else
        {
            if (stats.GlobalItem(champion, item.Id) is { PickRate: >= 20 and var pick })
                Bonus(true, PopularBonus(champion, item.Id), L($"build habitual: lo compra el {pick:0} % con {Data.ChampionName(champion)} en Mayhem", $"standard build: {pick:0}% of {Data.ChampionName(champion)} players buy it in Mayhem"));
            else Bonus(true, PopularBonus(champion, item.Id), "");
            // Objeto que no sale en las tablas de tu campeón (ni entre los más comprados ni entre los que más ganan): casi nadie
            // lo lleva con él. Contra tanques sumaba igual que el Rey Arruinado y colaba la Cicloespada Voltaica a Jax.
            Bonus(stats.Global.For(champion) is { } pageBuild && pageBuild.Item(item.Id) is null, OffMetaPenalty, "");
        }

        Bonus(profile.CritItems >= 3 && item.Is(ItemCounters.AntiCrit), 0.045, L($"menos daño de críticos: llevan {profile.CritItems} objetos/aumentos de crítico", $"less crit damage: they have {profile.CritItems} crit items/augments"));
        Bonus(profile.AttackSpeedItems >= 3 && item.Is(ItemCounters.AntiAttackSpeed), 0.04, L($"frena su velocidad de ataque ({profile.AttackSpeedItems} objetos/aumentos)", $"slows their attack speed ({profile.AttackSpeedItems} items/augments)"));
        Bonus(profile.Shields >= 2 && item.Is(ItemCounters.AntiShield), 0.04, L($"rompe escudos: {profile.Shields} escudos enemigos", $"breaks shields: {profile.Shields} enemy shields"));
        Bonus((profile.Tanks >= 2 || profile.HealthItems >= 6) && item.Is(ItemCounters.PercentHealth), 0.035,
            profile.Tanks >= 2 ? L($"daño por % de vida contra {profile.Tanks} tanques", $"% health damage vs {profile.Tanks} tanks") : L($"daño por % de vida: compran mucha vida ({profile.HealthItems})", $"% health damage: they stack health ({profile.HealthItems})"));
        // Resistencias según cuánto se inclina el daño enemigo (58 % mágico ya pide algo de resist. mágica).
        double magicLean = profile.MagicShare - 0.5;
        Bonus(magicLean > 0.03 && item.Has("SpellBlock"), Math.Min(0.05, magicLean * 0.2 + 0.01),
            L($"resist. mágica: el {profile.MagicShare:P0} del daño enemigo es mágico", $"magic resist: {profile.MagicShare:P0} of enemy damage is magic"));
        Bonus(magicLean < -0.03 && item.Has("Armor"), Math.Min(0.05, -magicLean * 0.2 + 0.01),
            L($"armadura: el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"armor: {1 - profile.MagicShare:P0} of enemy damage is physical"));
        Bonus(profile.Healers >= 2 && item.AppliesWounds, 0.04, L("heridas graves: el enemigo se cura mucho", "grievous wounds: the enemy heals a lot"));
        Bonus(!_tankBuild && !iAmMagic && (profile.Tanks >= 2 || profile.ArmorItems >= 3) && item.Has("ArmorPenetration"), 0.03, L("penetración de armadura: tanques / armadura", "armor penetration: tanks / armor"));
        Bonus(!_tankBuild && iAmMagic && (profile.Tanks >= 2 || profile.MagicResistItems >= 3) && item.Has("MagicPenetration"), 0.03, L("penetración mágica: tanques / resist. mágica", "magic penetration: tanks / magic resist"));
        // Resistencias del tipo equivocado restan.
        Bonus(magicLean < -0.05 && item.Has("SpellBlock") && !item.Has("Armor"), -0.03, "");
        Bonus(magicLean > 0.05 && item.Has("Armor") && !item.Has("SpellBlock"), -0.03, "");

        // Build de tanque (Amumu, Nasus… con objetos y aumentos de aguante): un objeto solo de daño encaja peor.
        // Los mixtos (Morellonomicón: AP + vida) también: de tanque se busca aguante (Cota de Espinas da las heridas graves).
        Bonus(_tankBuild && item.IsOffensive, -0.05, "");

        // Tus objetos ya deciden el daño (Shaco con Youmuu y Recordatorio Mortal): un objeto solo del otro tipo encaja mal.
        // La velocidad de ataque sola no hace a un objeto de AD: el Diente de Nashor (AP + vel. ataque) es de AP para Shaco AD.
        bool apOnly = ApItem.Any(item.Has) && !item.Has("Damage") && !item.Has("CriticalStrike") && !item.Has("ArmorPenetration"),
             adOnly = AdItem.Any(item.Has) && !ApItem.Any(item.Has);
        // Fuerte: un aumento de prisa o de "habilidades" no debe colar una Antorcha de Fuegoscuro en una build AD.
        Bonus(_fromItems && apOnly && _apShare < 0.35, -0.2, "");
        Bonus(_fromItems && adOnly && _apShare > 0.65, -0.2, "");

        // Un aumento que necesita este objeto para funcionar (misiones, "Mejorar X"…): va primero.
        foreach (var aug in myAugments)
            Bonus(stats.Links.RequiredBy(aug.Id).Contains(item.Id) && !WrongDamageFor(item, iAmMagic), RequiredBonus, L($"lo necesita tu aumento «{aug.DisplayName}»", $"your augment “{aug.DisplayName}” needs it"));

        // Sinergia con los aumentos que ya elegiste: es lo que más pesa al elegir entre objetos parecidos.
        // Se suma la de todos tus aumentos (con tope, para que no tape todo lo demás) y se nombran los que encajan.
        double augBonus = 0;
        var matches = new List<(string Name, double Amount)>();
        foreach (var aug in myAugments)
        {
            var wanted = StatTags.ItemCategories(aug.StatList, iAmMagic).ToHashSet();
            // De tanque, un aumento de "habilidades" pide prisa, no daño (antes recomendaba Morellonomicón a Amumu tanque).
            if (_tankBuild && !aug.StatList.Any(t => t is "AP" or "AD" or "Adaptive")) wanted.ExceptWith(["SpellDamage", "Damage"]);
            // Aceleración de habilidad y reducción de enfriamiento son la misma estadística: no cuentan dos veces.
            int match = item.Categories.Where(wanted.Contains).Select(c => c == "CooldownReduction" ? "AbilityHaste" : c).Distinct().Count();
            var lift = stats.AugmentItemLift(aug.Id, item.Id);
            double amount = Math.Min(match, 2) * AugmentMatchBonus + (lift is { Lift: > 0.02 } l ? l.Lift : 0);
            if (amount <= 0) continue;
            augBonus += amount;
            matches.Add((aug.DisplayName, amount));
        }
        var named = matches.OrderByDescending(m => m.Amount).Take(2).Select(m => English ? $"“{m.Name}”" : $"«{m.Name}»").ToList();
        Bonus(augBonus > 0, Math.Min(augBonus, MaxAugmentBonus),
            named.Count == 1 ? L($"va con tu aumento {named[0]}", $"fits your augment {named[0]}") : L($"va con tus aumentos {string.Join(" y ", named)}", $"fits your augments {string.Join(" and ", named)}"));
        // Un aumento que crece con tu maná máximo (Sobrecarga, A Tope, Paso Redoblado) pide objetos con maná. Va aparte del
        // tope: con 3-4 aumentos de AP todos los objetos de AP llegan al tope y la Vara de las Edades no se distinguía.
        if (item.Has("Mana") && myAugments.FirstOrDefault(ScalesWithMana) is { } manaAug)
            Bonus(true, ManaScalingBonus, L($"«{manaAug.DisplayName}» crece con tu maná máximo", $"“{manaAug.DisplayName}” scales with your max mana"));

        reason ??= WinRateReason(champion, item.Id, rate, games) ?? L("encaja con tu campeón", "fits your champion");
        return new ItemAdvice(item, score, games, reason);
    }

    ItemAdvice? BestBoots(string champion, EnemyProfile profile, IReadOnlyList<Augment> myAugments, bool iAmMagic, IReadOnlyList<int> myItems)
    {
        bool marksman = Data.Champion(champion)?.Roles.FirstOrDefault() == "marksman";
        var (_, abilityShare, _) = BuildProfile(champion, myItems, myAugments);
        var dataRates = stats.ItemRates(champion, profile.Mix).ToDictionary(r => r.Item, r => (r.Rate, r.Games));
        double baseRate = stats.ChampionBase(champion);

        return Data.Items.Values
            // Solo botas normales (nivel 2) que existen en Mayhem; las mejoradas de la Grieta no están en este modo.
            .Where(i => i.IsBoots && i.IsFinal && i.InStore && i.Id < 10000 && i.PriceTotal <= 1300 && stats.SeenInMayhem(i.Id))
            .Select(i =>
            {
                var (rate, games) = dataRates.GetValueOrDefault(i.Id, (baseRate, 0));
                double score = rate - baseRate + PopularBonus(champion, i.Id);
                string? reason = null;
                void Bonus(bool when, double amount, string why) { if (when) { score += amount; reason ??= why; } }

                double lean = profile.MagicShare - 0.5;
                Bonus(lean < -0.03 && i.Has("Armor"), Math.Min(0.05, -lean * 0.2 + 0.01), L($"armadura: el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"armor: {1 - profile.MagicShare:P0} of enemy damage is physical"));
                Bonus(lean > 0.03 && i.Has("SpellBlock"), Math.Min(0.05, lean * 0.2 + 0.01), L($"resist. mágica y tenacidad: el {profile.MagicShare:P0} del daño enemigo es mágico", $"magic resist and tenacity: {profile.MagicShare:P0} of enemy damage is magic"));
                Bonus(lean > 0.05 && i.Has("Armor") && !i.Has("SpellBlock"), -0.03, "");
                Bonus(lean < -0.05 && i.Has("SpellBlock") && !i.Has("Armor"), -0.03, "");
                Bonus(iAmMagic && !_tankBuild && i.Has("MagicPenetration"), 0.025, L("penetración mágica para tu daño", "magic penetration for your damage"));
                // Botas de AP con build AD (Botas del Hechicero a un Shaco AD: las ganan los Shaco AP): no.
                Bonus(!iAmMagic && i.Has("MagicPenetration"), -0.06, "");
                // …ni de ataques (Grebas del Berserker) a un mago de habilidades como Zilean o Vladimir.
                // (Con aumentos de ataques, como Espada de Fuego en Teemo, sí sirven.)
                Bonus(iAmMagic && i.Has("AttackSpeed") && abilityShare > 0.6 && !myAugments.Any(a => a.StatList.Any(t => t is "AS" or "OnHit")), -0.06, "");
                Bonus(marksman && i.Has("AttackSpeed"), 0.025, L("velocidad de ataque para tu campeón", "attack speed for your champion"));
                // Como mucho una vez: con dos aumentos de velocidad de ataque, Darius acababa con Grebas del Berserker (0,3 % en la página).
                if (myAugments.FirstOrDefault(aug => StatTags.ItemCategories(aug.StatList, iAmMagic).Any(i.Has)) is { } fitAug)
                    Bonus(true, AugmentMatchBonus, L($"va con tu aumento «{fitAug.DisplayName}»", $"fits your augment “{fitAug.DisplayName}”"));

                return new ItemAdvice(i, score, games, reason ?? WinRateReason(champion, i.Id, rate, games) ?? L("opción estándar", "standard pick"));
            })
            .Select(a => InBuilding(a, myItems)) // con las botas básicas ya compradas, las de nivel 2 están "a medias"
            .OrderByDescending(a => a.Score)
            .FirstOrDefault();
    }
}
