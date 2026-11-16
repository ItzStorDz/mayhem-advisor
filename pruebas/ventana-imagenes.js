// Cuenta las imágenes que se dibujan en la ventana principal (Inicio, Partidas, una partida, Campeones, un campeón y
// Aumentos). Sirve para probar las imágenes guardadas en el PC (src/Core/IconCache.cs) como si CommunityDragon no
// respondiera. Abrir la app (una copia compilada aparte, con MAYHEM_DATA_DIR de pruebas) con:
//   WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 --proxy-server=127.0.0.1:9   (la ventana, sin internet)
//   HTTPS_PROXY=http://127.0.0.1:9                                                                    (el programa, sin internet)
// y luego:  node pruebas\ventana-imagenes.js
// Con la carpeta datos\iconos junto al .exe deben salir todas (menos el fondo del campeón, que no va en el instalador);
// sin ella y sin internet, ninguna.
const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  let target;
  for (let i = 0; i < 90 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:9333/json')).json()).find(t => t.url.includes('mayhem.local')); } catch { }
    if (!target) await sleep(1000);
  }
  if (!target) { console.log('FALLO: no encuentro la ventana principal'); process.exit(1); }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(r => ws.onopen = r);
  let id = 0; const pending = new Map();
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
  const cdp = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const js = async expr => (await cdp('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
  // Las imágenes de la sección visible: dibujadas / total, y un ejemplo de las que faltan.
  const count = async page => {
    await sleep(4000);
    const r = await js(`(() => { const all = [...document.querySelectorAll("main section:not(.hidden) img")].filter(i => i.getAttribute("src"));
      const bad = all.filter(i => !(i.complete && i.naturalWidth > 0));
      return { total: all.length, ok: all.length - bad.length, example: bad[0]?.src?.split("/").slice(-2).join("/") ?? "" }; })()`);
    console.log(`${page}: ${r.ok} de ${r.total} imágenes dibujadas${r.example ? ' · falta p. ej. ' + r.example : ''}`);
    total += r.total; ok += r.ok;
  };
  let total = 0, ok = 0;
  await cdp('Runtime.enable');
  await sleep(6000);
  await js('showTab("home")'); await count('Inicio');
  await js('showTab("games")'); await sleep(1500);
  await count('Partidas');
  const firstGame = await js('gamesData?.games?.[0]?.id');
  if (firstGame) { await js(`openGame(${JSON.stringify(firstGame)})`); await count('Una partida'); }
  await js('showTab("champs")'); await count('Campeones');
  await js('openChampion("Kayle")'); await count('Un campeón (Kayle)');
  await js('showTab("augs")'); await count('Aumentos');
  console.log(`TOTAL: ${ok} de ${total}`);
  ws.close();
  process.exit(0);
})();
