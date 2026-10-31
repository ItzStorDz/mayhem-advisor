// Pestaña «Partidas» (1.3.7): resumen de hoy, lista de tus partidas y el resumen detallado de cada una.
// Se carga antes del script principal de index.html: aquí solo se definen funciones (los ayudantes $, esc, t… del
// principal se usan cuando ya existen, al pintar).

const GT = {
  es: {
    title: "Partidas", today: "PARTIDAS DE HOY", noToday: "Hoy aún no has jugado ARAM: Caos.", list: "TUS PARTIDAS",
    wr: "de victorias", played: m => `${m} min jugados`, kda: "KDA medio", dmg: "Daño medio", score: "Puntuación media",
    scoreSub: "de 0 a 100 frente a los 10 jugadores", best: "TU MEJOR PARTIDA DE HOY", champsToday: "CAMPEONES DE HOY",
    double: "Doble", triple: "Triple", quadra: "Cuádruple", penta: "¡PENTAKILL!", noGames: "Aún no hay partidas guardadas. Se copian solas del cliente de LoL al terminar cada partida.",
    win: "Victoria", loss: "Derrota", ago: ts => ago(ts), min: m => `${dec(m, 0)} min`, dealt: "Daño", tanked: "Aguantado", healed: "Curación",
    noReport: "Sin detalle (partida antigua)", back: "‹ Todas las partidas", loading: "Cargando la partida…",
    missing: "Esta partida no tiene el detalle guardado: se guarda para las partidas nuevas (y las 20 últimas, con el cliente de LoL abierto).",
    victory: "VICTORIA", defeat: "DERROTA", level: l => `Nv. ${l}`, place: (p) => p === 1 ? "MEJOR DE LOS 10" : `PUESTO ${p} DE 10`,
    kp: p => `${p} part. en asesinatos`, dpm: "DAÑO", dtpm: "AGUANTE", hpm: "CURACIÓN", gpm: "ORO",
    copy: "Copiar imagen", save: "Guardar imagen", copied: "Copiada: pégala en Discord o WhatsApp (Ctrl+V)", saved: "Guardada en Imágenes › Mayhem Advisor (abrir)", failed: "No se pudo crear la imagen",
    perf: "RENDIMIENTO", perMin: v => `${v}/min`, rankOf: r => `#${r} de 10`, scoreParts: "DE DÓNDE SALE TU PUNTUACIÓN (tu puesto entre los 10)",
    augs: "AUMENTOS", augsAvg: g => `nota media ${g}`, atPick: "nota al elegirlo", now: "nota de la app", basedOn: (g, c) => `con ${g} partidas · fiabilidad ${c}/3`,
    offered: "Te ofrecieron también:", bestPick: "✓ Elegiste la mejor según la app", betterWas: n => `Había otra mejor: ${n}`,
    notSeen: "No salió en cartas (lo da el juego) o la app no lo vio elegir", minute: m => `min ${dec(m, 0)}`,
    build: "BUILD", yourItems: "Lo que llevabas al final", appBuild: "Lo que recomendaba la app al final", match: (n, t) => `${n} de ${t} coinciden`,
    buys: "Compras que recomendó la app", bought: "lo compraste", noJournal: "La app no estaba abierta en esta partida: no hay diario de cartas ni de compras.",
    deaths: "MUERTES", deathsSub: (n, g) => g != null ? `${n} muertes · oro sin gastar de media al morir: ${num(g)}` : `${n} muertes`, highGold: "más de 1.300 de oro sin gastar",
    tips: "PARA LA PRÓXIMA", teams: "LOS 10 JUGADORES", ally: "TU EQUIPO", enemy: "RIVALES", kills: k => `${k} asesinatos`, mvp: "MVP",
    multi: "MULTIASESINATOS", spree: n => `racha máx. ${n}`, seeGame: "Ver la partida ›", names: ["MVP", "Muy buena", "Buena", "Normal", "Floja"],
  },
  en: {
    title: "Games", today: "TODAY'S GAMES", noToday: "You haven't played ARAM: Mayhem today yet.", list: "YOUR GAMES",
    wr: "win rate", played: m => `${m} min played`, kda: "Average KDA", dmg: "Average damage", score: "Average score",
    scoreSub: "0 to 100 against all 10 players", best: "YOUR BEST GAME TODAY", champsToday: "TODAY'S CHAMPIONS",
    double: "Double", triple: "Triple", quadra: "Quadra", penta: "PENTAKILL!", noGames: "No games saved yet. They are copied from the League client after each game.",
    win: "Victory", loss: "Defeat", ago: ts => ago(ts), min: m => `${dec(m, 0)} min`, dealt: "Damage", tanked: "Tanked", healed: "Healing",
    noReport: "No details (old game)", back: "‹ All games", loading: "Loading the game…",
    missing: "This game has no saved details: they are saved for new games (and the last 20, with the League client open).",
    victory: "VICTORY", defeat: "DEFEAT", level: l => `Lv. ${l}`, place: (p) => p === 1 ? "BEST OF ALL 10" : `#${p} OF 10`,
    kp: p => `${p} kill participation`, dpm: "DAMAGE", dtpm: "TANKED", hpm: "HEALING", gpm: "GOLD",
    copy: "Copy image", save: "Save image", copied: "Copied: paste it in Discord or WhatsApp (Ctrl+V)", saved: "Saved in Pictures › Mayhem Advisor (open)", failed: "Couldn't create the image",
    perf: "PERFORMANCE", perMin: v => `${v}/min`, rankOf: r => `#${r} of 10`, scoreParts: "WHERE YOUR SCORE COMES FROM (your place among 10)",
    augs: "AUGMENTS", augsAvg: g => `average ${g}`, atPick: "score when picked", now: "app score", basedOn: (g, c) => `from ${g} games · confidence ${c}/3`,
    offered: "You were also offered:", bestPick: "✓ You took the app's best", betterWas: n => `There was a better one: ${n}`,
    notSeen: "Not shown on cards (granted by the game) or the app didn't see the pick", minute: m => `min ${dec(m, 0)}`,
    build: "BUILD", yourItems: "Your final items", appBuild: "The app's final build", match: (n, t) => `${n} of ${t} match`,
    buys: "Buys the app recommended", bought: "you bought it", noJournal: "The app wasn't running this game: no card or buy journal.",
    deaths: "DEATHS", deathsSub: (n, g) => g != null ? `${n} deaths · average unspent gold on death: ${num(g)}` : `${n} deaths`, highGold: "1,300+ unspent gold",
    tips: "NEXT TIME", teams: "ALL 10 PLAYERS", ally: "YOUR TEAM", enemy: "ENEMIES", kills: k => `${k} kills`, mvp: "MVP",
    multi: "MULTIKILLS", spree: n => `best spree ${n}`, seeGame: "See the game ›", names: ["MVP", "Great", "Good", "Average", "Rough"],
  },
};
const g_ = (k, ...a) => { const v = GT[lang]?.[k] ?? GT.es[k] ?? k; return typeof v === "function" ? v(...a) : v; };

let gamesData = null, gameOpen = null, gameDetail = null, captureNote = null;

function ago(ts) {
  const m = Math.round((Date.now() - ts) / 60000);
  if (m < 60) return lang === "es" ? `hace ${Math.max(1, m)} min` : `${Math.max(1, m)} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return lang === "es" ? `hace ${h} h` : `${h} h ago`;
  return when(ts);
}
const scoreName = s => GT[lang]?.names?.[s >= 80 ? 0 : s >= 65 ? 1 : s >= 50 ? 2 : s >= 35 ? 3 : 4] ?? "";
const scoreClass = s => s >= 80 ? "sc-mvp" : s >= 65 ? "sc-hi" : s >= 50 ? "sc-mid" : s >= 35 ? "sc-lo" : "sc-bad";
const ring = (s, label, sm) => s == null ? "" :
  `<div class="ring ${sm ? "sm " : ""}${scoreClass(s)}" style="--v:${Math.max(2, s)}"><b>${dec(s, sm ? 0 : 1)}</b>${label ? `<small>${esc(label)}</small>` : ""}</div>`;
const rarColor = r => r === "kPrismatic" ? "#c29bff" : r === "kGold" ? "#f0d080" : r === "kSilver" ? "#c9d1da" : "#666";
const rk = r => r ? `<span class="rk r${r}">#${r}</span>` : "";
const k1 = v => v >= 10000 ? `${dec(v / 1000, 1)}k` : num(v);

function openGame(id) {
  showTab("games");
  gameOpen = id; gameDetail = null; captureNote = null;
  renderGames();
  send({ type: "game", id });
}

function renderGames() {
  const root = document.getElementById("gamesBody");
  if (!root) return;
  document.getElementById("gamesTitle").textContent = g_("title");
  if (gameOpen) { root.innerHTML = renderDetail(); bindDetail(); return; }
  if (!gamesData) { root.innerHTML = `<div class="empty">${g_("loading")}</div>`; return; }
  root.innerHTML = renderToday(gamesData.today) + `<div class="card"><div class="lbl">${g_("list")}<em>${gamesData.games.length ? t("last", gamesData.games.length) : ""}</em></div>
    ${gamesData.games.length ? `<div class="glist">${gamesData.games.map(renderRow).join("")}</div>` : `<div class="empty">${g_("noGames")}</div>`}</div>`;
  root.querySelectorAll("[data-game]").forEach(el => el.onclick = () => openGame(el.dataset.game));
}

function renderToday(s) {
  if (!s) return `<div class="card"><div class="lbl">${g_("today")}</div><div class="empty">${g_("noToday")}</div></div>`;
  const multis = [["double", s.multi.doubles], ["triple", s.multi.triples], ["quadra", s.multi.quadras], ["penta", s.multi.pentas]].filter(m => m[1] > 0);
  return `<div class="card"><div class="lbl">${g_("today")}<em>${new Date().toLocaleDateString(lang, { weekday: "long", day: "numeric", month: "long" })}</em></div>
    <div class="today">
      <div class="rec"><div class="wl"><span class="w">${s.wins}V</span> <span class="l">${s.losses}D</span></div>
        <div class="wrbar"><i style="width:${s.winRate * 100}%"></i></div>
        <div class="wr ${s.winRate >= .5 ? "up" : "down"}">${pct(s.winRate)} <span>${g_("wr")}</span></div><span>${g_("played", num(s.minutes))}</span></div>
      <div>
        <div class="tgrid">
          <div class="tile2"><b>${dec(s.kda, 2)}</b><span>${g_("kda")}</span><small class="gold">${dec(s.k)} / ${dec(s.d)} / ${dec(s.a)}</small></div>
          <div class="tile2"><b>${k1(s.dmg)}</b><span>${g_("dmg")}</span></div>
          <div class="tile2" style="display:flex;align-items:center;gap:12px" title="${g_("scoreSub")}">${ring(s.score, null, true)}<div><b style="font-size:15px">${s.score != null ? esc(scoreName(s.score)) : "–"}</b><span>${g_("score")}</span></div></div>
        </div>
        ${multis.length ? `<div class="multis">${multis.map(([k, n]) => `<span class="mk ${k === "penta" ? "p" : ""}">${g_(k)} ×${n}</span>`).join("")}</div>` : ""}
        <div class="champsToday">${s.champions.map(c => `<div title="${esc(c.name)}: ${c.wins}V ${c.games - c.wins}D"><img src="${esc(c.icon)}"><em class="${c.wins * 2 >= c.games ? "up" : "down"}">${c.wins}-${c.games - c.wins}</em></div>`).join("")}</div>
      </div>
      ${s.best ? `<div class="bestg" data-game="${esc(s.best.id)}"><div class="h">★ ${g_("best")}</div>
        <div class="row"><img src="${esc(s.best.icon)}"><div class="t"><b>${esc(s.best.champion)}</b><span>${s.best.k} / ${s.best.d} / ${s.best.a} · ${esc(scoreName(s.best.score))}</span></div>${ring(s.best.score, null, true)}</div>
        ${s.best.augs?.length ? `<div class="augs2">${s.best.augs.map(a => `<img src="${esc(a.icon)}" title="${esc(a.name)}">`).join("")}</div>` : ""}
        <div class="go">${g_("seeGame")}</div></div>` : "<div></div>"}
    </div></div>`;
}

function renderRow(g) {
  const bars = g.hasReport ? `<div class="mbars">
      <div class="mbar"><span>${g_("dealt")}</span><div class="tr"><i style="width:${Math.min(100, (11 - g.dmgRank) * 10)}%"></i></div><b>${k1(g.dmg)}</b>${rk(g.dmgRank)}</div>
      <div class="mbar"><span>${g_("tanked")}</span><div class="tr"><i style="width:${Math.min(100, (11 - g.takenRank) * 10)}%"></i></div><b>${k1(g.taken)}</b>${rk(g.takenRank)}</div>
      <div class="mbar"><span>${g_("healed")}</span><div class="tr"><i style="width:${Math.min(100, (11 - g.healRank) * 10)}%"></i></div><b>${k1(g.heal)}</b>${rk(g.healRank)}</div></div>`
    : `<div class="nojournal">${g_("noReport")}</div>`;
  return `<div class="grow ${g.win ? "win" : "loss"}" data-game="${esc(g.id)}"><i class="bar"></i><img class="ch" src="${esc(g.champion.icon)}">
    <div class="res"><b class="${g.win ? "up" : "down"}">${g_(g.win ? "win" : "loss")}</b><span>${esc(g.champion.name)} · ${g_("min", g.minutes)}</span></div>
    <div class="kd"><b>${g.k} / <span class="down">${g.d}</span> / ${g.a}</b><span>KDA ${dec(g.kda, 2)}</span></div>
    <div>${ring(g.score, null, true)}</div>
    ${bars}
    <div class="ico2"><div class="icons ai">${g.augs.map(a => `<img src="${esc(a.icon)}" title="${esc(a.name)}">`).join("")}</div>
      <div class="icons">${g.items.map(i => `<img src="${esc(i.icon)}" title="${esc(i.name)}">`).join("")}</div></div>
    <div class="ago">${g_("ago", g.date)}${g.players ? `<div class="pl" style="margin-top:6px;justify-content:end">${g.players.map(p => `<img class="${p.me ? "me" : ""}" src="${esc(p.icon)}" title="${esc(p.name)}">`).join("")}</div>` : ""}</div>
  </div>`;
}

function renderDetail() {
  const back = `<button class="back" data-back>${g_("back")}</button>`;
  if (!gameDetail) return `${back}<div class="empty">${g_("loading")}</div>`;
  const d = gameDetail.data;
  if (!d) return `${back}<div class="empty">${g_("missing")}</div>`;
  const pm = (key, label) => { const s = d.stats.find(x => x.key === key); return s ? `<div class="pm"><span>${label}</span><b>${num(s.perMin)}<small>/min</small></b><div class="tr"><i style="width:${s.max ? Math.round(s.value / s.max * 100) : 0}%"></i></div></div>` : ""; };
  const share = `<div class="share ${d.win ? "win" : "loss"}" id="shareCard">${d.champion.splash ? `<img class="bg" src="${esc(d.champion.splash)}" onerror="this.remove()">` : ""}
    <div class="in">
      <div class="who"><div class="por"><img src="${esc(d.champion.icon)}"><em>${g_("level", d.level)}</em></div>
        ${d.spells.length ? `<div class="spells">${d.spells.map(s => `<img src="${esc(s)}">`).join("")}</div>` : ""}
        <div><div class="res">${g_(d.win ? "victory" : "defeat")} · ${esc(t("mode").toUpperCase())}</div><h2>${esc(d.champion.name)}</h2>
          <div class="meta">${when(d.date)} · ${g_("min", d.minutes)} · ${g_("kp", pct(d.kp))}</div>
          <div class="kda">${d.k} / <span class="dd">${d.d}</span> / ${d.a}<small>KDA ${dec(d.kda, 2)}</small></div></div></div>
      <div class="mid">${pm("dmg", g_("dpm"))}${pm("tank", g_("dtpm"))}${pm("heal", g_("hpm"))}${pm("gold", g_("gpm"))}</div>
      <div class="right">${ring(d.score, d.place === 1 ? g_("mvp") : d.scoreLabel)}<div class="place">${g_("place", d.place)}</div></div>
    </div>
    <div class="augrow">${d.augments.map(a => `<div class="augpill" style="--rc:${rarColor(a.rarity)}"><img src="${esc(a.icon)}">${tierChip(a.grade)}<b>${esc(a.name)}</b></div>`).join("")}</div>
    <div class="brandmark"><span class="hex">M</span>MAYHEM ADVISOR</div>
  </div>`;
  const actions = `<div class="actions"><button class="btn" data-capture="copy">${g_("copy")}</button><button class="btn ghost" data-capture="save">${g_("save")}</button>
    ${captureNote ? `<span class="done ${captureNote.path ? "link" : ""}" ${captureNote.path ? "data-openfile" : ""}>${esc(captureNote.text)}</span>` : ""}</div>`;

  const multis = [["double", d.multi.doubles], ["triple", d.multi.triples], ["quadra", d.multi.quadras], ["penta", d.multi.pentas]].filter(m => m[1] > 0);
  const perf = `<div class="card perf"><div class="lbl">${g_("perf")}<em>${esc(d.scoreLabel)} · ${dec(d.score, 1)}/100</em></div>
    ${d.stats.map(s => `<div class="srow"><span>${esc(s.label)}</span><div class="tr"><i style="width:${s.max ? Math.round(s.value / s.max * 100) : 0}%"></i></div>
      <b>${num(s.value)}</b><em>${s.perMin != null ? g_("perMin", num(s.perMin)) : ""}</em>${rk(s.rank)}</div>`).join("")}
    ${multis.length || d.multi.spree > 2 ? `<div class="multis">${multis.map(([k, n]) => `<span class="mk ${k === "penta" ? "p" : ""}">${g_(k)} ×${n}</span>`).join("")}${d.multi.spree > 2 ? `<span class="mk">${g_("spree", d.multi.spree)}</span>` : ""}</div>` : ""}
    <div class="lbl" style="margin:16px 0 0">${g_("scoreParts")}</div>
    <div class="brk">${d.breakdown.map(b => `<div><b class="${b.rank <= 3 ? "gold" : b.rank >= 8 ? "down" : ""}">#${b.rank}</b><span>${esc(b.label)}</span></div>`).join("")}</div></div>`;

  const augs = `<div class="card"><div class="lbl">${g_("augs")}<em>${g_("augsAvg", d.augGrade)}</em></div>
    ${d.augments.map(a => `<div class="augd"><img src="${esc(a.icon)}" style="--rc:${rarColor(a.rarity)}"><div class="t">
      <div class="h"><b>${esc(a.name)}</b>${tierChip(a.grade)}<span class="score">${dec(a.score, 1)}/100 · ${g_(a.atPick ? "atPick" : "now")}</span>${a.minute != null ? `<span class="muted" style="font-size:12px">${g_("minute", a.minute)}</span>` : ""}</div>
      ${a.games > 0 ? `<div class="sub">${g_("basedOn", num(a.games), a.confidence)}</div>` : ""}
      ${a.offered?.length ? `<div class="alts"><span class="muted" style="font-size:12px">${g_("offered")}</span>${a.offered.map(o => `<span class="alt"><img src="${esc(o.icon)}">${tierChip(o.grade)}${esc(o.name)}</span>`).join("")}</div>
        <div class="verdict ${a.best ? "ok" : "bad"}">${a.best ? g_("bestPick") : g_("betterWas", esc(a.offered.reduce((x, y) => y.score > x.score ? y : x).name))}</div>` : ""}
      ${!a.seen ? `<div class="sub">${g_("notSeen")}</div>` : ""}
    </div></div>`).join("")}</div>`;

  const build = `<div class="card"><div class="lbl">${g_("build")}${d.buildMatch != null ? `<em>${g_("match", d.buildMatch, d.recommended.length)}</em>` : ""}</div>
    <div class="muted" style="font-size:12px;margin-bottom:6px">${g_("yourItems")}</div><div class="bitems">${d.items.map(i => `<img src="${esc(i.icon)}" title="${esc(i.name)}">`).join("")}</div>
    ${d.recommended.length ? `<div class="muted" style="font-size:12px;margin:12px 0 6px">${g_("appBuild")}</div><div class="bitems">${d.recommended.map(r => `<img class="${r.owned ? "" : "no"}" src="${esc(r.item.icon)}" title="${esc(r.item.name)}">`).join("")}</div>` : ""}
    ${d.buys.length ? `<div class="muted" style="font-size:12px;margin-top:14px">${g_("buys")}</div><div class="btl">${d.buys.map(b => `<div class="b"><em>${g_("minute", b.min)}</em><img src="${esc(b.item.icon)}">
      <span title="${esc(b.why)}">${esc(b.item.name)}${b.why ? ` <small>· ${esc(b.why)}</small>` : ""}</span>${b.bought ? `<b class="up" title="${g_("bought")}">✓</b>` : "<span></span>"}</div>`).join("")}</div>` : ""}
    ${!d.hasJournal ? `<div class="nojournal" style="margin-top:12px">${g_("noJournal")}</div>` : ""}</div>`;

  const maxGold = Math.max(1300, ...d.deaths.list.map(x => x.gold));
  const deaths = `<div class="card"><div class="lbl">${g_("deaths")}<em>${g_("deathsSub", d.deaths.count, d.deaths.avgGold)}</em></div>
    ${d.deaths.list.length ? `<div class="deaths">${d.deaths.list.map(x => `<div title="${num(x.gold)}"><i class="${x.gold >= 1300 ? "hi" : ""}" style="height:${Math.max(6, x.gold / maxGold * 60)}px"></i><span>${dec(x.min, 0)}'</span></div>`).join("")}</div>
      <div class="muted" style="font-size:11.5px;margin-top:6px"><span class="mk" style="padding:1px 7px;font-size:10.5px">■</span> ${g_("highGold")}</div>` : ""}</div>`;

  const tips = d.tips.length ? `<div class="card tip tips2"><div class="lbl gold">${g_("tips")}</div>${d.tips.map(x => `<p>${esc(x)}</p>`).join("")}</div>` : "";

  const team = (tm, mine) => `<div class="team"><div class="th"><span class="${tm.win ? "w" : "l"}">${g_(mine ? "ally" : "enemy")} · ${g_(tm.win ? "victory" : "defeat")}</span><span>${g_("kills", tm.kills)} · ${k1(tm.dmg)}</span></div>
    ${tm.players.map(p => { const maxD = Math.max(...d.teams.flatMap(x => x.players.map(y => y.dmg)), 1);
      return `<div class="prow ${p.me ? "me" : ""}"><img src="${esc(p.icon)}" title="${esc(p.champion)}">
        <div class="n"><b>${esc(p.player || p.champion)}${p.mvp ? `<span class="crown">♛ ${g_("mvp")}</span>` : ""}</b><span>${esc(p.champion)} · ${g_("level", p.level)}</span></div>
        <div class="k">${p.k}/${p.d}/${p.a}<small>KDA ${dec(p.kda, 1)}</small></div>
        <div class="d"><span>${k1(p.dmg)}</span><div class="tr"><i style="width:${Math.round(p.dmg / maxD * 100)}%"></i></div></div>
        <div>${ring(p.score, null, true)}</div>
        <div><div class="icons ai">${p.augs.map(a => `<img src="${esc(a.icon)}" title="${esc(a.name)}">`).join("")}</div>
          <div class="icons" style="margin-top:3px">${p.items.map(i => `<img src="${esc(i.icon)}" title="${esc(i.name)}">`).join("")}</div></div></div>`; }).join("")}</div>`;
  const teams = `<div class="card"><div class="lbl">${g_("teams")}</div>${team(d.teams[0], true)}${team(d.teams[1], false)}</div>`;

  return `<div class="gd">${back}${share}${actions}<div class="gcols"><div class="gcol">${perf}${augs}</div><div class="gcol">${tips}${build}${deaths}</div></div>${teams}</div>`;
}

function bindDetail() {
  const root = document.getElementById("gamesBody");
  root.querySelector("[data-back]")?.addEventListener("click", () => { gameOpen = null; gameDetail = null; renderGames(); send({ type: "games" }); });
  root.querySelectorAll("[data-capture]").forEach(b => b.onclick = () => capture(b.dataset.capture === "copy"));
  root.querySelector("[data-openfile]")?.addEventListener("click", () => send({ type: "openFile", path: captureNote.path }));
}

// La tarjeta se captura tal como se ve: primero se lleva arriba del todo para que salga entera.
function capture(copy) {
  const card = document.getElementById("shareCard");
  if (!card) return;
  card.scrollIntoView({ block: "start" });
  requestAnimationFrame(() => setTimeout(() => {
    const r = card.getBoundingClientRect();
    const d = gameDetail?.data;
    send({ type: "capture", copy, rect: { x: r.left, y: r.top, w: r.width, h: r.height, vw: window.innerWidth },
           name: d ? `${d.champion.name} ${d.k}-${d.d}-${d.a}` : (lang === "en" ? "game" : "partida") });
    captureNote = { pending: copy };
  }, 120));
}

window.chrome.webview.addEventListener("message", e => {
  const m = e.data;
  if (m.type === "games") { gamesData = m.data; if (!gameOpen) renderGames(); }
  if (m.type === "game" && m.id === gameOpen) { gameDetail = m; renderGames(); }
  // Cambio de idioma (llega "init" otra vez): los textos de las partidas vienen del programa, se piden de nuevo.
  if (m.type === "init" && (gamesData || gameOpen)) { if (gameOpen) send({ type: "game", id: gameOpen }); else send({ type: "games" }); }
  if (m.type === "captured") {
    captureNote = m.path ? { text: g_(captureNote?.pending ? "copied" : "saved"), path: m.path } : { text: g_("failed") };
    if (gameOpen) renderGames();
  }
});
