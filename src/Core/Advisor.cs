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
        Fed is { } f ? (f.MagicShare >= 0.6 ? LF($"{f.Name} va alimentado ({f.Kills}/{f.Deaths}): hace daño mágico", $"{f.Name} is fed ({f.Kills}/{f.Deaths}): magic damage")
                      : f.MagicShare <= 0.4 ? LF($"{f.Name} va alimentado ({f.Kills}/{f.Deaths}): hace daño físico", $"{f.Name} is fed ({f.Kills}/{f.Deaths}): physical damage")
                      : LF($"{f.Name} va alimentado ({f.Kills}/{f.Deaths})", $"{f.Name} is fed ({f.Kills}/{f.Deaths})"))
        : MagicChampions >= 3 ? LF($"{MagicChampions} campeones AP enfrente", $"{MagicChampions} AP champions on the enemy team")
        : PhysicalChampions >= 3 ? LF($"{PhysicalChampions} campeones AD enfrente", $"{PhysicalChampions} AD champions on the enemy team")
        : Tanks >= 2 ? LF($"{Tanks} tanques enfrente", $"{Tanks} tanks on the enemy team")
        : Healers >= 3 ? LF($"{Healers} enemigos con curación", $"{Healers} enemies with healing")
        : null;

    public string Describe()
    {
        var parts = new List<string>
        {
            Mix switch
            {
                DamageMix.Magic => LF($"Mucho daño mágico ({MagicShare:P0})", $"Lots of magic damage ({MagicShare:P0})"),
                DamageMix.Physical => LF($"Mucho daño físico ({1 - MagicShare:P0})", $"Lots of physical damage ({1 - MagicShare:P0})"),
                _ => T("Daño mixto", "Mixed damage", "Dano misto", "Dégâts mixtes", "Gemischter Schaden", "Sát thương hỗn hợp"),
            },
        };
        if (Tanks >= 2) parts.Add(LF($"{Tanks} tanques", $"{Tanks} tanks"));
        if (Healers >= 2) parts.Add(LF($"{Healers} con curación", $"{Healers} healers"));
        if (ArmorItems >= 3) parts.Add(LF($"compran armadura ({ArmorItems})", $"buying armor ({ArmorItems})"));
        if (MagicResistItems >= 3) parts.Add(LF($"compran resist. mágica ({MagicResistItems})", $"buying magic resist ({MagicResistItems})"));
        if (CritItems >= 3) parts.Add(LF($"van de crítico ({CritItems})", $"going crit ({CritItems})"));
        if (AttackSpeedItems >= 3) parts.Add(LF($"mucha velocidad de ataque ({AttackSpeedItems})", $"lots of attack speed ({AttackSpeedItems})"));
        if (Shields >= 2) parts.Add(LF($"escudos ({Shields})", $"shields ({Shields})"));
        if (HealthItems >= 6) parts.Add(LF($"mucha vida ({HealthItems})", $"lots of health ({HealthItems})"));
        if (AugmentsSeen > 0) parts.Add(LF($"{AugmentsSeen} aumentos enemigos vistos", $"{AugmentsSeen} enemy augments seen"));
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
/// <param name="Share">Qué parte de las partidas del campeón lo juega (0-1; 0 = de tus partidas, sin ese dato).</param>
public sealed record SkillAdvice(string MaxOrder, string Start, double WinRate, int Games, double Share = 0);
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
    GamePhase Phase = GamePhase.Mid,
    IReadOnlyList<StyleOption>? Styles = null,
    string? StyleSource = null,
    ItemInfo? MakeRoom = null,
    (ItemInfo Part, ItemInfo For)? EarlyPart = null,
    ItemInfo? Elixir = null,
    ItemInfo? StatBonus = null);

/// <summary>
/// Una build que se juega con tu campeón (selector del panel): cuánta gente la juega (0-1), cuánto gana (0-100), sus
/// objetos clave que aún no tienes, si es la que sigue la app ahora y si la fijaste tú.
/// </summary>
public sealed record StyleOption(string Key, string Name, double Share, double WinRate, IReadOnlyList<ItemInfo> Items, bool Active, bool Locked);

/// <summary>Un objeto de la build habitual que conviene saltarse, y por qué.</summary>
public sealed record Discard(ItemInfo Item, string Reason);
/// <summary>Quién del equipo enemigo hace qué daño (0 = todo físico, 1 = todo mágico).</summary>
public sealed record EnemyThreat(string Name, double MagicShare);

/// <summary>Un objeto de una categoría de "Adaptar build": su % de victorias con tu campeón (null = sin datos).</summary>
/// <param name="Augment">Nombre del aumento tuyo con el que encaja, si encaja con alguno.</param>
public sealed record CategoryItem(ItemInfo Item, double? WinRate, int Games, string? Augment, bool Owned);
/// <summary>Lo que pide la partida (p. ej. "RESISTENCIA MÁGICA"), por qué, y sus objetos por % de victorias.</summary>
/// <param name="Offensive">Para aprovechar al rival (heridas graves, antiescudos, % de vida, penetración), no para aguantar.</param>
public sealed record CounterCategory(string Title, string Why, IReadOnlyList<CategoryItem> Items, bool Offensive = false);

/// <summary>
/// "Adaptar build": cómo es el daño enemigo, una build situacional de 6 objetos para comparar con la recomendada,
/// las categorías de objetos que pide la partida (ordenados por % de victorias con tu campeón) y qué objetos de la
/// build habitual te puedes saltar. Son sugerencias.
/// </summary>
/// <param name="Changed">Objetos de la build situacional que no están en la recomendada.</param>
/// <param name="Replaced">Por cada objeto nuevo de la build situacional, el de la recomendada al que sustituye («cambia X por Y»).</param>
public sealed record CounterPlan(double MagicShare, IReadOnlyList<EnemyThreat> Enemies, IReadOnlyList<BuildSlot> Situational,
    IReadOnlySet<int> Changed, IReadOnlyList<CounterCategory> Categories, IReadOnlyList<Discard> Discards,
    IReadOnlyDictionary<int, ItemInfo>? Replaced = null);

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
                        IReadOnlyDictionary<string, (int Kills, int Deaths)>? enemyScores = null, double critChance = 0,
                        string? lockedStyle = null)
    {
        keep ??= new HashSet<int>();
        _critCapped = critChance >= CritCap;
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
        // La build que elegiste en el panel manda sobre todo. Si no, tus objetos ya dicen el estilo (Yi con Rey Arruinado y
        // Fin del Ingenio = on-hit): manda lo que se compra en ese estilo, sea o no el habitual del campeón. Pesa más que tus
        // aumentos: la build ya está empezada.
        if (lockedStyle is not null && LockedStyle(myChampion, lockedStyle) is { } locked) _style = locked;
        else if (StyleFromItems(myChampion, myItems) is { } byItems) _style = byItems;
        // Y al estilo habitual se le suma lo que se compra según la página del campeón (aramkit se queda corto).
        if (_style is not null) _style = WithPage(myChampion, _style);
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
            if (!Owns(myItems, id) && pool.All(p => p.Item.Id != id) && Data.Item(id) is { } wanted
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
        // Una poción (o un elixir, un Juguito de Gorrito…) ocupa casilla pero no es un objeto: con ella el inventario NO está
        // lleno, solo hay que venderla o usarla. Antes la app pedía vender un legendario recién hecho con una poción en la
        // esquina (opinión de un jugador, 2026-11-08).
        var slotItems = myItems.Select(Data.Item).OfType<ItemInfo>().Where(i => i.PriceTotal > 0 && !i.Has("Trinket")).ToList();
        static bool IsConsumable(ItemInfo i) => i.Has("Consumable");
        int usedSlots = slotItems.Count(i => !IsConsumable(i));
        bool full = usedSlots >= InventorySlots;
        var makeRoom = !full && slotItems.Count >= InventorySlots ? slotItems.Where(IsConsumable).MinBy(i => i.PriceTotal) : null;
        var buyable = full ? ranked.Where(a => a.InProgress).ToList() : ranked;
        // Build ya clara (Shaco con 2 objetos AD): los de daño contrario no entran ni como "otra opción"; si faltan opciones,
        // se completan abajo con objetos de la tienda de tu tipo de daño.
        if (_fromItems && _apShare is < 0.35 or > 0.65)
            buyable = buyable.Where(a => a.InProgress || !WrongDamageFor(a.Item, iAmMagic)
                                         || _style?.FromItems is not null && _style.Items.GetValueOrDefault(a.Item.Id) >= StyleItemMin).ToList();

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
        if (!full) EnsureCounter(myChampion, items, ranked, profile, ArmorPenSlotsLeft(myItems), myItems);
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

        var boots = ownBoots || SkipsBoots(myChampion) ? null : BestBoots(myChampion, profile, myAugments, iAmMagic, myItems);
        if (full && boots is { InProgress: false }) boots = null; // no caben unas botas nuevas

        var swap = full ? BestSwap(myChampion, myItems, ranked, profile, myAugments, iAmMagic, gold, keep) : null;

        // Lo próximo que conviene comprar: lo que ya estás terminando; si no, tu primer objeto grande, luego las botas y
        // después el mejor objeto. Lleno: terminar lo empezado o, si no hay nada, el cambio.
        // Antes las botas iban lo primero (costumbre de la Grieta del Invocador). En Mayhem casi nadie empieza por ellas:
        // según aramkit (16.21), salen con botas el 0,3 % de las Annie y el 18 % de las Jinx y los Garen, y el 26-38 % no
        // las compra en toda la partida.
        bool firstItemDone = myItems.Any(id => Data.Item(id) is { IsFinal: true, IsBoots: false, PriceTotal: >= FirstItemGold } i
                                               && !i.Has("Consumable") && !i.Has("Trinket"));
        var fullBuild = FullBuild(myChampion, myItems, ranked, boots, profile, myAugments, iAmMagic, swap?.Sell);
        // La primera opción tiene que ser de tu build completa. Con una casilla libre y un objeto contra el enemigo que hace
        // falta (Presagio de Randuin contra 4+ objetos de crítico), la build ponía el Presagio y la siguiente compra decía
        // Corazón de Acero (61 casos en pruebas/auditar-objetos, casi todos tanques con 4 objetos).
        if (!full && commit is null && items.FirstOrDefault() is { Item.IsBoots: false } top
            && fullBuild.Where(s => !s.Owned && !s.Item.Item.IsBoots).Select(s => s.Item).ToList() is { Count: > 0 } toBuy
            && toBuy.All(a => a.Item.Id != top.Item.Id))
        {
            var planned = items.FirstOrDefault(a => toBuy.Any(b => b.Item.Id == a.Item.Id)) ?? toBuy[0];
            items.Remove(planned);
            items.Insert(0, planned);
            if (items.Count > 3) items.RemoveAt(items.Count - 1);
        }
        var nextItem = commit?.Item
                       ?? (firstItemDone && boots is { InProgress: true } ? boots.Item : null)
                       ?? (full ? items.FirstOrDefault()?.Item
                           : firstItemDone ? boots?.Item ?? items.FirstOrDefault()?.Item
                           : items.FirstOrDefault()?.Item ?? boots?.Item);
        NextBuy? next = nextItem is not null
            ? new NextBuy(nextItem, Data.RemainingCost(nextItem, myItems), Math.Max(0, Data.RemainingCost(nextItem, myItems) - (int)gold))
            : swap is not null ? new NextBuy(swap.Buy.Item, swap.Cost - swap.SellGold, swap.GoldMissing) : null;

        // El orden de maximizar habilidades: el que más se juega con el campeón en Caos (aramkit); sin eso, el de tus partidas.
        var skills = CommonSkills(myChampion) ?? (stats.BestSkills(myChampion) is { } s ? new SkillAdvice(s.MaxOrder, s.Start, s.Rate, s.Games) : null);
        // Objetos empezados que no son la siguiente compra: el overlay ofrece "me quedo con este".
        var building = ranked.Where(a => a.InProgress && a.Item.Id != next?.Item.Id).Take(2).ToList();
        var plan = Plan(myChampion, myItems, enemies, profile, myAugments, iAmMagic, ranked, fullBuild);
        // Con el inventario lleno de objetos y oro de sobra: un elixir de tu tipo de build (Mayhem tiene los tres en la tienda)
        // o «Estadística adicional». Lo pidió un jugador («potion selection would be great», 2026-11-08). Es una sugerencia:
        // no hay datos de quién compra elixires.
        var spare = full && gold >= ElixirGold ? Data.Item(_tankBuild ? ElixirOfIron : iAmMagic ? ElixirOfSorcery : ElixirOfWrath) : null;
        return new Advice(profile, items, boots, next, skills, myAugments, swap, full, fullBuild, building, plan,
            Direction(myChampion, myItems, myAugments), phase, StyleOptions(myChampion, myItems), StyleSource(myChampion, myAugments),
            makeRoom, EarlyTear(fullBuild, myItems), spare, gold >= StatBonusGold && full ? Data.Item(StatBonus) : null);
    }

    const int TearOfTheGoddess = 3070, ElixirOfIron = 2138, ElixirOfSorcery = 2139, ElixirOfWrath = 2140, StatBonus = 6032;
    const int ElixirGold = 500, StatBonusGold = 750;

    /// <summary>
    /// Si tu build lleva un objeto que sale de la Lágrima de la Diosa (Báculo del Arcángel, Manamune, Umbral del Invierno) y aún
    /// no la tienes: la Lágrima y ese objeto. Se carga con el tiempo, así que conviene comprarla pronto (un jugador, 2026-11-08:
    /// «la app no dice que compres pronto una pieza importante, como la Lágrima»). Null si no toca.
    /// </summary>
    (ItemInfo Part, ItemInfo For)? EarlyTear(List<BuildSlot> fullBuild, IReadOnlyList<int> myItems)
    {
        if (myItems.Contains(TearOfTheGoddess) || Data.Item(TearOfTheGoddess) is not { } tear) return null;
        var target = fullBuild.Where(b => !b.Owned).Select(b => b.Item.Item)
            .FirstOrDefault(i => BuildsInto(i, TearOfTheGoddess) && !Owns(myItems, i.Id));
        return target is null ? null : (tear, target);
    }

    /// <summary>
    /// El orden de maximizar habilidades más jugado con el campeón en Caos (aramkit: los 5 más comunes, con millones de
    /// partidas) y qué parte de sus partidas lo juega. Lo pidió un jugador el 2026-11-06 («show common skill progression»).
    /// Null sin datos.
    /// </summary>
    public SkillAdvice? CommonSkills(string champion)
    {
        var alias = Data.Champion(champion)?.Alias ?? champion;
        var orders = stats.AramkitBuilds.Skills(alias);
        if (orders.Count == 0) return null;
        int total = Math.Max(stats.AramkitBuilds.Games(alias), orders.Sum(o => o.Games));
        var top = orders[0];
        return new SkillAdvice(top.Order, "", top.WinRate / 100, top.Games, (double)top.Games / total);
    }

    /// <summary>Precio desde el que un objeto terminado cuenta como «tu primer objeto» (después van las botas).</summary>
    const int FirstItemGold = 2000;

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
        string magicWho = apCount >= 2 ? LF($"{apCount} hacen daño mágico", $"{apCount} deal magic damage") : L("Mucho daño mágico", "Lots of magic damage");
        string physicalWho = adCount >= 2 ? LF($"{adCount} hacen daño físico", $"{adCount} deal physical damage") : L("Mucho daño físico", "Lots of physical damage");

        // Lo que pide la partida, de más a menos importante.
        var needs = new List<(string Title, string Why, double Weight, Func<ItemInfo, bool> Counters)>();
        double lean = profile.MagicShare - 0.5;
        if (lean > 0.03) needs.Add((T("RESISTENCIA MÁGICA", "MAGIC RESIST", "RESISTÊNCIA MÁGICA", "RÉSISTANCE MAGIQUE", "MAGIERESISTENZ", "KHÁNG PHÉP"),
            LF($"{magicWho}: el {profile.MagicShare:P0} del daño", $"{magicWho}: {profile.MagicShare:P0} of the damage") + With(Names(t => t.MagicShare >= 0.6)), lean, i => i.Has("SpellBlock")));
        if (lean < -0.03) needs.Add((T("ARMADURA", "ARMOR", "ARMADURA", "ARMURE", "RÜSTUNG", "GIÁP"),
            LF($"{physicalWho}: el {1 - profile.MagicShare:P0} del daño", $"{physicalWho}: {1 - profile.MagicShare:P0} of the damage") + With(Names(t => t.MagicShare <= 0.4)), -lean, i => i.Has("Armor")));
        if (profile.Healers >= 2) needs.Add((T("HERIDAS GRAVES", "GRIEVOUS WOUNDS", "FERIDAS DOLOROSAS", "BLESSURES GRAVES", "KLAFFENDE WUNDEN", "VẾT THƯƠNG SÂU"), LF($"{profile.Healers} enemigos se curan mucho", $"{profile.Healers} enemies heal a lot"), 0.03 * profile.Healers, i => i.AppliesWounds));
        if (profile.Shields >= 2) needs.Add((T("ANTIESCUDOS", "ANTI-SHIELD", "ANTIESCUDO", "ANTI-BOUCLIER", "ANTI-SCHILD", "PHÁ LÁ CHẮN"), LF($"{profile.Shields} escudos en el equipo enemigo", $"{profile.Shields} shields on the enemy team"), 0.025 * profile.Shields, i => i.Is(ItemCounters.AntiShield)));
        if (profile.CritItems >= 3) needs.Add((T("ANTICRÍTICO", "ANTI-CRIT", "ANTICRÍTICO", "ANTI-CRITIQUE", "ANTI-KRIT", "CHỐNG CHÍ MẠNG"), LF($"Llevan {profile.CritItems} objetos de crítico", $"They have {profile.CritItems} crit items"), 0.02 * profile.CritItems, i => i.Is(ItemCounters.AntiCrit)));
        if (profile.AttackSpeedItems >= 3) needs.Add((T("CONTRA VELOCIDAD DE ATAQUE", "ANTI-ATTACK SPEED", "CONTRA VELOCIDADE DE ATAQUE", "CONTRE LA VITESSE D'ATTAQUE", "GEGEN ANGRIFFSTEMPO", "CHỐNG TỐC ĐỘ ĐÁNH"), LF($"Llevan {profile.AttackSpeedItems} objetos de velocidad de ataque", $"They have {profile.AttackSpeedItems} attack speed items"), 0.02 * profile.AttackSpeedItems, i => i.Is(ItemCounters.AntiAttackSpeed)));
        if (profile.Tanks >= 2 || profile.HealthItems >= 6) needs.Add((T("DAÑO POR % DE VIDA", "% HEALTH DAMAGE", "DANO POR % DE VIDA", "DÉGÂTS EN % DES PV", "SCHADEN NACH % LEBEN", "SÁT THƯƠNG THEO % MÁU"), profile.Tanks >= 2 ? LF($"{profile.Tanks} tanques", $"{profile.Tanks} tanks") : L("Compran mucha vida", "They stack health"), 0.06, i => i.Is(ItemCounters.PercentHealth)));
        if (!_tankBuild && !iAmMagic && (profile.Tanks >= 2 || profile.ArmorItems >= 3)) needs.Add((T("PENETRACIÓN DE ARMADURA", "ARMOR PENETRATION", "PENETRAÇÃO DE ARMADURA", "PÉNÉTRATION D'ARMURE", "RÜSTUNGSDURCHDRINGUNG", "XUYÊN GIÁP"), LF($"Compran armadura ({profile.ArmorItems} objetos)", $"They buy armor ({profile.ArmorItems} items)"), 0.05, i => i.Has("ArmorPenetration")));
        if (!_tankBuild && iAmMagic && (profile.Tanks >= 2 || profile.MagicResistItems >= 3)) needs.Add((T("PENETRACIÓN MÁGICA", "MAGIC PENETRATION", "PENETRAÇÃO MÁGICA", "PÉNÉTRATION MAGIQUE", "MAGIEDURCHDRINGUNG", "XUYÊN KHÁNG PHÉP"), LF($"Compran resist. mágica ({profile.MagicResistItems} objetos)", $"They buy magic resist ({profile.MagicResistItems} items)"), 0.05, i => i.Has("MagicPenetration")));
        needs = needs.OrderByDescending(n => n.Weight).ToList();
        // Las de aprovechar al rival (no aguantar): para enseñarlas también aunque pese más una defensiva.
        var offensiveTitles = new HashSet<string> { T("HERIDAS GRAVES", "GRIEVOUS WOUNDS", "FERIDAS DOLOROSAS", "BLESSURES GRAVES", "KLAFFENDE WUNDEN", "VẾT THƯƠNG SÂU"), T("ANTIESCUDOS", "ANTI-SHIELD", "ANTIESCUDO", "ANTI-BOUCLIER", "ANTI-SCHILD", "PHÁ LÁ CHẮN"),
            T("DAÑO POR % DE VIDA", "% HEALTH DAMAGE", "DANO POR % DE VIDA", "DÉGÂTS EN % DES PV", "SCHADEN NACH % LEBEN", "SÁT THƯƠNG THEO % MÁU"), T("PENETRACIÓN DE ARMADURA", "ARMOR PENETRATION", "PENETRAÇÃO DE ARMADURA", "PÉNÉTRATION D'ARMURE", "RÜSTUNGSDURCHDRINGUNG", "XUYÊN GIÁP"), T("PENETRACIÓN MÁGICA", "MAGIC PENETRATION", "PENETRAÇÃO MÁGICA", "PÉNÉTRATION MAGIQUE", "MAGIEDURCHDRINGUNG", "XUYÊN KHÁNG PHÉP") };

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
                .Take(5).ToList(), offensiveTitles.Contains(n.Title)))
            .Where(c => c.Items.Count > 0).ToList();

        // Build situacional: la recomendada, cambiando lo que haga falta para cubrir las categorías (como mucho 3 cambios).
        var build = recommended.ToList();
        var changed = new HashSet<int>();
        var replaced = new Dictionary<int, ItemInfo>();
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
            string reason = LF($"cubre {category.Title.ToLowerInvariant()}", $"covers {category.Title.ToLowerInvariant()}")
                            + (pick.WinRate is { } wr ? LF($" ({wr:P1} de victorias con tu campeón)", $" ({wr:P1} win rate with your champion)") : "")
                            + (pick.Augment is not null ? LF($" · va con tu aumento «{pick.Augment}»", $" · fits your augment “{pick.Augment}”") : "");
            replaced[pick.Item.Id] = build[replace].Item.Item;
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
            if (_critCapped && item.Has("CriticalStrike") && item.Id != InfinityEdge) why = L("da crítico y ya tienes el 100 % (el de más solo se convierte en un poco de daño)", "it gives crit and you are already at 100% (extra crit only turns into a little damage)");
            else if (item.IsBoots && armorOnly && lean > 0.05) why = LF($"botas de armadura y el {profile.MagicShare:P0} del daño enemigo es mágico: mejor Botas de Mercurio", $"armor boots but {profile.MagicShare:P0} of enemy damage is magic: get Mercury's Treads");
            else if (armorOnly && lean > 0.07) why = LF($"da armadura y el {profile.MagicShare:P0} del daño enemigo es mágico", $"gives armor but {profile.MagicShare:P0} of enemy damage is magic");
            else if (mrOnly && lean < -0.07) why = LF($"da resist. mágica y el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"gives magic resist but {1 - profile.MagicShare:P0} of enemy damage is physical");
            else if (apItem && apShare < 0.3) why = L("es de AP y tu build (objetos y aumentos) va de AD", "it is AP and your build (items and augments) is AD");
            else if (adItem && apShare > 0.7) why = L("es de AD y tu build (objetos y aumentos) va de AP", "it is AD and your build (items and augments) is AP");
            else if (attackItem && abilityShare > 0.75) why = L("es de ataques básicos y tu build va de habilidades", "it is for basic attacks and your build uses abilities");
            else if (!item.IsBoots && ranked.FirstOrDefault(a => a.Item.Id == item.Id) is { } scored && scored.Score < top - 0.08)
                why = L("rinde poco con tus aumentos y contra este equipo", "performs poorly with your augments and against this team");
            if (why is not null) discards.Add(new Discard(item, why));
            if (discards.Count == 3) break;
        }

        return new CounterPlan(profile.MagicShare, threats, build, changed, categories, discards, replaced);
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
            Reason = LF($"ya lo estás haciendo (te faltan {missing} de oro)", $"you are already building it ({missing} gold to go)") + $" · {a.Reason}",
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
        // Con un campeón que no compra botas (Yuumi), 6 objetos.
        int itemSlots = InventorySlots - (owned.Any(i => i.IsBoots) || !SkipsBoots(champion) ? 1 : 0);
        var picks = TakeWithinLimit(candidates, itemSlots, MaxArmorPenItems);
        if (kept.Count < itemSlots)
            EnsureCounter(champion, picks, candidates, profile, MaxArmorPenItems, []); // aquí tus objetos ya van entre los candidatos (y primero)

        var slots = new List<BuildSlot>();
        if (owned.FirstOrDefault(i => i.IsBoots) is { } myBoots) slots.Add(new BuildSlot(ScoreOwned(myBoots), true));
        else if (boots is not null) slots.Add(new BuildSlot(boots, false));
        slots.AddRange(picks.OrderByDescending(a => ownedIds.Contains(a.Item.Id)).ThenByDescending(a => a.Score)
                            .Select(a => new BuildSlot(a, ownedIds.Contains(a.Item.Id))));
        return slots;
    }

    /// <summary>Partidas del campeón con el aumento (todos los jugadores) a partir de las que mandan sus datos (fiabilidad 2/3).</summary>
    const int ChampionDataGames = 300;

    /// <summary>
    /// ¿Tu build de esta partida no es la habitual del campeón? (tus objetos ya deciden, y van 30+ puntos más hacia AP/AD o
    /// hacia ataques/habilidades que lo normal con él: un Shaco de AP, una Kayle de crítico…). Sin objetos, es la habitual.
    /// </summary>
    bool BuildDiffersFromUsual(string champion, IReadOnlyList<int> myItems, IReadOnlyList<Augment> mine)
    {
        var (ap, ability, fromItems) = BuildProfile(champion, myItems, mine);
        if (!fromItems) return false;
        var (usualAp, usualAbility, _) = BuildProfile(champion, [], []);
        return Math.Abs(ap - usualAp) > 0.3 || Math.Abs(ability - usualAbility) > 0.3;
    }
    /// <summary>
    /// Combos de mecánica para parejas sin partidas que las midan: dos aumentos de la definitiva (Revolución Definitiva +
    /// Despertar Definitivo: cada definitiva extra te vuelve a reiniciar las básicas) o dos que se activan al inmovilizar
    /// (Devorador de Almas + Infranqueable). Medido con las parejas de mayhemmeta con 1.000+ partidas (2026-11-01): las de
    /// la definitiva ganan ~1 punto más que esos mismos aumentos con otros (13 de 14 positivas), las de inmovilizar ~0,7
    /// (10 de 10). Con el enfriamiento no se mide nada aparte. (Partida de Alistar del 2026-11-01: salían con B.)
    /// </summary>
    static AugmentSynergy? Combo(Augment candidate, Augment mine)
    {
        if (candidate.NeedsUltimate && mine.NeedsUltimate)
            return new AugmentSynergy(UltComboBonus, LF($"combo con «{mine.DisplayName}»: los dos giran en torno a tu definitiva",
                                                        $"combo with “{mine.DisplayName}”: both revolve around your ultimate"));
        if (StatTags.TriggersOnImmobilize(candidate) && StatTags.TriggersOnImmobilize(mine))
            return new AugmentSynergy(ImmobilizeComboBonus, LF($"combo con «{mine.DisplayName}»: al inmovilizar se activan los dos",
                                                               $"combo with “{mine.DisplayName}”: immobilizing triggers both"));
        return null;
    }
    const double UltComboBonus = 0.01, ImmobilizeComboBonus = 0.007;

    /// <summary>
    /// Qué tal combina un aumento que te ofrecen con lo que llevas, solo con lo que miden los datos o con lógica segura:
    /// cómo ganan juntos dos aumentos (mayhemmeta, o tus partidas), los combos medidos (definitiva, inmovilizar), lo que gana
    /// el aumento con tu campeón en tu build de ahora (aramkit), el crítico al 100 %, De Vuelta a lo Básico con aumentos de
    /// la definitiva y los aumentos que piden un objeto que no te sirve. Null si no hay nada que decir.
    /// Sin ajustes a ojo (2026-11-03, «la app recomienda aumentos que no van»): medidos con las 2.112 parejas campeón +
    /// aumento con 800+ partidas en mayhemmeta frente a lo que la app estimaría sin ellas, no aciertan nada. «Pide otra
    /// build» restaba 7,4 puntos donde la verdad es la de siempre (+0,24 frente a +0,33 de media); «encaja con la build»
    /// sumaba 2 por +0,1; «tu kit aplica efectos al impactar / usa el crítico» sumaba 1 por nada; «potencia curaciones y
    /// escudos y tu kit no tiene» restaba 2 donde gana más; y «los dos potencian X» entre aumentos sumaba hasta 1,5 cuando las
    /// parejas que comparten estadística combinan igual que las demás (+0,56 frente a +0,57, 1.534 parejas). Herramienta:
    /// pruebas/validar-ajustes. (Desgarraoro 0,1 con Alistar, Vulnerabilidad 0 con Sylas, Kog'Maw de crítico…)
    /// </summary>
    /// <param name="lockedStyle">La build que fijaste en el panel ("on_hit", "tank"…), si la fijaste.</param>
    public AugmentSynergy? SynergyWith(Augment candidate, IReadOnlyList<Augment> mine, string champion, IReadOnlyList<int>? myItems = null,
                                       double critChance = 0, string? lockedStyle = null)
    {
        if (candidate.IsAnvil) return null;
        // De Vuelta a lo Básico sella la definitiva: con un aumento que vive de ella (Axioma Arcano, Forma Final…) uno de
        // los dos no hace nada. Casi nadie los junta, así que no hay partidas que lo midan: va siempre al fondo.
        if (mine.FirstOrDefault(m => candidate.SealsUltimate ? m.NeedsUltimate : candidate.NeedsUltimate && m.SealsUltimate) is { } clash)
            return new AugmentSynergy(-0.12, candidate.SealsUltimate
                ? LF($"Sella tu definitiva, y «{clash.DisplayName}» la necesita", $"Seals your ultimate, which “{clash.DisplayName}” needs")
                : LF($"Necesita tu definitiva, y «{clash.DisplayName}» la sella", $"Needs your ultimate, which “{clash.DisplayName}” seals"));
        bool magic = PlaysMagic(champion, myItems ?? [], mine);
        var rec = stats.Rate(champion, candidate.Id);
        bool champData = rec.Global && rec.EvidenceGames >= ChampionDataGames;
        bool heuristics = !champData || BuildDiffersFromUsual(champion, myItems ?? [], mine);
        var needs = stats.Links.RequiredBy(candidate.Id);

        double total = 0, bestWeight = 0;
        string? reason = null;
        void Add(double amount, string why)
        {
            total += amount;
            if (Math.Abs(amount) > bestWeight) { bestWeight = Math.Abs(amount); reason = why; }
        }
        foreach (var m in mine.Where(m => !m.IsAnvil && m.Id != candidate.Id))
        {
            // Primero los datos de todos los jugadores (miles de partidas); si no hay, tus partidas guardadas.
            if (stats.AugmentTable.Pair(candidate.Id, m.Id) is { Games: >= 50 } global)
            {
                double lift = global.ShrunkSynergy / 100;
                if (Math.Abs(lift) >= 0.01)
                    Add(Math.Clamp(lift, -0.06, 0.06), lift > 0
                        ? lift >= 0.03 ? LF($"combina muy bien con «{m.DisplayName}» (~{global.Games:N0} partidas en Mayhem)", $"great combo with “{m.DisplayName}” (~{global.Games:N0} Mayhem games)")
                                       : LF($"combina bien con «{m.DisplayName}» (~{global.Games:N0} partidas en Mayhem)", $"good combo with “{m.DisplayName}” (~{global.Games:N0} Mayhem games)")
                        : LF($"no combina bien con «{m.DisplayName}» (~{global.Games:N0} partidas en Mayhem)", $"bad combo with “{m.DisplayName}” (~{global.Games:N0} Mayhem games)"));
            }
            else if (stats.AugmentPairLift(candidate.Id, m.Id) is { } pair && Math.Abs(pair.Lift) >= 0.02)
                Add(Math.Clamp(pair.Lift, -0.05, 0.05), pair.Lift > 0
                    ? LF($"combina bien con «{m.DisplayName}» en tus partidas ({pair.Games})", $"good combo with “{m.DisplayName}” in your games ({pair.Games})")
                    : LF($"no combina bien con «{m.DisplayName}» en tus partidas ({pair.Games})", $"bad combo with “{m.DisplayName}” in your games ({pair.Games})"));
            else if (Combo(candidate, m) is { } combo)
                Add(combo.Bonus, combo.Reason);

            var sharedItems = needs.Intersect(stats.Links.RequiredBy(m.Id)).Select(Data.Item).OfType<ItemInfo>().ToList();
            if (sharedItems.Count > 0) Add(0.04, LF($"va con «{m.DisplayName}»: los dos piden {sharedItems[0].DisplayName}", $"fits “{m.DisplayName}”: both want {sharedItems[0].DisplayName}"));
        }

        // La build de esta partida (la que dicen tus objetos o la que fijaste en el panel): lo que gana el aumento con el
        // campeón EN ESA build, frente a la media de todas sus builds (aramkit, ver AugmentBuilds.StyleEffect).
        string? styleNow = lockedStyle ?? (StyleFromItems(champion, myItems ?? []) is { FromItems.Length: > 0, Generic: false } byItems ? byItems.Key : null);
        var styleFx = styleNow is null ? null : stats.AramkitBuilds.StyleEffect(champion, candidate.Id, styleNow);
        if (styleFx is { } fx && fx.Weight * (fx.Delta / 100 - AugmentScale.Evidence(rec)) is var styleShift && Math.Abs(styleShift) >= 0.005)
            // Sin números de victorias (Riot no deja enseñar el % de los aumentos): solo si le va mejor o peor así.
            Add(styleShift, styleShift > 0
                ? LF($"con tu build {StyleName(styleNow!)}, a {Data.ChampionName(champion)} le va mejor con él (~{fx.Games:N0} partidas así)",
                    $"with your {StyleName(styleNow!)} build, {Data.ChampionName(champion)} does better with it (~{fx.Games:N0} games like that)")
                : LF($"con tu build {StyleName(styleNow!)}, a {Data.ChampionName(champion)} le va peor con él (~{fx.Games:N0} partidas así)",
                    $"with your {StyleName(styleNow!)} build, {Data.ChampionName(champion)} does worse with it (~{fx.Games:N0} games like that)"));

        // Con el 100 % de crítico, el crítico que da el aumento se pierde (Absorción de Alma salía con nota alta: Wakka, Jhin).
        if (critChance >= CritCap && StatTags.GivesCritChance(candidate))
            // En Caos el crítico de más no se pierde del todo: cada 1 % da 0,45 de AD o 0,75 de AP (wiki de LoL, ARAM: Mayhem).
            Add(-0.05, L("ya tienes el 100 % de crítico: el que da de más solo se convierte en un poco de daño",
                         "you are already at 100% crit: the extra crit only turns into a little damage"));

        // Aumentos que mejoran o piden un objeto («Mejorar Filo del Infinito», «Descarga Tormentosa Mejorada»…): si ese
        // objeto no es para tu build, o no se puede llevar con lo que ya tienes, el aumento casi no te sirve.
        var needed = needs.Select(Data.Item).OfType<ItemInfo>().Where(i => i.IsFinal).ToList();
        if (heuristics && needed.Count > 0 && needed.All(i => WrongDamageFor(i, magic)))
            Add(-0.08, LF($"Pide {needed[0].DisplayName}, que no es para tu build de {(magic ? "AP" : "AD")}", $"Wants {needed[0].DisplayName}, which doesn't fit your {(magic ? "AP" : "AD")} build"));
        else if (needed.Count > 0 && myItems is not null && needed.All(i => !Owns(myItems, i.Id) && ConflictsWithOwned(i, myItems)))
            Add(-0.08, LF($"Pide {needed[0].DisplayName}, que no se puede llevar con lo que ya tienes", $"Wants {needed[0].DisplayName}, which can't be combined with your items"));

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
        // (El crítico de habilidades cuenta como habilidades: ver abajo.)
        foreach (var m in mine.Where(m => !m.IsAnvil && StatTags.UsesCritChance(m) && m.StatList.Contains("Crit") && !m.StatList.Any(t => t is "AS" or "OnHit")))
        {
            attack -= AugmentGold;
            ability += AugmentGold;
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

    /// <summary>True durante <see cref="Build"/> si ya tienes (casi) el 100 % de crítico: más crítico se desperdicia.</summary>
    bool _critCapped;
    const double CritCap = 0.95;
    /// <summary>
    /// Lo que pierde un objeto de crítico con el crítico al tope (Wakka, 2026-10-31: le recomendaba El Coleccionista a una
    /// Vayne con el 100 %). El Filo del Infinito no: su fuerza es el daño crítico.
    /// </summary>
    const double CritCappedPenalty = -0.2;
    const int InfinityEdge = 3031;

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
        "utility" => T("apoyo", "support", "suporte", "support", "Support", "hỗ trợ"), "ap" => "AP", "ad" => "AD", "tank" => T("tanque", "tank", "tanque", "tank", "Tank", "đỡ đòn"),
        "on_hit" => "on-hit", "crit" => T("crítico", "crit", "crítico", "critique", "Krit", "chí mạng"), "lethality" => T("letalidad", "lethality", "letalidade", "létalité", "Tödlichkeit", "sát lực"),
        "bruiser" => T("luchador", "bruiser", "lutador", "combattant", "Kämpfer", "đấu sĩ"), "hybrid" => T("híbrido", "hybrid", "híbrido", "hybride", "Hybrid", "lai"),
        _ => key.ToUpperInvariant(),
    };

    /// <summary>
    /// El estilo que fijaste en el selector del panel: el del campeón con ese nombre o, si no lo juega casi nadie con él
    /// (Twitch on-hit), el de todos los campeones. Null si no existe.
    /// </summary>
    StylePick? LockedStyle(string champion, string key)
    {
        var styles = stats.AramkitBuilds.Styles(champion);
        var own = styles.FirstOrDefault(s => s.Key == key);
        if ((own ?? stats.AramkitBuilds.Generic(key)) is not { } style) return null;
        int total = styles.Sum(s => s.Games);
        return new StylePick(key, styles.FirstOrDefault()?.Key ?? key, own is null || total == 0 ? 0 : (double)own.Games / total, style.WinRate,
            styles.FirstOrDefault()?.WinRate ?? style.WinRate, style.Games, 0, style.Items, L("la elegiste tú", "your pick"), own is null, Locked: true);
    }

    /// <summary>Lo menos jugado que sale en el selector de build (parte de las partidas del campeón).</summary>
    const double MinStyleShare = 0.03;

    /// <summary>
    /// Las builds que se juegan con tu campeón para el selector del panel, con la que sigue la app ahora marcada (aunque
    /// casi nadie la juegue con él: Twitch on-hit) y 3 objetos clave de cada una que aún no tienes. Con un kit on-hit
    /// también sale on-hit aunque el campeón no lo tenga en sus datos, para poder elegirlo.
    /// </summary>
    IReadOnlyList<StyleOption> StyleOptions(string champion, IReadOnlyList<int> myItems)
    {
        var styles = stats.AramkitBuilds.Styles(champion);
        int total = styles.Sum(s => s.Games);
        if (total == 0) return [];
        string active = _style?.Key ?? styles[0].Key;
        bool locked = _style?.Locked == true;
        // Los objetos clave son los propios de esa build: lo que sale en ella más que en las otras del campeón. Con solo "los más
        // comprados", Teemo on-hit enseñaba el Tormento de Liandry, que se compra igual en su build de AP (Wakka, 2026-11-01);
        // la auditoría lo veía en 244 de 388 builds.
        double Other(int id, string key) => styles.Where(o => o.Key != key).Select(o => o.Items.GetValueOrDefault(id)).DefaultIfEmpty(0).Max();
        IReadOnlyList<ItemInfo> KeyItems(IReadOnlyDictionary<int, double> shares, string key) => shares
            .Where(kv => kv.Value >= 0.1 && !Owns(myItems, kv.Key) && Data.Item(kv.Key) is { IsBoots: false, InStore: true, IsFinal: true })
            .OrderByDescending(kv => kv.Value - 0.8 * Other(kv.Key, key)).ThenByDescending(kv => kv.Value)
            .Take(3).Select(kv => Data.Item(kv.Key)!).ToList();
        var options = styles.Where(s => (double)s.Games / total >= MinStyleShare || s.Key == active)
            .Select(s => new StyleOption(s.Key, StyleName(s.Key), (double)s.Games / total, s.WinRate,
                KeyItems(s.Key == active && _style is not null ? _style.Items : s.Items, s.Key), s.Key == active, s.Key == active && locked))
            .ToList();
        foreach (var key in new[] { active, "on_hit" })
        {
            if (options.Any(o => o.Key == key) || stats.AramkitBuilds.Generic(key) is not { } generic) continue;
            if (key == "on_hit" && key != active && stats.Kits.For(champion) is not { HasOnHit: true }) continue;
            options.Add(new StyleOption(key, StyleName(key), 0, generic.WinRate, KeyItems(key == active && _style is not null ? _style.Items : generic.Items, key),
                key == active, key == active && locked));
        }
        return options;
    }

    /// <summary>Por qué la app sigue esa build: la elegiste tú, tus objetos, tus aumentos o lo habitual con el campeón.</summary>
    string? StyleSource(string champion, IReadOnlyList<Augment> mine) => _style switch
    {
        { Mixed: true } => L("tus objetos aún no deciden la build", "your items don't settle the build yet"),
        null or { FromItems: "" } => LF($"lo habitual con {Data.ChampionName(champion)}", $"the usual {Data.ChampionName(champion)} build"),
        { Locked: true } => L("la elegiste tú · púlsala otra vez para que elija la app", "your pick · tap it again to let the app choose"),
        { FromItems: { } decisive } => LF($"por tus objetos: {decisive}", $"from your items: {decisive}"),
        _ => mine.FirstOrDefault(a => a.Id == _style.AugmentId) is { } aug
            ? LF($"por tu aumento «{aug.DisplayName}»", $"from your augment “{aug.DisplayName}”") : null,
    };

    /// <summary>
    /// Al estilo habitual del campeón le suma lo que dice su página de mayhemmeta (lo que se compra de verdad con él).
    /// aramkit solo trae los objetos más repetidos de cada estilo y con el resto se queda corto: a Amumu no le daba la
    /// Máscara Abisal (la lleva el 30 % y gana 2,6 puntos más), ni el Umbral del Invierno (36 %), y la Égida de Fuego Solar le
    /// salía con un 12 % (la lleva el 42 %; en las partidas guardadas, 22 de 40). Cada objeto cuenta lo que diga la fuente
    /// que más le da. No se toca: lo que aramkit pone en otro estilo del campeón (Maldad con Amumu es de los
    /// que van de AP), ni lo que es solo de AP en un estilo de AD o al revés (Descarga Tormentosa a Shaco, Bailarín
    /// Espectral a Ashe AP: salía y luego mandaba venderlo).
    /// Medido con 7.569 builds reales (2026-11-09): de los 5 objetos que más da cada fuente, la página acierta el 62,9 %,
    /// AramHex (China, 800.000 partidas por campeón, otra forma de comprar) el 60,9 % y aramkit el 55,7 %.
    /// Con pruebas/evaluar-objetos, acierto de la build sin objetos / con 1 / 2 / 3: 1.8.5 51,2 / 49,1 / 46,8 / 42,1;
    /// así 52,2 / 50,1 / 47,4 / 42,9. Con la página MANDANDO sobre aramkit (lo de aramkit que la página no trae al 0-70 %)
    /// subía a 52,4 / 50,2 / 47,6 / 43,2, pero la build del principio pasaba a ser una mezcla de estilos (Volibear tanque con
    /// Crepúsculo y Alba) y la app cambiaba de idea: partidas con 2+ cambios de plan 26 → 47 y 6 de «vende lo que te
    /// recomendó» (pruebas/auditar-objetos). No compensa. Peor o igual: solo los objetos que aramkit no trae, la página a
    /// medias o de más (×0,7, ×1,4), la página en los estilos que no son el habitual, sumar AramHex.
    /// </summary>
    StylePick WithPage(string champion, StylePick style)
    {
        var styles = stats.AramkitBuilds.Styles(champion);
        if (styles.Count == 0 || style.Key != styles[0].Key || style.Generic || stats.Global.For(champion) is not { } page) return style;
        var items = new Dictionary<int, double>(style.Items);
        foreach (var p in page.ItemsCommon.Concat(page.ItemsBest))
        {
            if (p.PickRate is not { } pick) continue;
            // La página cuenta el objeto ya transformado (Invierno Nórdico); se compra el de antes (Umbral del Invierno).
            int id = TransformedFrom.GetValueOrDefault(p.Id, p.Id);
            if (Data.Item(id) is not { IsFinal: true, InStore: true, IsBoots: false } item) continue;
            if (!style.Items.ContainsKey(id))
            {
                if (styles.Any(s => s.Key != style.Key && s.Items.ContainsKey(id))) continue;
                bool apOnly = ApItem.Any(item.Has) && !item.Has("Damage") && !item.Has("CriticalStrike") && !item.Has("ArmorPenetration"),
                     adOnly = AdItem.Any(item.Has) && !ApItem.Any(item.Has);
                if (style.Key == "ap" ? adOnly : style.Key is "ad" or "crit" or "lethality" or "bruiser" && apOnly) continue;
            }
            items[id] = Math.Max(items.GetValueOrDefault(id), pick / 100);
        }
        return style with { Items = items };
    }

    /// <summary>
    /// El estilo que dicen tus objetos terminados (ver <see cref="AugmentBuilds.StyleFromItems"/>). Con él se recomienda lo
    /// que se compra en ESE estilo y no la mezcla de todas las builds del campeón (los Yi de crítico y los on-hit juntos).
    /// Si es el mismo que piden tus aumentos, se queda el de tus aumentos (trae lo que se compra con ellos). Si el campeón
    /// no tiene ese estilo en sus datos (Twitch on-hit), los objetos salen de la media de todos los campeones (mezclarlo
    /// con lo suyo colaba lo de su estilo habitual: Filo del Infinito a un Twitch on-hit, Piedra Lunar a una Lulu on-hit). Sin objetos terminados, el estilo habitual del campeón. Null si no está claro.
    /// </summary>
    public StylePick? StyleFromItems(string champion, IReadOnlyList<int> myItems)
    {
        var finals = myItems.Distinct().Where(id => Data.Item(id) is { IsFinal: true, IsBoots: false, PriceTotal: > 0 } i
                                                    && !i.Has("Trinket") && !i.Has("Consumable") && !stats.Links.IsReward(id)).ToList();
        var styles = stats.AramkitBuilds.Styles(champion);
        // Sin objetos terminados: lo que se compra en el estilo habitual del campeón, no la mezcla de todos sus estilos
        // (con pruebas/evaluar-objetos, el primer objeto acierta el 48,8 % en vez del 44 %). FromItems "" = sin objetos.
        if (finals.Count == 0)
            return _style ?? (styles.Count > 0 ? new StylePick(styles[0].Key, styles[0].Key, (double)styles[0].Games / styles.Sum(s => s.Games),
                styles[0].WinRate, styles[0].WinRate, styles[0].Games, 0, styles[0].Items, "") : null);
        var kit = stats.Kits.For(champion);
        bool marksman = Data.Champion(champion)?.Roles.FirstOrDefault() == "marksman";
        bool KitFits(string key) => key switch
        {
            "on_hit" => kit is { HasOnHit: true } || kit is { HasAttackSpeed: true },
            "crit" => kit is { HasCrit: true } || marksman,
            _ => false,
        };
        var posterior = stats.AramkitBuilds.StylesFromItems(champion, finals, KitFits);
        if (posterior.Count == 0) return null;
        var found = posterior[0];
        if (found.Probability < AugmentBuilds.ItemStyleConfidence)
        {
            // Tus objetos aún no dejan claro el estilo (un Volibear con Corazón de Acero puede acabar tanque u on-hit): se
            // mezcla lo que se compra en cada estilo según lo probable que es con lo que llevas. Antes se volvía a la mezcla
            // de TODAS las builds de la página y la app cambiaba de idea a mitad de partida: recomendaba Cicloespada Voltaica
            // a Lee Sin y luego mandaba venderla (16 partidas de pruebas/auditar-objetos acababan vendiendo algo que la app
            // había recomendado; con la mezcla, ninguna). Con pruebas/evaluar-objetos acierta más con 1, 2 y 3 objetos ya
            // comprados (49,1 → 49,4 %, 46,8 → 47,0 % y 42,1 → 42,5 %). Quedarse solo con el estilo más probable acertaba menos.
            if (_style is not null || styles.Count == 0) return null;
            // Solo con los estilos del propio campeón: con los de «cualquier campeón» a medias, a una Annie con un Cuchillo
            // de Statikk le salía el Rey Arruinado («lo compra el 27 % de los Annie», y no lo compra ninguna).
            var own = posterior.Where(o => !o.Generic).ToList();
            double known = own.Sum(o => o.Probability);
            if (known <= 0) return null;
            var mix = new Dictionary<int, double>();
            foreach (var (style, p, _) in own)
                foreach (var (id, share) in style.Items) mix[id] = mix.GetValueOrDefault(id) + p / known * share;
            return new StylePick(own[0].Style.Key, styles[0].Key, own[0].Probability / known, own[0].Style.WinRate,
                styles[0].WinRate, own[0].Style.Games, 0, mix, "", Mixed: true);
        }

        string usual = styles.FirstOrDefault()?.Key ?? found.Style.Key;
        if (found.Style.Key == _style?.Key) return _style; // el de tus aumentos ya trae lo que se compra con ellos

        var items = found.Style.Items;
        var decisive = finals.OrderByDescending(id => found.Style.Items.GetValueOrDefault(id)).Take(2).Select(id => Data.Item(id)!.DisplayName);
        int total = styles.Sum(s => s.Games);
        return new StylePick(found.Style.Key, usual, found.Generic || total == 0 ? 0 : (double)found.Style.Games / total, found.Style.WinRate,
            styles.FirstOrDefault()?.WinRate ?? found.Style.WinRate, found.Style.Games, 0, items, string.Join(" + ", decisive), found.Generic);
    }
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
        // Ni el crítico de las habilidades (Guantelete Enjoyado, Fuego Gemelo, Curación Crítica) es de ataques básicos.
        bool critForAbilities = StatTags.UsesCritChance(candidate);
        bool needsAbilities = tags.Contains("Abilities") && !candidate.NeedsUltimate,
             needsAttacks = tags.Overlaps(["AS", "OnHit"]) || (tags.Contains("Crit") && !critForAbilities);
        if (needsAp == needsAd && needsAbilities == needsAttacks) return null; // pide de todo o nada concreto

        var (apShare, abilityShare, fromItems) = BuildProfile(champion, myItems, mine);
        var checks = new List<(double Fit, string Wants, string Have, string Fits)>();
        if (needsAp && !needsAd) checks.Add((apShare, L("build de AP", "an AP build"), L("de AD", "AD"), L("de AP", "AP")));
        if (needsAd && !needsAp) checks.Add((1 - apShare, L("build de AD", "an AD build"), L("de AP", "AP"), L("de AD", "AD")));
        if (needsAbilities && !needsAttacks) checks.Add((abilityShare, L("habilidades", "abilities"), L("de ataques básicos", "basic attacks"), L("de habilidades", "abilities")));
        if (needsAttacks && !needsAbilities) checks.Add((1 - abilityShare, L("ataques básicos", "basic attacks"), L("de habilidades", "abilities"), L("de ataques básicos", "basic attacks")));
        var worst = checks.MinBy(c => c.Fit);

        string whose = fromItems ? L("tu build va", "your build is") : LF($"con {Data.ChampionName(champion)} se suele ir", $"{Data.ChampionName(champion)} usually goes");
        if (worst.Fit < 0.5)
        {
            double penalty = (worst.Fit - 0.5) * 0.2; // hasta −0,10 si no encaja nada
            return penalty <= -0.02 ? new AugmentSynergy(penalty, LF($"Pide {worst.Wants}, y {whose} {worst.Have}", $"Wants {worst.Wants}, and {whose} {worst.Have}")) : null;
        }
        return worst.Fit >= 0.75
            ? new AugmentSynergy(0.02, fromItems ? LF($"Encaja con tu build {worst.Fits}", $"Fits your {worst.Fits} build") : LF($"Encaja con la build habitual de {Data.ChampionName(champion)} ({worst.Fits})", $"Fits the usual {Data.ChampionName(champion)} build ({worst.Fits})"))
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
                              : lift is { Lift: > 0.02 } l ? LF($"rinde mejor con este aumento ({l.Games} partidas)", $"does better with this augment ({l.Games} games)")
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
        myItems.Contains(id) || TransformedFrom.Any(kv => kv.Value == id && myItems.Contains(kv.Key))
        || QuestRewardFrom.Any(kv => myItems.Contains(kv.Key) && kv.Value.Contains(id));

    /// <summary>
    /// Las misiones que convierten sus objetos en la recompensa al completarse (wiki de LoL, ARAM: Mayhem): con la recompensa
    /// ya «tienes» esos objetos. Antes, con el Sombrero de Wooglet la app seguía pidiendo el Sombrero Mortífero de Rabadon
    /// (opinión de un jugador con Kayle, 2026-11-08), porque la misión lo pedía y no se sabía que ya estaba hecha.
    /// </summary>
    static readonly Dictionary<int, int[]> QuestRewardFrom = new()
    {
        [228002] = [3089, 3157],       // Sombrero de Wooglet ← Sombrero Mortífero de Rabadon + Reloj de Arena de Zhonya
        [223069] = [6664, 3068],       // Inmolación del Vacío ← Resplandor Vacío + Égida de Fuego Solar
        [226668] = [6698, 3074, 3748], // Ultrahidra ← Hidra Profana + Hidra Voraz + Hidra Titánica
    };

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
        // Con un kit de ataques (efectos al impactar o velocidad de ataque: Kayle, Kog'Maw, Yi…) la velocidad de ataque
        // le sirve aunque no sea tirador, y a los de AP también (Diente de Nashor, Espadafuria de Guinsoo).
        bool attackKit = role == "marksman" || stats.Kits.For(champion) is { HasOnHit: true } or { HasAttackSpeed: true };
        return magic ? i.Has("SpellDamage") || (attackKit && i.Has("AttackSpeed") && i.Has("OnHit") && !i.Has("Damage"))
             : i.Has("Damage") || i.Has("CriticalStrike") || (attackKit && i.Has("AttackSpeed"));
    }

    const int InventorySlots = 6;
    /// <summary>Por cada estadística que pide un aumento tuyo y da el objeto (hasta 2 por aumento).</summary>
    const double AugmentMatchBonus = 0.04;
    /// <summary>Lo que suma el objeto que compra la mayoría con tu campeón (el 60 % o más de las partidas: todo).</summary>
    const double MaxPopularBonus = 0.06;
    const double OffMetaPenalty = -0.05;
    /// <summary>
    /// Con el estilo que dicen tus objetos (<see cref="StyleFromItems"/>) lo que se compra en ese estilo pesa más que la
    /// build habitual. Medido con pruebas/evaluar-objetos (1.687 jugadores de partidas guardadas, 2 objetos dados):
    /// con 0,06 / −0,05 acertaba el 37,5 % de lo que compraron después; con 0,15 / −0,12, el 43 %; más ya no mejora y
    /// taparía al equipo enemigo y a tus aumentos.
    /// </summary>
    const double StyleItemsBonus = 0.15;
    const double StyleItemsPenalty = -0.12;

    /// <summary>
    /// Cuanto más se compra con tu campeón en Mayhem, más suma: la build habitual de la página manda salvo que haya un
    /// motivo claro (un aumento, el equipo enemigo). Antes un objeto raro con buen % en pocas partidas le ganaba.
    /// Con otro estilo de build por tus aumentos (<see cref="_style"/>), cuenta lo que se compra en ese estilo.
    /// </summary>
    /// <param name="shared">Botas: se compran igual en todas las builds, así que con la build fijada cuenta lo que se
    /// compran en ella, no lo propias que sean (Yi letalidad pasaba de Grebas del Berserker a Punteras de Acero, que casi
    /// nadie lleva así: 76 de 340 builds fijadas cambiaban a unas botas menos compradas; tu partida del 2026-11-02).</param>
    double PopularBonus(string champion, int itemId, bool shared = false) => WinFactor(champion, itemId) * (
        _style is { Locked: true } && !shared ? StyleItemsBonus * Math.Min(LockedShare(champion, itemId), 0.6) / 0.6
        : _style is not null ? (_style.FromItems is null ? MaxPopularBonus : StyleItemsBonus) * Math.Min(_style.Items.GetValueOrDefault(itemId), 0.6) / 0.6
        : stats.GlobalItem(champion, itemId) is { PickRate: { } pick } ? MaxPopularBonus * Math.Min(pick, 60) / 60 : 0);

    /// <summary>
    /// Con la build que fijaste tú, lo que cuenta es lo propio de esa build: lo que sale en ella menos lo que sale igual en
    /// las otras del campeón. Teemo con on-hit fijado recibía Liandry y Maldad (los compran también sus AP) en vez de lo
    /// on-hit (Wakka, 2026-11-01). Con la que elige la app se sigue lo que compra la gente (acierta más, evaluar-objetos).
    /// </summary>
    double LockedShare(string champion, int itemId)
    {
        double share = _style!.Items.GetValueOrDefault(itemId);
        double other = stats.AramkitBuilds.Styles(champion).Where(s => s.Key != _style.Key).Select(s => s.Items.GetValueOrDefault(itemId)).DefaultIfEmpty(0).Max();
        return Math.Max(0, share - 0.8 * other);
    }

    /// <summary>
    /// Lo que pesa que un objeto sea popular según lo que gana con el campeón (mayhemmeta, 300+ partidas): entero si pierde
    /// como mucho 1 punto frente al campeón; menos cuanto más pierde (la mitad con −4 o peor). Que mucha gente lo compre no
    /// lo hace bueno: El Coleccionista con Senna gana 3,4 puntos menos que Senna; con Xayah, 2,2 (Wakka, 2026-11-01, se
    /// quejaba de él con Vayne, donde gana lo mismo). Con la auditoría: 8 → 3 campeones con un objeto perdedor de primeras,
    /// sin bajar el acierto con lo que compran los que ganan (pruebas/evaluar-objetos). Premiar al que gana más bajaba el acierto.
    /// </summary>
    double WinFactor(string champion, int itemId) =>
        stats.Global.For(champion) is { WinRate: { } champWr } build && build.Item(itemId) is { WinRate: { } wr } item && build.GamesWith(item) >= 300
            ? Math.Clamp(1 + Math.Min(0, wr - champWr + 1) / 6, 0.5, 1) : 1;
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
    /// <summary>Desde esta parte de jugadores que lo compran con el campeón, un objeto terminado no se manda vender.</summary>
    const double UsualShare = 0.25;

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
            // Lo que se compra mucho con tu campeón en alguna de sus builds no «rinde poco»: a una Kayle que pasaba de on-hit
            // a AP se le mandaba vender la Espadafuria de Guinsoo que la propia app le había recomendado.
            bool wrongResist = profile.Mix == DamageMix.Physical && sell.Item.Has("SpellBlock") && !sell.Item.Has("Armor")
                               || profile.Mix == DamageMix.Magic && sell.Item.Has("Armor") && !sell.Item.Has("SpellBlock");
            if (sell.Item.IsFinal && !wrongResist && ChampionShare(champion, sell.Item.Id, anyBuild: true) >= UsualShare) continue;
            if (sell.Item.IsFinal && OnlyCounter(profile, sell.Item, myItems)) continue;

            int sellGold = (int)(sell.Item.PriceTotal * SellRatio);
            int cost = Data.RemainingCost(buy.Item, rest);
            string why =
                !sell.Item.IsFinal ? L("es un componente suelto", "it is a loose component")
                : profile.Mix == DamageMix.Physical && sell.Item.Has("SpellBlock") && !sell.Item.Has("Armor") ? L("resist. mágica contra un equipo de daño físico", "magic resist against a physical team")
                : profile.Mix == DamageMix.Magic && sell.Item.Has("Armor") && !sell.Item.Has("SpellBlock") ? L("armadura contra un equipo de daño mágico", "armor against a magic team")
                // Con datos de todos los jugadores, su % real; si no, el de tus partidas.
                : stats.GlobalItem(champion, sell.Item.Id) is { WinRate: { } globalRate } ? LF($"rinde poco con tu campeón: {globalRate:0.#} % de victorias (~{games:N0} partidas en Mayhem)", $"weak on your champion: {globalRate:0.#}% win rate (~{games:N0} Mayhem games)")
                : games >= 5 ? LF($"solo {rate:P0} de victorias en tus partidas ({games})", $"only {rate:P0} win rate in your games ({games})")
                : L("es el que menos te aporta en esta partida", "it helps you the least this game");
            return new SwapAdvice(sell.Item, sellGold, why, buy, cost, Math.Max(0, cost - sellGold - (int)gold));
        }
        return null;
    }

    /// <summary>
    /// Lo que se compra un objeto con este campeón (0-1): lo más alto entre su build de ahora, sus builds en aramkit y la
    /// página de mayhemmeta. Para los objetos «contra el enemigo»: solo los que se compran con tu campeón (al menos
    /// <see cref="CounterMinShare"/>). Antes salían de toda la tienda y se forzaba uno en la build: con 449 partidas de
    /// prueba (3.382 jugadores), Presagio de Randuin se recomendó 439 veces y se compró el 1 %; Corazón Helado 267 y el 7 %;
    /// Morellonomicón 319 y el 8 %; Colmillo de Serpiente 128 y el 3 % (también los de las barbaridades con Seraphine).
    /// </summary>
    /// <param name="anyBuild">En cualquiera de sus builds (no solo la de ahora): para «va con tu aumento», que puede ser justo
    /// lo que te cambia de build (un soporte con un aumento de AP compra lo de AP aunque su build habitual sea de apoyo).</param>
    double ChampionShare(string champion, int itemId, bool anyBuild = false) => Math.Max(Math.Max(
        _style is not null ? _style.Items.GetValueOrDefault(itemId) : stats.AramkitBuilds.Styles(champion).FirstOrDefault()?.Items.GetValueOrDefault(itemId) ?? 0,
        anyBuild ? stats.AramkitBuilds.Styles(champion).Select(s => s.Items.GetValueOrDefault(itemId)).DefaultIfEmpty(0).Max() : 0),
        (stats.GlobalItem(champion, itemId)?.PickRate ?? 0) / 100);
    const double CounterMinShare = 0.05, CounterFullShare = 0.15;

    /// <summary>Lo que el enemigo tiene muy marcado (mucha curación, escudos, críticos o velocidad de ataque) y qué lo contrarresta.</summary>
    static (bool Strong, Func<ItemInfo, bool> Counters)[] StrongNeeds(EnemyProfile profile) =>
    [
        (profile.Healers >= 3, i => i.AppliesWounds),
        (profile.Shields >= 3, i => i.Is(ItemCounters.AntiShield)),
        (profile.CritItems >= 4, i => i.Is(ItemCounters.AntiCrit)),
        (profile.AttackSpeedItems >= 4, i => i.Is(ItemCounters.AntiAttackSpeed)),
    ];

    /// <summary>
    /// ¿Es tu único objeto contra algo muy marcado del enemigo? (el que la propia app metió en tu build con
    /// <see cref="EnsureCounter"/>). Ese no se manda vender: con el inventario lleno, la app pedía vender el Presagio de
    /// Randuin que acababa de recomendar contra 4 objetos de crítico (15 partidas en pruebas/auditar-objetos).
    /// </summary>
    bool OnlyCounter(EnemyProfile profile, ItemInfo item, IReadOnlyList<int> myItems) =>
        StrongNeeds(profile).Any(n => n.Strong && n.Counters(item)
                                      && myItems.Count(id => Data.Item(id) is { } own && n.Counters(own)) == 1);

    /// <summary>
    /// Si el enemigo tiene algo muy marcado (mucha curación, escudos, críticos o velocidad de ataque) y ninguno
    /// de los 3 objetos lo contrarresta, el tercero se cambia por el mejor objeto que sí lo hace.
    /// </summary>
    void EnsureCounter(string champion, List<ItemAdvice> items, List<ItemAdvice> ranked, EnemyProfile profile, int penSlots, IReadOnlyList<int> myItems)
    {
        foreach (var (strong, counters) in StrongNeeds(profile))
        {
            // Si ya llevas uno (Recordatorio Mortal contra curaciones…), no hace falta otro.
            if (!strong || items.Any(a => counters(a.Item)) || myItems.Any(id => Data.Item(id) is { } own && counters(own))) continue;
            var best = ranked.FirstOrDefault(a => counters(a.Item) && !items.Contains(a) && ChampionShare(champion, a.Item.Id) >= CounterMinShare);
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
            if (Fits(i, champion, iAmMagic) && !OffRoleSupportItem(i, champion) && !exclude.Any(e => Clash(e, i.Id))
                && ChampionShare(champion, i.Id) >= CounterMinShare) yield return (i, baseRate, 0);
        }
    }

    /// <summary>"62 % de victorias en Mayhem (25.800 partidas)" con datos de todos; con los tuyos, tu %; sin datos, null.</summary>
    string? WinRateReason(string champion, int itemId, double rate, int games) =>
        stats.GlobalItem(champion, itemId) is { WinRate: { } wr } ? LF($"{wr / 100:P0} de victorias en Mayhem ({games:N0} partidas)", $"{wr / 100:P0} win rate in Mayhem ({games:N0} games)")
        : games > 0 ? LF($"{rate:P0} de victorias en {games} partidas", $"{rate:P0} win rate in {games} games") : null;

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
            if (_style.Locked)
                Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : LF(
                    $"tu build {StyleName(_style.Key)} (la elegiste tú): lo compra el {share:P0} de los que van así",
                    $"your {StyleName(_style.Key)} build (your pick): {share:P0} of those builds have it"));
            else if (_style.Mixed)
                Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : LF(
                    $"con lo que llevas, lo compra el {share:P0} de los {Data.ChampionName(champion)}",
                    $"with your items, {share:P0} of {Data.ChampionName(champion)} players buy it"));
            else if (_style.FromItems == "")
                Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : LF(
                    $"build habitual de {Data.ChampionName(champion)} ({StyleName(_style.Key)}): lo compra el {share:P0}",
                    $"standard {Data.ChampionName(champion)} build ({StyleName(_style.Key)}): {share:P0} buy it"));
            else if (_style.FromItems is { } decisive)
                Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : LF(
                    $"tu build va {StyleName(_style.Key)} ({decisive}): lo compra el {share:P0} de los que van así{(_style.Generic ? L(" (con cualquier campeón)", " (any champion)") : "")}",
                    $"your build is {StyleName(_style.Key)} ({decisive}): {share:P0} of those builds have it{(_style.Generic ? L(" (con cualquier campeón)", " (any champion)") : "")}"));
            else
            Bonus(true, PopularBonus(champion, item.Id), share < 0.2 ? "" : LF(
                $"con «{aug}» el {_style.Share:P0} va de {StyleName(_style.Key)} y gana más ({_style.WinRate:0.#} % frente a {_style.UsualWinRate:0.#} % de {StyleName(_style.Usual)}): lo compra el {share:P0}",
                $"with “{aug}” {_style.Share:P0} go {StyleName(_style.Key)} and win more ({_style.WinRate:0.#}% vs {_style.UsualWinRate:0.#}% {StyleName(_style.Usual)}): {share:P0} buy it"));
            // Con el crítico al tope, lo que no da crítico y se compra con el campeón (Danza de la Muerte, Rey Arruinado con
            // Vayne) es justo lo que toca después aunque el estilo no lo traiga.
            bool afterCritCap = _critCapped && !item.Has("CriticalStrike") && stats.GlobalItem(champion, item.Id) is { PickRate: >= 5 };
            // Lo que necesita un aumento tuyo no se castiga por «no ser de la build»: nadie lo compra sin ese aumento, y con
            // él es lo primero (Corazón de Acero con «Endurece tu Corazón» se quedaba fuera de la build de 28 campeones).
            bool needed = myAugments.Any(a => stats.Links.RequiredBy(a.Id).Contains(item.Id)) && !WrongDamageFor(item, iAmMagic);
            Bonus(share < 0.05 && !afterCritCap && !needed, _style.FromItems is null ? OffMetaPenalty : StyleItemsPenalty, "");
        }
        else
        {
            if (stats.GlobalItem(champion, item.Id) is { PickRate: >= 20 and var pick })
                Bonus(true, PopularBonus(champion, item.Id), LF($"build habitual: lo compra el {pick:0} % con {Data.ChampionName(champion)} en Mayhem", $"standard build: {pick:0}% of {Data.ChampionName(champion)} players buy it in Mayhem"));
            else Bonus(true, PopularBonus(champion, item.Id), "");
            // Objeto que no sale en las tablas de tu campeón (ni entre los más comprados ni entre los que más ganan): casi nadie
            // lo lleva con él. Contra tanques sumaba igual que el Rey Arruinado y colaba la Cicloespada Voltaica a Jax.
            Bonus(stats.Global.For(champion) is { } pageBuild && pageBuild.Item(item.Id) is null, OffMetaPenalty, "");
        }

        // Lo que pesa un objeto «contra el enemigo», según lo que se compra con tu campeón (entero desde el 15 %): Presagio de
        // Randuin a un campeón que casi nunca lo lleva salía 247 veces de 3.382 jugadores y se compraba el 2 %.
        // Y si gana claramente menos que tu campeón, menos aún (Randuin: 43,3 % con Cho'Gath, 43,5 % con Braum).
        double fit = Math.Min(1, ChampionShare(champion, item.Id) / CounterFullShare) * WinFactor(champion, item.Id);
        Bonus(profile.CritItems >= 3 && item.Is(ItemCounters.AntiCrit), 0.045 * fit, LF($"menos daño de críticos: llevan {profile.CritItems} objetos/aumentos de crítico", $"less crit damage: they have {profile.CritItems} crit items/augments"));
        Bonus(profile.AttackSpeedItems >= 3 && item.Is(ItemCounters.AntiAttackSpeed), 0.04 * fit, LF($"frena su velocidad de ataque ({profile.AttackSpeedItems} objetos/aumentos)", $"slows their attack speed ({profile.AttackSpeedItems} items/augments)"));
        Bonus(profile.Shields >= 2 && item.Is(ItemCounters.AntiShield), 0.04 * fit, LF($"rompe escudos: {profile.Shields} escudos enemigos", $"breaks shields: {profile.Shields} enemy shields"));
        Bonus((profile.Tanks >= 2 || profile.HealthItems >= 6) && item.Is(ItemCounters.PercentHealth), 0.035 * fit,
            profile.Tanks >= 2 ? LF($"daño por % de vida contra {profile.Tanks} tanques", $"% health damage vs {profile.Tanks} tanks") : LF($"daño por % de vida: compran mucha vida ({profile.HealthItems})", $"% health damage: they stack health ({profile.HealthItems})"));
        // Resistencias según cuánto se inclina el daño enemigo (58 % mágico ya pide algo de resist. mágica).
        double magicLean = profile.MagicShare - 0.5;
        Bonus(magicLean > 0.03 && item.Has("SpellBlock"), Math.Min(0.05, magicLean * 0.2 + 0.01),
            LF($"resist. mágica: el {profile.MagicShare:P0} del daño enemigo es mágico", $"magic resist: {profile.MagicShare:P0} of enemy damage is magic"));
        Bonus(magicLean < -0.03 && item.Has("Armor"), Math.Min(0.05, -magicLean * 0.2 + 0.01),
            LF($"armadura: el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"armor: {1 - profile.MagicShare:P0} of enemy damage is physical"));
        Bonus(profile.Healers >= 2 && item.AppliesWounds, 0.04 * fit, L("heridas graves: el enemigo se cura mucho", "grievous wounds: the enemy heals a lot"));
        // La penetración también según lo que se compra con tu campeón: sin eso, la Cicloespada Voltaica le salía a Ezreal y a
        // Kai'Sa contra tanques (no la compra ni el 1 %) y Florescencia Críptica a Miss Fortune.
        Bonus(!_tankBuild && !iAmMagic && (profile.Tanks >= 2 || profile.ArmorItems >= 3) && item.Has("ArmorPenetration"), 0.03 * fit, L("penetración de armadura: tanques / armadura", "armor penetration: tanks / armor"));
        Bonus(!_tankBuild && iAmMagic && (profile.Tanks >= 2 || profile.MagicResistItems >= 3) && item.Has("MagicPenetration"), 0.03 * fit, L("penetración mágica: tanques / resist. mágica", "magic penetration: tanks / magic resist"));
        // Resistencias del tipo equivocado restan.
        Bonus(magicLean < -0.05 && item.Has("SpellBlock") && !item.Has("Armor"), -0.03, "");
        Bonus(magicLean > 0.05 && item.Has("Armor") && !item.Has("SpellBlock"), -0.03, "");

        Bonus(_critCapped && item.Has("CriticalStrike") && item.Id != InfinityEdge, CritCappedPenalty, "");

        // Build de tanque (Amumu, Nasus… con objetos y aumentos de aguante): un objeto solo de daño encaja peor.
        // Los mixtos (Morellonomicón: AP + vida) también: de tanque se busca aguante (Cota de Espinas da las heridas graves).
        Bonus(_tankBuild && item.IsOffensive, -0.05, "");

        // Tus objetos ya deciden el daño (Shaco con Youmuu y Recordatorio Mortal): un objeto solo del otro tipo encaja mal.
        // La velocidad de ataque sola no hace a un objeto de AD: el Diente de Nashor (AP + vel. ataque) es de AP para Shaco AD.
        bool apOnly = ApItem.Any(item.Has) && !item.Has("Damage") && !item.Has("CriticalStrike") && !item.Has("ArmorPenetration"),
             adOnly = AdItem.Any(item.Has) && !ApItem.Any(item.Has);
        // Fuerte: un aumento de prisa o de "habilidades" no debe colar una Antorcha de Fuegoscuro en una build AD.
        // Salvo lo normal del estilo de tus objetos: un on-hit mezcla AD y AP (Matakrakens con Diente de Nashor en Lulu).
        bool styleItem = _style?.FromItems is not null && _style.Items.GetValueOrDefault(item.Id) >= StyleItemMin;
        Bonus(_fromItems && apOnly && _apShare < 0.35 && !styleItem, -0.2, "");
        Bonus(_fromItems && adOnly && _apShare > 0.65 && !styleItem, -0.2, "");

        // Un aumento que necesita este objeto para funcionar (misiones, "Mejorar X"…): va primero.
        foreach (var aug in myAugments)
            Bonus(stats.Links.RequiredBy(aug.Id).Contains(item.Id) && !WrongDamageFor(item, iAmMagic), RequiredBonus, LF($"lo necesita tu aumento «{aug.DisplayName}»", $"your augment “{aug.DisplayName}” needs it"));

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
        // También según lo que se compra con tu campeón: «Goliat da vida» subía cualquier objeto con vida (Presagio de Randuin
        // a Yasuo, Cho'Gath, Leona…; se compraba el 2 % de las veces). Lo que se compra con tus aumentos ya lo trae tu build.
        double augFit = Math.Min(1, ChampionShare(champion, item.Id, anyBuild: true) / CounterFullShare);
        Bonus(augBonus > 0, Math.Min(augBonus, MaxAugmentBonus) * augFit,
            named.Count == 1 ? LF($"va con tu aumento {named[0]}", $"fits your augment {named[0]}") : LF($"va con tus aumentos {string.Join(" y ", named)}", $"fits your augments {string.Join(L(" y ", " and "), named)}"));
        // Un aumento que crece con tu maná máximo (Sobrecarga, A Tope, Paso Redoblado) pide objetos con maná. Va aparte del
        // tope: con 3-4 aumentos de AP todos los objetos de AP llegan al tope y la Vara de las Edades no se distinguía.
        if (item.Has("Mana") && myAugments.FirstOrDefault(ScalesWithMana) is { } manaAug)
            Bonus(true, ManaScalingBonus, LF($"«{manaAug.DisplayName}» crece con tu maná máximo", $"“{manaAug.DisplayName}” scales with your max mana"));

        reason ??= WinRateReason(champion, item.Id, rate, games) ?? L("encaja con tu campeón", "fits your champion");
        return new ItemAdvice(item, score, games, reason);
    }

    /// <summary>
    /// ¿Casi nadie compra botas con este campeón? Yuumi va pegada a un aliado: según su página de mayhemmeta, botas el
    /// 3,7 % (la siguiente, Karthus, el 42 %). La app le recomendaba Botas del Hechicero mejoradas o Grebas del Berserker.
    /// </summary>
    bool SkipsBoots(string champion) =>
        stats.Global.For(champion) is { Boots.Count: > 0 } page && page.Boots.Sum(b => b.PickRate ?? 0) < NoBootsPickRate;
    const double NoBootsPickRate = 15;

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
                double score = rate - baseRate + PopularBonus(champion, i.Id, shared: true);
                string? reason = null;
                void Bonus(bool when, double amount, string why) { if (when) { score += amount; reason ??= why; } }

                double lean = profile.MagicShare - 0.5;
                Bonus(lean < -0.03 && i.Has("Armor"), Math.Min(0.05, -lean * 0.2 + 0.01), LF($"armadura: el {1 - profile.MagicShare:P0} del daño enemigo es físico", $"armor: {1 - profile.MagicShare:P0} of enemy damage is physical"));
                Bonus(lean > 0.03 && i.Has("SpellBlock"), Math.Min(0.05, lean * 0.2 + 0.01), LF($"resist. mágica y tenacidad: el {profile.MagicShare:P0} del daño enemigo es mágico", $"magic resist and tenacity: {profile.MagicShare:P0} of enemy damage is magic"));
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
                    Bonus(true, AugmentMatchBonus, LF($"va con tu aumento «{fitAug.DisplayName}»", $"fits your augment “{fitAug.DisplayName}”"));

                return new ItemAdvice(i, score, games, reason ?? WinRateReason(champion, i.Id, rate, games) ?? L("opción estándar", "standard pick"));
            })
            .Select(a => InBuilding(a, myItems)) // con las botas básicas ya compradas, las de nivel 2 están "a medias"
            .OrderByDescending(a => a.Score)
            .FirstOrDefault();
    }
}
