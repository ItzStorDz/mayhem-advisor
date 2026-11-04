#!/usr/bin/env node
/**
 * Descarga de aramkit.com el % de victorias de cada campeón con cada aumento en ARAM Mayhem (parche actual):
 *
 *   node herramientas/actualizar-aramkit.mjs     # ~7 min (1 archivo cada 2 s, para no cargar su servidor)
 *
 * Escribe datos\aramkit-augments.json (se lee con ExternalAugments.cs): % de cada campeón con cada aumento, de reserva
 * para los aumentos que AramHex aún no tiene. aramkit tiene ~1,3 millones de partidas por campeón (mayhemmeta, ~20.000).
 * Sus objetos NO se usan: probado el 2026-10-27, devolvían objetos raros (Arco Axiomático a Aatrox) sin predecir mejor.
 *
 * Sus condiciones (aramkit.com/en-US/terms) permiten uso personal y no comercial, sin descargas masivas ni frecuentes:
 * por eso va despacio y se ejecuta solo cuando ellos publican datos nuevos (su web lo dice: «Data updated»), como mucho
 * una vez por semana. No se presentan como datos oficiales (ver CREDITOS.txt).
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "datos");
const FILE = "aramkit-augments.json";
const BUILDS_FILE = "aramkit-builds.json";
/** Aumentos con menos partidas que esto con el campeón no se guardan en aramkit-builds.json (casi todo sería azar). */
const MIN_BUILD_GAMES = 500;
const headers = { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0 (Mayhem Advisor, uso personal)" };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function get(url, as = "text") {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, { headers });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return await res[as]();
    } catch (e) {
      if (attempt >= 2) throw new Error(`${url}: ${e.message}`);
      await sleep(5000);
    }
  }
}

// La página dice qué versión de los datos usa (p. ej. "data/16.21-20261027-0c5dc0fc7a3c"); los JSON están en data.aramkit.com.
const page = (await get("https://aramkit.com/en-US/champions")).replace(/\\u002F/gi, "/");
const dataVer = page.match(/data\/(\d+\.\d+-\d{8}-[0-9a-f]+)/)?.[1];
const resVer = page.match(/resources\/(\d+\.\d+-[0-9a-f]+)/)?.[1];
if (!dataVer || !resVer) {
  console.error("No encuentro la versión de los datos en aramkit.com (¿cambió la web?). No se toca " + FILE);
  process.exit(1);
}
const patch = dataVer.split("-")[0];
const champions = await get(`https://data.aramkit.com/resources/${resVer}/en-US/resources/champions.json`, "json");

/**
 * Estilos de build de aramkit ("utility", "ap", "ad", "tank"…): [estilo, partidas, % de victorias, {objeto: parte}],
 * donde parte = en qué parte de las partidas con ese estilo sale el objeto (sus 5 builds más comunes, con botas y
 * objetos posteriores). Solo objetos que salen en el 5 % o más, los 15 primeros.
 */
function styles(archetypes) {
  return (archetypes ?? []).filter((a) => a.sampleCount > 0).map((a) => {
    const count = new Map();
    const add = (id, n) => { if (id) count.set(id, (count.get(id) ?? 0) + n); };
    for (const p of a.profiles ?? []) {
      for (const it of p.itemSet ?? []) add(it.id, p.sampleCount);
      for (const r of p.routes ?? []) {
        for (const b of r.boots ?? []) add(b.item?.id, b.sampleCount);
        for (const l of r.laterItems ?? []) add(l.item?.id, l.sampleCount);
      }
    }
    const items = Object.fromEntries([...count].map(([id, n]) => [id, Math.min(1, n / a.sampleCount)])
      .filter(([, share]) => share >= 0.05).sort((x, y) => y[1] - x[1]).slice(0, 15).map(([id, share]) => [id, +share.toFixed(3)]));
    return [a.key, a.sampleCount, +(a.winRate * 100).toFixed(2), items];
  });
}

const out = {};
const builds = {};
const ids = Object.keys(champions);
let failed = 0;
for (const [i, id] of ids.entries()) {
  const alias = champions[id].riotId;
  try {
    const d = await get(`https://data.aramkit.com/data/${dataVer}/stats/all/champion-details/${id}.json`, "json");
    const st = d.champion.stats;
    out[alias] = {
      games: st.sampleCount,
      winRate: +(st.winRate * 100).toFixed(2),
      augments: d.augments.all.map((a) => [a.id, a.sampleCount, +(a.winRate * 100).toFixed(2)]),
    };
    await sleep(2000);
    const single = await get(`https://data.aramkit.com/data/${dataVer}/stats/all/champion-details/${id}-single-augments.json`, "json");
    const augments = {};
    for (const b of single.builds ?? [])
      if (b.sampleCount >= MIN_BUILD_GAMES) augments[b.augmentId] = { games: b.sampleCount, styles: styles(b.archetypes) };
    builds[alias] = { games: st.sampleCount, winRate: +(st.winRate * 100).toFixed(2), styles: styles(d.builds?.filtered?.archetypes), augments };
  } catch (e) {
    failed++;
    console.error(`  ! ${alias}: ${e.message}`);
  }
  if ((i + 1) % 20 === 0) console.log(`  ${i + 1}/${ids.length}`);
  await sleep(2000);
}

const count = Object.keys(out).length;
if (count < ids.length / 2) {
  console.error(`Solo ${count}/${ids.length} campeones. No se guarda ${FILE}.`);
  process.exit(1);
}
const target = path.join(DIR, FILE);
fs.writeFileSync(target + ".tmp", JSON.stringify({
  schema: "mayhem-advisor.aramkit-augments/1",
  source: "aramkit.com (todas las ligas)",
  gameMode: "ARAM Mayhem",
  augmentsColumns: ["augmentId", "games", "winRate"],
  patch,
  dataVersion: dataVer,
  fetchedAt: new Date().toISOString(),
  champions: out,
}));
fs.renameSync(target + ".tmp", target);
const buildsTarget = path.join(DIR, BUILDS_FILE);
fs.writeFileSync(buildsTarget + ".tmp", JSON.stringify({
  schema: "mayhem-advisor.aramkit-builds/1",
  source: "aramkit.com (todas las ligas)",
  gameMode: "ARAM Mayhem",
  stylesColumns: ["style", "games", "winRate", "itemShare"],
  patch,
  dataVersion: dataVer,
  fetchedAt: new Date().toISOString(),
  champions: builds,
}));
fs.renameSync(buildsTarget + ".tmp", buildsTarget);
const rows = Object.values(out).reduce((n, c) => n + c.augments.length, 0);
const buildRows = Object.values(builds).reduce((n, c) => n + Object.keys(c.augments).length, 0);
console.log(`aramkit: ${count} campeones${failed ? ` (${failed} fallaron)` : ""} | ${rows} filas campeón+aumento | parche ${patch} | ${FILE}`);
console.log(`aramkit: ${buildRows} builds campeón+aumento | ${BUILDS_FILE}`);
