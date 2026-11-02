// Auditoría con TODOS los campeones: cuando un jugador avisa de un fallo con un campeón (Wakka: el Coleccionista con Vayne,
// el fragmento de crítico con Cho'Gath…), se comprueba lo mismo con los 173 para ver a cuántos les pasa.
// Uso: MAYHEM_DATA_DIR=<copia de tus datos y de datos\> dotnet run -c Release [-- detalle]
// Cada apartado imprime cuántos campeones/aumentos fallan y, con "detalle", cuáles.
using MayhemAdvisor.Core;

bool detail = args.Contains("detalle");
using var http = new HttpClient();
var catalog = await AugmentCatalog.LoadAsync(http);
var data = await GameData.LoadAsync(http);
var stats = StatsStore.Load(Path.Combine(Paths.DataDir, "no-existe.jsonl"), data);
var builds = MayhemBuilds.Load(DataFiles.Resolve("mayhem-champion-builds.json"));
var table = MayhemAugments.Load(DataFiles.Resolve("mayhem-augments.json"));
catalog = catalog.WithExtraDescriptions(table.Descriptions);
stats.Global = builds;
stats.AugmentTable = table;
stats.Aramhex = ExternalAugments.Load(DataFiles.Resolve("aramhex-augments.json"));
stats.Aramkit = ExternalAugments.Load(DataFiles.Resolve("aramkit-augments.json"));
stats.AramkitBuilds = AugmentBuilds.Load(DataFiles.Resolve("aramkit-builds.json"));
stats.Links = new AugmentItemLinks(catalog.Augments, data, builds.ItemNames);
stats.Kits = ChampionKits.Load(DataFiles.Resolve("campeones-kit.json"));

var mayhem = MayhemChampions.Load(DataFiles.Resolve("mayhem-champions.json"));
var advisor = new Advisor(stats);
var champions = data.Champions.Values.Select(c => c.Alias).Where(a => stats.AramkitBuilds.Styles(a).Count > 0).OrderBy(a => a).ToList();
var augments = catalog.Augments.Where(a => !a.IsAnvil).ToList();
Augment Named(string name) => augments.First(a => a.Names.GetValueOrDefault("default") == name);
string Item(int id) => data.Item(id)?.NameEn ?? id.ToString();
// Un equipo enemigo mezclado (AP y AD), sin objetos: que no decida la build.
(string, IReadOnlyList<int>)[] enemies = [("Garen", []), ("Lux", []), ("Jinx", []), ("Leona", []), ("Ahri", [])];
Console.WriteLine($"{champions.Count} campeones con datos de builds, {augments.Count} aumentos");
void Report(string title, int bad, int total, IEnumerable<string> examples)
{
    Console.WriteLine($"\n== {title}: {bad} de {total}");
    foreach (var e in examples.Take(detail ? 60 : 8)) Console.WriteLine("   " + e);
}

// 1. Aumentos "de todo" (dan o mejoran cualquier estadística: Arma del Destino) no deben pedir ninguna build.
string[] core = ["AD", "AP", "AS", "Crit", "Armor", "MR", "Health", "Haste", "Lethality", "MagicPen", "Mana"];
var statLists = augments.Where(a => a.StatList.Count(core.Contains) >= 5).ToList();
Report("Aumentos con 5+ estadísticas (¿de todo?)", statLists.Count, augments.Count,
    statLists.Select(a => $"{a.Names["default"]}: {string.Join(", ", a.StatList)}"));

// 2. Aumentos que "piden otra build" a la mayoría de campeones: suelen estar mal etiquetados.
var fits = augments.Select(a => (Aug: a, Bad: champions.Select(c => (c, f: advisor.BuildFit(a, c, [], []))).Where(x => x.f is { Bonus: < 0 }).ToList()))
    .Where(x => x.Bad.Count >= champions.Count * 0.4).OrderByDescending(x => x.Bad.Count).ToList();
Report("Aumentos que 'piden otra build' a 40 %+ de los campeones", fits.Count, augments.Count,
    fits.Select(x => $"{x.Aug.Names["default"]} [{string.Join(", ", x.Aug.StatList)}]: {x.Bad.Count} campeones, p. ej. {x.Bad[0].c}: {x.Bad[0].f!.Reason}"));

// 3. Con el crítico al 100 %, no recomendar más crítico (salvo el Filo del Infinito).
var critBad = new List<string>(); int critTotal = 0;
foreach (var c in champions)
{
    if (stats.AramkitBuilds.Styles(c).FirstOrDefault(s => s.Key == "crit") is not { } crit) continue;
    var core3 = crit.Items.Where(kv => data.Item(kv.Key) is { IsFinal: true, IsBoots: false } i && i.Has("CriticalStrike")).OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
    if (core3.Count < 3) continue;
    critTotal++;
    var advice = advisor.Build(c, core3, enemies, [], 0, critChance: 1.0);
    var wrong = advice.Items.Where(a => a.Item.Has("CriticalStrike") && a.Item.Id != 3031).ToList();
    if (wrong.Count > 0) critBad.Add($"{c} con {string.Join(" + ", core3.Select(Item))}: {string.Join(", ", wrong.Select(w => w.Item.NameEn))}");
}
Report("Campeones de crítico al 100 % a los que aún se les recomienda crítico", critBad.Count, critTotal, critBad);

// 4. Fragmento de crítico para campeones de AP con un aumento que usa el crítico (Guantelete Enjoyado).
var gauntlet = Named("Jeweled Gauntlet");
var shards = catalog.Augments.Where(a => a.IsAnvil).ToList();
var critShard = shards.FirstOrDefault(s => s.StatList.Contains("Crit"));
var anvilBad = new List<string>(); int anvilTotal = 0;
if (critShard is not null)
    foreach (var c in champions.Where(c => advisor.IsMagic(c)))
    {
        anvilTotal++;
        var ranked = Anvil.Rank(shards.Where(s => s.StatList.Contains("Crit") || s.StatList.Contains("AP") || s.StatList.Contains("MagicPen")), c, stats,
            EnemyProfile.Build(enemies, stats), [gauntlet], []);
        var crit = ranked.First(r => r.Fragment.Id == critShard.Id);
        if (crit.Score <= 0) anvilBad.Add($"{c}: fragmento de crítico {crit.Score:+0.0;-0.0} «{crit.Reason}»");
    }
Report("Campeones de AP con Guantelete Enjoyado a los que el crítico 'no les sirve'", anvilBad.Count, anvilTotal, anvilBad);

// 4b. Con el 100 % de crítico: el fragmento de crítico no debe ser el mejor y Absorción de Alma debe bajar (a todos).
var siphon = Named("Soul Siphon");
var capBad = new List<string>(); int capTotal = 0;
if (critShard is not null)
    foreach (var c in champions)
    {
        capTotal++;
        var ranked = Anvil.Rank(shards.Where(s => s.StatList.Contains("Crit") || s.StatList.Contains("AD") || s.StatList.Contains("Health")), c, stats,
            EnemyProfile.Build(enemies, stats), [], [], critChance: 1.0);
        bool shardTop = ranked[0].Fragment.Id == critShard.Id;
        var syn = advisor.SynergyWith(siphon, [], c, [], critChance: 1.0);
        if (shardTop || syn is not { Bonus: < 0 }) capBad.Add($"{c}: fragmento de crítico {(shardTop ? "el mejor" : "bien")} · Absorción de Alma {syn?.Bonus:+0.00;-0.00} {syn?.Reason}");
    }
Report("Campeones al 100 % de crítico a los que el crítico sigue sumando", capBad.Count, capTotal, capBad);

// 4c. Aumentos de crítico para habilidades (Fuego Gemelo, Curación Crítica) no deben "pedir ataques básicos" a los magos.
var critAbility = augments.Where(StatTags.UsesCritChance).ToList();
var critFitBad = new List<string>(); int critFitTotal = 0;
foreach (var a in critAbility)
    foreach (var c in champions.Where(c => advisor.IsMagic(c)))
    {
        critFitTotal++;
        if (advisor.BuildFit(a, c, [], []) is { Bonus: < 0 } f && f.Reason.Contains("ataques")) critFitBad.Add($"{a.Names["default"]} con {c}: {f.Reason}");
    }
Report($"Aumentos de crítico de habilidades ({string.Join(", ", critAbility.Select(a => a.Names["default"]))}) que 'piden ataques' a magos", critFitBad.Count, critFitTotal, critFitBad);

// 5. Objetos clave de cada build en el selector: ¿son de ESA build o salen igual en otra del mismo campeón?
var keyBad = new List<string>(); int keyTotal = 0;
foreach (var c in champions)
{
    var styles = stats.AramkitBuilds.Styles(c);
    if (styles.Count < 2) continue;
    foreach (var s in styles)
    {
        var advice = advisor.Build(c, [], enemies, [], 0, lockedStyle: s.Key);
        if (advice.Styles?.FirstOrDefault(o => o.Active) is not { } active) continue;
        keyTotal++;
        // Muy comprado también en otra build del campeón (y no más en esta): no dice nada de esta build.
        var shared = active.Items.Where(i => styles.Any(o => o.Key != s.Key && o.Items.GetValueOrDefault(i.Id) >= s.Items.GetValueOrDefault(i.Id) * 0.8
                                                          && o.Items.GetValueOrDefault(i.Id) >= 0.25)).ToList();
        if (shared.Count > 0) keyBad.Add($"{c} {s.Key}: {string.Join(", ", shared.Select(i => i.NameEn))}");
    }
}
Report("Builds del selector con objetos clave que no son de esa build", keyBad.Count, keyTotal, keyBad);

// 6. Lo primero que recomienda (sin objetos): ¿algún objeto que gana claramente menos que el campeón (2+ puntos, 500+ partidas)?
var loseBad = new List<string>(); int loseTotal = 0, loseItems = 0;
foreach (var c in champions)
{
    var build = stats.Global.For(c);
    if (build is null) continue;
    loseTotal++;
    if (mayhem.For(c)?.WinRate is not { } champWr) continue;
    var advice = advisor.Build(c, [], enemies, [], 0);
    var bad = advice.Items.Where(a => stats.GlobalItem(c, a.Item.Id) is { WinRate: { } wr } g && build.GamesWith(g) >= 500 && wr - champWr <= -2).ToList();
    // ¿Había en la build habitual algo que gana más y no se recomendó?
    loseItems += bad.Count;
    if (bad.Count > 0) loseBad.Add($"{c} ({champWr:0.0} %): {string.Join(", ", bad.Select(a => $"{a.Item.NameEn} {stats.GlobalItem(c, a.Item.Id)!.WinRate:0.0} %"))}");
}
Report($"Campeones a los que se recomienda de primeras un objeto que gana 2+ puntos menos ({loseItems} objetos)", loseBad.Count, loseTotal, loseBad);

// 7. Aumentos que valen con ataques O habilidades (Desgarraoro, Patada Aérea, Captura al Paso…) no deben «pedir» ninguno de
// los dos (Desgarraoro salía «Pide ataques básicos» con Alistar, partida del usuario del 2026-11-01).
string Plain(Augment a) => new string((a.Description + " " + a.ExtraDescription).Normalize(System.Text.NormalizationForm.FormD)
    .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
// (No cuenta «el siguiente ataque o habilidad de tu aliado» de Amanecer Abrasador: eso lo hace tu aliado, el aumento va de tus habilidades.)
var eitherRx = new System.Text.RegularExpressions.Regex(@"(?<!ally's next (basic )?)((basic )?attacks? (or|and) (an )?abilit(y|ies)|abilit(y|ies) (or|and) (basic )?attacks?)|(ataques? (basicos? )?(o|y) (con )?(una |sus |tus )?habilidad(es)?|habilidad(es)? (o|y) (un |tus )?ataques?)(?! de tu aliado)");
var either = augments.Where(a => eitherRx.IsMatch(Plain(a))).ToList();
var eitherBad = new List<(Augment Aug, string Champ, string Reason)>(); int eitherTotal = 0;
foreach (var a in either)
    foreach (var c in champions)
    {
        eitherTotal++;
        if (advisor.BuildFit(a, c, [], []) is { Bonus: < 0 } f && (f.Reason.Contains("ataques") || f.Reason.Contains("habilidades"))) eitherBad.Add((a, c, f.Reason));
    }
Report($"Aumentos de «ataques o habilidades» ({string.Join(", ", either.Select(a => a.Names["default"]))}) que piden uno de los dos", eitherBad.Count, eitherTotal,
    eitherBad.GroupBy(x => x.Aug).OrderByDescending(g => g.Count()).Select(g => $"{g.Key.Names["default"]} [{string.Join(", ", g.Key.StatList)}]: {g.Count()} campeones, p. ej. {g.First().Champ}: {g.First().Reason}"));

// 8. Combos de mecánica sin partidas que midan la pareja (dos de la definitiva, dos que se activan al inmovilizar): ¿los
// reconoce la app? (Revolución Definitiva + Despertar Definitivo y Devorador de Almas + Infranqueable con Alistar.)
var ccRx = new System.Text.RegularExpressions.Regex(@"immobiliz(ing|e) or ground|(after|when you) immobiliz|(al|tras|despues de) inmovilizar");
var families = new[] { ("definitiva", augments.Where(a => a.NeedsUltimate).ToList()), ("inmovilizar", augments.Where(a => ccRx.IsMatch(Plain(a))).ToList()) };
foreach (var (fam, set) in families)
{
    var unmeasured = set.SelectMany((a, i) => set.Skip(i + 1).Select(b => (a, b))).Where(p => table.Pair(p.a.Id, p.b.Id) is not { Games: >= 50 }).ToList();
    var missed = new List<string>(); int famTotal = 0, unexplained = 0;
    foreach (var c in champions)
        foreach (var (a, b) in unmeasured)
        {
            famTotal++;
            // Lo que suma la pareja: la nota con el otro aumento menos la nota sin él. Se mide ~+0,7 / +1 punto (ver Advisor.Combo).
            var syn = advisor.SynergyWith(a, [b], c, []);
            double gain = (syn?.Bonus ?? 0) - (advisor.SynergyWith(a, [], c, [])?.Bonus ?? 0);
            if (gain < 0.006) missed.Add($"{a.Names["default"]} + {b.Names["default"]} con {c}: suma {gain * 100:+0.0;-0.0} · {syn?.Reason ?? "nada"}");
            else if (!syn!.Reason.Contains("combo")) unexplained++;
        }
    Report($"Combos de {fam} sin partidas medidas ({unmeasured.Count} parejas × campeones) a los que la pareja no suma (+0,6 o más)", missed.Count, famTotal, missed);
    Console.WriteLine($"   (y {unexplained} que suman pero la carta lo explica con otra cosa)");
}

// 9. La nota con tu build (Wakka: «tiene que mirar tus objetos»): con los 3 objetos más propios de cada build del campeón,
// ¿cuántas cartas cambian 10+ puntos de nota frente a no tener objetos? (AugmentBuilds.StyleEffect)
var moved = new List<(double Diff, string Text)>(); int movedTotal = 0;
// Con AUDIT_CARTAS=<archivo> apunta cada carta (campeón, build, aumento, nota sin objetos, con objetos) para comparar versiones.
using var cardsOut = Environment.GetEnvironmentVariable("AUDIT_CARTAS") is { Length: > 0 } cardsPath ? new StreamWriter(cardsPath) : null;
foreach (var c in champions)
{
    var styles = stats.AramkitBuilds.Styles(c);
    if (styles.Count < 2) continue;
    var scale9 = AugmentScale.For(c, DamageMix.Mixed, stats, catalog);
    foreach (var s in styles.Where(s => s.Games >= 5000))
    {
        var own = s.Items.Where(kv => data.Item(kv.Key) is { IsFinal: true, IsBoots: false })
            .OrderByDescending(kv => kv.Value - styles.Where(o => o.Key != s.Key).Select(o => o.Items.GetValueOrDefault(kv.Key)).DefaultIfEmpty(0).Max())
            .Take(3).Select(kv => kv.Key).ToList();
        if (advisor.StyleFromItems(c, own) is not { FromItems.Length: > 0 } got || got.Key != s.Key) continue;
        foreach (var a in augments)
        {
            if (stats.AramkitBuilds.Styles(c, a.Id) is null) continue;
            movedTotal++;
            var rec = stats.Rate(c, a.Id);
            double Score(IReadOnlyList<int> items) => scale9.Score(AugmentScale.Evidence(rec) + (advisor.SynergyWith(a, [], c, items)?.Bonus ?? 0), AugmentScale.IsReliable(rec));
            double before = Score([]), after = Score(own);
            cardsOut?.WriteLine($"{c}	{s.Key}	{a.Id}	{before:0.0}	{after:0.0}");
            if (Math.Abs(after - before) >= 10)
                moved.Add((after - before, $"{c} {s.Key} + {a.Names["default"]}: {before:0} → {after:0} · {advisor.SynergyWith(a, [], c, own)?.Reason}"));
        }
    }
}
Report("Cartas cuya nota cambia 10+ puntos con los objetos de una build del campeón", moved.Count, movedTotal,
    moved.OrderByDescending(m => Math.Abs(m.Diff)).Select(m => m.Text));

// 10. Aumentos de «cualquier estadística» (Arma del Destino, Campeón de Urf): ninguna carta «va con» ellos por potenciar lo
// mismo (Wakka, 2026-11-02: cartas «fits Weapon of Destiny» con Evelynn).
var anyStat = augments.Where(a => a.Names.GetValueOrDefault("default") is "Weapon of Destiny" or "Urf's Champion").ToList();
var anyBad = new List<string>(); int anyTotal = 0;
foreach (var w in anyStat)
    foreach (var c in champions)
        foreach (var a in augments.Where(a => a.Id != w.Id))
        {
            anyTotal++;
            if (advisor.SynergyWith(a, [w], c, []) is { } syn && syn.Reason.Contains(w.DisplayName) && syn.Reason.Contains("potencian"))
                anyBad.Add($"{a.Names["default"]} con {c}: {syn.Reason}");
        }
Report($"Cartas que «van con» {string.Join(" / ", anyStat.Select(a => a.Names["default"]))} por potenciar lo mismo", anyBad.Count, anyTotal, anyBad);

// 11. «Tu kit aplica efectos al impactar» solo con velocidad de ataque o con un efecto al impactar de verdad: Mago Tirador
// («tus ataques infligen daño adicional») no lo es (Wakka, 2026-11-02, con Kog'Maw).
var onHitRx = new System.Text.RegularExpressions.Regex(@"on-hit|on hit|al impactar|al impacto");
var kitBad = new List<(Augment Aug, string Champ)>(); int kitTotal = 0;
foreach (var c in champions.Where(c => stats.Kits.For(c) is { HasOnHit: true }))
    foreach (var a in augments.Where(a => !a.StatList.Contains("AS") && !onHitRx.IsMatch(Plain(a))))
    {
        kitTotal++;
        if (advisor.SynergyWith(a, [], c, []) is { } syn && syn.Reason.Contains("efectos al impactar")) kitBad.Add((a, c));
    }
Report("Aumentos sin efecto al impactar ni velocidad de ataque a los que «tu kit aplica efectos al impactar»", kitBad.Count, kitTotal,
    kitBad.GroupBy(x => x.Aug).OrderByDescending(g => g.Count()).Select(g => $"{g.Key.Names["default"]}: {g.Count()} campeones, p. ej. {g.First().Champ}"));
