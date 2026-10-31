// Prueba del idioma de la ventana principal: recorre todas las pantallas en inglés (también cambiando de idioma con
// páginas ya abiertas) y apunta el texto visible que parece español. Uso (node de Electron):
//   ELECTRON_RUN_AS_NODE=1 electron.exe dash-en.js <carpeta de salida>
const fs = require('node:fs');
const path = require('node:path');
const out = process.argv[2];
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const log = [];
const note = s => { console.log(s); log.push(s); };

// Palabras que en inglés no salen nunca (los nombres de campeones/objetos en inglés no las llevan).
const ES = /[áéíóúñ¿¡]|\b(de|del|con|los|las|una|tus|sin|para|más|partidas?|victorias?|derrotas?|aumentos?|objetos?|campeón|campeones|nivel|hoy|ayer|daño|vida|oro|elegiste|compra|botas|tienes|juega|todos|nota|fiabilidad|fiable|rendimiento|mejor|peor|enemigo|aliado|equipo|habilidad|minutos?|hace|cargando|ajustes|inicio|idioma|aceptar|selección|resumen|copiar|guardar|ver|rango|puntuación|muertes?|asesinatos|asistencias)\b/i;
const ALLOW = /Español|App, panel del juego y nombres de objetos y aumentos|IDIOMA · LANGUAGE/;

(async () => {
  let target;
  for (let i = 0; i < 120 && !target; i++) {
    try { target = (await (await fetch('http://127.0.0.1:9333/json')).json()).find(t => t.url.includes('mayhem.local')); } catch { }
    if (!target) await sleep(1000);
  }
  if (!target) { note('FALLO: no encuentro la ventana principal'); return finish(); }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(r => ws.onopen = r);
  let id = 0; const pending = new Map();
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
    if (m.method === 'Runtime.exceptionThrown') note('ERROR JS: ' + JSON.stringify(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') note('console.error: ' + JSON.stringify(m.params.args.map(a => a.value ?? a.description)));
  };
  const cdp = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const js = async expr => (await cdp('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
  const shot = async name => {
    const r = await cdp('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    fs.writeFileSync(path.join(out, name + '.png'), Buffer.from(r.result.data, 'base64'));
  };
  // Texto visible de la sección abierta (más los títulos y atributos title), línea a línea.
  const spanish = async page => {
    const text = await js(`(() => { const s = [...document.querySelectorAll("main section, nav")].filter(e => !e.classList.contains("hidden") && e.offsetParent !== null);
      const lines = s.flatMap(e => e.innerText.split("\\n"));
      const titles = s.flatMap(e => [...e.querySelectorAll("[title]")].map(x => "title=" + x.title));
      return [...lines, ...titles].map(l => l.trim()).filter(Boolean); })()`) ?? [];
    const bad = [...new Set(text.filter(l => ES.test(l) && !ALLOW.test(l)))];
    note(`${page}: ${text.length} líneas, ${bad.length} en español${bad.length ? '\n    · ' + bad.slice(0, 40).join('\n    · ') : ''}`);
    return bad.length;
  };
  await cdp('Runtime.enable'); await cdp('Page.enable');
  await cdp('Emulation.setDeviceMetricsOverride', { width: 1360, height: 860, deviceScaleFactor: 1, mobile: false });
  await sleep(5000);

  // 1) En español, abrimos páginas con datos y LUEGO cambiamos a inglés (lo que hace el usuario).
  if ((await js('lang')) !== 'es') { await js('send({ type: "setLanguage", value: "es" })'); await sleep(2500); }
  await js('showTab("games")'); await sleep(2500);
  const firstGame = await js('gamesData?.games?.[0]?.id');
  note('primera partida: ' + firstGame);
  if (firstGame) { await js(`openGame(${JSON.stringify(firstGame)})`); await sleep(2500); }
  await js('send({ type: "setLanguage", value: "en" })'); await sleep(3500);
  let total = 0;
  total += await spanish('partida abierta tras cambiar a inglés');
  await shot('en-partida-tras-cambio');

  // 2) Todo en inglés.
  await js('showTab("home")'); await sleep(1500); total += await spanish('inicio'); await shot('en-1-inicio');
  await js('showTab("games")'); await sleep(2500); total += await spanish('partidas'); await shot('en-2-partidas');
  const ids = (await js('(gamesData?.games ?? []).slice(0, 6).map(g => g.id)')) ?? [];
  for (const [n, gid] of ids.entries()) {
    await js(`openGame(${JSON.stringify(gid)})`); await sleep(2000);
    total += await spanish('partida ' + gid);
    if (n === 0) { await shot('en-3-partida'); await js('document.querySelector("main").scrollTo(0, 99999)'); await sleep(300); await shot('en-3b-partida-abajo'); }
  }
  await js('showTab("champs")'); await sleep(1000); total += await spanish('campeones'); await shot('en-4-campeones');
  for (const alias of ['Nasus', 'Rammus', 'Jinx', 'Karma']) {
    await js(`openChampion("${alias}")`); await sleep(2000);
    total += await spanish('campeón ' + alias);
    if (alias === 'Nasus') await shot('en-5-campeon');
    await js('document.querySelector("[data-back]")?.click()'); await sleep(300);
  }
  await js('showTab("augs")'); await sleep(300); await js('selectAugChampion("Rammus")'); await sleep(2500);
  total += await spanish('aumentos'); await shot('en-6-aumentos');
  await js('showTab("settings")'); await sleep(800); total += await spanish('ajustes'); await shot('en-7-ajustes');
  await js('showTab("about")'); await sleep(500); total += await spanish('acerca de'); await shot('en-8-acerca');
  note(`TOTAL líneas en español: ${total}`);
  await js('send({ type: "setLanguage", value: "es" })'); await sleep(1500);
  finish();
})().catch(e => { note('ERROR del test: ' + e); finish(); });

function finish() { fs.writeFileSync(path.join(out, 'informe.txt'), log.join('\n')); process.exit(0); }
