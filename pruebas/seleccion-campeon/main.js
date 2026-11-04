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

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 420, height: 760, show: false, transparent: false, backgroundColor: '#1b1f27',
    webPreferences: { preload: path.join(overlayDir, 'preload.js'), contextIsolation: true } });
  await win.loadFile(path.join(overlayDir, 'champselect.html'));
  for (const lang of ['es', 'en']) {
    win.webContents.send('data', live(lang));
    await new Promise(r => setTimeout(r, 2500));
    const text = await win.webContents.executeJavaScript('document.body.innerText');
    fs.writeFileSync(path.join(out, `champselect-${lang}.txt`), text);
    fs.writeFileSync(path.join(out, `champselect-${lang}.png`), (await win.webContents.capturePage()).toPNG());
    console.log(lang, 'ok');
  }
  win.destroy();
  app.quit();
});
