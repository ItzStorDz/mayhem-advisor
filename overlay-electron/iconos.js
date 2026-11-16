const { protocol, net: webNet } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');

// Las imágenes del juego (campeones, objetos, aumentos) salen del PC: el programa en C# las guarda en MAYHEM_ICON_DIR y
// el instalador trae una copia en MAYHEM_ICON_SEED (ver src/Core/IconCache.cs; el nombre del archivo es el SHA-1 de la
// dirección, la misma regla que allí). Las páginas las piden a CommunityDragon como siempre y aquí se les da el archivo;
// si no está, se baja de la web y se guarda. Cuando esa web cayó (12-14 de noviembre de 2026) el panel salía sin imágenes.
const ICON_HOST = 'raw.communitydragon.org';
const iconDirs = [process.env.MAYHEM_ICON_DIR, process.env.MAYHEM_ICON_SEED].filter(Boolean);
const ICON_TYPES = { '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp', '.img': 'image/png' };

function iconFileName(url) {
  let key = url;
  try { key = decodeURIComponent(url); } catch { }
  key = key.toLowerCase();
  const ext = path.extname(key.split(/[?#]/)[0]);
  return crypto.createHash('sha1').update(key, 'utf8').digest('hex') + (['.png', '.jpg', '.jpeg', '.webp'].includes(ext) ? ext : '.img');
}

async function iconResponse(request) {
  const passThrough = () => webNet.fetch(request, { bypassCustomProtocolHandlers: true });
  let host = '';
  try { host = new URL(request.url).hostname; } catch { }
  if (host !== ICON_HOST || request.method !== 'GET') return passThrough();
  const name = iconFileName(request.url);
  for (const dir of iconDirs) {
    try {
      const data = await fs.promises.readFile(path.join(dir, name));
      if (data.length > 0) return new Response(data, { headers: { 'content-type': ICON_TYPES[path.extname(name)], 'cache-control': 'max-age=86400' } });
    } catch { /* no está en esa carpeta */ }
  }
  const response = await passThrough();
  if (response.ok && (response.headers.get('content-type') ?? '').startsWith('image/')) {
    const copy = response.clone();
    (async () => {
      const data = Buffer.from(await copy.arrayBuffer());
      if (data.length === 0 || data.length > 3_000_000) return;
      await fs.promises.mkdir(iconDirs[0], { recursive: true });
      const file = path.join(iconDirs[0], name), temp = `${file}.${process.pid}-${Date.now() % 100000}.tmp`;
      await fs.promises.writeFile(temp, data);
      await fs.promises.rename(temp, file).catch(() => fs.promises.unlink(temp).catch(() => { }));
    })().catch(() => { /* sin sitio o sin permiso: la próxima vez se pide otra vez */ });
  }
  return response;
}

/** Desde ahora las imágenes de CommunityDragon que pidan las páginas salen de las carpetas del PC (llamar con la app lista). */
function install() {
  if (iconDirs.length > 0) protocol.handle('https', iconResponse);
}

module.exports = { install, iconFileName };
