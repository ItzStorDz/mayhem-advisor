// Selección de campeón: tu campeón y los del banco con su rango y % de victorias en Mayhem (de campeones, que Riot sí
// permite), botón para coger uno del banco, peticiones de cambio de tus aliados, aviso de pantalla completa y si la build
// ya está en la tienda del juego. Los botones solo hacen algo cuando los pulsas tú.
const sideEl = document.getElementById('side');

// La ventana deja pasar el ratón al cliente de LoL salvo encima de un botón.
let overButton = false;
document.addEventListener('mousemove', e => {
  const over = !!e.target.closest('button');
  if (over !== overButton) { overButton = over; window.mayhem.setChampMouse?.(over); }
});
document.addEventListener('click', e => {
  const b = e.target.closest('button[data-act]');
  if (!b || b.disabled) return;
  b.disabled = true; b.classList.add('busy');
  setTimeout(() => { b.disabled = false; b.classList.remove('busy'); }, 3000); // si el cliente no lo hizo, se puede repetir
  const id = Number(b.dataset.id);
  if (b.dataset.act === 'swap') window.mayhem.send({ type: 'benchswap', championId: id });
  if (b.dataset.act === 'accept' || b.dataset.act === 'decline') window.mayhem.send({ type: 'trade', id, accept: b.dataset.act === 'accept' });
  if (b.dataset.act === 'borderless') window.mayhem.send({ type: 'borderless' });
});

window.mayhem.onData(message => {
  if (message.lang) LANG = message.lang;
  if (message.type !== 'live') return;
  const options = message.phase === 'champselect' ? message.champSelect ?? [] : [];
  if (options.length === 0) { sideEl.classList.add('hidden'); return; }
  const cs = message.champSelectInfo ?? {};

  const rate = o => o.mayhemWinRate ?? (o.games >= 3 ? o.winRate : null);
  const ranked = options.filter(o => rate(o) != null);
  const best = ranked.length ? ranked.reduce((a, b) => (rate(b) > rate(a) ? b : a)) : null;
  const sorted = [...options].sort((a, b) => (rate(b) ?? -1) - (rate(a) ?? -1));
  const dmg = o => o.damage === 'magic' ? 'AP' : o.damage === 'physical' ? 'AD' : o.damage === 'mixed' ? tr('mixedDmg') : null;

  const notes = [];
  for (const t of cs.trades ?? []) notes.push(`<div class="cs-note trade">${icon(t.icon, 'cs-ti')}<div class="cs-nt">${tr('tradeAsk', `<b>${esc(t.name)}</b>`)}
      ${t.tier ? `<span class="chip ${gradeClass(t.tier)}">${esc(t.tier)}</span>` : ''}${t.winRate != null ? ` <span class="cs-muted">${pct(t.winRate)}</span>` : ''}</div>
      <div class="cs-btns"><button class="cs-btn ok" data-act="accept" data-id="${t.id}">${tr('accept')}</button><button class="cs-btn" data-act="decline" data-id="${t.id}">${tr('decline')}</button></div></div>`);
  if (cs.fullscreen) notes.push(`<div class="cs-note warn"><div class="cs-nt">⚠ ${tr('fullscreen')}</div>
      <div class="cs-btns"><button class="cs-btn ok" data-act="borderless">${tr('toBorderless')}</button></div></div>`);
  if (cs.notice) notes.push(`<div class="cs-note info"><div class="cs-nt">${esc(cs.notice)}</div></div>`);

  // De qué hacen daño tus aliados: ayuda a ver si al equipo le falta AP o AD al coger del banco.
  const team = cs.team;
  const teamLine = team ? `<div class="cs-team">${tr('allies', team.physical, team.magic, team.mixed)}</div>` : '';
  const html = `<div class="cs-head"><div class="pn-logo">M</div><b>MAYHEM ADVISOR</b><span>${tr('selectTitle')}</span></div>
    ${notes.join('')}${teamLine}
    <div class="sec cs-sec">${tr('whoFits')}</div>
    <div class="cs-list">${sorted.map(o => {
      const wr = rate(o);
      const source = o.mayhemWinRate != null ? tr('mayhemGames', num(o.mayhemGames)) : o.games > 0 ? tr('yourGames', o.games) : tr('noData');
      const color = wr == null ? 'var(--muted)' : wr >= 0.52 ? 'var(--green)' : wr < 0.48 ? 'var(--red)' : 'var(--text)';
      return `<div class="champ cs-row ${o === best ? 'best' : o.isCurrent ? 'current' : ''}">
        <img src="${esc(o.icon)}" alt="">
        <div class="t">
          <div class="n">${o.tier ? `<span class="chip ${gradeClass(o.tier)}">${esc(o.tier)}</span>` : ''}<span class="cs-name">${esc(o.name)}</span>
            ${dmg(o) ? `<span class="cs-dmg ${o.damage}">${esc(dmg(o))}</span>` : ''}
            ${o === best ? `<span class="pill">${tr('bestShort')}</span>` : o.isCurrent ? `<span class="cs-you">${tr('you')}</span>` : ''}</div>
          <div class="r">${esc(source)}${o.mayhemWinRate != null && o.games > 0 ? ` · ${tr('youShort', Math.round(o.winRate * o.games), o.games)}` : ''}</div>
          ${o.spells ? `<div class="spells">${o.spells.icons.map(i => i ? `<img src="${esc(i)}" alt="">` : '').join('')}
            <b>${esc(o.spells.names.join(' + '))}</b></div>
            ${o.isCurrent || o === best ? `<div class="r why">${esc(o.spells.reason)}</div>` : ''}` : ''}
        </div>
        <div class="cs-side"><div class="wr" style="color:${color}">${pct(wr)}</div><small>${tr('wins')}</small>
          ${!o.isCurrent && o.championId ? `<button class="cs-btn ok sm" data-act="swap" data-id="${o.championId}">${tr('take')}</button>` : ''}</div>
      </div>`;
    }).join('')}</div>
    ${cs.itemSet ? `<div class="cs-foot">🛒 ${tr('inShop', esc(cs.itemSet))}</div>` : ''}`;

  if (sideEl.dataset.html !== html) { sideEl.innerHTML = html; sideEl.dataset.html = html; overButton = false; window.mayhem.setChampMouse?.(false); }
  sideEl.classList.remove('hidden');
});
