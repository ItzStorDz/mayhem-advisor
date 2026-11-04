// Prueba de la ventanita «¿Qué te parece la app?»: espera a que salga tras la partida del simulador (WebView2 con
// --remote-debugging-port=9333), la captura, la rellena y la envía (al servidor local), y comprueba que se cierra.
// Uso: node probar-ventanita.mjs <carpeta de salida> [enviar|nada]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
const out = process.argv[2];
const act = process.argv[3] ?? 'enviar';
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const note = s => { const line = `${new Date().toISOString().slice(11, 23)}  ${s}`; console.log(line); fs.appendFileSync(path.join(out, 'ventanita.txt'), line + '\n'); };

// Captura de toda la pantalla (para ver dónde sale la ventanita).
function screen(name) {
  const file = path.join(out, name + '.png');
  const ps = `Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; ` +
    `$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height; $g=[System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); ` +
    `$s=New-Object System.Drawing.Bitmap $bmp,($b.Width/2),($b.Height/2); $s.Save('${file.replace(/'/g, "''")}'); "ok"`;
  try { execFileSync('powershell.exe', ['-NoProfile', '-Command', ps], { stdio: 'pipe' }); note('pantalla: ' + name); } catch (e) { note('FALLO captura de pantalla: ' + e.message); }
}

let target;
for (let i = 0; i < 400 && !target; i++) {
  try { target = (await (await fetch('http://127.0.0.1:9333/json')).json()).find(t => t.url.includes('sugerencias.html')); } catch { }
  if (!target) await sleep(500);
}
if (!target) { note('FALLO: la ventanita no salió'); process.exit(1); }
note('ventanita abierta: ' + target.url);

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
await sleep(2500);
screen('pantalla-ventanita');
await shot('ventanita-1');
note('texto: ' + JSON.stringify(await js('document.body.innerText')));
note('adjuntar partida: ' + await js('document.querySelector(".fb-attach span")?.textContent ?? "(no hay)"'));
note('botón Enviar desactivado al principio: ' + await js('document.querySelector(\'[data-fb="send"]\').disabled'));
note('foco en la ventanita: ' + await js('document.hasFocus()'));

if (act === 'enviar') {
  await js('document.querySelector(\'[data-star="4"]\').click()');
  await js('document.querySelector(\'[data-kind="bug"]\').click()');
  await js(`(() => { const a = document.querySelector(".fb-text"); a.value = "Prueba del simulador: con Seraphine la build sale rara. <b>sin html</b> @nadie"; a.dispatchEvent(new Event("input")); })()`);
  await sleep(300);
  await shot('ventanita-2-rellena');
  note('botón Enviar activo: ' + !(await js('document.querySelector(\'[data-fb="send"]\').disabled')));
  await js('document.querySelector(\'[data-fb="send"]\').click()');
  await sleep(1500);
  await shot('ventanita-3-gracias');
  note('tras enviar: ' + JSON.stringify(await js('document.body.innerText')));
  // Se cierra sola a los ~2,6 s.
  let closed = false;
  for (let i = 0; i < 20 && !closed; i++) {
    await sleep(500);
    try { closed = !(await (await fetch('http://127.0.0.1:9333/json')).json()).some(t => t.url.includes('sugerencias.html')); } catch { closed = true; }
  }
  note(closed ? 'la ventanita se cerró sola tras enviar' : 'FALLO: la ventanita no se cerró');
}
ws.close();
