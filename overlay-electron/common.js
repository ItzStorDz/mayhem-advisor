// Utilidades compartidas por el overlay y la ventana de selección de campeón.

// Idioma: lo manda el programa en C# en cada mensaje (Ajustes → Idioma). Los textos que vienen calculados
// (motivos, nombres de objetos y aumentos) ya llegan traducidos; aquí van las etiquetas fijas.
let LANG = 'es';
const OT = {
  es: {
    best: '★ NOTA MÁS ALTA', noData: 'sin datos', allChampsAvg: 'media de todos', yours: n => `${n} tuyas`, tabBuy: 'Compra', tabAdapt: 'Adaptar', tabAugs: 'Aumentos',
    buyNow: 'COMPRA AHORA', other: (i, n) => `OTRA OPCIÓN (${i}/${n})`, affordable: '¡Ya te alcanza!', missing: g => `Te faltan ${g} de oro`,
    otherOpt: 'Otra opción ›', backFirst: 'Volver a la primera ›', keep: n => `Me quedo con ${n}`, full: (a, b) => `Inventario lleno: vende <b>${a}</b> y compra <b>${b}</b>`,
    keepMine: 'Me quedo con mi objeto', yourBuild: 'TU BUILD', showDetails: 'Ver detalles ▾', hideDetails: 'Ocultar detalles ▴', enemyTeam: 'EQUIPO ENEMIGO',
    finish: 'TERMINAR LO QUE LLEVAS', items: 'OBJETOS', boots: 'BOTAS', yourAugs: 'TUS AUMENTOS', skills: 'HABILIDADES', maxOrder: o => `Maximiza <b>${o}</b>`,
    noEnemy: 'Aún no hay datos del equipo enemigo.', magicMost: m => `El <b class="mag-t">${m} % del daño</b> enemigo es <b class="mag-t">mágico</b>`,
    physMost: m => `El <b class="phy-t">${m} % del daño</b> enemigo es <b class="phy-t">físico</b>`, mixed: (a, b) => `Daño enemigo <b>mixto</b>: ${a} % mágico, ${b} % físico`,
    buyCat: c => `COMPRA ${c}`, avoid: 'EVITA', showMore: 'Ver más ▾', showLess: 'Ver menos ▴', situational: 'BUILD SITUACIONAL',
    wrGames: (w, g) => `${w} de victorias · ${g} partidas`, noChampData: 'sin datos con este campeón',
    augsWait: 'Cuando te salgan cartas de aumento verás aquí los mejores de esa rareza para tu campeón.', bestOf: (r, c) => `${r} CON MEJOR NOTA PARA ${c}`,
    gotIt: '¡Te ha salido!', reroll: 'Ninguno de estos está en tus cartas: si te queda re-roll, puede valer la pena.',
    kept: 'Hecho: no te diré que cambies ese objeto en esta partida', minimize: 'Minimizar', restore: 'Mostrar el panel', up: 'Subir', down: 'Bajar',
    dead: s => s > 0 ? `💀 Estás muerto (${s} s): es momento de comprar` : '💀 Estás muerto: es momento de comprar', dragHint: 'Arrastra para mover · doble clic: volver a su sitio', resizeHint: 'Arrastra para cambiar el tamaño · doble clic: tamaño normal',
    anvil: 'Yunque', dirTitle: 'TU ESTILO:', selectTitle: 'Selección de campeón', you: 'tuyo', whoFits: 'QUIÉN TE CONVIENE', bestShort: 'MEJOR', mayhemGames: n => `${n} partidas en Mayhem`, yourGames: n => `${n} partidas tuyas`, bestPick: '★ MEJOR OPCIÓN', games: n => `${n} partidas`, spells: 'HECHIZOS',
    tradeAsk: n => `Un aliado te ofrece ${n}`, accept: 'Aceptar', decline: 'Rechazar', wins: 'victorias', take: 'Coger',
    fullscreen: 'El juego está en pantalla completa: así el lector de cartas no ve nada.', toBorderless: 'Pasar a sin bordes',
    inShop: c => `Build de ${c} en la tienda del juego`, youShort: (w, g) => `tú ${w}/${g}`,
  },
  en: {
    best: '★ TOP SCORE', noData: 'no data', allChampsAvg: 'all-champ avg', yours: n => `${n} yours`, tabBuy: 'Buy', tabAdapt: 'Adapt', tabAugs: 'Augments',
    buyNow: 'BUY NOW', other: (i, n) => `OTHER OPTION (${i}/${n})`, affordable: 'You can afford it!', missing: g => `${g} gold to go`,
    otherOpt: 'Other option ›', backFirst: 'Back to the first ›', keep: n => `Keep building ${n}`, full: (a, b) => `Inventory full: sell <b>${a}</b> and buy <b>${b}</b>`,
    keepMine: 'Keep my item', yourBuild: 'YOUR BUILD', showDetails: 'Show details ▾', hideDetails: 'Hide details ▴', enemyTeam: 'ENEMY TEAM',
    finish: 'FINISH WHAT YOU HAVE', items: 'ITEMS', boots: 'BOOTS', yourAugs: 'YOUR AUGMENTS', skills: 'SKILLS', maxOrder: o => `Max <b>${o}</b>`,
    noEnemy: 'No enemy team data yet.', magicMost: m => `<b class="mag-t">${m}% of enemy damage</b> is <b class="mag-t">magic</b>`,
    physMost: m => `<b class="phy-t">${m}% of enemy damage</b> is <b class="phy-t">physical</b>`, mixed: (a, b) => `<b>Mixed</b> enemy damage: ${a}% magic, ${b}% physical`,
    buyCat: c => `BUY ${c}`, avoid: 'AVOID', showMore: 'Show more ▾', showLess: 'Show less ▴', situational: 'SITUATIONAL BUILD',
    wrGames: (w, g) => `${w} win rate · ${g} games`, noChampData: 'no data with this champion',
    augsWait: 'When augment cards show up you will see the best of that rarity for your champion here.', bestOf: (r, c) => `TOP-SCORED ${r} FOR ${c}`,
    gotIt: 'You got it!', reroll: 'None of these are in your cards: if you have a reroll left, it may be worth it.',
    kept: "Done: I won't tell you to change that item this game", minimize: 'Minimize', restore: 'Show the panel', up: 'Up', down: 'Down',
    dead: s => s > 0 ? `💀 You're dead (${s}s): time to shop` : "💀 You're dead: time to shop", dragHint: 'Drag to move · double-click: put it back', resizeHint: 'Drag to resize · double-click: normal size',
    anvil: 'Anvil', dirTitle: 'YOUR STYLE:', selectTitle: 'Champion select', you: 'yours', whoFits: 'WHO TO PICK', bestShort: 'BEST', mayhemGames: n => `${n} Mayhem games`, yourGames: n => `${n} of your games`, bestPick: '★ BEST PICK', games: n => `${n} games`, spells: 'SPELLS',
    tradeAsk: n => `A teammate offers you ${n}`, accept: 'Accept', decline: 'Decline', wins: 'win rate', take: 'Take',
    fullscreen: 'The game is in fullscreen: the card reader can’t see anything like that.', toBorderless: 'Switch to borderless',
    inShop: c => `${c} build in the in-game shop`, youShort: (w, g) => `you ${w}/${g}`,
  },
};
const tr = (k, ...a) => { const v = OT[LANG]?.[k] ?? OT.es[k] ?? k; return typeof v === 'function' ? v(...a) : v; };
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const pct = (v, digits = 1) => v == null ? '–' : (v * 100).toLocaleString(LANG, { minimumFractionDigits: digits, maximumFractionDigits: digits }) + (LANG === 'en' ? '%' : ' %');
const num = v => (v ?? 0).toLocaleString(LANG);
const signed = (v, digits = 1) => (v >= 0 ? '+' : '') + (v * 100).toLocaleString(LANG, { minimumFractionDigits: digits, maximumFractionDigits: digits });
const GRADES = ['OP', 'S', 'A', 'B', 'C', 'D'];
const gradeClass = g => 'g-' + (GRADES.includes(g) ? g : 'C');
// Fiabilidad de una nota en 3 rayitas (por las partidas que la respaldan).
const confBars = c => c ? `<span class="conf c${c}"><i></i><i></i><i></i></span>` : '';
const icon = (url, cls = 'icon', title = '') => url ? `<img class="${cls}" src="${esc(url)}" alt="" title="${esc(title)}">` : '';
