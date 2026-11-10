#!/usr/bin/env node
/**
 * ¿Están traducidos todos los textos que genera la app? Busca en src\ cada L("es", "en") / LF($"es", $"en"), los consejos
 * de ModeTips y las etiquetas de ItemBadges, y los compara con src\Core\LangTexts.cs (portugués, francés, alemán y
 * vietnamita). Dice qué textos faltan (salen en inglés en esos idiomas), cuáles sobran y si alguna traducción pierde o
 * cambia un hueco ({0}, {1:P0}…).
 *
 *   node herramientas/traducciones/comprobar.js
 *
 * Para un texto nuevo: añade a LangTexts.cs  ["texto en inglés con los huecos sin formato"] = ["pt", "fr", "de", "vi"],
 * con los mismos huecos y formatos que en inglés. Los textos con huecos se escriben con LF($"…", $"…"), no con L.
 */
const fs = require('fs'), path = require('path');
const { readStr, format } = require('./cs.js');
const BS = String.fromCharCode(92);
const root = path.join(__dirname, '..', '..');
const norm = s => s.replace(/\{(\d+)(,[^:}]*)?(:[^}]*)?\}/g, '{$1}');
const holes = s => [...s.matchAll(/\{(\d+)(?:,[^:}]*)?(:[^}]*)?\}/g)].map(m => m[1] + (m[2] || '')).sort().join(' ');

function csFiles(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e =>
    e.isDirectory() ? (['bin', 'obj'].includes(e.name) ? [] : csFiles(path.join(dir, e.name)))
      : e.name.endsWith('.cs') && e.name !== 'LangTexts.cs' && e.name !== 'Lang.cs' ? [path.join(dir, e.name)] : []);
}

// Textos en inglés que usa la app → dónde
const used = new Map();
let mixed = 0;
for (const file of csFiles(path.join(root, 'src'))) {
  const s = fs.readFileSync(file, 'utf8');
  const rel = path.relative(root, file);
  const pairs = [];
  for (const re of [/\bLF?\(\s*(?=[$@"])/g, /\b(?:Mode|App)\(\s*(?=")/g]) {
    let m;
    while ((m = re.exec(s))) {
      const a = readStr(s, m.index + m[0].length); if (!a) continue;
      let k = a.end; while (/\s/.test(s[k])) k++; if (s[k] !== ',') continue; k++; while (/\s/.test(s[k])) k++;
      const b = readStr(s, k); if (!b) continue;
      const isL = m[0].startsWith('L(') || m[0].startsWith('L (');
      if (isL && b.interp) { console.log(`USA LF: ${rel}:${s.slice(0, m.index).split('\n').length} tiene huecos y va con L (no se puede traducir)`); mixed++; }
      pairs.push([format(b), s.slice(0, m.index).split('\n').length]);
    }
  }
  for (const mm of s.matchAll(/\(i => [^\n]*, "([^"]+)", "([^"]+)"\),/g)) pairs.push([mm[2], s.slice(0, mm.index).split('\n').length]);
  for (const [en, line] of pairs) if (!used.has(norm(en))) used.set(norm(en), { en, where: `${rel}:${line}` });
}

// Lo que hay en LangTexts.cs
const lt = fs.readFileSync(path.join(root, 'src', 'Core', 'LangTexts.cs'), 'utf8');
const unq = q => q.slice(1, -1).replace(new RegExp(BS + BS + '(.)', 'g'), (_, c) => c === 'n' ? '\n' : c);
const table = new Map();
for (const m of lt.matchAll(/^\s*\[("(?:[^"\\]|\\.)*")\] = \[((?:"(?:[^"\\]|\\.)*",? ?)+)\],$/gm)) {
  const texts = [...m[2].matchAll(/"(?:[^"\\]|\\.)*"/g)].map(x => unq(x[0]));
  table.set(unq(m[1]), texts);
}

let missing = 0, bad = 0;
for (const [key, { en, where }] of used) {
  const t = table.get(key);
  if (!t) { console.log(`FALTA  ${where}  ${JSON.stringify(en)}`); missing++; continue; }
  t.forEach((x, j) => {
    const want = holes(en).split(' ').filter(h => x.includes('{' + h.split(':')[0])).join(' ');
    if (holes(x) !== want) { console.log(`HUECOS ${['pt', 'fr', 'de', 'vi'][j]}  ${JSON.stringify(en)} → ${JSON.stringify(x)}`); bad++; }
  });
}
const unused = [...table.keys()].filter(k => !used.has(k));
for (const k of unused) console.log(`SOBRA  ${JSON.stringify(k)}`);
console.log(`${used.size} textos en la app · ${table.size} traducidos · faltan ${missing} · huecos mal ${bad} · sobran ${unused.length} · con L en vez de LF ${mixed}`);
process.exit(missing || bad || mixed ? 1 : 0);
