// Overlay de Mayhem Advisor en Electron: una ventana web transparente que
// electron-overlay-window pega encima de la ventana del juego (se mueve y cambia de tamaño con ella,
// aparece cuando el juego tiene el foco y deja pasar los clics). Los datos llegan del programa en C#
// por una tubería con nombre de Windows (--mayhem-pipe=nombre), un JSON por línea.
const { app, BrowserWindow, screen, ipcMain } = require('electron');
const { OverlayController, OVERLAY_WINDOW_OPTS } = require('electron-overlay-window');
const path = require('node:path');
const net = require('node:net');
const readline = require('node:readline');

const GAME_WINDOW_TITLE = 'League of Legends (TM) Client';

// Códigos de salida para el registro del programa en C# («Overlay Electron se cerró solo (código 0x…)»): un jugador
// (informe del 05-11) se quedaba sin este overlay tras 5 intentos y no se sabía por qué.
const EXIT_ALREADY_OPEN = 17, EXIT_NO_PIPE = 18, EXIT_CANT_CONNECT = 19;

if (!app.requestSingleInstanceLock()) app.exit(EXIT_ALREADY_OPEN); // ya hay otro overlay abierto

const webPreferences = {
  preload: path.join(__dirname, 'preload.js'),
  contextIsolation: true,
  sandbox: true,
  backgroundThrottling: false,
};

// Icono de Mayhem Advisor (herramientas/crear-icono.js) en vez del de Electron.
const icon = path.join(__dirname, 'icono.ico');

let overlay = null;
let champ = null;
let socket = null;      // tubería con el programa en C#
let interactive = false; // el ratón está encima de un botón del overlay
let buttons = [];        // rectángulos de los botones del overlay (px CSS, relativos a la ventana)
let dragging = false;    // se está arrastrando el panel: el overlay se queda con el ratón hasta soltar

// El overlay deja pasar los clics al juego, salvo encima de sus botones. En vez de fiarnos de los eventos de
// ratón que reenvía Electron (en modo ventana no llegaban), miramos dónde está el cursor 20 veces por segundo.
function applyMouseMode() {
  if (overlay && !overlay.isDestroyed()) overlay.setIgnoreMouseEvents(!interactive);
}

let sentRects = '';
setInterval(() => {
  if (!overlay || overlay.isDestroyed()) return;
  let over = false;
  const visible = buttons.length > 0 && overlay.isVisible();
  const b = overlay.getContentBounds();
  if (visible) {
    const p = screen.getCursorScreenPoint();
    over = buttons.some(r => p.x >= b.x + r.x && p.x <= b.x + r.x + r.w && p.y >= b.y + r.y && p.y <= b.y + r.y + r.h);
  }
  if (dragging) over = true;
  if (over !== interactive) { interactive = over; applyMouseMode(); }

  // Al programa en C#: dónde están los botones en píxeles reales de pantalla. Si el juego se queda el clic
  // (pasa al empezar la partida), él lo ve igualmente y nos lo reenvía como {type:'click'}.
  const rects = visible ? buttons.map(r => {
    const s = screen.dipToScreenRect(overlay, { x: b.x + r.x, y: b.y + r.y, width: r.w, height: r.h });
    return { x: s.x, y: s.y, w: s.width, h: s.height, act: r.act, id: r.id ?? null };
  }) : [];
  const key = JSON.stringify(rects);
  if (key !== sentRects && socket && !socket.destroyed) { sentRects = key; socket.write(JSON.stringify({ type: 'buttons', rects }) + '\n'); }
}, 50);

function createOverlay() {
  // focusable: false → al pulsar un botón el juego no pierde el foco (y el overlay no se esconde).
  overlay = new BrowserWindow({ ...OVERLAY_WINDOW_OPTS, focusable: false, icon, webPreferences });
  // Invisible para las capturas de pantalla: el programa en C# lee la pantalla con OCR y, si viera el panel
  // (que lista nombres de aumentos), los tomaría por cartas. Windows 10 2004+ lo excluye de la captura.
  overlay.setContentProtection(true);
  applyMouseMode();
  // Solo para las pruebas: guarda cómo se ve el panel (las capturas normales ya no lo ven).
  const shotDir = process.env.MAYHEM_OVERLAY_SHOT;
  if (shotDir) setInterval(async () => {
    try { require('node:fs').writeFileSync(require('node:path').join(shotDir, 'overlay.png'), (await overlay.webContents.capturePage()).toPNG()); } catch { }
    // También el texto (con los title) para revisar el idioma.
    try { require('node:fs').writeFileSync(require('node:path').join(shotDir, 'overlay.txt'), await overlay.webContents.executeJavaScript('document.body.innerText + "\\n" + [...document.querySelectorAll("[title]")].map(e => "title=" + e.title).join("\\n")')); } catch { }
  }, 400);
  overlay.loadFile('overlay.html');
  overlay.webContents.on('console-message', (_e, level, text) => console.error(`[overlay page] ${text}`));
  overlay.webContents.on('render-process-gone', (_e, d) => console.error('[overlay page] se cerró', JSON.stringify(d)));
  OverlayController.attachByTitle(overlay, GAME_WINDOW_TITLE);
  // La librería vuelve a "ignorar el ratón" (sin forward) cada vez que el juego toma el foco: lo reponemos.
  for (const name of ['attach', 'focus']) OverlayController.events.on(name, () => applyMouseMode());
  // Diagnóstico por la salida de error (el programa en C# no la lee; sirve al probarlo a mano).
  for (const name of ['attach', 'detach', 'focus', 'blur', 'fullscreen', 'moveresize']) {
    OverlayController.events.on(name, e => console.error(`[overlay] ${name}`, JSON.stringify(e ?? {})));
  }
}

// En la selección de campeón todavía no hay juego al que pegarse: ventana fija a la derecha de la pantalla.
function createChampSelect() {
  const area = screen.getPrimaryDisplay().workArea;
  const width = 330;
  champ = new BrowserWindow({
    x: area.x + area.width - width - 12,
    y: area.y + Math.round(area.height * 0.1),
    width,
    height: Math.round(area.height * 0.8),
    frame: false, transparent: true, resizable: false, movable: false, skipTaskbar: true,
    focusable: false, show: false, hasShadow: false, alwaysOnTop: true,
    icon, webPreferences,
  });
  champ.setAlwaysOnTop(true, 'screen-saver');
  champ.setContentProtection(true);
  // El ratón pasa al cliente de LoL salvo encima de un botón del panel (Coger, Aceptar…): forward deja que la página
  // vea por dónde va el ratón y avise (ipc 'champ-mouse') para capturar los clics solo ahí.
  champ.setIgnoreMouseEvents(true, { forward: true });
  champ.loadFile('champselect.html');
}

/**
 * Pone el panel de selección de campeón junto a la ventana del cliente de LoL (que llega en píxeles de pantalla) para
 * no taparle los textos: a su derecha si cabe, si no a su izquierda, y si no hay hueco, pegado al borde derecho.
 */
let lastPlace = '';
function placeChampSelect(client) {
  const width = 330, gap = 10;
  const area = screen.getPrimaryDisplay().workArea;
  let x = area.x + area.width - width - 12, y = area.y + Math.round(area.height * 0.1), height = Math.round(area.height * 0.8);
  if (client && client.w > 0) {
    const r = screen.screenToDipRect(null, { x: client.x, y: client.y, width: client.w, height: client.h });
    const d = screen.getDisplayMatching(r).workArea;
    if (r.x + r.width + gap + width <= d.x + d.width) x = r.x + r.width + gap;
    else if (r.x - gap - width >= d.x) x = r.x - gap - width;
    else x = d.x + d.width - width - 12;
    y = Math.max(d.y, r.y); height = Math.max(420, Math.min(r.height, d.y + d.height - y));
  }
  const key = `${x},${y},${height}`;
  if (key === lastPlace) return;
  lastPlace = key;
  champ.setBounds({ x: Math.round(x), y: Math.round(y), width, height: Math.round(height) });
}

function broadcast(message) {
  for (const win of [overlay, champ]) {
    if (win && !win.isDestroyed()) win.webContents.send('data', message);
  }
}

function onMessage(message) {
  if (message.type === 'quit') { app.quit(); return; }
  broadcast(message);
  if (message.type === 'live' && champ && !champ.isDestroyed()) {
    const show = message.phase === 'champselect' && (message.champSelect?.length ?? 0) > 0 && message.panel !== false;
    if (show) placeChampSelect(message.champSelectInfo?.client);
    if (show && !champ.isVisible()) champ.showInactive();
    if (!show && champ.isVisible()) champ.hide();
  }
}

// Botones del overlay: el ratón entra o sale de uno, o se pulsa (se reenvía al programa en C#).
ipcMain.on('buttons', (_e, rects) => { buttons = Array.isArray(rects) ? rects : []; });
ipcMain.on('dragging', (_e, on) => {
  dragging = !!on;
  if (dragging && !interactive) { interactive = true; applyMouseMode(); }
});
ipcMain.on('champ-mouse', (_e, over) => {
  if (champ && !champ.isDestroyed()) champ.setIgnoreMouseEvents(!over, { forward: true });
});
ipcMain.on('to-app', (_e, message) => {
  if (socket && !socket.destroyed) socket.write(JSON.stringify(message) + '\n');
});

app.whenReady().then(() => {
  createOverlay();
  createChampSelect();

  connect();
});

/** Se conecta a la tubería del programa en C#; si este se cierra, nos cerramos con él. */
function connect(attempt = 1) {
  const name = process.argv.find(a => a.startsWith('--mayhem-pipe='))?.slice('--mayhem-pipe='.length);
  if (!name) { console.error('Falta --mayhem-pipe: este overlay lo arranca Mayhem Advisor'); app.exit(EXIT_NO_PIPE); return; }

  socket = net.connect('\\\\.\\pipe\\' + name);
  socket.setEncoding('utf8');
  let connected = false;
  socket.on('connect', () => {
    connected = true;
    const lines = readline.createInterface({ input: socket });
    lines.on('line', line => {
      try { onMessage(JSON.parse(line)); } catch { /* línea incompleta: la siguiente llega en menos de un segundo */ }
    });
  });
  socket.on('error', err => {
    if (connected) return; // el cierre lo gestiona 'close'
    if (attempt < 20) setTimeout(() => connect(attempt + 1), 250);
    else { console.error('No pude conectar con Mayhem Advisor:', err.message); app.exit(EXIT_CANT_CONNECT); }
  });
  // Conectados alguna vez y se cortó (Mayhem Advisor se cerró, aunque sea de golpe): nos cerramos con él.
  socket.on('close', () => { if (connected) app.quit(); });
}

app.on('window-all-closed', () => app.quit());
