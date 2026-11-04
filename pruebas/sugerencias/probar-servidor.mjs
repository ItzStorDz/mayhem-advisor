// Prueba del buzón de sugerencias contra el servidor en local (wrangler dev).
const base = process.argv[2] ?? 'http://127.0.0.1:8787';
const post = async (body, note) => {
  const r = await fetch(base + '/v1/feedback', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  console.log(`${r.status} ${await r.text()}  ← ${note}`);
  return r.status;
};
const ok = { app: '1.8.2', lang: 'es', os: 'Windows 26200', kind: 'bug', source: 'postgame', rating: 4,
  text: 'Con Seraphine me salió una build de AD.\nY una @mencion <b>html</b> que no debe avisar a nadie.',
  context: { c: 'Seraphine', a: [1001, 1002], i: [3089, 3020], r: [3089, 3157, 3020], w: true, min: 17.4 } };
let fails = 0;
const expect = async (body, note, want) => { const s = await post(body, note); if (s !== want) { fails++; console.log(`   FALLO: esperaba ${want}`); } };

await expect(ok, 'válida con partida', 200);
await expect({ ...ok, context: undefined, kind: 'idea', source: 'app', rating: undefined, text: 'Un botón para copiar la build' }, 'idea sin nota ni partida', 200);
await expect({ ...ok, text: '', rating: 5, context: undefined }, 'solo nota', 200);
await expect({ ...ok, text: '   ', rating: undefined }, 'vacía', 400);
await expect({ ...ok, rating: 6 }, 'nota 6', 400);
await expect({ ...ok, kind: 'spam' }, 'tipo raro', 400);
await expect({ ...ok, text: 'x'.repeat(2001) }, 'texto largo', 400);
await expect({ ...ok, context: { c: 'Sera phine!', a: [], i: [] } }, 'campeón raro', 400);
await expect({ ...ok, context: { c: 'Seraphine', a: [], i: [], r: [0] } }, 'build de la app con id 0', 400);
await expect({ ...ok, app: 'hola' }, 'versión rara', 400);

// Informe diario (sin GITHUB_TOKEN en local, se escribe en la consola de wrangler).
const r = await fetch(base + '/__scheduled?cron=0+14+*+*+*');
console.log('informe:', r.status);
console.log(fails ? `${fails} FALLOS` : 'todo bien');
