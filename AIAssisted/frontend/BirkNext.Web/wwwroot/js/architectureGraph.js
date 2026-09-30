// Source architecture graph: SVG, layered layout, pan (drag blank space), zoom (wheel), drag nodes, select nodes and edges (pointer or
// Enter/Space), highlight sets, fit, save/reset layout per source snapshot and view. Same technique as databaseDiagram.js, separate module:
// it knows nothing about tables. State is carried by line style and label text, never by colour alone.
const instances = new WeakMap();
const NS = 'http://www.w3.org/2000/svg';
const W = 216, H = 58;
function el(tag, attrs = {}, text) {
    const n = document.createElementNS(NS, tag);
    for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v));
    if (text !== undefined) n.textContent = text;
    return n;
}
const dash = state => state === 'Inferred' ? '8 5' : state === 'Unresolved' ? '2 4' : state === 'Conflict' ? '12 3 2 3' : '';
const cut = (s, n) => s.length > n ? s.slice(0, n - 1) + '…' : s;

function layout(nodes, edges) {
    // Longest-path layering (bounded, so cycles terminate), then order within a layer by first appearance.
    const rank = Object.fromEntries(nodes.map(n => [n.id, 0]));
    for (let pass = 0; pass < nodes.length; pass++) {
        let changed = false;
        for (const e of edges) if (rank[e.from] !== undefined && rank[e.to] !== undefined && rank[e.to] < rank[e.from] + 1 && rank[e.from] + 1 < nodes.length) { rank[e.to] = rank[e.from] + 1; changed = true; }
        if (!changed) break;
    }
    const layers = {};
    for (const n of nodes) (layers[rank[n.id]] ||= []).push(n.id);
    const positions = {};
    for (const [r, ids] of Object.entries(layers)) ids.forEach((id, i) => positions[id] = {x: Number(r) * (W + 90), y: i * (H + 46)});
    return positions;
}

export function render(host, nodes, edges, key, receiver) {
    const old = instances.get(host);
    let positions = old?.key === key ? old.positions : null;
    dispose(host);
    let restored = false;
    const initial = layout(nodes, edges);
    if (!positions) {
        positions = initial;
        try {
            const stored = JSON.parse(localStorage.getItem(key) || '{}');
            for (const n of nodes) { const p = stored[n.id]; if (p && Number.isFinite(p.x) && Number.isFinite(p.y) && Math.abs(p.x) < 100000 && Math.abs(p.y) < 100000) { positions[n.id] = p; restored = true; } }
        } catch { /* storage unavailable or corrupt: deterministic layout */ }
    }
    for (const n of nodes) positions[n.id] ||= initial[n.id];
    const svg = el('svg', {width: '100%', height: '100%', role: 'group', 'aria-roledescription': 'graph', 'aria-label': 'Source architecture graph'});
    svg.append(el('title', {}, 'Source architecture only. Solid: confirmed or strongly supported · Dashed: inferred · Dotted: unresolved · Dash-dot: conflict.'));
    const defs = el('defs');
    const marker = el('marker', {id: 'arch-arrow', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse'});
    marker.append(el('path', {d: 'M0,0 L10,5 L0,10 z', fill: '#44546b'}));
    defs.append(marker); svg.append(defs);
    const viewport = el('g'); svg.append(viewport); host.replaceChildren(svg);
    const s = {svg, viewport, positions, nodes, edges, key, receiver, x: 20, y: 20, zoom: 1, handlers: [], highlight: null, selected: null};
    instances.set(host, s);
    const transform = () => viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(${s.zoom})`);
    s.draw = () => {
        viewport.replaceChildren();
        const dim = id => s.highlight && !s.highlight.has(id);
        for (const e of edges) {
            const a = positions[e.from], b = positions[e.to]; if (!a || !b) continue;
            const x1 = a.x + W, y1 = a.y + H / 2, x2 = b.x, y2 = b.y + H / 2;
            const back = x2 <= x1 + 10;
            const d = back ? `M${a.x + W / 2},${a.y + H} C${a.x + W / 2},${a.y + H + 60} ${b.x + W / 2},${b.y + H + 60} ${b.x + W / 2},${b.y + H}` : `M${x1},${y1} C${x1 + 45},${y1} ${x2 - 45},${y2} ${x2},${y2}`;
            const g = el('g', {'data-edge-id': e.id, tabindex: 0, role: 'button', 'aria-label': `Open dependency ${e.label}`, opacity: dim(e.id) ? .18 : 1});
            g.append(el('path', {d, stroke: 'transparent', 'stroke-width': 14, fill: 'none'}));
            g.append(el('path', {d, stroke: s.selected === e.id ? '#1d4ed8' : '#44546b', 'stroke-width': s.selected === e.id ? 3.5 : 2, fill: 'none', 'stroke-dasharray': dash(e.state), 'marker-end': 'url(#arch-arrow)'}));
            const lx = back ? (a.x + b.x) / 2 + W / 2 : (x1 + x2) / 2, ly = back ? Math.max(a.y, b.y) + H + 48 : (y1 + y2) / 2 - 6;
            g.append(el('text', {x: lx, y: ly, 'text-anchor': 'middle', fill: '#22334b', 'font-size': 11, 'paint-order': 'stroke', stroke: '#f7f9fc', 'stroke-width': 4}, cut(e.label, 34)));
            g.append(el('title', {}, `${e.label} · ${e.state}`));
            g.addEventListener('keydown', ev => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); receiver.invokeMethodAsync('SelectEdge', e.id); } });
            viewport.append(g);
        }
        for (const n of nodes) {
            const p = positions[n.id];
            const g = el('g', {'data-node-id': n.id, 'data-kind': n.kind, transform: `translate(${p.x} ${p.y})`, tabindex: 0, role: 'button', 'aria-label': `Open ${n.name} (${n.subtitle}) details`, opacity: dim(n.id) ? .25 : 1});
            const rx = n.kind === 'channel' ? 26 : n.kind === 'datastore' ? 4 : 8;
            g.append(el('rect', {width: W, height: H, rx, fill: n.kind === 'unresolved' ? '#fff7ed' : n.kind === 'external' ? '#f1f5f9' : '#ffffff',
                stroke: s.selected === n.id ? '#1d4ed8' : '#59718c', 'stroke-width': s.selected === n.id ? 3.5 : 2, 'stroke-dasharray': n.kind === 'unresolved' ? '4 3' : ''}));
            g.append(el('text', {x: 12, y: 24, 'font-size': 14, 'font-weight': 600, fill: '#172d48'}, cut(n.name, 25)));
            g.append(el('text', {x: 12, y: 43, 'font-size': 11, fill: '#44546b'}, cut(n.subtitle, 34)));
            g.append(el('title', {}, `${n.name} — ${n.subtitle} · ${n.state}`));
            g.addEventListener('keydown', ev => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); receiver.invokeMethodAsync('SelectNode', n.id); } });
            viewport.append(g);
        }
    };
    let gesture;
    function down(e) {
        const node = e.target.closest('[data-node-id]'), edge = e.target.closest('[data-edge-id]');
        gesture = {id: node?.getAttribute('data-node-id'), edge: edge?.getAttribute('data-edge-id'), x: e.clientX, y: e.clientY, moved: false};
        svg.setPointerCapture(e.pointerId);
    }
    function move(e) {
        if (!gesture) return;
        const dx = e.clientX - gesture.x, dy = e.clientY - gesture.y;
        if (Math.abs(dx) + Math.abs(dy) > 2) gesture.moved = true;
        if (gesture.id) { const p = positions[gesture.id]; p.x += dx / s.zoom; p.y += dy / s.zoom; s.draw(); } else if (!gesture.edge) { s.x += dx; s.y += dy; transform(); }
        gesture.x = e.clientX; gesture.y = e.clientY;
    }
    function up(e) {
        if (gesture && !gesture.moved) { if (gesture.id) receiver.invokeMethodAsync('SelectNode', gesture.id); else if (gesture.edge) receiver.invokeMethodAsync('SelectEdge', gesture.edge); }
        gesture = null;
        if (svg.hasPointerCapture(e.pointerId)) svg.releasePointerCapture(e.pointerId);
    }
    function wheel(e) {
        e.preventDefault();
        const r = svg.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top, before = s.zoom;
        s.zoom = Math.max(.08, Math.min(3, before * Math.exp(-e.deltaY * .001)));
        s.x = x - (x - s.x) * s.zoom / before; s.y = y - (y - s.y) * s.zoom / before; transform();
    }
    for (const [name, fn] of [['pointerdown', down], ['pointermove', move], ['pointerup', up], ['pointercancel', up], ['wheel', wheel]]) { svg.addEventListener(name, fn, {passive: false}); s.handlers.push([name, fn]); }
    s.draw(); fit(host);
    return restored ? 'Layout restored for this exact source snapshot and view.' : `${nodes.length} nodes · ${edges.length} relationships. Layout has not been saved.`;
}

export function fit(host) {
    const s = instances.get(host); if (!s || !s.nodes.length) return;
    const ps = s.nodes.map(n => s.positions[n.id]);
    const minX = Math.min(...ps.map(p => p.x)), minY = Math.min(...ps.map(p => p.y)), maxX = Math.max(...ps.map(p => p.x)) + W + 20, maxY = Math.max(...ps.map(p => p.y)) + H + 70;
    s.zoom = Math.max(.08, Math.min(1.4, (host.clientWidth - 30) / (maxX - minX), (host.clientHeight - 30) / (maxY - minY)));
    s.x = 15 - minX * s.zoom; s.y = 15 - minY * s.zoom;
    s.viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(${s.zoom})`);
}
export function highlight(host, ids, selected) { const s = instances.get(host); if (!s) return; s.highlight = ids && ids.length ? new Set(ids) : null; s.selected = selected || null; s.draw(); }
export function focus(host, id) { const s = instances.get(host), p = s?.positions[id]; if (!p) return; s.zoom = 1; s.x = host.clientWidth / 2 - p.x - W / 2; s.y = host.clientHeight / 2 - p.y - H / 2; s.viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(1)`); }
export function save(host) { const s = instances.get(host); if (!s) return 'No graph to save.'; try { localStorage.setItem(s.key, JSON.stringify(s.positions)); return 'Layout saved for this source snapshot and view in this browser.'; } catch { return 'Browser storage unavailable; layout was not saved.'; } }
export function reset(host) { const s = instances.get(host); if (!s) return; try { localStorage.removeItem(s.key); } catch { } const {nodes, edges, key, receiver} = s; instances.delete(host); for (const [n, fn] of s.handlers) s.svg.removeEventListener(n, fn); render(host, nodes, edges, key + '', receiver); }
export function exportSvg(host) {
    const s = instances.get(host); if (!s) return;
    const clone = s.svg.cloneNode(true); clone.setAttribute('xmlns', NS); clone.setAttribute('width', host.clientWidth); clone.setAttribute('height', host.clientHeight);
    clone.append(el('text', {x: 10, y: host.clientHeight - 10, fill: '#172d48', 'font-size': 11}, 'Source architecture only — not deployed topology · Solid confirmed · Dashed inferred · Dotted unresolved'));
    const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(clone)], {type: 'image/svg+xml'})), a = document.createElement('a');
    a.href = url; a.download = 'source-architecture.svg'; a.click(); URL.revokeObjectURL(url);
}
export function dispose(host) { const s = instances.get(host); if (!s) return; for (const [n, fn] of s.handlers) s.svg.removeEventListener(n, fn); instances.delete(host); }
