const BS = String.fromCharCode(92);
// Lee un literal de C# empezando en i ("…", $"…", @"…", $@"…"); devuelve {end, interp, verb, parts}
function readStr(s, i) {
  let interp = false, verb = false;
  while (s[i] === '$' || s[i] === '@') { if (s[i] === '$') interp = true; else verb = true; i++; }
  if (s[i] !== '"') return null;
  i++;
  const parts = []; let text = '';
  while (i < s.length) {
    const c = s[i];
    if (!verb && c === BS) { text += s.slice(i, i + 2); i += 2; continue; }
    if (c === '"') { if (verb && s[i + 1] === '"') { text += '""'; i += 2; continue; } i++; break; }
    if (interp && c === '{') {
      if (s[i + 1] === '{') { text += '{{'; i += 2; continue; }
      let j = i + 1, depth = 0, expr = '';
      while (j < s.length) {
        const d = s[j];
        if (d === '"' || ((d === '$' || d === '@') && (s[j + 1] === '"' || s[j + 1] === '$' || s[j + 1] === '@'))) {
          const r = readStr(s, j); expr += s.slice(j, r.end); j = r.end; continue;
        }
        if (d === "'") { const k = s.indexOf("'", j + (s[j + 1] === BS ? 3 : 2)); expr += s.slice(j, k + 1); j = k + 1; continue; }
        if (d === '(' || d === '[' || d === '{') { depth++; expr += d; j++; continue; }
        if (d === ')' || d === ']') { depth--; expr += d; j++; continue; }
        if (d === '}') { if (depth === 0) break; depth--; expr += d; j++; continue; }
        expr += d; j++;
      }
      // formato: el primer ':' a profundidad 0, fuera de cadenas, que no sea de un ternario ni de "::"
      let fmt = null, e = expr, dd = 0, q = false, tern = 0;
      for (let k = 0; k < expr.length; k++) {
        const d = expr[k];
        if (d === '"') q = !q;
        if (q) continue;
        if ('([{'.includes(d)) dd++;
        if (')]}'.includes(d)) dd--;
        if (dd !== 0) continue;
        if (d === '?' && expr[k + 1] !== '?' && expr[k - 1] !== '?' && expr[k + 1] !== '.' && expr[k + 1] !== '[') tern++;
        if (d === ':') { if (tern > 0) { tern--; continue; } fmt = expr.slice(k + 1); e = expr.slice(0, k); break; }
      }
      parts.push(text); text = '';
      parts.push({ expr: e, fmt });
      i = j + 1; continue;
    }
    if (interp && c === '}' && s[i + 1] === '}') { text += '}}'; i += 2; continue; }
    text += c; i++;
  }
  parts.push(text);
  return { end: i, interp, verb, parts };
}
function unescape(t, verb) {
  if (verb) return t.replace(/""/g, '"');
  let out = '';
  for (let i = 0; i < t.length; i++) {
    if (t[i] === BS) { const n = t[i + 1]; out += n === 'n' ? '\n' : n === 't' ? '\t' : n; i++; } else out += t[i];
  }
  return out;
}
// Lo que pondría el compilador en FormattableString.Format; los textos sin $ se quedan tal cual (sin {{ }}).
function format(r) {
  if (!r.interp) return unescape(r.parts.join(''), r.verb);
  let n = 0, out = '';
  for (const p of r.parts) out += typeof p === 'string' ? unescape(p, r.verb) : `{${n++}${p.fmt != null ? ':' + p.fmt : ''}}`;
  return out;
}
module.exports = { readStr, format, unescape };
