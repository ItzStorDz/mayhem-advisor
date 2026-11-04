// Formulario de sugerencias (1.8.2). Lo usan la ventanita de después de la partida (sugerencias.html, modo "postgame")
// y la pestaña «Sugerencias» de la ventana principal (index.html, modo "app"). Habla con el programa (App.Feedback.cs):
// pide «feedbackInfo» (idioma y tu última partida), envía «feedback» y recibe «feedbackResult».
(() => {
  const TX = {
    es: {
      title: "¿Qué te parece Mayhem Advisor?",
      sub: "Tu opinión ayuda a mejorar la app. La lee su creador.",
      rate: "TU NOTA", rateOpt: "TU NOTA (OPCIONAL)", what: "¿SOBRE QUÉ?",
      stars: ["Muy mala", "Mala", "Normal", "Buena", "Muy buena"],
      kinds: { idea: "💡 Idea o mejora", bug: "🐞 Fallo", other: "💬 Otra cosa" },
      ph: {
        idea: "¿Qué añadirías o cambiarías? (opcional)",
        bug: "¿Qué pasó? ¿Con qué campeón, aumento u objeto?",
        other: "Escribe lo que quieras (opcional)",
      },
      phApp: { idea: "¿Qué añadirías o cambiarías?", bug: "¿Qué pasó? ¿Con qué campeón, aumento u objeto? ¿Qué esperabas?", other: "Escribe lo que quieras" },
      attach: (name, win) => `Adjuntar mi última partida (${name}${win == null ? "" : win ? ", victoria" : ", derrota"}): campeón, aumentos y objetos`,
      note: "Se envía sin tu nombre ni tu cuenta. No pongas contraseñas ni datos personales.",
      left: n => `Quedan ${n} letras`,
      send: "Enviar", sending: "Enviando…", later: "Ahora no", never: "No volver a preguntar",
      thanks: "¡Gracias! Me llega tal cual y lo leo.", queued: "¡Gracias! Lo guardé y se enviará en cuanto haya conexión.",
      error: "No se pudo enviar. Prueba otra vez.", again: "Enviar otra",
      needSome: "Pon una nota o escribe algo para enviar.",
    },
    en: {
      title: "What do you think of Mayhem Advisor?",
      sub: "Your feedback helps improve the app. Its creator reads every reply.",
      rate: "YOUR RATING", rateOpt: "YOUR RATING (OPTIONAL)", what: "ABOUT WHAT?",
      stars: ["Very bad", "Bad", "Okay", "Good", "Very good"],
      kinds: { idea: "💡 Idea or improvement", bug: "🐞 Bug", other: "💬 Something else" },
      ph: {
        idea: "What would you add or change? (optional)",
        bug: "What happened? With which champion, augment or item?",
        other: "Write anything you like (optional)",
      },
      phApp: { idea: "What would you add or change?", bug: "What happened? With which champion, augment or item? What did you expect?", other: "Write anything you like" },
      attach: (name, win) => `Attach my last game (${name}${win == null ? "" : win ? ", win" : ", loss"}): champion, augments and items`,
      note: "Sent without your name or account. Don't include passwords or personal details.",
      left: n => `${n} characters left`,
      send: "Send", sending: "Sending…", later: "Not now", never: "Don't ask again",
      thanks: "Thanks! It reaches me as is and I read it.", queued: "Thanks! Saved — it'll be sent as soon as you're online.",
      error: "Couldn't send it. Please try again.", again: "Send another",
      needSome: "Pick a rating or write something to send.",
    },
  };
  const MAX = 2000;
  const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
  const post = m => window.chrome?.webview?.postMessage(m);

  /**
   * @param root   elemento donde va el formulario
   * @param source "postgame" (ventanita: nota, «Ahora no», «No volver a preguntar») o "app" (pestaña de la ventana principal)
   * @param getLang idioma actual ("es" / "en")
   */
  window.FeedbackForm = function (root, source, getLang) {
    const st = { rating: 0, kind: "idea", text: "", attach: true, lastGame: null, state: "form", sent: false };
    const tx = () => TX[getLang()] ?? TX.es;
    const canSend = () => st.rating > 0 || st.text.trim().length >= 3;

    function render() {
      const t = tx(), prompt = source === "postgame";
      if (st.state === "done") {
        root.innerHTML = `<div class="fb fb-done"><div class="fb-check">✓</div><p>${esc(st.sent ? t.thanks : t.queued)}</p>
          ${prompt ? "" : `<button class="fb-btn ghost" data-fb="again">${esc(t.again)}</button>`}</div>`;
        return;
      }
      const ph = (prompt ? t.ph : t.phApp)[st.kind];
      root.innerHTML = `<div class="fb">
        ${prompt ? `<div class="fb-title">${esc(t.title)}</div><div class="fb-sub">${esc(t.sub)}</div>` : ""}
        <div class="fb-lbl">${esc(prompt ? t.rate : t.rateOpt)}</div>
        <div class="fb-stars">${[1, 2, 3, 4, 5].map(i => `<button data-star="${i}" class="${i <= st.rating ? "on" : ""}" title="${esc(t.stars[i - 1])}">★</button>`).join("")}
          <span class="fb-starname">${st.rating ? esc(t.stars[st.rating - 1]) : ""}</span></div>
        <div class="fb-lbl">${esc(t.what)}</div>
        <div class="fb-kinds">${Object.entries(t.kinds).map(([k, label]) => `<button data-kind="${k}" class="${k === st.kind ? "on" : ""}">${esc(label)}</button>`).join("")}</div>
        <textarea class="fb-text" maxlength="${MAX}" placeholder="${esc(ph)}" spellcheck="true"></textarea>
        <div class="fb-count"></div>
        ${st.lastGame ? `<label class="fb-attach"><input type="checkbox" ${st.attach ? "checked" : ""}><span>${esc(t.attach(st.lastGame.name, st.lastGame.win))}</span></label>` : ""}
        <div class="fb-note">${esc(t.note)}</div>
        <div class="fb-error"></div>
        <div class="fb-actions"><button class="fb-btn" data-fb="send">${esc(st.state === "sending" ? t.sending : t.send)}</button>
          ${prompt ? `<button class="fb-btn ghost" data-fb="later">${esc(t.later)}</button><span class="fb-grow"></span><button class="fb-never" data-fb="never">${esc(t.never)}</button>` : ""}</div>
      </div>`;
      const area = root.querySelector(".fb-text");
      area.value = st.text;
      area.oninput = () => { st.text = area.value; update(); };
      const box = root.querySelector(".fb-attach input");
      if (box) box.onchange = () => st.attach = box.checked;
      update();
    }

    // Lo que cambia mientras escribes (sin volver a dibujar el cuadro de texto: no se pierde el cursor).
    function update() {
      const t = tx();
      const btn = root.querySelector('[data-fb="send"]');
      if (btn) btn.disabled = st.state === "sending" || !canSend();
      const left = MAX - st.text.length;
      const count = root.querySelector(".fb-count");
      if (count) count.textContent = left <= 300 ? t.left(left) : "";
    }

    root.addEventListener("click", e => {
      const star = e.target.closest("[data-star]");
      if (star) { st.rating = +star.dataset.star === st.rating ? 0 : +star.dataset.star; render(); return; }
      const kind = e.target.closest("[data-kind]");
      if (kind) { st.kind = kind.dataset.kind; render(); root.querySelector(".fb-text")?.focus(); return; }
      const act = e.target.closest("[data-fb]")?.dataset.fb;
      if (act === "send") {
        if (!canSend()) { root.querySelector(".fb-error").textContent = tx().needSome; return; }
        st.state = "sending"; render();
        post({ type: "feedback", source, kind: st.kind, rating: st.rating || null, text: st.text.trim(), attach: !!(st.lastGame && st.attach) });
      }
      if (act === "later") post({ type: "close" });
      if (act === "never") post({ type: "never" });
      if (act === "again") { Object.assign(st, { rating: 0, kind: "idea", text: "", attach: true, state: "form" }); render(); post({ type: "feedbackInfo" }); }
    });
    // Pasar el ratón por las estrellas enciende las de antes (como en las tiendas de apps).
    root.addEventListener("mouseover", e => {
      const star = e.target.closest("[data-star]");
      root.querySelectorAll("[data-star]").forEach(s => s.classList.toggle("hl", !!star && +s.dataset.star <= +star.dataset.star));
    });
    root.addEventListener("mouseleave", () => root.querySelectorAll("[data-star]").forEach(s => s.classList.remove("hl")));

    function onMessage(m) {
      if (m.type === "feedbackInfo") {
        const before = JSON.stringify(st.lastGame);
        st.lastGame = m.lastGame ?? null;
        if (st.state === "form" && before !== JSON.stringify(st.lastGame)) render();
      }
      if (m.type === "feedbackResult") {
        if (m.ok) {
          st.state = "done"; st.sent = !!m.sent; render();
          if (source === "postgame") setTimeout(() => post({ type: "close" }), 2600);
        } else {
          st.state = "form"; render();
          root.querySelector(".fb-error").textContent = tx().error;
        }
      }
    }

    render();
    return { render, onMessage };
  };
})();
