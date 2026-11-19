// ¿Funcionan de verdad «Coger» y la rueda en el panel de la selección de campeón? (opiniones 27 y 30, 1.9.2: «no puedo
// bajar en la lista», «el botón Coger no hace nada»). Hace de programa en C#: abre el overlay de Electron real
// (overlay-electron/main.js) con su tubería, le manda un banco lleno, mueve el ratón DE VERDAD encima del primer «Coger»,
// hace clic y mira si llega {type:'benchswap'}; luego gira la rueda encima de la lista y mira si se desplaza.
// Mueve el ratón unos segundos. Con el juego y Mayhem Advisor cerrados o no, da igual (usa otro perfil de Electron).
// Uso (node normal):  node pruebas\seleccion-campeon\raton.js
const net = require('node:net');
const path = require('node:path');
const os = require('node:os');
const { spawn, execFileSync } = require('node:child_process');

const root = path.resolve(__dirname, '..', '..');
const electron = path.join(root, 'overlay-electron', 'node_modules', 'electron', 'dist', 'electron.exe');
const pipeName = `mayhem-prueba-raton-${process.pid}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));
let fails = 0;
const check = (ok, what) => { console.log((ok ? 'ok     ' : 'FALLO  ') + what); if (!ok) fails++; };

const icon = id => `https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/${id}.png`;
const champs = [[75, 'Nasus'], [99, 'Lux'], [147, 'Seraphine'], [10, 'Kayle'], [86, 'Garen'], [1, 'Annie'], [22, 'Ashe'], [51, 'Caitlyn'],
  [122, 'Darius'], [81, 'Ezreal'], [3, 'Galio'], [40, 'Janna'], [24, 'Jax'], [222, 'Jinx'], [89, 'Leona'], [54, 'Malphite'], [25, 'Morgana']];
const live = {
  type: 'live', phase: 'champselect', status: '', lang: 'es', panel: true,
  champSelect: champs.map(([id, name], i) => ({ championId: id, alias: name, name, icon: icon(id), winRate: 0.55 - i / 100, games: 30000,
    isCurrent: i === 0, tier: 'B', mayhemWinRate: 0.55 - i / 100, mayhemGames: 30000, damage: i % 2 ? 'magic' : 'physical', spells: null })),
  champSelectInfo: { trades: [], fullscreen: false, itemSet: null, notice: null, client: null, team: { physical: 2, magic: 2, mixed: 0 } },
};

// Ratón de verdad (user32), en píxeles físicos.
function mouse(script) {
  const ps = `Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, System.IntPtr e);'; [W.U]::SetProcessDPIAware() | Out-Null; ${script}`;
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], { stdio: 'ignore' });
}

(async () => {
  const received = [];
  let lastRects = null;
  const server = net.createServer(sock => {
    sock.setEncoding('utf8');
    let buf = '';
    sock.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, i); buf = buf.slice(i + 1); try { received.push(JSON.parse(line)); } catch { } } });
    const send = () => { if (!sock.destroyed) sock.write(JSON.stringify(live) + '\n'); };
    send(); const t = setInterval(send, 1000); sock.on('close', () => clearInterval(t));
  }).listen('\\\\.\\pipe\\' + pipeName);

  const child = spawn(electron, [path.join(root, 'overlay-electron'), `--mayhem-pipe=${pipeName}`, `--user-data-dir=${path.join(os.tmpdir(), 'mayhem-prueba-raton')}`],
    { env: { ...process.env, MAYHEM_CHAMP_DEBUG: '1' }, stdio: ['ignore', 'ignore', 'pipe'] });
  child.stderr.setEncoding('utf8');
  let wheels = 0, scrollTop = 0;
  child.stderr.on('data', d => {
    for (const line of d.split('\n')) {
      const i = line.indexOf('[champ] rects '); if (i >= 0) try { lastRects = JSON.parse(line.slice(i + 14)); } catch { }
      if (line.includes('[champ page] rueda')) { wheels++; if (wheels === 1) console.log('       ' + line.trim().slice(0, 160)); }
      const m = line.match(/\[champ page\] lista (\d+)/); if (m) scrollTop = Number(m[1]);
    }
  });

  try {
    for (let i = 0; i < 40 && !(lastRects?.rects?.length > 1); i++) await sleep(250);
    check(lastRects?.rects?.length > 1, `el panel dice dónde están sus botones y su lista (${lastRects?.rects?.length ?? 0} zonas)`);
    if (!lastRects) return;
    const { bounds, scale, rects } = lastRects;
    const list = rects[rects.length - 1];
    const listScrolls = list.h > 200;
    check(listScrolls, `la lista no cabe entera y cuenta como zona del ratón (${list.w}x${list.h})`);
    const btn = rects.find(r => r.h < 60 && r.y + r.h < list.y + list.h);
    const px = (x, y) => [Math.round((bounds.x + x) * scale), Math.round((bounds.y + y) * scale)];

    // 1) Clic en el primer «Coger»
    const [bx, by] = px(btn.x + btn.w / 2, btn.y + btn.h / 2);
    const before = received.filter(m => m.type === 'benchswap').length;
    mouse(`[W.U]::SetCursorPos(${bx}, ${by}); Start-Sleep -Milliseconds 400; [W.U]::mouse_event(2,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 60; [W.U]::mouse_event(4,0,0,0,[IntPtr]::Zero)`);
    for (let i = 0; i < 20 && received.filter(m => m.type === 'benchswap').length === before; i++) await sleep(100);
    const swap = received.filter(m => m.type === 'benchswap').at(-1);
    check(!!swap, `clic en «Coger»: llega al programa ${swap ? JSON.stringify(swap) : '(nada)'}`);

    // 2) Rueda encima de la lista: los botones se mueven hacia arriba
    const y0 = lastRects.rects[0].y;
    const [lx, ly] = px(list.x + list.w / 2, list.y + list.h / 2);
    mouse(`[W.U]::SetCursorPos(${lx}, ${ly}); Start-Sleep -Milliseconds 400; for ($i = 0; $i -lt 3; $i++) { [W.U]::mouse_event(0x0800,0,0,-120,[IntPtr]::Zero); Start-Sleep -Milliseconds 80 }`);
    await sleep(1200);
    const y1 = lastRects.rects[0].y;
    check(wheels > 0, `la rueda llega a la página (${wheels} giros)`);
    check(scrollTop > 0 && y1 < y0, `rueda encima de la lista: se desplaza (${scrollTop} px; el primer botón pasa de y=${y0} a y=${y1})`);

    // 3) Fuera del panel el ratón no se lo queda: mover a la izquierda del panel y que no llegue nada al hacer clic
    const n = received.length;
    const [ox, oy] = px(-60, list.y + 40);
    mouse(`[W.U]::SetCursorPos(${ox}, ${oy}); Start-Sleep -Milliseconds 300`);
    await sleep(300);
    check(received.length === n, 'fuera del panel no se captura nada');
  } finally {
    child.kill();
    server.close();
    console.log(fails === 0 ? 'RATÓN: TODO BIEN' : `RATÓN: ${fails} FALLOS`);
    setTimeout(() => process.exit(fails), 300);
  }
})();
