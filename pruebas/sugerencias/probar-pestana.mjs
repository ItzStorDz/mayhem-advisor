// Prueba de la pestaña «Sugerencias» de la ventana principal (app abierta con MAYHEM_OPEN=sugerencias y WebView2 con
// --remote-debugging-port=9333): captura en español e inglés, envía una idea sin nota y vuelve a «Enviar otra».
// Uso: node probar-pestana.mjs <carpeta de salida>
import fs from 'node:fs';
import path from 'node:path';
const out = process.argv[2];
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const note = s => { const line = `${new Date().toISOString().slice(11, 23)}  ${s}`; console.log(line); fs.appendFileSync(path.join(out, 'pestana.txt'), line + '\n'); };

let target;
for (let i = 0; i < 120 && !target; i++) {
  try { target = (await (await fetch('http://127.0.0.1:9333/json')).json()).find(t => t.url.includes('mayhem.local/index.html')); } catch { }
  if (!target) await sleep(1000);
}
if (!target) { note('FALLO: no encuentro la ventana principal'); process.exit(1); }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const pending = new Map();
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  if (m.method === 'Runtime.exceptionThrown') note('ERROR JS: ' + JSON.stringify(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text));
};
const cdp = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const js = async expr => (await cdp('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result?.result?.value;
const shot = async name => {
  const r = await cdp('Page.captureScreenshot', { format: 'png' });
  if (r.result?.data) { fs.writeFileSync(path.join(out, name + '.png'), Buffer.from(r.result.data, 'base64')); note('captura: ' + name); }
};
await cdp('Runtime.enable'); await cdp('Page.enable');
await cdp('Emulation.setDeviceMetricsOverride', { width: 1120, height: 740, deviceScaleFactor: 1, mobile: false });
await sleep(6000);

note('pestaña abierta: ' + await js('[...document.querySelectorAll("main section")].find(s => !s.classList.contains("hidden"))?.id'));
note('botón en el menú: ' + await js('document.querySelector(\'nav [data-tab="feedback"]\')?.innerText'));
note('adjuntar partida: ' + await js('document.querySelector("#fbForm .fb-attach span")?.textContent ?? "(no hay)"'));
await shot('pestana-1-es');

await js('send({ type: "setLanguage", value: "en" })'); await sleep(3000);
note('en inglés: ' + JSON.stringify(await js('document.querySelector("#feedback").innerText')));
await shot('pestana-2-en');
await js('send({ type: "setLanguage", value: "es" })'); await sleep(3000);

await js('document.querySelector(\'#fbForm [data-kind="idea"]\').click()');
await js(`(() => { const a = document.querySelector("#fbForm .fb-text"); a.value = "Prueba desde la pestaña: un botón para copiar la build al portapapeles."; a.dispatchEvent(new Event("input")); })()`);
await sleep(300);
await shot('pestana-3-rellena');
await js('document.querySelector(\'#fbForm [data-fb="send"]\').click()');
await sleep(2000);
note('tras enviar: ' + JSON.stringify(await js('document.querySelector("#fbForm").innerText')));
await shot('pestana-4-gracias');
await js('document.querySelector(\'#fbForm [data-fb="again"]\')?.click()'); await sleep(800);
note('«Enviar otra» vuelve al formulario vacío: ' + await js('!!document.querySelector("#fbForm .fb-text") && document.querySelector("#fbForm .fb-text").value === ""'));
ws.close();
