# ¿Se predice mejor el % de daño mágico del equipo enemigo pesando a cada enemigo por lo peligroso que es?
# Compara con el daño real (físico vs mágico a campeones) de cada equipo en tus partidas guardadas.
import json, io, os, sys, statistics
D = sys.argv[1]
gd = json.load(io.open(os.path.join(D, 'gamedata-v6.json'), encoding='utf-8-sig'))
items = {i['Id']: i for i in gd['Items']}
champs = {c['Alias'].lower(): c for c in gd['Champions']}
byid = {c['Id']: c['Alias'].lower() for c in gd['Champions']}
ROLE = {'mage': .85, 'support': .7, 'marksman': .1, 'assassin': .35, 'fighter': .2, 'tank': .5}

matches = [json.loads(l) for l in io.open(os.path.join(D, 'matches.jsonl'), encoding='utf-8') if l.strip()]
# Totales por campeón (para dejar fuera la propia partida al evaluar).
tot = {}
for m in matches:
    for p in m['p']:
        t = tot.setdefault(p['c'].lower(), [0, 0, 0, 0])
        t[0] += p.get('pd', 0); t[1] += p.get('md', 0); t[2] += 1; t[3] += p.get('pd', 0) + p.get('md', 0)
allavg = sum(v[3] for v in tot.values()) / sum(v[2] for v in tot.values())

def prior(c, p):
    t = tot.get(c, [0, 0, 0, 0])
    phys, mag, n = t[0] - p.get('pd', 0), t[1] - p.get('md', 0), t[2] - 1
    if n >= 3 and phys + mag > 0: return mag / (phys + mag)
    roles = champs.get(c, {}).get('Roles') or []
    return ROLE.get(roles[0] if roles else '', .5)

def dmg_potential(c, p):
    t = tot.get(c, [0, 0, 0, 0]); n = t[2] - 1
    s = t[3] - p.get('pd', 0) - p.get('md', 0)
    return (s + allavg * 3) / (n + 3)  # encogido hacia la media

def share(c, p):
    sh = prior(c, p)
    fin = [items[i] for i in p.get('i', []) if i in items and items[i]['IsFinal']]
    ap = sum('SpellDamage' in i['Categories'] for i in fin)
    ad = sum(('Damage' in i['Categories']) or ('CriticalStrike' in i['Categories']) for i in fin)
    if ap + ad > 0: sh = .5 * sh + .5 * ap / (ap + ad)
    return sh

def gold(p):
    return sum(items[i]['PriceTotal'] for i in p.get('i', []) if i in items and items[i]['IsFinal'])

variants = {
    'igual (ahora)': lambda c, p: 1.0,
    'por oro en objetos': lambda c, p: 0.5 + gold(p) / 10000,
    'por daño habitual del campeón': lambda c, p: dmg_potential(c, p) / allavg,
    'daño habitual × oro': lambda c, p: dmg_potential(c, p) / allavg * (0.5 + gold(p) / 10000),
    'raíz daño habitual': lambda c, p: (dmg_potential(c, p) / allavg) ** 0.5,
}
err = {k: [] for k in variants}; cls = {k: 0 for k in variants}; n = 0
for m in matches:
    for team in (100, 200):
        ps = [p for p in m['p'] if p['t'] == team]
        real_md = sum(p.get('md', 0) for p in ps); real = real_md / max(1, sum(p.get('pd', 0) + p.get('md', 0) for p in ps))
        n += 1
        for k, w in variants.items():
            ws = [w(p['c'].lower(), p) for p in ps]
            pred = sum(wi * share(p['c'].lower(), p) for wi, p in zip(ws, ps)) / sum(ws)
            err[k].append(abs(pred - real))
            # ¿acierta la clase (físico < 45 %, mixto, mágico > 55 %)?
            c1 = 0 if pred < .45 else 2 if pred > .55 else 1; c2 = 0 if real < .45 else 2 if real > .55 else 1
            cls[k] += c1 == c2
print(f'{n} equipos de {len(matches)} partidas')
for k in variants:
    e = err[k]
    print(f'{k:32s} error medio {statistics.mean(e)*100:5.2f} pts · mediana {statistics.median(e)*100:5.2f} · clase acertada {cls[k]/n*100:5.1f} %')

# Con muertes/asesinatos (solo las partidas completas guardadas): el "enemigo alimentado" de Hexsight.
fe, fb, fk, nn = [], [], [], 0
for f in os.listdir(os.path.join(D, 'partidas')):
    g = json.load(io.open(os.path.join(D, 'partidas', f), encoding='utf-8'))['game']
    for team in (100, 200):
        ps = [q for q in g['participants'] if q['teamId'] == team]
        st = [q['stats'] for q in ps]
        real = sum(s['magicDamageDealtToChampions'] for s in st) / max(1, sum(s['magicDamageDealtToChampions'] + s['physicalDamageDealtToChampions'] for s in st))
        rows = []
        for q, s in zip(ps, st):
            c = byid.get(q['championId'], '')
            p = {'i': [s[f'item{k}'] for k in range(6)], 'pd': 0, 'md': 0}
            rows.append((c, p, s))
        base = [share(c, p) for c, p, s in rows]
        fed = [1.5 if s['kills'] >= 6 and s['kills'] - s['deaths'] >= 5 else 1 for c, p, s in rows]
        kda = [0.5 + (s['kills'] + 0.5 * s['assists']) / max(1, sum(x['kills'] + 0.5 * x['assists'] for x in st) / 5) * 0.5 for c, p, s in rows]
        fe.append(abs(sum(base) / 5 - real))
        fb.append(abs(sum(b * w for b, w in zip(base, fed)) / sum(fed) - real))
        fk.append(abs(sum(b * w for b, w in zip(base, kda)) / sum(kda) - real))
        nn += 1
print(f'\nCon K/D/A ({nn} equipos): igual {statistics.mean(fe)*100:.2f} · alimentado ×1,5 (Hexsight) {statistics.mean(fb)*100:.2f} · por K+A/2 {statistics.mean(fk)*100:.2f}')

res = {}
for f in os.listdir(os.path.join(D, 'partidas')):
    g = json.load(io.open(os.path.join(D, 'partidas', f), encoding='utf-8'))['game']
    for team in (100, 200):
        ps = [q for q in g['participants'] if q['teamId'] == team]
        st = [q['stats'] for q in ps]
        real = sum(s['magicDamageDealtToChampions'] for s in st) / max(1, sum(s['magicDamageDealtToChampions'] + s['physicalDamageDealtToChampions'] for s in st))
        rows = [(byid.get(q['championId'], ''), {'i': [s[f'item{k}'] for k in range(6)], 'pd': 0, 'md': 0}, s) for q, s in zip(ps, st)]
        base = [share(c, p) for c, p, s in rows]
        for fw in (1.0, 1.5, 2.0):
            for gw in (0, 1):
                w = [(fw if s['kills'] >= 6 and s['kills'] - s['deaths'] >= 5 else 1) * ((0.5 + gold(p) / 10000) if gw else 1) for c, p, s in rows]
                res.setdefault((fw, gw), []).append(abs(sum(b * x for b, x in zip(base, w)) / sum(w) - real))
for k, v in sorted(res.items()): print(f'alimentado ×{k[0]} {"+ oro" if k[1] else "     "}: {statistics.mean(v)*100:.2f}')
