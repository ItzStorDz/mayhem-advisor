#!/usr/bin/env node
/**
 * Actualiza TODO lo de Mayhem que lee la app, desde mayhemmeta.com (parche actual):
 *
 *   node herramientas/actualizar-mayhem.mjs          # campeones + builds + aumentos (3-4 min)
 *   node herramientas/actualizar-mayhem.mjs --stats  # solo tier list (5 seg)
 *
 * Escribe en la carpeta datos\ del proyecto (se copia junto al .exe al compilar: "Iniciar overlay.bat" lo hace solo):
 *   mayhem-champions.json        -> MayhemChampions.cs   (tier, win rate, pick rate, partidas)
 *   mayhem-champion-builds.json  -> MayhemBuilds.cs      (objetos, botas, hechizos, aumentos)
 *   mayhem-augments.json         -> MayhemAugments.cs    (descripción de cada aumento + su win rate con cada campeón)
 *
 * Solo ARAM Mayhem (cola 2400), sin nada de Arena. Los alias de campeón salen de
 * CommunityDragon (los mismos que usa la app).
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { championSummary } from "./campeones.mjs";

// Los datos viven en la carpeta "datos" del repo: se compilan junto al .exe y funcionan offline.
const DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "datos");
const STATS_ONLY = process.argv.includes("--stats");
const headers = { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0" };

/** Descarga con un reintento; un error del servidor (429, 500...) falla en vez de devolver una página vacía. */
async function get(url, as = "text") {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await fetch(url, { headers });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return await res[as]();
    } catch (e) {
      if (attempt >= 2) throw new Error(`${url}: ${e.message}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
  }
}

/** Escribe a un archivo temporal y lo renombra: la app nunca ve un JSON a medio escribir. */
function save(file, data, indent) {
  const target = path.join(DIR, file);
  fs.writeFileSync(target + ".tmp", JSON.stringify(data, null, indent));
  fs.renameSync(target + ".tmp", target);
  return target;
}

/** Lo que había antes en un archivo (para no perderlo si algo falla), o null. */
function previous(file) {
  try {
    return JSON.parse(fs.readFileSync(path.join(DIR, file), "utf8"));
  } catch {
    return null;
  }
}

const decode = (s) =>
  s
    .replace(/<!--[\s\S]*?-->/g, "")
    .replace(/<[^>]+>/g, " ")
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(Number(d)))
    .replace(/&amp;/g, "&")
    .replace(/&quot;/g, '"')
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/\s+/g, " ")
    .trim();

const pct = (s) => {
  const v = Number(decode(s).replace(",", ".").replace("%", ""));
  return Number.isFinite(v) ? v : null;
};

const deltaOf = (row) => {
  const d = row.match(/\(<!-- -->([+-])<!-- -->([\d.]+)/);
  return d ? Number((d[1] === "-" ? -1 : 1) * Number(d[2]).toFixed(2)) : null;
};

/** Filas de una tabla de objetos/aumentos: Item | Pick Rate | Win Rate (+delta) */
function parseTable(sectionHtml) {
  const out = [];
  for (const m of sectionHtml.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/g)) {
    const row = m[1];
    if (!row.includes("%")) continue;
    const icon = row.match(/\/(?:icons\/)?(items|augments|spells|champions)\/(\d+)\.png/);
    const named = row.match(/title="([^"]+)"/);
    const name = named ? decode(named[1]) : (row.match(/<span[^>]*>([^<]+)<\/span>/) ?? [])[1];
    if (!name || !icon) continue;
    const values = [...row.matchAll(/font-mono[^>]*>\s*([\d.,]+)\s*<!-- -->%/g)].map((v) => pct(v[1]));
    if (values.length === 0) continue;
    out.push({
      kind: icon[1],
      id: Number(icon[2]),
      name: decode(name),
      pickRate: values[0] ?? null,
      winRate: values[1] ?? null,
      delta: deltaOf(row),
    });
  }
  return out;
}

/** Hechizos: "Flash + Mark" | Pick Rate | Win Rate */
function parseSpells(sectionHtml) {
  const out = [];
  for (const m of sectionHtml.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/g)) {
    const row = m[1];
    if (!row.includes("%")) continue;
    const names = [...row.matchAll(/alt="([^"]+)" loading="lazy"/g)].map((x) => decode(x[1]));
    const label = row.match(/<span class="truncate">([\s\S]*?)<\/span>/);
    if (!label) continue;
    const values = [...row.matchAll(/font-mono[^>]*>\s*([\d.,]+)\s*<!-- -->%/g)].map((v) => pct(v[1]));
    if (values.length === 0) continue;
    out.push({
      name: decode(label[1]),
      spells: names,
      pickRate: values[0] ?? null,
      winRate: values[1] ?? null,
      delta: deltaOf(row),
    });
  }
  return out;
}

function parseSections(html) {
  const parts = html.split(/<h2[^>]*>([\s\S]*?)<\/h2>/);
  const sections = {};
  for (let i = 1; i < parts.length; i += 2) {
    const body = parts[i + 1] ?? "";
    const end = body.indexOf("</table>");
    sections[decode(parts[i])] = parseTable(end === -1 ? body : body.slice(0, end));
  }
  return sections;
}

function rawSection(html, title) {
  const parts = html.split(/<h2[^>]*>([\s\S]*?)<\/h2>/);
  for (let i = 1; i < parts.length; i += 2) if (decode(parts[i]) === title) return parts[i + 1] ?? "";
  return "";
}

// ---------- 1. Tier list ----------
const home = await get("https://mayhemmeta.com/");
const patch = (home.match(/<option value="([\d.]+)" selected="">Patch/) ?? [])[1] ?? "?";

const list = [];
for (const m of home.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/g)) {
  const row = m[1];
  const cells = [...row.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/g)].map((c) => c[1]);
  if (cells.length < 9) continue;
  const href = row.match(/href="\/champions\/([^"]+)"/);
  const name = row.match(/href="\/champions\/[^"]+"[\s\S]*?<span[^>]*>([^<]+)<\/span>/);
  const tier = decode(cells[2]);
  if (!href || !name || !/^(OP|S|A|B|C|D|F)$/.test(tier)) continue;
  list.push({
    slug: href[1],
    name: decode(name[1]),
    tier,
    winRate: pct(cells[3]),
    pickRate: pct(cells[4]),
    kills: pct(cells[5]),
    deaths: pct(cells[6]),
    assists: pct(cells[7]),
    games: Number(decode(cells[8]).replace(/,/g, "")) || null,
  });
}
// Si la web cambió de diseño y no se lee casi nada, mejor no tocar los datos que ya tenías.
if (list.length < 100) {
  console.error(`Solo se leyeron ${list.length} campeones de mayhemmeta.com (¿cambió la web?). No se guarda nada.`);
  process.exit(1);
}

// ---------- 2. Alias de CommunityDragon (los que usa la app) ----------
// { name, key, id } sin "Ninguno" ni las copias de modos especiales ("Jade_Aatrox", id 60266).
const cd = await championSummary();
const meta = cd.filter((m) => m.id > 0 && !m.alias.includes("_")).map((m) => ({ name: m.name, key: m.alias, id: m.id }));
const aliasById = new Map(meta.map((m) => [m.id, m.key]));
const norm = (s) => s.toLowerCase().replace(/[^a-z0-9]/g, "");
const byNorm = new Map();
for (const m of meta) {
  byNorm.set(norm(m.name), m);
  byNorm.set(norm(m.key), m);
}
const OVERRIDES = { wukong: "MonkeyKing", nunuandwillump: "Nunu", nunuwillump: "Nunu" };
for (const c of list) {
  const hit = byNorm.get(norm(c.name));
  c.alias = hit?.key ?? OVERRIDES[norm(c.name)] ?? null;
  c.championId = hit?.id ?? 0;
}

fs.mkdirSync(DIR, { recursive: true });
const statsOut = {
  schema: "mayhem-advisor.mayhem-champions/1",
  patch,
  generatedAt: new Date().toISOString().slice(0, 10),
  championCount: list.length,
  champions: list
    .map((c) => ({
      alias: c.alias,
      championId: c.championId,
      name: c.name,
      tier: c.tier,
      winRate: c.winRate,
      pickRate: c.pickRate,
      games: c.games,
      kills: c.kills,
      deaths: c.deaths,
      assists: c.assists,
    }))
    .sort((a, b) => (b.winRate ?? 0) - (a.winRate ?? 0)),
};
save("mayhem-champions.json", statsOut, 1);
console.log(`tier list: patch ${patch} | ${list.length} campeones | mayhem-champions.json`);

if (STATS_ONLY) process.exit(0);

// ---------- 3. Builds por campeon ----------
const champions = [];
const previousBuilds = new Map((previous("mayhem-champion-builds.json")?.champions ?? []).map((c) => [c.slug, c]));
let done = 0;
const queue = [...list];

async function worker() {
  while (queue.length) {
    const c = queue.shift();
    try {
      const html = await get(`https://mayhemmeta.com/champions/${c.slug}`);
      const s = parseSections(html);
      const wr = html.match(/([\d.]+)% win rate over/i);
      const games = html.match(/over ([\d,]+) games/i);
      champions.push({
        alias: c.alias,
        championId: c.championId,
        name: c.name,
        slug: c.slug,
        tier: c.tier,
        winRate: wr ? pct(wr[1]) : c.winRate,
        games: games ? Number(games[1].replace(/,/g, "")) : c.games,
        spells: parseSpells(rawSection(html, "Summoner Spells")),
        boots: s["Boots"] ?? [],
        itemsCommon: s["Most Common Items"] ?? [],
        itemsBest: s["Best Items"] ?? [],
        augmentsBest: s["Best Augments"] ?? [],
        augmentsPopular: s["Popular Augments"] ?? [],
      });
    } catch (e) {
      // Si falla un campeón, nos quedamos con su build de la última vez (si la había).
      const old = previousBuilds.get(c.slug);
      if (old) champions.push(old);
      console.error(`  ! ${c.slug}: ${e.message}${old ? " (se mantiene la build anterior)" : ""}`);
    }
    if (++done % 40 === 0) console.error(`  ${done}/${list.length}`);
  }
}
await Promise.all(Array.from({ length: 6 }, () => worker()));

champions.sort((a, b) => (b.winRate ?? 0) - (a.winRate ?? 0));
const buildsOut = {
  schema: "mayhem-advisor.mayhem-builds/1",
  patch,
  generatedAt: new Date().toISOString().slice(0, 10),
  championCount: champions.length,
  champions,
};
if (champions.length < list.length / 2) {
  console.error(`Solo ${champions.length}/${list.length} builds (¿la web bloqueó las descargas?). No se guarda nada.`);
  process.exit(1);
}
save("mayhem-champion-builds.json", buildsOut);
console.log(
  `builds: ${champions.length} campeones | mayhem-champion-builds.json (${(fs.statSync(path.join(DIR, "mayhem-champion-builds.json")).size / 1024).toFixed(0)} KB)`,
);
const h = champions.find((x) => x.slug === "hecarim");
if (h) console.log(`  Hecarim: ${h.winRate}% | core: ${h.itemsBest.slice(0, 3).map((i) => i.name).join(" + ")} | augment top: ${h.augmentsBest[0]?.name}`);

// ---------- 4. Cada aumento: descripción + win rate con cada campeón ----------
// El juego deja sin texto unos 80 aumentos (o con el texto en claves cifradas); sin él, la app no sabe
// con qué objetos van. mayhemmeta.com enseña la descripción en la página de cada aumento, y en los datos
// de la página viene la tabla "All Champions" completa (~170 campeones, no solo los 15 que se ven).

/** Texto de los datos internos de la página (Next.js los manda en trozos self.__next_f.push). */
function pageData(html) {
  return [...html.matchAll(/self\.__next_f\.push\(\[1,"([\s\S]*?)"\]\)<\/script>/g)]
    .map((m) => {
      try {
        return JSON.parse(`"${m[1]}"`);
      } catch {
        return "";
      }
    })
    .join("");
}

/** La lista "rows" más larga con filas campeón+aumento (games, win_rate, baseline_win_rate). */
function championRows(data) {
  let best = [];
  for (let at = data.indexOf('"rows":['); at >= 0; at = data.indexOf('"rows":[', at + 1)) {
    let depth = 0, end = -1;
    for (let i = at + 7; i < data.length; i++) {
      if (data[i] === "[") depth++;
      else if (data[i] === "]" && --depth === 0) { end = i; break; }
    }
    if (end < 0) continue;
    try {
      const rows = JSON.parse(data.slice(at + 7, end + 1));
      if (rows.length > best.length && rows[0]?.baseline_win_rate !== undefined) best = rows;
    } catch {}
  }
  return best;
}
/**
 * Tablas "Best Co-picked Augments" (100+ partidas) y "Most Co-picked Augments" de la página de un aumento:
 * filas [id del otro aumento, pick rate %, % victorias juntos, sinergia]. La sinergia (+/-) ya descuenta
 * lo fuerte que es cada aumento por separado, así que un aumento muy bueno no parece "sinergia" con todo.
 */
function coPickedRows(data) {
  const rows = new Map();
  const parts = data.split('["$","tr","').slice(1);
  for (const part of parts) {
    const id = part.match(/"href":"\/augments\/(\d+)"/);
    const pcts = [...part.matchAll(/"children":\["(-?[\d.]+)","%"\]/g)].map((m) => Number(m[1]));
    // Positivas: ["(","+","4.2",")"]; negativas: ["(","","-0.67",")"].
    const delta = part.match(/\["\(","([+-]?)","(-?[\d.]+)","\)"\]/);
    if (!id || pcts.length < 2) continue;
    const row = [Number(id[1]), pcts[0], pcts[1], delta ? Number(delta[1] + delta[2]) : null];
    if (!rows.has(row[0]) && row.slice(1, 3).every(Number.isFinite)) rows.set(row[0], row);
  }
  return [...rows.values()];
}

const sitemap = await get("https://mayhemmeta.com/sitemap.xml");
const augmentIds = [...new Set([...sitemap.matchAll(/mayhemmeta\.com\/augments\/(\d+)</g)].map((m) => Number(m[1])))];
const augments = [];
const augQueue = [...augmentIds];
async function augmentWorker() {
  while (augQueue.length) {
    const id = augQueue.shift();
    try {
      const html = await get(`https://mayhemmeta.com/augments/${id}`);
      const name = html.match(/<h1[^>]*>([\s\S]*?)<\/h1>/);
      const desc = html.match(/<p class="mt-4 max-w-3xl whitespace-pre-line[^"]*"[^>]*>([\s\S]*?)<\/p>/);
      const data = pageData(html);
      // Filas compactas: [alias, partidas, % victorias con el aumento, pick rate, % victorias del campeón sin más].
      const rows = championRows(data)
        .map((r) => [aliasById.get(r.id) ?? null, r.games, Number(r.win_rate), Number(r.pick_rate), Number(r.baseline_win_rate)])
        .filter((r) => r[0] && r[1] > 0 && Number.isFinite(r[2]) && Number.isFinite(r[4]));
      const games = Number((html.match(/across ([\d,]+) games/)?.[1] ?? "0").replaceAll(",", ""));
      // Combinaciones: [otro aumento, partidas juntos (aprox., del pick rate), % victorias juntos, sinergia].
      const coPicked = coPickedRows(data)
        .filter((r) => r[3] !== null)
        .map(([other, pick, wr, synergy]) => [other, Math.round((games * pick) / 100), wr, synergy]);
      if (desc || rows.length)
        augments.push({ id, name: name ? decode(name[1]) : "", description: desc ? decode(desc[1]) : "", games, champions: rows, coPicked });
    } catch (e) {
      console.error(`  ! aumento ${id}: ${e.message}`);
    }
  }
}
await Promise.all(Array.from({ length: 6 }, () => augmentWorker()));
if (augments.length < augmentIds.length / 2) {
  console.error(`Solo ${augments.length}/${augmentIds.length} aumentos con descripción. No se guarda mayhem-augments.json.`);
} else {
  augments.sort((a, b) => a.id - b.id);
  save("mayhem-augments.json", {
    schema: "mayhem-advisor.mayhem-augments/2",
    source: "mayhemmeta.com",
    gameMode: "ARAM Mayhem (cola 2400)",
    championsColumns: ["alias", "games", "winRate", "pickRate", "championWinRate"],
    coPickedColumns: ["augmentId", "gamesTogether", "winRateTogether", "synergy"],
    patch,
    fetchedAt: new Date().toISOString(),
    augments,
  });
  const rowCount = augments.reduce((n, a) => n + a.champions.length, 0);
  const pairCount = augments.reduce((n, a) => n + a.coPicked.length, 0);
  console.log(`aumentos: ${augments.length} (${augments.filter((a) => a.description).length} con descripción) | ${rowCount} filas campeón+aumento | ${pairCount} combinaciones | mayhem-augments.json`);
}
