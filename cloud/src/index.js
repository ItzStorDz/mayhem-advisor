// Servidor de Mayhem Advisor (Cloudflare Worker + base de datos D1).
//
// Recibe, de quien no lo haya desactivado en Ajustes:
//   POST /v1/match   una partida de ARAM: Caos al acabar (campeones, aumentos, objetos y daño de los 10, sin nombres;
//                    las cartas que le ofrecieron a ese jugador con su nota y lo que recomendó la app).
//   POST /v1/report  un resumen de errores y de salud de la app (cada pocas horas, ya sin rutas ni nombres).
// Y sirve, para quien no llega a api.github.com (un jugador con «No such host is known»: se quedó en la 1.5.1):
//   GET /v1/latest                       la última Release de GitHub (solo lo que usa la app; caché de 10 min).
//   GET /v1/download/vX.Y.Z/<instalador> el instalador de esa Release, tal cual (la app comprueba su huella SHA-256).
// Y cada mañana (cron) junta lo que llegó desde el último informe y crea una Issue en un repositorio privado.
//
// Privacidad: no se guarda la IP (solo un contador por día con la IP cifrada con una clave que cambia cada día) y el
// id de la partida se guarda cifrado (HMAC con una clave secreta), así que no se puede buscar la partida en Riot.

const MAX_BODY = 48 * 1024;
const LIMIT_MATCHES = 80; // por IP y día (nadie juega 80 partidas en un día)
const LIMIT_REPORTS = 40;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    try {
      if (request.method === 'GET' && url.pathname === '/v1/health') return json({ ok: true });
      if (request.method === 'GET' && url.pathname === '/v1/community') return await getCommunity(request, env, ctx);
      if (request.method === 'GET' && url.pathname === '/v1/latest') return await getLatest(request, env, ctx);
      if (request.method === 'GET' && url.pathname.startsWith('/v1/download/')) return await getDownload(url);
      if (request.method !== 'POST') return json({ error: 'not found' }, 404);
      if (url.pathname === '/v1/match') return await postMatch(request, env);
      if (url.pathname === '/v1/report') return await postReport(request, env);
      return json({ error: 'not found' }, 404);
    } catch (e) {
      if (e instanceof Bad) return json({ error: e.message }, 400);
      console.error(e);
      return json({ error: 'server error' }, 500);
    }
  },

  async scheduled(event, env, ctx) {
    ctx.waitUntil(dailyReport(env));
  },
};

class Bad extends Error { }
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
const today = () => new Date().toISOString().slice(0, 10);

async function readBody(request) {
  const text = await request.text();
  if (text.length > MAX_BODY) throw new Bad('too large');
  try { return JSON.parse(text); } catch { throw new Bad('bad json'); }
}

async function hmac(secret, text) {
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const sig = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(text));
  return [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');
}

/** Cuenta un envío de esta IP hoy; false si ya pasó el límite. La IP solo se guarda cifrada y con la fecha. */
async function allow(request, env, kind, max) {
  const ip = request.headers.get('cf-connecting-ip') ?? 'local';
  const day = today();
  const key = kind + ':' + (await hmac(env.HMAC_SECRET + day, ip)).slice(0, 24);
  const row = await env.DB.prepare(
    'INSERT INTO limits (key, day, n) VALUES (?1, ?2, 1) ON CONFLICT(key) DO UPDATE SET n = n + 1 RETURNING n').bind(key, day).first();
  return (row?.n ?? 1) <= max;
}

// ───────── Comprobaciones ─────────

const int = (v, min, max) => Number.isInteger(v) && v >= min && v <= max;
const str = (v, re) => typeof v === 'string' && re.test(v);
const ints = (v, maxLen, min, max) => Array.isArray(v) && v.length <= maxLen && v.every(x => int(x, min, max));
const need = (ok, what) => { if (!ok) throw new Bad('bad ' + what); };

const APP = /^\d{1,2}\.\d{1,2}\.\d{1,3}$/;
const LANG = /^(es|en)$/;

function checkParticipant(p) {
  need(p && typeof p === 'object', 'participant');
  need(str(p.c, /^[A-Za-z]{2,20}$/), 'champion');
  need(typeof p.w === 'boolean', 'win');
  need(p.t === 100 || p.t === 200, 'team');
  need(ints(p.a, 8, 1, 999999), 'augments');
  need(ints(p.i, 8, 1, 999999), 'items');
  need(int(p.pd, 0, 2_000_000) && int(p.md, 0, 2_000_000) && int(p.h, 0, 2_000_000), 'damage');
  need(p.s === undefined || str(p.s, /^[QWER]{0,30}$/), 'skills');
  return { c: p.c, w: p.w, t: p.t, a: p.a, i: p.i, pd: p.pd, md: p.md, h: p.h, s: p.s ?? '' };
}

function checkJournal(j) {
  if (j == null) return null;
  need(typeof j === 'object', 'journal');
  const card = c => { need(int(c?.id, 1, 999999) && typeof c.score === 'number' && c.score >= 0 && c.score <= 100 && str(c.grade, /^[A-Z]{1,2}$/), 'card'); return { id: c.id, score: Math.round(c.score * 10) / 10, grade: c.grade }; };
  const min = m => { need(typeof m === 'number' && m >= 0 && m <= 90, 'minute'); return Math.round(m * 10) / 10; };
  need(Array.isArray(j.picks) && j.picks.length <= 20, 'picks');
  need(Array.isArray(j.buys) && j.buys.length <= 80, 'buys');
  need(Array.isArray(j.deaths ?? []) && (j.deaths ?? []).length <= 60, 'deaths');
  return {
    // guess (1.6.1+): la carta que la app creyó que elegiste, si al final resultó otra (se corrige con el historial).
    picks: j.picks.map(p => {
      need(Array.isArray(p?.offered) && p.offered.length <= 4 && int(p.chosen, 1, 999999) && (p.guess == null || int(p.guess, 1, 999999)), 'pick');
      return { min: min(p.min), offered: p.offered.map(card), chosen: p.chosen, ...(p.guess == null ? {} : { guess: p.guess }) };
    }),
    buys: j.buys.map(b => { need(int(b?.item, 1, 999999), 'buy'); return { min: min(b.min), item: b.item }; }),
    build: (need(ints(j.build ?? [], 8, 1, 999999), 'build'), j.build ?? []),
    deaths: (j.deaths ?? []).map(d => { need(int(d?.gold, 0, 100000), 'death'); return { min: min(d.min), gold: d.gold }; }),
    minutes: min(j.minutes ?? 0),
  };
}

async function postMatch(request, env) {
  const b = await readBody(request);
  need(str(b.app, APP) && str(b.lang, LANG), 'app');
  need(str(b.id, /^[A-Z0-9]{2,5}_\d{6,14}$/), 'id');
  need(str(b.patch, /^\d{1,2}\.\d{1,2}[\d.]*$/), 'patch');
  need(Array.isArray(b.p) && b.p.length === 10, 'players');
  const players = b.p.map(checkParticipant);
  need(int(b.me, 0, 9), 'me');
  need(int(b.dur, 60, 5400), 'duration');
  const me = players[b.me];
  // Un diario raro (p. ej. el reloj del juego en el minuto 168) no tira la partida: se guarda sin diario.
  let journal = null;
  try { journal = checkJournal(b.journal); } catch (e) { if (!(e instanceof Bad)) throw e; }
  const data = { patch: b.patch, dur: b.dur, me: b.me, p: players, journal };

  if (!(await allow(request, env, 'm', LIMIT_MATCHES))) return json({ error: 'limit' }, 429);
  const id = await hmac(env.HMAC_SECRET, b.id);
  const r = await env.DB.prepare(
    `INSERT OR IGNORE INTO matches (id, received, app, lang, patch, champion, win, data) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`)
    .bind(id, new Date().toISOString(), b.app, b.lang, b.patch, me.c, me.w ? 1 : 0, JSON.stringify(data)).run();
  return json({ ok: true, new: r.meta.changes > 0 });
}

async function postReport(request, env) {
  const b = await readBody(request);
  need(str(b.app, APP) && str(b.lang, LANG), 'app');
  need(str(b.os ?? '', /^[\w .()-]{0,40}$/), 'os');
  need(Array.isArray(b.errors) && b.errors.length <= 60, 'errors');
  const errors = b.errors.map(e => {
    need(str(e?.k, /^(error|warn)$/) && typeof e.m === 'string' && e.m.length <= 500 && int(e.n, 1, 100000), 'error');
    return { k: e.k, m: e.m, n: e.n };
  });
  const h = b.health ?? {};
  need(typeof h === 'object', 'health');
  const health = {};
  for (const k of ['starts', 'games', 'cardSets', 'picks', 'scans', 'scanMs', 'crashes']) {
    if (h[k] === undefined) continue;
    need(int(h[k], 0, 10_000_000), 'health ' + k);
    health[k] = h[k];
  }
  if (!(await allow(request, env, 'r', LIMIT_REPORTS))) return json({ error: 'limit' }, 429);
  await env.DB.prepare('INSERT INTO reports (received, app, lang, os, data) VALUES (?1, ?2, ?3, ?4, ?5)')
    .bind(new Date().toISOString(), b.app, b.lang, b.os ?? '', JSON.stringify({ errors, health })).run();
  return json({ ok: true });
}

// ───────── Partidas de la comunidad ─────────

/**
 * Las partidas compartidas de los 2 parches más nuevos (máximo 6000), en el formato de matches.jsonl de la app, para
 * que las estadísticas de cada usuario aprendan de todos. Sin nombres; el id es el cifrado. Caché de 1 hora.
 */
async function getCommunity(request, env, ctx) {
  const cache = caches.default;
  const hit = await cache.match(request);
  if (hit) return hit;
  const patches = (await env.DB.prepare(
    `SELECT DISTINCT substr(patch, 1, instr(substr(patch, 4), '.') + 2) p FROM matches ORDER BY received DESC LIMIT 200`).all()).results
    .map(r => r.p).filter((p, i, a) => a.indexOf(p) === i).slice(0, 2);
  const rows = patches.length === 0 ? [] : (await env.DB.prepare(
    `SELECT id, patch, data FROM matches WHERE ${patches.map((_, i) => `patch LIKE ?${i + 1}`).join(' OR ')} ORDER BY received DESC LIMIT 6000`)
    .bind(...patches.map(p => p + '%')).all()).results;
  const body = rows.map(r => JSON.stringify({ id: r.id, v: r.patch, p: JSON.parse(r.data).p })).join('\n');
  const resp = new Response(body, { headers: { 'content-type': 'application/x-ndjson', 'cache-control': 'public, max-age=3600' } });
  ctx.waitUntil(cache.put(request, resp.clone()));
  return resp;
}

// ───────── Actualizaciones (copia de GitHub) ─────────

const REPO = 'ItzStorDz/mayhem-advisor';
const INSTALLER = /^MayhemAdvisor-Instalador-\d+\.\d+\.\d+\.exe$/;
const TAG = /^v\d+\.\d+\.\d+$/;

/** La última Release, con lo mismo que lee la app de la API de GitHub (versión, notas, instaladores y su huella). */
async function getLatest(request, env, ctx) {
  const cache = caches.default;
  const key = new Request(new URL('/v1/latest', request.url).toString());
  const hit = await cache.match(key);
  if (hit) return hit;
  const headers = { 'user-agent': 'mayhem-advisor-worker', accept: 'application/vnd.github+json' };
  if (env.GITHUB_TOKEN) headers.authorization = `Bearer ${env.GITHUB_TOKEN}`;
  const gh = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers });
  if (gh.status === 404) return json({ error: 'no release' }, 404);
  if (!gh.ok) return json({ error: `github ${gh.status}` }, 502);
  const r = await gh.json();
  const body = {
    tag_name: r.tag_name, html_url: r.html_url, body: r.body ?? '',
    assets: (r.assets ?? []).filter(a => INSTALLER.test(a.name ?? ''))
      .map(a => ({ name: a.name, size: a.size, digest: a.digest, browser_download_url: a.browser_download_url })),
  };
  const resp = new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json', 'cache-control': 'public, max-age=600' } });
  ctx.waitUntil(cache.put(key, resp.clone()));
  return resp;
}

/** El instalador de una Release de este repositorio, sin tocarlo (solo nombres de instalador y etiquetas vX.Y.Z). */
async function getDownload(url) {
  const [tag, name] = url.pathname.slice('/v1/download/'.length).split('/');
  if (!TAG.test(tag ?? '') || !INSTALLER.test(name ?? '') || !name.includes(`-${tag.slice(1)}.exe`)) return json({ error: 'not found' }, 404);
  const gh = await fetch(`https://github.com/${REPO}/releases/download/${tag}/${name}`, { headers: { 'user-agent': 'mayhem-advisor-worker' } });
  if (!gh.ok) return json({ error: `github ${gh.status}` }, gh.status === 404 ? 404 : 502);
  const headers = { 'content-type': 'application/octet-stream', 'content-disposition': `attachment; filename="${name}"` };
  if (gh.headers.get('content-length')) headers['content-length'] = gh.headers.get('content-length');
  return new Response(gh.body, { headers });
}

// ───────── Informe diario ─────────

async function dailyReport(env) {
  const since = (await env.DB.prepare(`SELECT value FROM state WHERE key = 'lastReport'`).first())?.value
    ?? new Date(Date.now() - 24 * 3600 * 1000).toISOString();
  const until = new Date().toISOString();
  const matches = (await env.DB.prepare('SELECT app, lang, patch, champion, win, data FROM matches WHERE received > ?1 AND received <= ?2')
    .bind(since, until).all()).results;
  const reports = (await env.DB.prepare('SELECT app, lang, os, data FROM reports WHERE received > ?1 AND received <= ?2')
    .bind(since, until).all()).results;

  // Limpieza: contadores de días pasados y datos de errores de hace más de 90 días.
  await env.DB.prepare('DELETE FROM limits WHERE day < ?1').bind(today()).run();
  await env.DB.prepare('DELETE FROM reports WHERE received < ?1').bind(new Date(Date.now() - 90 * 86400_000).toISOString()).run();

  if (matches.length === 0 && reports.length === 0) {
    await env.DB.prepare(`INSERT OR REPLACE INTO state (key, value) VALUES ('lastReport', ?1)`).bind(until).run();
    return;
  }
  const body = await buildReport(env, matches, reports, since);
  const title = `Daily report ${until.slice(0, 10)} · ${matches.length} games · ${reports.length} error reports`;
  if (env.GITHUB_TOKEN && env.REPORT_REPO) {
    const r = await fetch(`https://api.github.com/repos/${env.REPORT_REPO}/issues`, {
      method: 'POST',
      headers: { authorization: `Bearer ${env.GITHUB_TOKEN}`, accept: 'application/vnd.github+json', 'user-agent': 'mayhem-advisor-cloud', 'content-type': 'application/json' },
      body: JSON.stringify({ title, body, labels: ['daily-report'] }),
    });
    if (!r.ok) { console.error('GitHub', r.status, await r.text()); return; } // se reintenta mañana con todo
  } else console.log(title + '\n\n' + body);
  await env.DB.prepare(`INSERT OR REPLACE INTO state (key, value) VALUES ('lastReport', ?1)`).bind(until).run();
}

const count = (list, f) => { const m = new Map(); for (const x of list) { const k = f(x); m.set(k, (m.get(k) ?? 0) + 1); } return [...m].sort((a, b) => b[1] - a[1]); };
const pct = (a, b) => b ? `${Math.round(a / b * 100)}%` : '–';

async function buildReport(env, matches, reports, since) {
  const lines = [];
  lines.push(`Since ${since.slice(0, 16).replace('T', ' ')} UTC.`, '');

  // Errores agrupados por mensaje (ya normalizado en la app), con las versiones donde salen.
  const errors = new Map();
  const health = { starts: 0, games: 0, cardSets: 0, picks: 0, scans: 0, scanMs: 0, crashes: 0 };
  for (const r of reports) {
    const d = JSON.parse(r.data);
    for (const e of d.errors) {
      const key = e.k + '|' + e.m;
      const x = errors.get(key) ?? { k: e.k, m: e.m, n: 0, reports: 0, apps: new Set() };
      x.n += e.n; x.reports++; x.apps.add(r.app);
      errors.set(key, x);
    }
    for (const k in health) health[k] += d.health?.[k] ?? 0;
  }
  // Errores que no habían salido nunca.
  const fresh = new Set();
  for (const e of errors.values()) {
    const r = await env.DB.prepare('INSERT OR IGNORE INTO seen_errors (m, first) VALUES (?1, ?2)').bind(e.m.slice(0, 300), new Date().toISOString()).run();
    if (r.meta.changes > 0) fresh.add(e.m);
  }

  lines.push('## Summary', '');
  lines.push(`- **Games uploaded:** ${matches.length} (${pct(matches.filter(m => m.win).length, matches.length)} wins)`);
  lines.push(`- **Error reports:** ${reports.length} · app starts ${health.starts} · crashes/unhandled ${health.crashes}`);
  if (health.games) lines.push(`- **Health:** ${health.games} games seen in-game · ${health.cardSets} card sets read · ${health.picks} picks saved (${(health.picks / Math.max(1, health.games)).toFixed(1)} per game)`);
  if (health.scans) lines.push(`- **Screen reading:** ${(health.scanMs / health.scans).toFixed(0)} ms average over ${health.scans} scans`);
  lines.push(`- **Versions:** ${count([...matches, ...reports], x => x.app).map(([k, n]) => `${k} (${n})`).join(', ') || '–'}`);
  lines.push(`- **Languages:** ${count([...matches, ...reports], x => x.lang).map(([k, n]) => `${k} (${n})`).join(', ') || '–'}`);
  lines.push('');

  const list = [...errors.values()].sort((a, b) => (a.k === b.k ? 0 : a.k === 'error' ? -1 : 1) || b.reports - a.reports || b.n - a.n);
  lines.push('## Errors and warnings', '');
  if (list.length === 0) lines.push('None. 🎉', '');
  else {
    lines.push('| | Message | Times | Users* | Versions |', '|---|---|---|---|---|');
    for (const e of list.slice(0, 40))
      lines.push(`| ${e.k === 'error' ? '🔴' : '🟡'}${fresh.has(e.m) ? ' **NEW**' : ''} | \`${e.m.replace(/\|/g, '/').replace(/`/g, "'").slice(0, 300)}\` | ${e.n} | ${e.reports} | ${[...e.apps].join(', ')} |`);
    lines.push('', '*Users = number of reports that contain it (one report per app every few hours).', '');
  }

  if (matches.length) {
    lines.push('## Games', '');
    lines.push(`- **Champions:** ${count(matches, m => m.champion).slice(0, 15).map(([k, n]) => `${k} ${n}`).join(' · ')}`);
    // ¿Se eligió la carta que la app puntuó más alto? La elección de verdad sale de los aumentos del historial (las apps de
    // antes de 1.6.1 suben lo que creyeron ver). Y ¿cuántas apuntó mal la app en la partida? (guess, o chosen en las viejas).
    let picks = 0, followed = 0, checked = 0, wrong = 0;
    for (const m of matches) {
      const d = JSON.parse(m.data);
      const actual = new Set(d.p?.[d.me]?.a ?? []);
      for (const p of d.journal?.picks ?? []) {
        if (p.offered.length < 2) continue;
        const real = p.offered.filter(c => actual.has(c.id));
        const chosen = actual.has(p.chosen) || real.length !== 1 ? p.chosen : real[0].id;
        picks++;
        const best = p.offered.reduce((a, b) => b.score > a.score ? b : a);
        if (best.id === chosen) followed++;
        if (real.length === 1) { checked++; if ((p.guess ?? p.chosen) !== real[0].id) wrong++; }
      }
    }
    if (picks) lines.push(`- **Augment picks:** ${picks} · top-scored card chosen ${pct(followed, picks)}`);
    if (checked) lines.push(`- **Picks the app got wrong in-game:** ${wrong} of ${checked} (${pct(wrong, checked)}), checked against the match history`);
    lines.push(`- **Patches:** ${count(matches, m => m.patch.split('.').slice(0, 2).join('.')).map(([k, n]) => `${k} (${n})`).join(', ')}`);
    lines.push('');
  }
  return lines.join('\n');
}
