#!/usr/bin/env node
/**
 * Actualiza los datos de la app y los publica en GitHub para que la app se los baje sola (sin sacar una versión):
 * la Release «datos» del repositorio, con los 6 archivos de datos\ y manifest-v1.json (fecha, tamaño y SHA-256 de cada
 * uno). La app (src\Core\DataUpdater.cs) mira ese manifiesto al abrirse y cada pocas horas.
 *
 *   node herramientas/publicar-datos.mjs               # baja lo que toque de cada fuente y publica lo que cambió
 *   node herramientas/publicar-datos.mjs --sin-bajar   # publica lo que hay ahora en datos\ (para sembrar la Release)
 *   node herramientas/publicar-datos.mjs --no-publicar # solo baja y comprueba (no toca GitHub)
 *   node herramientas/publicar-datos.mjs --probar      # solo mira si cada fuente responde desde aquí (una página de cada)
 *
 * Lo ejecuta GitHub una vez al día (.github/workflows/datos.yml). Cada fuente a su ritmo, para no cargarlas:
 *   · AramHex: cada vez (2 archivos de su GitHub; ellos piden usar el repositorio).
 *   · mayhemmeta: cada 2 días (~400 páginas).
 *   · aramkit: solo si publicaron datos nuevos y hace 6+ días de la última vez (sus condiciones piden descargas
 *     poco frecuentes: ver herramientas/actualizar-aramkit.mjs).
 * Si una fuente falla o lo que devuelve se ve raro (pocos campeones, archivo mucho más pequeño), se queda lo anterior.
 * Usa «gh» (GitHub CLI): en tu PC, con la variable GH si no está en el PATH.
 */
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DIR = path.join(HERE, "..", "datos");
const REPO = process.env.GITHUB_REPOSITORY || "ItzStorDz/mayhem-advisor";
const TAG = "datos";
const MANIFEST = "manifest-v1.json";
const GH = process.env.GH || "gh";
const NO_FETCH = process.argv.includes("--sin-bajar");
const NO_PUBLISH = process.argv.includes("--no-publicar");
const HOUR = 3600_000;

/** Cada archivo: de qué fuente sale y cómo se cuentan sus campeones (o aumentos) para comprobarlo. */
const FILES = {
  "aramhex-augments.json": { source: "aramhex", count: (j) => Object.keys(j.champions ?? {}).length },
  "aramkit-augments.json": { source: "aramkit", count: (j) => Object.keys(j.champions ?? {}).length },
  "aramkit-builds.json": { source: "aramkit", count: (j) => Object.keys(j.champions ?? {}).length },
  "mayhem-champions.json": { source: "mayhem", count: (j) => (j.champions ?? []).length },
  "mayhem-champion-builds.json": { source: "mayhem", count: (j) => (j.champions ?? []).length },
  "mayhem-augments.json": { source: "mayhem", count: (j) => (j.augments ?? []).length },
};
const MIN_COUNT = 150;
const MIN_SIZE_RATIO = 0.6;

// --probar: ¿responden las fuentes desde esta máquina? (los servidores de GitHub podrían estar bloqueados por alguna)
if (process.argv.includes("--probar")) {
  const ua = { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0 (Mayhem Advisor, uso personal)" };
  let bad = 0;
  for (const [name, url] of [
    ["AramHex", "https://raw.githubusercontent.com/czgreat/aramhex-data/main/latest.json"],
    ["mayhemmeta (portada)", "https://mayhemmeta.com/"],
    ["mayhemmeta (campeón)", "https://mayhemmeta.com/champions/amumu"],
    ["aramkit (web)", "https://aramkit.com/en-US/champions"],
  ]) {
    try {
      const res = await fetch(url, { headers: ua });
      const text = await res.text();
      console.log(`${res.ok ? "ok " : "MAL"} ${name}: HTTP ${res.status}, ${text.length} caracteres`);
      if (!res.ok) bad++;
      if (name.startsWith("aramkit") && res.ok) {
        const version = text.replace(/\\u002F/gi, "/").match(/data\/(\d+\.\d+-\d{8}-[0-9a-f]+)/)?.[1];
        const data = version ? await fetch(`https://data.aramkit.com/data/${version}/stats/all/champion-details/1.json`, { headers: ua }) : null;
        console.log(`${data?.ok ? "ok " : "MAL"} aramkit (datos ${version ?? "sin versión"}): HTTP ${data?.status ?? "-"}`);
        if (!data?.ok) bad++;
      }
    } catch (e) {
      console.log(`MAL ${name}: ${e.message}`);
      bad++;
    }
  }
  process.exit(bad ? 1 : 0);
}

const gh = (args, opts = {}) => execFileSync(GH, args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], ...opts });
const sha256 = (file) => crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const readJson = (file) => JSON.parse(fs.readFileSync(file, "utf8"));

/** ¿Dicen lo mismo dos versiones de un archivo, sin contar cuándo se bajaron? */
function sameData(a, b) {
  const strip = (buf) => {
    const j = JSON.parse(buf.toString("utf8"));
    delete j.fetchedAt;
    delete j.generatedAt;
    return JSON.stringify(j);
  };
  try {
    return strip(a) === strip(b);
  } catch {
    return false;
  }
}

/** ¿Se puede publicar este archivo? Devuelve por qué no, o null si está bien. */
function problem(name, file, previousSize) {
  let json;
  try {
    json = readJson(file);
  } catch (e) {
    return `no es JSON válido (${e.message})`;
  }
  const n = FILES[name].count(json);
  if (n < MIN_COUNT) return `solo trae ${n} (mínimo ${MIN_COUNT})`;
  const size = fs.statSync(file).size;
  if (previousSize && size < previousSize * MIN_SIZE_RATIO) return `pesa ${size} bytes y el anterior ${previousSize}`;
  return null;
}

// 1. Lo publicado ahora mismo (para comparar y para no perder lo que una fuente no dé hoy).
fs.mkdirSync(DIR, { recursive: true });
let released = true;
let previous = { files: {}, sources: {} };
if (!NO_PUBLISH || !NO_FETCH) {
  try {
    gh(["release", "view", TAG, "--repo", REPO, "--json", "tagName"]);
  } catch {
    released = false;
  }
  if (released) {
    const tmp = fs.mkdtempSync(path.join(DIR, ".publicado-"));
    try {
      gh(["release", "download", TAG, "--repo", REPO, "--dir", tmp, "--clobber"]);
      if (fs.existsSync(path.join(tmp, MANIFEST))) previous = readJson(path.join(tmp, MANIFEST));
      // En GitHub (carpeta recién clonada) faltan los archivos que no van en el repositorio: se parte de los publicados.
      for (const name of Object.keys(FILES))
        if (!NO_FETCH && fs.existsSync(path.join(tmp, name))) fs.copyFileSync(path.join(tmp, name), path.join(DIR, name));
    } finally {
      fs.rmSync(tmp, { recursive: true, force: true });
    }
  }
}
previous.files ??= {};
previous.sources ??= {};
const now = new Date();
const since = (iso) => (iso ? (now - new Date(iso)) / HOUR : Infinity);

// 2. Qué fuentes tocan hoy.
const sources = { ...previous.sources };
const due = { aramhex: !NO_FETCH, mayhem: !NO_FETCH && since(sources.mayhem?.fetchedAt) >= 44, aramkit: false };
let aramkitVersion = sources.aramkit?.dataVersion ?? null;
if (!NO_FETCH && since(sources.aramkit?.fetchedAt) >= 6 * 24) {
  try {
    const page = (await (await fetch("https://aramkit.com/en-US/champions", {
      headers: { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/130.0 (Mayhem Advisor, uso personal)" },
    })).text()).replace(/\\u002F/gi, "/");
    const version = page.match(/data\/(\d+\.\d+-\d{8}-[0-9a-f]+)/)?.[1];
    if (version && version !== aramkitVersion) {
      due.aramkit = true;
      aramkitVersion = version;
    } else console.log(`aramkit: sin datos nuevos (${version ?? "no encuentro la versión"})`);
  } catch (e) {
    console.log(`aramkit: no responde (${e.message})`);
  }
} else if (!NO_FETCH) console.log(`aramkit: hace ${Math.round(since(sources.aramkit?.fetchedAt) / 24)} días de la última vez (se espera a 6)`);

// 3. Bajar cada fuente; si falla o lo que deja se ve raro, vuelve lo anterior.
for (const source of ["aramhex", "mayhem", "aramkit"]) {
  if (!due[source]) continue;
  const names = Object.keys(FILES).filter((n) => FILES[n].source === source);
  const backup = Object.fromEntries(names.filter((n) => fs.existsSync(path.join(DIR, n))).map((n) => [n, fs.readFileSync(path.join(DIR, n))]));
  console.log(`== ${source}`);
  const run = spawnSync(process.execPath, [path.join(HERE, `actualizar-${source}.mjs`)], { stdio: ["ignore", "inherit", "inherit"], timeout: 45 * 60_000 });
  let why = run.status === 0 ? null : `el script acabó con código ${run.status ?? run.signal}`;
  for (const n of names) why ??= fs.existsSync(path.join(DIR, n)) ? problem(n, path.join(DIR, n), backup[n]?.length) && `${n}: ${problem(n, path.join(DIR, n), backup[n]?.length)}` : `${n} no existe`;
  if (why) {
    console.log(`!! ${source}: ${why}. Se queda lo anterior.`);
    for (const n of names) if (backup[n]) fs.writeFileSync(path.join(DIR, n), backup[n]);
    continue;
  }
  // Mismos datos que antes y solo cambia la hora de descarga: se deja el archivo anterior, para que nadie lo baje otra vez.
  for (const n of names) if (backup[n] && sameData(backup[n], fs.readFileSync(path.join(DIR, n)))) fs.writeFileSync(path.join(DIR, n), backup[n]);
  sources[source] = { fetchedAt: now.toISOString(), ...(source === "aramkit" ? { dataVersion: aramkitVersion } : {}) };
}

// Sembrando la Release desde un PC: cuándo se bajó cada fuente lo dicen los propios archivos (para que GitHub no las
// vuelva a pedir enseguida).
if (NO_FETCH) {
  const stamp = (name, ...keys) => {
    try {
      const j = readJson(path.join(DIR, name));
      for (const k of keys) if (j[k]) return new Date(j[k]).toISOString();
    } catch { /* falta o está mal: se verá abajo */ }
    return null;
  };
  let kitVersion = null;
  try { kitVersion = readJson(path.join(DIR, "aramkit-builds.json")).dataVersion ?? null; } catch { /* igual */ }
  sources.aramhex ??= { fetchedAt: stamp("aramhex-augments.json", "fetchedAt") ?? now.toISOString() };
  sources.mayhem ??= { fetchedAt: stamp("mayhem-augments.json", "fetchedAt", "generatedAt") ?? now.toISOString() };
  sources.aramkit ??= { fetchedAt: stamp("aramkit-builds.json", "fetchedAt") ?? now.toISOString(), dataVersion: kitVersion };
}

// 4. El manifiesto: lo que cambió lleva la fecha de ahora; lo que no, la que tenía.
const files = {};
const changed = [];
for (const name of Object.keys(FILES)) {
  const file = path.join(DIR, name);
  if (!fs.existsSync(file)) {
    console.log(`!! falta ${name}: no se publica`);
    continue;
  }
  const why = problem(name, file, null);
  if (why) {
    console.log(`!! ${name}: ${why}. No se publica.`);
    if (previous.files[name]) files[name] = previous.files[name];
    continue;
  }
  const hash = sha256(file);
  const old = previous.files[name];
  if (old?.sha256 === hash) files[name] = old;
  else {
    const json = readJson(file);
    files[name] = { sha256: hash, size: fs.statSync(file).size, updatedAt: now.toISOString(), patch: json.patch ?? null };
    changed.push(name);
  }
}
const manifest = { schema: 1, generatedAt: now.toISOString(), files, sources };
console.log(changed.length ? `Cambian: ${changed.join(", ")}` : "Nada cambió.");
if (NO_PUBLISH) process.exit(0);
if (Object.keys(files).length < Object.keys(FILES).length) {
  console.error("Faltan archivos: no se publica nada.");
  process.exit(1);
}

// 5. Publicar: primero los archivos, al final el manifiesto (la app comprueba el SHA-256 de lo que baja).
const manifestFile = path.join(DIR, MANIFEST);
fs.writeFileSync(manifestFile, JSON.stringify(manifest, null, 1));
try {
  if (!released) {
    const notes = "Data files the app downloads by itself (champion, item and augment statistics for ARAM: Mayhem). Not an app version: "
      + "get the app from the latest release. Sources and licenses: datos/CREDITOS.txt (mayhemmeta.com, AramHex CC BY-NC 4.0, aramkit.com).";
    gh(["release", "create", TAG, "--repo", REPO, "--title", "Data (auto-updated)", "--notes", notes, "--latest=false"]);
  }
  const upload = [...(released ? changed : Object.keys(files)).map((n) => path.join(DIR, n))];
  if (upload.length || !released) gh(["release", "upload", TAG, ...upload, "--repo", REPO, "--clobber"]);
  if (changed.length || !released || !previous.files || JSON.stringify(previous.sources) !== JSON.stringify(sources))
    gh(["release", "upload", TAG, manifestFile, "--repo", REPO, "--clobber"]);
  console.log(`Publicado en la Release «${TAG}» de ${REPO}.`);
} finally {
  fs.rmSync(manifestFile, { force: true });
}
