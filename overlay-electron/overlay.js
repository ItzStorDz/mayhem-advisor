// Dibuja las cartas de aumento y el panel lateral encima del juego.
// La ventana ocupa exactamente el interior de la ventana del juego, así que una posición en píxeles de
// pantalla (como las que manda el programa en C#) se pasa a CSS restando la esquina del juego y
// dividiendo por la escala de Windows.

const cardsEl = document.getElementById('cards');
const sideEl = document.getElementById('side');
const toastEl = document.getElementById('toast');

/** id del aumento → { el, html } de las tarjetas que hay en pantalla. */
const shown = new Map();
let area = null;
let lastCards = [];
let toastTimer = 0;
let lastGame = null;
// Botones del panel: minimizado (se recuerda entre partidas) y qué sugerencia de compra se está viendo.
let minimized = false;
try { minimized = localStorage.getItem('panelMinimized') === '1'; } catch { }
let nextIndex = 0, nextFirstId = null;

window.mayhem.onData(message => {
  if (message.lang) LANG = message.lang;
  if (message.type === 'toast') return toast(message.text);
  if (message.type === 'click') return doAction(message.act, message.id); // clic que vio el programa en C#
  if (message.type === 'wheel') return wheel(message.delta); // rueda del ratón encima del panel (la ve el programa en C#)
  if (message.type === 'dragstart') return dragStart(); // arrastrar el panel por la cabecera (también lo ve el programa en C#)
  if (message.type === 'dragmove') return dragMove(message.dx, message.dy);
  if (message.type === 'dragend') return dragEnd();
  if (message.type === 'dragreset') return dragReset();
  if (message.type === 'resizestart') return resizeStart(); // cambiar el tamaño con la esquina de abajo a la derecha
  if (message.type === 'resizemove') return resizeMove(message.dx, message.dy);
  if (message.type === 'resizeend') return resizeEnd();
  if (message.type === 'resizereset') return resizeReset();
  if (message.type !== 'live') return;
  area = message.area;
  const game = message.phase === 'ingame' ? message.game : null;
  renderCards(game?.cards ?? []);
  autoAugments = message.autoAugmentsTab !== false;
  if (!game) keptHere.clear(); // partida nueva: los "Me quedo con…" vuelven a empezar
  lastGame = message.panel !== false ? game : null;
  renderSide(lastGame);
});

// ───────── Cartas ─────────

function cardHtml(c) {
  const score = c.score ?? 50;
  const anvil = c.note != null;
  // Compacta: en el juego solo queda ~19 % del alto encima de las cartas, y no queremos tapar su icono.
  const items = c.goodItems?.length ? `<span class="mini">${c.goodItems.map(i => icon(i.icon, 'icon', i.name)).join('')}</span>` : '';
  const foot = anvil
    ? `<div class="why">${esc(c.note)}</div>`
    // Sin % de victorias del aumento (Riot no lo permite en overlays): partidas que respaldan la nota y su fiabilidad.
    // Con el nombre del campeón: la nota sale de sus partidas con ese aumento, no del tipo de build (Wakka, 2026-11-01).
    : `<div class="foot"><span class="stats">${c.global ? (lastGame?.championName ? tr('gamesWith', `~${num(c.games)}`, lastGame.championName) : tr('games', `~${num(c.games)}`)) : c.games > 0 ? tr('yours', c.games) : c.average ? tr('allChampsAvg') : tr('noData')}
        ${confBars(c.confidence)}${c.popularityLabel ? ` · <span class="pop p${c.popularity}">${esc(c.popularityLabel)}</span>` : ''}</span>${items}</div>`;
  const synergy = c.synergy ? `<div class="syn ${c.synergy.good ? 'good' : 'bad'}">${c.synergy.good ? '⚡' : '✗'} ${esc(c.synergy.reason)}</div>` : '';

  return `${c.best ? `<div class="ribbon">${tr('best')}</div>` : ''}
    <div class="head">
      <div class="emblem">${esc(c.grade)}</div>
      <div class="htext">
        <div class="name">${esc(c.name)}${c.lean ? ` <span class="lean">→ ${esc(c.lean)}</span>` : ''}</div>
        <div class="scoreRow"><span class="score">${score.toLocaleString(LANG, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}</span><span class="of">/100</span>
          <span class="label">${esc(anvil ? tr('anvil') : c.scoreLabel ?? '')}</span></div>
      </div>
    </div>
    <div class="meter"><i style="width:${Math.max(3, Math.min(100, score))}%"></i></div>
    ${foot}${synergy}`;
}

function renderCards(cards) {
  const ids = new Set(cards.map(c => c.id));
  for (const [id, entry] of shown) {
    if (ids.has(id)) continue;
    shown.delete(id);
    entry.el.style.opacity = '0';
    setTimeout(() => entry.el.remove(), 220);
  }

  for (const c of cards) {
    const html = cardHtml(c);
    let entry = shown.get(c.id);
    if (!entry) {
      const el = document.createElement('div');
      el.className = 'aug enter';
      el.addEventListener('animationend', e => { if (e.animationName === 'enter') el.classList.remove('enter'); });
      cardsEl.appendChild(el);
      entry = { el, html: '' };
      shown.set(c.id, entry);
    }
    if (entry.html !== html) { entry.el.innerHTML = html; entry.html = html; }
    entry.el.className = entry.el.className.replace(/\bg-\w+\b|\bbest\b/g, '').trim() + ` ${gradeClass(c.grade)}${c.best ? ' best' : ''}`;
  }
  lastCards = cards;
  place(cards);
}

const CARD_TOP_ABOVE_NAME = 0.273;

/** Coloca cada tarjeta encima de su carta del juego, con el ancho según la separación entre cartas. */
function place(cards) {
  if (!area || cards.length === 0) return;
  const dpr = window.devicePixelRatio || 1;
  const centers = cards.map(c => (c.box.x + c.box.w / 2 - area.x) / dpr);
  const sorted = [...centers].sort((a, b) => a - b);
  let gap = Infinity;
  for (let i = 1; i < sorted.length; i++) gap = Math.min(gap, sorted[i] - sorted[i - 1]);
  const width = Math.round(Math.max(224, Math.min(300, Number.isFinite(gap) ? gap * 0.84 : 260)));

  cards.forEach((c, i) => {
    const el = shown.get(c.id)?.el;
    if (!el) return;
    el.style.width = width + 'px';
    // Medido en el juego: el borde de arriba de la carta está ~27 % del alto por encima del nombre.
    // La tarjeta termina justo ahí, sin tapar el icono de la carta.
    const nameTop = (c.box.y - area.y) / dpr;
    const anchor = nameTop - window.innerHeight * CARD_TOP_ABOVE_NAME - 4;
    el.style.left = Math.round(centers[i]) + 'px';
    el.style.top = Math.round(Math.max(8, anchor - el.offsetHeight)) + 'px';
  });
}

// ───────── Panel (uno solo, con pestañas Compra / Adaptar / Aumentos y scroll) ─────────
// Idea: un consejo claro a la vista y los detalles solo si los pides ("Ver detalles").

const TABS = [['compra', 'tabBuy'], ['adaptar', 'tabAdapt'], ['aumentos', 'tabAugs']];
let tab = 'compra';
let autoTab = null;           // pestaña a la que volver cuando se van las cartas (si saltamos solos a "Aumentos")
let details = false;          // "Ver detalles" / "Ver más" abierto
let scrollY = 0;
let hadBest = false;
let autoAugments = true; // Ajustes → «Saltar a Aumentos al salir las cartas»
let wasDead = false;
const keptHere = new Set(); // "Me quedo con…" pulsado: el botón se va al momento (sin esperar al programa)

function renderSide(game) {
  if (!game) { sideEl.classList.add('hidden'); reportButtons(); return; }

  // Con cartas de aumento en pantalla saltamos solos a "Aumentos"; al elegir, volvemos a donde estabas.
  const hasBest = !!game.bestAugments;
  if (hasBest && !hadBest && tab !== 'aumentos' && autoAugments) { autoTab = tab; tab = 'aumentos'; scrollY = 0; }
  if (!hasBest && hadBest && autoTab) { tab = autoTab; autoTab = null; scrollY = 0; }
  hadBest = hasBest;

  // Muerto = estás en la tienda: el panel se abre en «Compra» aunque lo tengas minimizado (no se guarda).
  if (game.dead && !wasDead) { tab = 'compra'; autoTab = null; details = false; scrollY = 0; }
  wasDead = !!game.dead;

  if (minimized && !game.dead) {
    return showSide(`<div class="pn-head"><span class="pn-logo">M</span><b>MAYHEM</b><span class="pn-champ">${esc(game.championName)}</span>
      <button class="btn-icon" data-act="restore" title="${tr('restore')}">▸</button></div>`, true);
  }

  const head = `<div class="pn-head" title="${tr('dragHint')}"><span class="pn-logo">M</span><b>MAYHEM</b><span class="pn-champ">${esc(game.championName)}</span>
    <button class="btn-icon" data-act="minimize" title="${tr('minimize')}">–</button></div>
    <div class="pn-tabs">${TABS.map(([id, label]) => `<button class="pn-tab${tab === id ? ' on' : ''}" data-act="tab" data-id="${id}">${tr(label)}</button>`).join('')}</div>`;
  const body = tab === 'adaptar' ? adaptTab(game) : tab === 'aumentos' ? augmentsTab(game) : buyTab(game);
  showSide(`${head}<div class="pn-body">${body}</div>
    <div class="pn-scroll"><button class="btn-icon" data-act="scroll" data-id="up" title="${tr('up')}">▲</button><button class="btn-icon" data-act="scroll" data-id="down" title="${tr('down')}">▼</button></div>
    <button class="pn-grip" data-act="resize" title="${tr('resizeHint')}"></button>`, false);
}

const moreBtn = (open, closed) => `<button class="pn-more" data-act="details">${details ? open : closed}</button>`;
const iconRow = slots => `<div class="pn-build">${slots.map(b =>
  `<span class="${b.owned ? 'own' : ''}${b.changed ? ' chg' : ''}" title="${esc(b.name)}">${icon(b.icon)}</span>`).join('')}</div>`;

function buyTab(game) {
  const parts = [];
  if (game.dead) parts.push(`<div class="pn-dead">${tr('dead', game.respawn)}</div>`);
  // Primero lo que compras (al morir hay pocos segundos de tienda: antes salía debajo del selector de build), luego el contexto.
  buyNowBlock(game, parts);
  // Momento de la partida y el dato del equipo enemigo que más condiciona la build.
  const facts = [game.gamePhase, game.enemyFact].filter(Boolean);
  if (facts.length) parts.push(`<div class="pn-facts">${facts.map(f => `<span>${esc(f)}</span>`).join('')}</div>`);
  // Selector de build (1.7.0): las builds que se juegan con tu campeón, la que sigue la app marcada (AUTO) y, con un clic,
  // la tuya fijada para el resto de la partida: todas las compras la siguen. Sin datos de builds, la dirección de antes.
  const styles = game.styles ?? [];
  const d = game.direction;
  if (styles.length) {
    const active = styles.find(s => s.active) ?? styles[0];
    const pc = x => Math.round(x * 100) + (LANG === 'en' ? '%' : ' %');
    parts.push(`<div class="pn-dir pn-style"><div class="pn-dir-h">🧭 ${tr('buildTitle')} <b>${esc(active.name)}</b>
        <span class="${active.locked ? 'pn-lock' : 'pn-auto'}">${active.locked ? '🔒 ' + tr('lockedTag') : tr('autoTag')}</span></div>
      <div class="pn-chips">${styles.map(s => `<button class="pn-chip${s.active ? ' on' : ''}${s.locked ? ' lock' : ''}" data-act="style" data-id="${esc(s.key)}"
          title="${esc(s.locked ? tr('unlockHint') : tr('lockHint', s.name))}"><b>${esc(s.name)}</b><small>${s.share > 0 ? pc(s.share) : tr('anyChamp')} · ${tr('chipWr', pc(s.winRate))}</small></button>`).join('')}</div>
      ${game.styleSource ? `<div class="pn-src">${esc(game.styleSource)}</div>` : ''}
      ${active.items?.length ? `<div class="pn-three">${active.items.map(i => `<div title="${esc(i.name)}">${icon(i.icon, 'icon')}</div>`).join('')}</div>` : ''}</div>`);
  } else if (d) parts.push(`<div class="pn-dir"><div class="pn-dir-h">🧭 ${tr('dirTitle')} <b>${esc(d.label)}</b></div>
    ${d.reason ? `<div class="pn-text">${esc(d.reason)}</div>` : ''}
    <div class="pn-three">${d.items.map(i => `<div title="${esc(i.name)}">${icon(i.icon, 'icon')}<small>${i.winRate != null ? Math.round(i.winRate * 100) + (LANG === 'en' ? '%' : ' %') : '–'}</small></div>`).join('')}</div>
    ${d.warning ? `<div class="pn-warn">⚠ ${esc(d.warning)}</div>` : ''}</div>`);

  parts.push(moreBtn(tr('hideDetails'), tr('showDetails')));
  if (details) {
    parts.push(`<div class="pn-lbl">${tr('enemyTeam')}</div><div class="pn-text">${esc(game.enemies)}</div>`);
    if (game.items?.length) parts.push(`<div class="pn-lbl">${game.inventoryFull ? tr('finish') : tr('items')}</div>${game.items.map(it => itemRow(it, false)).join('')}`);
    if (game.boots) parts.push(`<div class="pn-lbl">${tr('boots')}</div>${itemRow(game.boots, false)}`);
    if (game.augments?.length) parts.push(`<div class="pn-lbl">${tr('yourAugs')}</div><div class="augchips">${game.augments
      .map(a => `<span class="augchip">${icon(a.icon, '')}${esc(a.name)}</span>`).join('')}</div>`);
    if (game.skills) parts.push(`<div class="pn-lbl">${tr('skills')}</div><div class="pn-text">${tr('maxOrder', esc(game.skills.order.split('').join(' › ')))}</div>`);
  }
  return parts.join('');
}

/** Lo que compras ahora (con la pieza que ya puedes comprar si no te llega), qué conservar y la build completa. */
function buyNowBlock(game, parts) {
  const options = game.nextOptions?.length ? game.nextOptions : game.next ? [game.next] : [];
  if (options[0]?.id !== nextFirstId) { nextFirstId = options[0]?.id; nextIndex = 0; } // cambió la recomendación
  if (nextIndex >= options.length) nextIndex = 0;
  const next = options[nextIndex];
  if (next) {
    const ok = !next.missing;
    const why = next.reason ?? [...(game.items ?? []), game.boots].find(i => i?.id === next.id)?.reason;
    parts.push(`<div class="pn-lbl">${nextIndex === 0 ? tr('buyNow') : tr('other', nextIndex + 1, options.length)}</div>
      <div class="pn-big">${icon(next.icon, 'icon xl')}<div><div class="pn-name">${esc(next.name)}</div>
        <div class="${ok ? 'pn-ok' : 'pn-gold'}">${ok ? tr('affordable') : tr('missing', num(next.missing))}</div>
        ${next.badges?.length ? `<div class="pn-badges">${next.badges.map(b => `<span>${esc(b)}</span>`).join('')}</div>` : ''}</div></div>
      ${next.part ? `<div class="pn-part">${icon(next.part.icon, 'icon sm')}<span>${tr('canBuyPart', esc(next.part.name), num(next.part.cost))}</span></div>` : ''}
      ${why ? `<div class="pn-why">${esc(why)}</div>` : ''}
      ${options.length > 1 ? `<button class="pn-link" data-act="next">${nextIndex + 1 < options.length ? tr('otherOpt') : tr('backFirst')}</button>` : ''}`);
  }
  for (const b of (game.building ?? []).filter(b => !(game.kept ?? []).includes(b.id) && !keptHere.has(b.id)))
    parts.push(`<button class="pn-keep" data-act="keep" data-id="${b.id}">${tr('keep', esc(b.name))}</button>`);
  if (game.swap && !keptHere.has(game.swap.sell.id)) parts.push(`<div class="pn-alert">${tr('full', esc(game.swap.sell.name), esc(game.swap.buy.name))}
      <button class="pn-keep" data-act="keep" data-id="${game.swap.sell.id}">${tr('keepMine')}</button></div>`);
  const build = game.plan?.recommended ?? (game.fullBuild ?? []).map(b => ({ ...b.item, owned: b.owned }));
  if (build.length) parts.push(`<div class="pn-lbl">${tr('yourBuild')}</div>${iconRow(build)}`);
}

function adaptTab(game) {
  const plan = game.plan;
  if (!plan) return `<div class="pn-text">${tr('noEnemy')}</div>`;
  const magic = Math.round(plan.magicShare * 100);
  const sentence = magic >= 55 ? tr('magicMost', magic) : magic <= 45 ? tr('physMost', 100 - magic) : tr('mixed', magic, 100 - magic);
  const parts = [`<div class="pn-sentence">${sentence}</div>
    <div class="dmg"><i class="mag" style="width:${magic}%"></i><i class="phy" style="width:${100 - magic}%"></i></div>`];
  const first = plan.categories?.[0];
  if (first) parts.push(`<div class="pn-lbl">${tr('buyCat', esc(first.title))}</div><div class="pn-three">${first.items.slice(0, 3).map(i =>
    `<div title="${esc(i.name)}">${icon(i.icon, 'icon lg')}<small>${i.winRate != null ? Math.round(i.winRate * 100) + (LANG === 'en' ? '%' : ' %') : '–'}${i.augment ? ' ⚡' : ''}${i.owned ? ' ✓' : ''}</small></div>`).join('')}</div>`);
  if (plan.discards?.length) parts.push(`<div class="pn-lbl">${tr('avoid')}</div>${plan.discards.map(d =>
    `<div class="pn-avoid">${icon(d.icon, 'icon sm')}<span>${esc(d.name)}</span></div>`).join('')}`);

  parts.push(moreBtn(tr('showLess'), tr('showMore')));
  if (details) {
    parts.push(`<div class="pn-text">${plan.enemies.map(e => `<span class="${e.magic >= 0.6 ? 'mag-t' : e.magic <= 0.4 ? 'phy-t' : ''}">${esc(e.name)}</span>`).join(' · ')}</div>`);
    parts.push(`<div class="pn-lbl">${tr('situational')}</div>${iconRow(plan.situational ?? [])}`);
    for (const b of (plan.situational ?? []).filter(b => b.changed)) parts.push(`<div class="pn-text"><b class="chg-t">↻ ${esc(b.name)}</b>: ${esc(b.reason ?? '')}</div>`);
    for (const c of plan.categories ?? []) {
      parts.push(`<div class="pn-lbl">${esc(c.title)}</div><div class="pn-text">${esc(c.why)}</div>`);
      parts.push(c.items.map(i => `<div class="row">${icon(i.icon)}<div class="t"><div class="n">${esc(i.name)}${i.owned ? ' <span class="ok">✓</span>' : ''}</div>
        <div class="r">${i.winRate != null ? tr('wrGames', pct(i.winRate), num(i.games)) : tr('noChampData')}${i.augment ? ` · <span class="aug-t">⚡ ${esc(i.augment)}</span>` : ''}</div></div></div>`).join(''));
    }
    for (const d of plan.discards ?? []) parts.push(`<div class="pn-text"><b class="phy-t">✗ ${esc(d.name)}</b>: ${esc(d.reason)}</div>`);
  }
  return parts.join('');
}

function augmentsTab(game) {
  const best = game.bestAugments;
  if (!best) return `<div class="pn-text">${tr('augsWait')}</div>`;
  const anyOnScreen = best.list.some(a => a.onScreen);
  return `<div class="pn-lbl">${tr('bestOf', esc(best.rarity), esc(best.champion.toUpperCase()))}</div>
    ${best.list.map(a => `<div class="pn-aug${a.onScreen ? ' hit' : ''}">${icon(a.icon, 'icon lg round')}<div>
      <div class="pn-name sm">${esc(a.name)}</div><div class="pn-text"><span class="chip ${gradeClass(a.grade)}">${esc(a.grade)}</span> <b>${a.score.toLocaleString(LANG, { maximumFractionDigits: 1 })}</b>/100 · ${tr('games', num(a.games))} ${confBars(a.confidence)}</div>
      ${a.onScreen ? `<div class="pn-hit">${tr('gotIt')}</div>` : ''}</div></div>`).join('')}
    ${anyOnScreen ? '' : `<div class="pn-why">${tr('reroll')}</div>`}`;
}

function showSide(html, small) {
  if (sideEl.dataset.html !== html) {
    sideEl.innerHTML = html; sideEl.dataset.html = html;
    const body = sideEl.querySelector('.pn-body');
    if (body) body.scrollTop = scrollY;
  }
  sideEl.classList.toggle('mini', small);
  placeSide();
  sideEl.classList.remove('hidden');
  const body = sideEl.querySelector('.pn-body');
  sideEl.querySelector('.pn-scroll')?.classList.toggle('hidden', !body || body.scrollHeight <= body.clientHeight + 2);
  reportButtons();
}

// ───────── Mover el panel ─────────
// Se arrastra por la cabecera y el sitio se recuerda entre partidas (en proporción a la ventana del juego, por si
// cambias de resolución). Doble clic en la cabecera: vuelve a su sitio de siempre (derecha, al 16 % de alto).
let panelPos = null;
try { panelPos = JSON.parse(localStorage.getItem('panelPos') ?? 'null'); } catch { }
let drag = null;

function placeSide() {
  if (drag) return;
  applySize();
  // Con el panel más grande (scale) su tamaño en pantalla es el del rectángulo, no offsetWidth.
  const box = sideEl.getBoundingClientRect(), w = box.width, h = box.height;
  if (!panelPos) {
    sideEl.style.right = 'auto';
    sideEl.style.left = Math.round(Math.max(0, window.innerWidth - 14 - w)) + 'px';
    sideEl.style.top = Math.round(window.innerHeight * 0.16) + 'px';
    return;
  }
  const x = Math.min(Math.max(0, panelPos.x * window.innerWidth), Math.max(0, window.innerWidth - w));
  const y = Math.min(Math.max(0, panelPos.y * window.innerHeight), Math.max(0, window.innerHeight - Math.min(h, 60)));
  sideEl.style.right = 'auto';
  sideEl.style.left = Math.round(x) + 'px';
  sideEl.style.top = Math.round(y) + 'px';
}

// El arrastre lo ve el programa en C# (el juego se queda el ratón): nos manda cuánto se ha movido el cursor desde
// que pulsaste la cabecera, en píxeles de pantalla, y aquí se pasa a CSS con la escala de Windows.
function dragStart() {
  const r = sideEl.getBoundingClientRect();
  drag = { left: r.left, top: r.top, moved: false };
  window.mayhem.setDragging(true);
}
function dragMove(dx, dy) {
  if (!drag) return;
  drag.moved = true;
  const s = window.devicePixelRatio || 1;
  const x = Math.min(Math.max(0, drag.left + dx / s), window.innerWidth - sideEl.getBoundingClientRect().width);
  const y = Math.min(Math.max(0, drag.top + dy / s), window.innerHeight - 40);
  sideEl.style.right = 'auto';
  sideEl.style.left = Math.round(x) + 'px';
  sideEl.style.top = Math.round(y) + 'px';
}
function dragEnd() {
  if (!drag) return;
  const moved = drag.moved;
  drag = null;
  window.mayhem.setDragging(false);
  if (moved) {
    const r = sideEl.getBoundingClientRect();
    panelPos = { x: r.left / window.innerWidth, y: r.top / window.innerHeight };
    try { localStorage.setItem('panelPos', JSON.stringify(panelPos)); } catch { }
  }
  reportButtons();
}
function dragReset() {
  drag = null;
  window.mayhem.setDragging(false);
  panelPos = null;
  try { localStorage.removeItem('panelPos'); } catch { }
  placeSide();
  reportButtons();
}
window.addEventListener('resize', () => placeSide());

// ───────── Tamaño del panel ─────────
// La esquina de abajo a la izquierda (el panel suele estar pegado a la derecha, así crece hacia dentro de la pantalla):
// hacia la izquierda o la derecha lo hace más grande o más pequeño entero (letra incluida, 80-170 %), hacia arriba o
// abajo cambia cuánto se ve sin hacer scroll. El borde derecho no se mueve. Se recuerda entre partidas; doble clic:
// tamaño normal.
let panelSize = null; // { zoom, body } — body: alto de la zona de contenido, en proporción a la ventana del juego
try { panelSize = JSON.parse(localStorage.getItem('panelSize') ?? 'null'); } catch { }
let resizing = null;

function applySize() {
  const zoom = panelSize?.zoom ?? 1;
  sideEl.style.transformOrigin = 'top left';
  sideEl.style.scale = zoom === 1 ? '' : String(zoom);
  // El alto va en px "sin escalar": en pantalla se ve multiplicado por zoom.
  if (panelSize?.body) sideEl.style.setProperty('--pn-body-h', Math.round(panelSize.body * window.innerHeight / zoom) + 'px');
  else sideEl.style.removeProperty('--pn-body-h');
}
function resizeStart() {
  const body = sideEl.querySelector('.pn-body');
  const zoom = panelSize?.zoom ?? 1, width = sideEl.getBoundingClientRect().width;
  resizing = { zoom, width, base: width / zoom, right: sideEl.getBoundingClientRect().right,
    body: body ? body.getBoundingClientRect().height : window.innerHeight * 0.46, moved: false };
  window.mayhem.setDragging(true);
}
function resizeMove(dx, dy) {
  if (!resizing) return;
  resizing.moved = true;
  const s = window.devicePixelRatio || 1;
  const zoom = Math.min(1.7, Math.max(0.8, (resizing.width - dx / s) / resizing.base));
  const body = Math.min(0.85, Math.max(0.2, (resizing.body + dy / s) / window.innerHeight));
  panelSize = { zoom: Math.round(zoom * 100) / 100, body: Math.round(body * 1000) / 1000 };
  // Movido a mano: se mantiene su borde derecho (el de por defecto ya va pegado a la derecha).
  if (panelPos) panelPos = { ...panelPos, x: Math.max(0, resizing.right - resizing.base * panelSize.zoom) / window.innerWidth };
  placeSide();
  reportButtons();
}
function resizeEnd() {
  if (!resizing) return;
  const moved = resizing.moved;
  resizing = null;
  window.mayhem.setDragging(false);
  if (moved) try { localStorage.setItem('panelSize', JSON.stringify(panelSize)); if (panelPos) localStorage.setItem('panelPos', JSON.stringify(panelPos)); } catch { }
  placeSide();
  reportButtons();
}
function resizeReset() {
  resizing = null;
  window.mayhem.setDragging(false);
  panelSize = null;
  try { localStorage.removeItem('panelSize'); } catch { }
  placeSide();
  reportButtons();
}

// Botones: el overlay deja pasar los clics al juego; solo los captura con el ratón encima de un botón (o del panel,
// para que la rueda del ratón haga scroll). Electron mira el cursor; el programa en C# también vigila los clics
// por si el juego se los queda (pasa al empezar la partida) y nos los reenvía.
let lastRects = '';
function reportButtons() {
  const rect = el => { const r = el.getBoundingClientRect(); return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) }; };
  const visible = !sideEl.classList.contains('hidden');
  const rects = visible ? [...sideEl.querySelectorAll('button[data-act]')].map(b => ({ ...rect(b), act: b.dataset.act, id: b.dataset.id })) : [];
  const headEl = visible ? sideEl.querySelector('.pn-head') : null;
  if (headEl) rects.push({ ...rect(headEl), act: 'drag' }); // la cabecera: para arrastrar (después de sus botones)
  if (visible) rects.push({ ...rect(sideEl), act: 'panel' }); // el último: los botones de dentro van antes
  const key = JSON.stringify(rects);
  if (key !== lastRects) { lastRects = key; window.mayhem.setButtons(rects); }
}
window.addEventListener('resize', reportButtons);
sideEl.addEventListener('click', e => {
  const b = e.target.closest('button[data-act]');
  if (b) doAction(b.dataset.act, b.dataset.id);
});
// Rueda del ratón: 120 = un "clic" de rueda; positivo = hacia arriba. Si llega aquí directamente, también vale.
function wheel(delta) {
  const body = sideEl.querySelector('.pn-body');
  if (!body) return;
  body.scrollTop -= Math.round(delta / 120 * 70);
  scrollY = body.scrollTop;
}
sideEl.addEventListener('wheel', e => { e.preventDefault(); wheel(-e.deltaY / 100 * 120); }, { passive: false });
sideEl.addEventListener('scroll', e => { if (e.target.classList?.contains('pn-body')) scrollY = e.target.scrollTop; }, true);

// El mismo clic puede llegar dos veces (al overlay y reenviado por el programa en C#): el segundo se ignora.
const lastAction = new Map();
function doAction(act, id) {
  if (act === 'panel' || act === 'drag' || act === 'resize') return;
  const key = act + ':' + (id ?? '');
  const now = Date.now();
  if (now - (lastAction.get(key) ?? 0) < 400) return;
  lastAction.set(key, now);
  if (act === 'tab') { tab = id; autoTab = null; details = false; scrollY = 0; nextIndex = 0; }
  if (act === 'details') details = !details;
  if (act === 'scroll') {
    const body = sideEl.querySelector('.pn-body');
    if (body) { body.scrollTop += id === 'up' ? -140 : 140; scrollY = body.scrollTop; }
    return reportButtons();
  }
  if (act === 'minimize' || act === 'restore') {
    minimized = act === 'minimize';
    try { localStorage.setItem('panelMinimized', minimized ? '1' : '0'); } catch { }
  }
  if (act === 'next') nextIndex++;
  if (act === 'style') {
    // Se marca ya en el panel; la app recalcula las compras y manda la build nueva en un momento.
    const styles = lastGame?.styles ?? [];
    const chosen = styles.find(s => s.key === id);
    const unlock = chosen?.locked;
    if (!unlock) for (const s of styles) { s.active = s.key === id; s.locked = s.key === id; }
    else chosen.locked = false;
    window.mayhem.send({ type: 'style', key: id });
    toast(unlock ? tr('styleAuto') : tr('styleLocked', chosen?.name ?? id));
  }
  if (act === 'keep') {
    if (keptHere.has(Number(id))) return;
    keptHere.add(Number(id));
    window.mayhem.send({ type: 'keep', itemId: Number(id) });
    toast(tr('kept'));
  }
  renderSide(lastGame);
}

function itemRow(it, highlight) {
  return `<div class="row">${icon(it.icon)}<div class="t"><div class="n ${highlight ? 'hl' : ''}">${esc(it.name)}</div>
    ${it.badges?.length ? `<div class="pn-badges">${it.badges.map(b => `<span>${esc(b)}</span>`).join('')}</div>` : ''}
    <div class="r">${esc(it.reason)}</div></div></div>`;
}

// ───────── Aviso ─────────

function toast(text) {
  toastEl.textContent = text;
  toastEl.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toastEl.classList.add('hidden'), 5000);
}

// El juego cambió de tamaño (la ventana se ajusta sola): recolocamos las tarjetas.
window.addEventListener('resize', () => place(lastCards));
