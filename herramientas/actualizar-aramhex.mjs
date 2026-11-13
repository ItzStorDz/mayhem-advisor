#!/usr/bin/env node
/**
 * Descarga de AramHex el % de victorias de cada campeón con cada aumento en ARAM Mayhem, servidor CHINO oficial
 * (7 regiones, ~5 millones de partidas por parche):
 *
 *   node herramientas/actualizar-aramhex.mjs     # unos segundos (2 archivos de GitHub)
 *
 * Escribe datos\aramhex-augments.json -> ExternalAugments.cs (mismo formato que aramkit-augments.json). La app lo usa
 * como punto de partida de la nota de cada aumento, junto con mayhemmeta.com.
 *
 * Se usa la banda «casual» (jugadores de nivel normal, 90 de los ~200 millones de apariciones): se parece más a los
 * jugadores de mayhemmeta que el total. Medido el 2026-11-10 con 4.492 filas campeón + aumento de mayhemmeta (300+
 * partidas, validación cruzada): error 1,651 con todos, 1,633 con «casual», 1,85 con «mid», 2,14 con «high» (con las de
 * 800+: 1,544 → 1,512). aramkit no añade nada encima. Lo que la banda no tiene (o con menos de 100 partidas) sale del total.
 *
 * Datos: github.com/czgreat/aramhex-data, licencia CC BY-NC 4.0 (citar la fuente, sin uso comercial).
 * Piden descargar el repositorio en vez de leer su web página por página: por eso se usa GitHub.
 */
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { championSummary } from "./campeones.mjs";

const DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "datos");
const FILE = "aramhex-augments.json";
const RAW = "https://raw.githubusercontent.com/czgreat/aramhex-data/main";
const headers = { "user-agent": "Mayhem Advisor (uso personal)" };

async function get(url) {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, { headers });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return await res.text();
    } catch (e) {
      if (attempt >= 2) throw new Error(`${url}: ${e.message}`);
      await new Promise((r) => setTimeout(r, 3000));
    }
  }
}

/** Descarga un archivo de la versión y comprueba que llegó entero (SHA-256 de latest.json). */
async function file(latest, name) {
  const text = await get(`${RAW}/${latest.path}/${name}`);
  const want = latest.files?.[name]?.sha256;
  const got = crypto.createHash("sha256").update(text).digest("hex");
  if (want && want !== got) throw new Error(`${name} llegó incompleto (SHA-256 distinto)`);
  return JSON.parse(text);
}

const latest = JSON.parse(await get(`${RAW}/latest.json`));
const heroes = await file(latest, "heroes.json");
const heroAugments = await file(latest, "hero_augments.json");
// La banda de jugadores de nivel normal (si esta versión de los datos la trae).
const BAND = "bands/casual";
const band = latest.files?.[`${BAND}/hero_augments.json`]
  ? { heroes: await file(latest, `${BAND}/heroes.json`), augments: await file(latest, `${BAND}/hero_augments.json`) } : null;
const MIN_BAND_GAMES = 100;
// Los ids de campeón son los de Riot; el alias (el que usa la app) sale de CommunityDragon.
const summary = await championSummary();
const alias = Object.fromEntries(summary.filter((c) => c.id > 0).map((c) => [String(c.id), c.alias]));

const champions = {};
for (const [id, augs] of Object.entries(heroAugments)) {
  const h = heroes[id];
  if (!alias[id] || !h) {
    console.error(`  ! campeón ${id} sin alias o sin % general`);
    continue;
  }
  const bh = band?.heroes[id];
  champions[alias[id]] = {
    games: (bh ?? h).games,
    winRate: +((bh ?? h).win_rate * 100).toFixed(2),
    augments: Object.entries(augs).map(([aug, r]) => {
      const b = bh ? band.augments[id]?.[aug] : null;
      if (b && b.games >= MIN_BAND_GAMES) return [Number(aug), b.games, +(b.win_rate * 100).toFixed(2)];
      // Del total, puesto en la escala de la banda (lo que sube o baja el aumento, sobre el % del campeón en la banda).
      return [Number(aug), r.games, +((r.win_rate - h.win_rate + (bh ?? h).win_rate) * 100).toFixed(2)];
    }),
  };
}

const count = Object.keys(champions).length;
if (count < 100) {
  console.error(`Solo ${count} campeones. No se guarda ${FILE}.`);
  process.exit(1);
}
const target = path.join(DIR, FILE);
fs.writeFileSync(target + ".tmp", JSON.stringify({
  schema: "mayhem-advisor.aramhex-augments/1",
  source: "AramHex — github.com/czgreat/aramhex-data (CC BY-NC 4.0), servidor chino",
  band: band ? "casual" : "all",
  gameMode: "ARAM Mayhem (海克斯大乱斗)",
  augmentsColumns: ["augmentId", "games", "winRate"],
  patch: latest.patch,
  matches: latest.matches,
  snapshotAt: latest.snapshot_at,
  dataSeq: latest.seq,
  fetchedAt: new Date().toISOString(),
  champions,
}));
fs.renameSync(target + ".tmp", target);
const rows = Object.values(champions).reduce((n, c) => n + c.augments.length, 0);
console.log(`AramHex: ${count} campeones | ${rows} filas campeón+aumento | parche ${latest.patch} | ${latest.matches?.toLocaleString?.() ?? latest.matches} partidas | ${FILE}`);
