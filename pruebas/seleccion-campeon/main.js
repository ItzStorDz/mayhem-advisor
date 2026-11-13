// Dibuja champselect.html del overlay con datos de ejemplo (AD/AP de cada campeón y daño de los aliados) y lo captura.
// Uso: overlay-electron\node_modules\electron\dist\electron.exe <esta carpeta> <overlay-electron> <salida>
const { app, BrowserWindow } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const [overlayDir, out] = process.argv.slice(-2);
fs.mkdirSync(out, { recursive: true });

const icon = id => `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/${id}.png`;
const option = (id, alias, name, damage, tier, wr, games, extra = {}) => ({ championId: id, alias, name, icon: icon(id), winRate: wr, games,
  isCurrent: false, tier, mayhemWinRate: wr, mayhemGames: games, damage, spells: null, ...extra });
const live = lang => ({
  type: 'live', phase: 'champselect', status: '', lang,
  champSelect: [
    option(75, 'Nasus', 'Nasus', 'physical', 'A', 0.521, 41000, { isCurrent: true }),
    option(99, 'Lux', 'Lux', 'magic', 'S', 0.548, 52000),
    option(147, 'Seraphine', 'Seraphine', 'magic', 'A', 0.531, 38000),
    option(10, 'Kayle', 'Kayle', 'mixed', 'B', 0.509, 21000),
    option(86, 'Garen', 'Garen', 'physical', 'B', 0.503, 33000),
  ],
  champSelectInfo: { trades: [], fullscreen: false, itemSet: 'Nasus', notice: null, client: null, team: { physical: 3, magic: 0, mixed: 1 } },
  panel: true,
});

// CS_MUCHOS=1: banco lleno en una ventana baja, para comprobar que la lista se puede desplazar.
const many = process.env.CS_MUCHOS === '1';
const extra = [[1, 'Annie'], [22, 'Ashe'], [51, 'Caitlyn'], [122, 'Darius'], [81, 'Ezreal'], [3, 'Galio'], [40, 'Janna'], [24, 'Jax'], [222, 'Jinx'], [89, 'Leona'], [54, 'Malphite']];

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: many ? 330 : 420, height: many ? 480 : 760, show: false, transparent: false, backgroundColor: '#1b1f27',
    webPreferences: { preload: path.join(overlayDir, 'preload.js'), contextIsolation: true } });
  await win.loadFile(path.join(overlayDir, 'champselect.html'));
  // Otros idiomas: PANEL_LANGS=es,en,pt,fr,de,vi (1.8.5). Lo que calcula el programa llega en inglés en pt/fr/de/vi.
  for (const lang of (process.env.PANEL_LANGS || 'es,en').split(',')) {
    const data = live(lang);
    if (many) data.champSelect.push(...extra.map(([id, name], i) => option(id, name, name, i % 2 ? 'magic' : 'physical', 'B', 0.5 - i / 200, 20000)));
    win.webContents.send('data', data);
    await new Promise(r => setTimeout(r, 2500));
    const text = await win.webContents.executeJavaScript('document.body.innerText');
    fs.writeFileSync(path.join(out, `champselect-${lang}.txt`), text);
    fs.writeFileSync(path.join(out, `champselect-${lang}.png`), (await win.webContents.capturePage()).toPNG());
    if (many) console.log(lang, await win.webContents.executeJavaScript(`(() => { const l = document.querySelector('.cs-list'), s = document.getElementById('side');
      l.scrollTop = 99999; const r = l.lastElementChild.getBoundingClientRect();
      return 'lista ' + l.clientHeight + ' de ' + l.scrollHeight + ' px · desplazada ' + l.scrollTop + ' · última fila visible: ' + (r.bottom <= innerHeight + 1) + ' · panel ' + s.offsetHeight + ' de ' + innerHeight; })()`));
    console.log(lang, 'ok');
  }
  win.destroy();
  app.quit();
});
