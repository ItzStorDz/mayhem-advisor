// Dibuja el panel del juego (overlay.html) con una partida de ejemplo y lo captura: normal (con la pista de cómo moverlo
// las primeras partidas), compacto (botón ⊟) y otra vez normal, en español e inglés.
// Uso: overlay-electron\node_modules\electron\dist\electron.exe <esta carpeta> <overlay-electron> <salida>
const { app, BrowserWindow } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const [overlayDir, out] = process.argv.slice(-2);
fs.mkdirSync(out, { recursive: true });
// Perfil aparte (localStorage del panel: posición, tamaño, compacto, pista): nunca el de la app de verdad.
app.setPath('userData', path.join(out, 'perfil'));

// Los iconos de CommunityDragon (los mismos que usa la app; el overlay solo deja cargar imágenes de ahí).
const ICONS = { 1043: '1043_base_t2_recurvebow', 2510: '2510_apfightersheen', 3006: '3006_class_t2_berserkersgreaves',
  3115: '3115_mage_t3_nashorstooth', 3124: '3124_marksman_t3_guinsoosrageblade', 3153: '3153_fighter_t3_bladeoftheruinedking', 3302: '3302_terminus' };
const icon = id => `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/items/icons2d/${ICONS[id]}.png`;
const item = (id, name, extra = {}) => ({ id, name, icon: icon(id), badges: ['Daño'], reason: '', inProgress: false, ...extra });
const game = lang => {
  const es = lang === 'es';
  return {
    championName: 'Kayle', champion: 'Kayle', enemies: es ? 'Mucho daño mágico (62 %)' : 'Lots of magic damage (62%)',
    enemyFact: es ? '3 campeones AP enfrente' : '3 AP champions on the enemy team', gamePhase: es ? 'Inicio de partida' : 'Early game',
    dead: false, respawn: 0,
    next: { id: 3124, name: es ? 'Espadafuria de Guinsoo' : "Guinsoo's Rageblade", icon: icon(3124), missing: 1550, progress: 0.4, badges: ['Daño'] },
    boots: item(3006, es ? 'Grebas del Berserker' : "Berserker's Greaves", { reason: es ? 'opción estándar' : 'standard pick' }),
    swap: null,
    items: [item(3124, es ? 'Espadafuria de Guinsoo' : "Guinsoo's Rageblade", { reason: es ? 'build habitual: lo compra el 61 % con Kayle en Mayhem' : 'standard build: 61% of Kayle players buy it in Mayhem' })],
    nextOptions: [
      { id: 3124, name: es ? 'Espadafuria de Guinsoo' : "Guinsoo's Rageblade", icon: icon(3124), badges: ['Daño'],
        reason: es ? 'build habitual: lo compra el 61 % con Kayle en Mayhem' : 'standard build: 61% of Kayle players buy it in Mayhem',
        missing: 1550, part: { name: es ? 'Arco Recurvo' : 'Recurve Bow', icon: icon(1043), cost: 700 }, progress: 0.4,
        // Sus piezas (1.8.4): una ya comprada.
        recipe: [{ name: es ? 'Arco Recurvo' : 'Recurve Bow', icon: icon(1043), cost: 700, owned: true }, { name: 'Bastón', icon: icon(3115), cost: 850, owned: false },
                 { name: es ? 'Arco Recurvo' : 'Recurve Bow', icon: icon(1043), cost: 700, owned: false }] },
      { id: 3006, name: es ? 'Grebas del Berserker' : "Berserker's Greaves", icon: icon(3006), badges: ['Movilidad'], reason: '', missing: 0, part: null, progress: 1 },
    ],
    building: [], kept: [], bestAugments: null, direction: null, inventoryFull: false,
    // 1.8.4: inventario lleno por una poción, la Lágrima pronto y oro de sobra (en la partida no salen los tres a la vez).
    makeRoom: { name: es ? 'Poción de Vida' : 'Health Potion', icon: icon(3006) },
    earlyPart: { name: es ? 'Lágrima de la Diosa' : 'Tear of the Goddess', icon: icon(3115), cost: 400, forName: 'Manamune' },
    elixir: { name: es ? 'Elixir de Furia' : 'Elixir of Wrath', icon: icon(3124), cost: 500 },
    statBonus: { name: es ? 'Estadística adicional' : 'Stat Bonus', icon: icon(3153), cost: 750 },
    // «Adaptar» con «cambia X por Y» y una categoría defensiva + una para aprovechar al rival.
    plan: { magicShare: 0.68, enemies: [{ name: 'Lux', magic: 0.95 }, { name: 'Garen', magic: 0.1 }],
      recommended: [3006, 3124, 3115, 3302, 2510, 3153].map(id => ({ name: String(id), icon: icon(id), owned: false })),
      situational: [{ name: es ? 'Fauces de Malmortius' : 'Maw of Malmortius', icon: icon(3302), owned: false, changed: true, reason: es ? 'cubre resistencia mágica' : 'covers magic resist',
        replaces: { name: es ? 'Filo de la Noche' : 'Edge of Night', icon: icon(2510) } }],
      categories: [{ title: es ? 'RESISTENCIA MÁGICA' : 'MAGIC RESIST', why: '68 %', offensive: false, items: [3302, 3153].map(id => ({ name: String(id), icon: icon(id), winRate: 0.52, games: 900 })) },
                   { title: es ? 'DAÑO POR % DE VIDA' : '% HEALTH DAMAGE', why: '2 tanques', offensive: true, items: [3153].map(id => ({ name: String(id), icon: icon(id), winRate: 0.53, games: 1200 })) }],
      discards: [] },
    styles: [
      { key: 'crit', name: es ? 'crítico' : 'crit', share: 0.61, winRate: 0.524, active: true, locked: false, items: [3124, 3115, 3302].map(id => ({ name: String(id), icon: icon(id) })) },
      { key: 'on_hit', name: 'on-hit', share: 0.32, winRate: 0.511, active: false, locked: false, items: [] },
    ],
    styleSource: es ? 'lo habitual con Kayle' : 'the usual Kayle build',
    fullBuild: [3006, 3124, 3115, 3302, 2510, 3153].map(id => ({ item: item(id, String(id)), owned: false })),
    augments: [], cards: [], skills: { order: 'QEW', winRate: 0.51, games: 900000, share: 0.62 },
    // 2+ aumentos de ataques y on-hit sin fijar: el aviso de «puedes fijar on-hit».
    onHitHint: { names: es ? ['Espada de Fuego', '¡¡¡ABEJAS!!!'] : ['Firebrand', 'BEES!!!'] },
  };
};
const live = lang => ({ type: 'live', phase: 'ingame', status: '', lang, panel: true, autoAugmentsTab: true,
  area: { x: 0, y: 0, w: 1280, h: 720 }, game: game(lang) });

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1280, height: 720, show: false, backgroundColor: '#20304a',
    webPreferences: { preload: path.join(overlayDir, 'preload.js'), contextIsolation: true } });
  await win.loadFile(path.join(overlayDir, 'overlay.html'));
  const shot = async name => {
    await new Promise(r => setTimeout(r, 1800));
    fs.writeFileSync(path.join(out, name + '.txt'), await win.webContents.executeJavaScript('document.getElementById("side").innerText'));
    fs.writeFileSync(path.join(out, name + '.png'), (await win.webContents.capturePage()).toPNG());
    const box = await win.webContents.executeJavaScript('JSON.stringify(document.getElementById("side").getBoundingClientRect())');
    console.log(name, box);
  };
  for (const lang of ['es', 'en']) {
    win.webContents.send('data', live(lang));
    await shot(`panel-${lang}`);
    // El botón ⊟ (como si lo pulsara el jugador): panel compacto.
    await win.webContents.executeJavaScript('doAction("compact")');
    win.webContents.send('data', live(lang));
    await shot(`compacto-${lang}`);
    await win.webContents.executeJavaScript('doAction("compact")'); // vuelve al normal; la pista ya no sale (lo usó)
    await new Promise(r => setTimeout(r, 450)); // el mismo botón dos veces seguidas se ignora (400 ms)
    win.webContents.send('data', live(lang));
    await shot(`normal-sin-pista-${lang}`);
    await win.webContents.executeJavaScript('doAction("tab", "adaptar")');
    await new Promise(r => setTimeout(r, 450));
    win.webContents.send('data', live(lang));
    await shot(`adaptar-${lang}`);
    await win.webContents.executeJavaScript('doAction("tab", "compra")');
    await win.webContents.executeJavaScript('localStorage.clear(); location.reload()');
    await new Promise(r => setTimeout(r, 1500));
  }
  win.destroy();
  app.quit();
});
