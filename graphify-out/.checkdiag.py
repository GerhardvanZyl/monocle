import sys, xml.etree.ElementTree as ET

def load(path):
    r = ET.parse(path).getroot()
    rects, edges = {}, []
    for c in r.iter('mxCell'):
        g = c.find('mxGeometry')
        if c.get('vertex') and g is not None:
            rects[c.get('id')] = (float(g.get('x')), float(g.get('y')),
                                  float(g.get('width')), float(g.get('height')))
        elif c.get('edge'):
            pts = []
            if g is not None:
                arr = g.find("Array[@as='points']")
                if arr is not None:
                    pts = [(float(p.get('x')), float(p.get('y'))) for p in arr]
            edges.append((c.get('id'), c.get('source'), c.get('target'),
                          dict(kv.split('=', 1) for kv in (c.get('style') or '').split(';') if '=' in kv), pts))
    return rects, edges

def contains(a, b):
    ax, ay, aw, ah = a; bx, by, bw, bh = b
    return ax <= bx and ay <= by and ax + aw >= bx + bw and ay + ah >= by + bh

def parent_of(rects, rid):
    for other, r in rects.items():
        if other != rid and contains(r, rects[rid]):
            return other
    return None

rects, edges = load(sys.argv[1])

# partial overlaps only - full containment is legitimate nesting
for a in rects:
    for b in rects:
        if a < b:
            ax, ay, aw, ah = rects[a]; bx, by, bw, bh = rects[b]
            if ax < bx + bw and ax + aw > bx and ay < by + bh and ay + ah > by:
                if not contains(rects[a], rects[b]) and not contains(rects[b], rects[a]):
                    print(f'PARTIAL SHAPE OVERLAP: {a} / {b}')

def anchor(rect, fx, fy):
    x, y, w, h = rect
    return (x + fx * w, y + fy * h)

routes = {}
for eid, src, tgt, st, pts in edges:
    if not all(k in st for k in ('exitX', 'exitY', 'entryX', 'entryY')):
        print(f'EDGE {eid}: no fixed exit/entry - drawio picks the route, cannot verify')
        continue
    chain = [anchor(rects[src], float(st['exitX']), float(st['exitY']))] + pts + \
            [anchor(rects[tgt], float(st['entryX']), float(st['entryY']))]
    segs, ok = [], True
    for a, b in zip(chain, chain[1:]):
        if abs(a[0] - b[0]) > 0.01 and abs(a[1] - b[1]) > 0.01:
            print(f'EDGE {eid}: NON-ORTHOGONAL {a} -> {b} (drawio will insert its own bend)')
            ok = False
        segs.append((a, b))
    if ok:
        routes[eid] = (segs, src, tgt)

def hits(a, b, rect, tol=0.5):
    rx, ry, rw, rh = rect
    x1, x2 = sorted((a[0], b[0])); y1, y2 = sorted((a[1], b[1]))
    return x1 < rx + rw - tol and x2 > rx + tol and y1 < ry + rh - tol and y2 > ry + tol

def cross(s1, s2, tol=0.5):
    (a1, b1), (a2, b2) = s1, s2
    h1, h2 = abs(a1[1] - b1[1]) < .01, abs(a2[1] - b2[1]) < .01
    if h1 == h2:
        return False
    if h2:
        (a1, b1), (a2, b2) = (a2, b2), (a1, b1)
    hx1, hx2 = sorted((a1[0], b1[0])); vy1, vy2 = sorted((a2[1], b2[1]))
    return hx1 + tol < a2[0] < hx2 - tol and vy1 + tol < a1[1] < vy2 - tol

sh = 0
for eid, (segs, src, tgt) in routes.items():
    allowed = {src, tgt}
    for p in (parent_of(rects, src), parent_of(rects, tgt)):
        if p:
            allowed.add(p)
    for a, b in segs:
        for rid, rect in rects.items():
            if rid in allowed:
                continue
            if hits(a, b, rect):
                print(f'LINE CROSSES SHAPE: {eid} {a}->{b} hits {rid}')
                sh += 1

xs = 0
ids = list(routes)
for i, e1 in enumerate(ids):
    for e2 in ids[i + 1:]:
        for s1 in routes[e1][0]:
            for s2 in routes[e2][0]:
                if cross(s1, s2):
                    print(f'LINE CROSSES LINE: {e1} x {e2} at {s1} / {s2}')
                    xs += 1

print(f'--- {len(rects)} shapes, {len(routes)}/{len(edges)} edges verifiable, {sh} shape crossings, {xs} line crossings')
