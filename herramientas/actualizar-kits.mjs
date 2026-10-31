#!/usr/bin/env node
/**
 * Saca de los datos del cliente del juego (CommunityDragon) para qué sirve cada habilidad de cada campeón:
 *
 *   node herramientas/actualizar-kits.mjs     # ~1 minuto (2 archivos por campeón)
 *
 * Escribe datos\campeones-kit.json -> ChampionKits.cs. Por campeón:
 *  - ratios: cuánto escala su daño con cada estadística (suma de las 5 habilidades; en cada una, el mayor ratio de sus
 *    daños). Sale de los cálculos de los tooltips (mSpellCalculations), el mismo número que ves en el juego.
 *  - qué habilidades aplican efectos al impactar, dan velocidad de ataque, interactúan con el crítico, curan, dan
 *    escudo o acumulan cargas permanentes (del texto en inglés de cada habilidad; el texto NO se guarda).
 *  - late: 1 normal, 2 escala bien en partida larga, 3 escala sin límite (cargas permanentes).
 * Se ejecuta una vez por parche (o cuando Riot cambie un campeón).
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "datos");
const FILE = "campeones-kit.json";
const CD = "https://raw.communitydragon.org/latest";
const headers = { "user-agent": "Mayhem Advisor (uso personal)" };

async function json(url) {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, { headers });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return await res.json();
    } catch (e) {
      if (attempt >= 3) throw new Error(`${url}: ${e.message}`);
      await new Promise((r) => setTimeout(r, 2000 * attempt));
    }
  }
}

// Estadística de los cálculos del juego (mStat) y si es total (0), base (1) o adicional (2) (mStatFormula).
// Comprobado con Malphite (armadura), Galio (resist. mágica), Cho'Gath / Garen (vida), Garen / Nasus (crítico).
function statName(stat = 0, formula = 0) {
  switch (stat) {
    case 0: return "ap";
    case 1: return formula === 2 ? "bonusArmor" : "armor";
    case 2: return formula === 2 ? "bonusAd" : "ad";
    case 3: return "as";
    case 6: return formula === 2 ? "bonusMr" : "mr";
    case 9: return "crit";
    case 12: return formula === 2 ? "bonusHealth" : "health";
    default: return null;
  }
}

/** Ratio de un valor con nombre (DataValues): el mayor entre los rangos 1-5 (el 0 no se usa). */
function dataValue(spell, name) {
  const row = (spell.DataValues ?? []).find((v) => v.name === name);
  const values = (row?.values ?? []).slice(1, 6);
  return values.length ? Math.max(...values) : 0;
}

/** Recorre un cálculo y suma sus partes "estadística × ratio" (no entra en productos ni modificadores raros). */
function statParts(part, spell, out) {
  if (!part || typeof part !== "object") return;
  const type = part.__type;
  if (type === "StatByNamedDataValueCalculationPart" || type === "StatByCoefficientCalculationPart") {
    const stat = statName(part.mStat, part.mStatFormula);
    const ratio = type === "StatByCoefficientCalculationPart" ? part.mCoefficient ?? 0 : dataValue(spell, part.mDataValue);
    if (stat && ratio > 0 && ratio < 20) out[stat] = (out[stat] ?? 0) + ratio;
    return;
  }
  if (type === "ProductOfSubPartsCalculationPart") return;
  for (const p of part.mFormulaParts ?? []) statParts(p, spell, out);
  for (const p of part.mSubparts ?? []) statParts(p, spell, out);
}

/** Qué hace la habilidad según su texto (inglés de Riot). Solo se guardan estas marcas, nunca el texto. */
function textFlags(html) {
  const text = (html ?? "").replace(/<br\s*\/?>/gi, " ");
  const plain = text.replace(/<[^>]+>/g, "").replace(/@[^@]*@/g, "X");
  return {
    // "applies on-hit effects", "Attacks deal bonus magic damage", "her Attacks apply..." (no "next Attack": eso es un golpe potenciado).
    onHit: /on-hit/i.test(plain) || /\b(?<!next )(?<!empowered )(basic )?attacks (also |additionally |now |against [a-z ]+ )?(deal|apply|fire|burn|shred|mark|infect)/i.test(plain),
    attackSpeed: /<attackSpeed>/i.test(text) || /attack speed/i.test(plain),
    crit: /critical(ly)? strike|crit chance|crit damage/i.test(plain),
    heal: /<healing>/i.test(text) || /\b(heals?|healing|restor(es|ing) [^.]*health)\b/i.test(plain),
    shield: /<shield>/i.test(text) || /\bshield(s|ed|ing)?\b/i.test(plain),
    stacks: /\bpermanent(ly)?\b/i.test(plain),
    levelSpike: /\blevel (6|11|13|16)\b|\bat levels? \d/i.test(plain),
    mana: /(maximum|max|bonus) mana/i.test(plain),
  };
}

// Los textos del juego son resúmenes: no dicen, por ejemplo, que la Q de Ezreal o de Yasuo aplica efectos al impactar.
// Esto se suma a lo que sale del texto (revisado a mano, octubre 2026; los campeones nuevos solo con lo del texto).
const CURATED = {
  // Habilidades que aplican efectos al impactar o hacen daño al impactar: con ellas rinden Rey Arruinado, Matakrakens…
  onHit: {
    Akshan: "P", Ashe: "Q", Azir: "W", Belveth: "PE", Diana: "P", Ezreal: "Q", Fiora: "QE", Gangplank: "Q", Gwen: "PQ",
    Irelia: "PQ", Jax: "R", Kaisa: "P", Kalista: "PE", Kassadin: "W", Katarina: "E", Kayle: "PE", KogMaw: "W",
    Lucian: "P", MasterYi: "QE", Nilah: "Q", Orianna: "P", Quinn: "P", Sivir: "W", Teemo: "E", Twitch: "PE", Udyr: "Q",
    Varus: "W", Vayne: "W", Viego: "Q", Volibear: "P", Warwick: "P", XinZhao: "Q", Yasuo: "Q", Yone: "Q", Zeri: "PE",
  },
  // Habilidades que dan velocidad de ataque o escalan con ella.
  attackSpeed: {
    Ashe: "Q", Belveth: "PE", Draven: "W", Ezreal: "P", Irelia: "P", Jax: "P", Jinx: "PQ", Kaisa: "E", Kayle: "P",
    KogMaw: "Q", MasterYi: "R", Nilah: "Q", Samira: "E", Sivir: "R", Tristana: "Q", Twitch: "Q", Udyr: "P", Warwick: "W",
    XinZhao: "E", Yasuo: "Q", Zeri: "R",
  },
  // Habilidades que usan o mejoran el crítico.
  crit: { Ashe: "P", Gangplank: "Q", Jhin: "P", Samira: "P", Tryndamere: "PR", Yasuo: "P", Yone: "P" },
  // Cargas que crecen toda la partida sin tope: cuanto más dura, más fuertes.
  stacks: {
    AurelionSol: "P", Bard: "P", Belveth: "P", Chogath: "R", Kindred: "P", Nasus: "Q", Senna: "P", Sion: "W",
    Smolder: "P", Swain: "P", Thresh: "P", Veigar: "P", Viktor: "P",
  },
};
// Escalan muy bien en partida larga sin cargas infinitas (niveles clave, carries de velocidad de ataque…).
const LATE = new Set(["Aphelios", "Azir", "Cassiopeia", "Fiora", "Gangplank", "Gwen", "Jax", "Jinx", "Kaisa", "Kassadin",
  "Kayle", "KogMaw", "MasterYi", "Nilah", "Ornn", "Ryze", "Sivir", "Tryndamere", "Twitch", "Vayne", "Vladimir", "Xayah",
  "Yasuo", "Yone", "Zeri"]);
// Lo del texto que no es lo que parece: "permanentemente" en Garen es un tope de armadura, no una carga sin límite.
const NOT_STACKS = new Set(["Garen"]);

const version = (await json(`${CD}/content-metadata.json`)).version ?? "";
// Los campeones de verdad tienen id < 1000 (los demás son de otros modos: "Jade_Ahri"…).
const summary = (await json(`${CD}/plugins/rcp-be-lol-game-data/global/en_gb/v1/champion-summary.json`)).filter((c) => c.id > 0 && c.id < 1000);
console.log(`Versión del juego ${version}: ${summary.length} campeones`);

const champions = {};
const failed = [];
let next = 0;
async function worker() {
  while (next < summary.length) {
    const { id, alias } = summary[next++];
    try {
      const lower = alias.toLowerCase();
      const [info, bin] = await Promise.all([
        json(`${CD}/plugins/rcp-be-lol-game-data/global/en_gb/v1/champions/${id}.json`),
        json(`${CD}/game/data/characters/${lower}/${lower}.bin.json`),
      ]);
      champions[alias] = kit(info, bin, alias);
    } catch (e) {
      failed.push(`${alias}: ${e.message}`);
    }
  }
}

function kit(info, bin, alias) {
  const root = Object.values(bin).find((v) => v?.__type === "CharacterRecord" && v.mCharacterName?.toLowerCase() === alias.toLowerCase())
    ?? Object.values(bin).find((v) => v?.__type === "CharacterRecord");
  // Carpeta de cada habilidad (Q W E R) y la pasiva (la carpeta que sobra).
  const folders = (root?.spellNames ?? []).map((n) => n.split("/")[0]);
  const prefix = `Characters/${root?.mCharacterName ?? alias}/Spells/`;
  const passiveFolder = (root?.mAbilities ?? []).map((a) => a.split("/").pop()).find((f) => !folders.includes(f));
  const keyOf = (objectPath) => {
    if (!objectPath.toLowerCase().startsWith(prefix.toLowerCase())) return null;
    const folder = objectPath.slice(prefix.length).split("/")[0];
    const i = folders.indexOf(folder);
    return i >= 0 ? "QWER"[i] : folder === passiveFolder ? "P" : null;
  };

  // Por habilidad: el mayor ratio de cada estadística entre sus cálculos de daño (no se suman variantes del mismo golpe).
  const damage = {};
  const support = { heal: new Set(), shield: new Set() };
  for (const [objectPath, obj] of Object.entries(bin)) {
    const spell = obj?.mSpell;
    const key = spell && keyOf(objectPath);
    if (!key) continue;
    for (const [name, calc] of Object.entries(spell.mSpellCalculations ?? {})) {
      const parts = {};
      statParts(calc, spell, parts);
      const lower = name.toLowerCase();
      // "heal" pero no "health" ("TotalHealthDamage" de Kog'Maw es daño, no curación).
      const kind = /heal(?!th)/.test(lower) ? "heal" : /shield/.test(lower) ? "shield" : /dmg|damage/.test(lower) ? "damage" : null;
      if (kind === "heal" || kind === "shield") { support[kind].add(key); continue; }
      if (kind !== "damage") continue;
      damage[key] ??= {};
      for (const [stat, ratio] of Object.entries(parts)) damage[key][stat] = Math.max(damage[key][stat] ?? 0, ratio);
    }
  }
  const ratios = {};
  for (const perStat of Object.values(damage))
    for (const [stat, ratio] of Object.entries(perStat)) ratios[stat] = Math.round(((ratios[stat] ?? 0) + ratio) * 100) / 100;

  const flags = { onHit: [], attackSpeed: [], crit: [], heal: [...support.heal], shield: [...support.shield], stacks: [], levelSpike: [], mana: [] };
  const abilities = [["P", info.passive?.description], ...(info.spells ?? []).map((s) => [s.spellKey.toUpperCase(), s.dynamicDescription || s.description])];
  for (const [key, html] of abilities) {
    const f = textFlags(html);
    for (const name of Object.keys(flags)) if (f[name] && !flags[name].includes(key)) flags[name].push(key);
  }
  if (NOT_STACKS.has(alias)) flags.stacks = [];
  for (const [name, byChampion] of Object.entries(CURATED))
    for (const key of byChampion[alias] ?? "") if (!flags[name].includes(key)) flags[name].push(key);
  for (const name of Object.keys(flags)) flags[name].sort((a, b) => "PQWER".indexOf(a) - "PQWER".indexOf(b));

  const roles = info.roles ?? [];
  const marksmanCarry = roles[0] === "marksman" && (flags.attackSpeed.length > 0 || flags.crit.length > 0 || flags.onHit.length > 0);
  const late = flags.stacks.length > 0 ? 3 : LATE.has(alias) || flags.levelSpike.length > 0 || marksmanCarry ? 2 : 1;
  return {
    name: info.name,
    attack: info.tacticalInfo?.attackType ?? "",
    damageType: (info.tacticalInfo?.damageType ?? "").replace(/^k/, "").toLowerCase(),
    roles,
    ratios,
    ...Object.fromEntries(Object.entries(flags).filter(([, v]) => v.length > 0)),
    late,
  };
}

await Promise.all(Array.from({ length: 6 }, worker));
if (Object.keys(champions).length < summary.length * 0.9) {
  console.error(`Solo ${Object.keys(champions).length} de ${summary.length} campeones; no se toca ${FILE}.\n${failed.join("\n")}`);
  process.exit(1);
}
const sorted = Object.fromEntries(Object.entries(champions).sort(([a], [b]) => a.localeCompare(b)));
const target = path.join(DIR, FILE);
fs.writeFileSync(target + ".tmp", JSON.stringify({ schema: 1, version, generatedAt: new Date().toISOString(), champions: sorted }, null, 1));
fs.renameSync(target + ".tmp", target);
console.log(`Escrito ${target}: ${Object.keys(sorted).length} campeones${failed.length ? `; fallaron ${failed.length}:\n${failed.join("\n")}` : ""}`);
