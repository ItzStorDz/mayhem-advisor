/**
 * La lista de campeones (id, alias y nombre) que usan los scripts de datos: de CommunityDragon y, si no responde, de la
 * copia guardada en herramientas/campeones.json. CommunityDragon no contesta a los servidores de GitHub (HTTP 522,
 * comprobado el 2026-11-12), que es donde se ejecutan a diario (.github/workflows/datos.yml). Cada vez que responde
 * (desde un PC), la copia se renueva: tras un parche con campeón nuevo, ejecuta un script de datos en tu PC y sube el
 * archivo.
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const FILE = path.join(path.dirname(fileURLToPath(import.meta.url)), "campeones.json");
const URL = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json";

/** Devuelve [{ id, alias, name }] como champion-summary.json de CommunityDragon. */
export async function championSummary() {
  try {
    const res = await fetch(URL, { headers: { "user-agent": "Mayhem Advisor (uso personal)" }, signal: AbortSignal.timeout(20_000) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const list = (await res.json()).filter((c) => c.id > 0).map((c) => ({ id: c.id, alias: c.alias, name: c.name }));
    if (list.length < 150) throw new Error(`solo ${list.length} campeones`);
    const text = JSON.stringify(list.sort((a, b) => a.id - b.id), null, 0).replace(/\},\{/g, "},\n{") + "\n";
    if (!fs.existsSync(FILE) || fs.readFileSync(FILE, "utf8") !== text) fs.writeFileSync(FILE, text);
    return list;
  } catch (e) {
    console.error(`  CommunityDragon no responde (${e.message}): uso la lista guardada de campeones`);
    return JSON.parse(fs.readFileSync(FILE, "utf8"));
  }
}
