const instances = new WeakMap();
const NS = 'http://www.w3.org/2000/svg';
function element(tag, attrs = {}, value) {
    const node = document.createElementNS(NS, tag);
    for (const [key, val] of Object.entries(attrs)) node.setAttribute(key, String(val));
    if (value !== undefined) node.textContent = value;
    return node;
}
export function render(host, nodes, edges, key, receiver) {
    const old = instances.get(host);
    const positions = old?.key === key ? old.positions : {};
    dispose(host);
    let restored = false;
    if (!Object.keys(positions).length) {
        try { const stored = JSON.parse(localStorage.getItem(key) || '{}');
            for (const node of nodes) { const p = stored[node.id]; if (p && Number.isFinite(p.x) && Number.isFinite(p.y) && Math.abs(p.x) < 100000 && Math.abs(p.y) < 100000) { positions[node.id] = p; restored = true; } }
        } catch { /* Corrupt or unavailable storage falls back to deterministic layout. */ }
    }
    const svg = element('svg', {width: '100%', height: '100%', role: 'img', 'aria-label': 'Source database relationship diagram'});
    const title = element('title', {}, 'Source schema only. Solid confirmed; dashed inferred; dotted migration/DDL.'); svg.append(title);
    const viewport = element('g'); svg.append(viewport); host.replaceChildren(svg);
    const s = {svg, viewport, positions, nodes, edges, key, receiver, x: 20, y: 20, zoom: 1, handlers: []}; instances.set(host, s);
    const columns = Math.max(1, Math.ceil(Math.sqrt(nodes.length)));
    let rowY = 0;
    nodes.forEach((n, i) => { if (i > 0 && i % columns === 0) rowY += Math.max(...nodes.slice(i - columns, i).map(x => 62 + x.columns.length * 21)) + 70; positions[n.id] ||= {x: i % columns * 310, y: rowY}; });
    function transform() { viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(${s.zoom})`); }
    function draw() {
        viewport.replaceChildren();
        for (const edge of edges) {
            const a = positions[edge.from], b = positions[edge.to]; if (!a || !b) continue;
            const style = edge.state === 'Inferred' ? '8 5' : edge.state === 'Confirmed' ? '' : '2 4';
            const line = element('line', {x1: a.x + 130, y1: a.y + 45, x2: b.x + 130, y2: b.y + 45, stroke: '#44546b', 'stroke-width': 2, 'stroke-dasharray': style});
            line.append(element('title', {}, edge.label)); viewport.append(line);
            const text = element('text', {x: (a.x + b.x) / 2 + 130, y: (a.y + b.y) / 2 + 35, fill: '#22334b', 'font-size': 12, 'paint-order': 'stroke', stroke: '#f7f9fc', 'stroke-width': 4}, edge.label); viewport.append(text);
        }
        for (const n of nodes) {
            const p = positions[n.id], g = element('g', {'data-table-id': n.id, transform: `translate(${p.x} ${p.y})`, tabindex: 0, role: 'button', 'aria-label': `Open ${n.name} table details`});
            const height = Math.max(82, 62 + n.columns.length * 21);
            g.append(element('rect', {width: 260, height, rx: 8, fill: '#ffffff', stroke: '#59718c', 'stroke-width': 2}));
            g.append(element('text', {x: 12, y: 23, 'font-size': 15, 'font-weight': 600, fill: '#172d48'}, n.name.length > 30 ? n.name.slice(0, 29) + '…' : n.name));
            g.append(element('title', {}, n.name));
            g.append(element('text', {x: 12, y: 43, 'font-size': 11, fill: '#44546b'}, n.state));
            n.columns.forEach((c, i) => g.append(element('text', {x: 12, y: 66 + i * 21, 'font-size': 12, fill: '#24364d'}, c.length > 34 ? c.slice(0, 33) + '…' : c)));
            g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); receiver.invokeMethodAsync('SelectTable', n.id); } });
            viewport.append(g);
        }
    }
    let gesture;
    function down(e) { const node = e.target.closest('[data-table-id]'); gesture = {id: node?.getAttribute('data-table-id'), x: e.clientX, y: e.clientY, moved: false}; svg.setPointerCapture(e.pointerId); }
    function move(e) { if (!gesture) return; const dx = e.clientX - gesture.x, dy = e.clientY - gesture.y; if (Math.abs(dx) + Math.abs(dy) > 2) gesture.moved = true; if (gesture.id) { const p = positions[gesture.id]; p.x += dx / s.zoom; p.y += dy / s.zoom; draw(); } else { s.x += dx; s.y += dy; transform(); } gesture.x = e.clientX; gesture.y = e.clientY; }
    function up(e) { if (gesture?.id && !gesture.moved) receiver.invokeMethodAsync('SelectTable', gesture.id); gesture = null; if (svg.hasPointerCapture(e.pointerId)) svg.releasePointerCapture(e.pointerId); }
    function wheel(e) { e.preventDefault(); const bounds = svg.getBoundingClientRect(), x = e.clientX - bounds.left, y = e.clientY - bounds.top, previous = s.zoom; s.zoom = Math.max(.08, Math.min(3, previous * Math.exp(-e.deltaY * .001))); s.x = x - (x - s.x) * s.zoom / previous; s.y = y - (y - s.y) * s.zoom / previous; transform(); }
    for (const [name, fn] of [['pointerdown', down], ['pointermove', move], ['pointerup', up], ['pointercancel', up], ['wheel', wheel]]) { svg.addEventListener(name, fn, {passive: false}); s.handlers.push([name, fn]); }
    draw(); fit(host);
    return restored ? 'Layout restored for this exact source snapshot.' : `${nodes.length} visible tables. Layout has not been saved.`;
}
export function fit(host) {
    const s = instances.get(host); if (!s || !s.nodes.length) return;
    const coords = s.nodes.map(n => s.positions[n.id]), minX = Math.min(...coords.map(p => p.x)), minY = Math.min(...coords.map(p => p.y));
    const maxX = Math.max(...coords.map(p => p.x)) + 280, maxY = Math.max(...s.nodes.map(n => s.positions[n.id].y + Math.max(82, 62 + n.columns.length * 21))) + 20;
    s.zoom = Math.min(1.5, (host.clientWidth - 30) / (maxX - minX), (host.clientHeight - 30) / (maxY - minY)); s.x = 15 - minX * s.zoom; s.y = 15 - minY * s.zoom;
    s.viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(${s.zoom})`);
}
export function save(host) { const s = instances.get(host); if (!s) return 'No diagram to save.'; try { localStorage.setItem(s.key, JSON.stringify(s.positions)); return 'Layout saved for this source snapshot and database view in this browser.'; } catch { return 'Browser storage unavailable; layout was not saved.'; } }
export function focus(host, id) { const s = instances.get(host), p = s?.positions[id]; if (!p) return; s.zoom = 1; s.x = host.clientWidth / 2 - p.x - 130; s.y = host.clientHeight / 2 - p.y - 80; s.viewport.setAttribute('transform', `translate(${s.x} ${s.y}) scale(1)`); }
export function reset(host) { const s = instances.get(host); if (!s) return; try { localStorage.removeItem(s.key); } catch { } const {nodes, edges, key, receiver} = s; dispose(host); render(host, nodes, edges, key, receiver); }
export function exportSvg(host) { const s = instances.get(host); if (!s) return; const clone = s.svg.cloneNode(true); clone.setAttribute('xmlns', NS); clone.setAttribute('width', host.clientWidth); clone.setAttribute('height', host.clientHeight); clone.append(element('text', {x: 10, y: host.clientHeight - 10, fill: '#172d48', 'font-size': 11}, 'Source only · Solid confirmed · Dashed inferred · Dotted migration/DDL')); const blob = new Blob([new XMLSerializer().serializeToString(clone)], {type: 'image/svg+xml'}), url = URL.createObjectURL(blob), a = document.createElement('a'); a.href = url; a.download = 'source-database-diagram.svg'; a.click(); URL.revokeObjectURL(url); }
export function dispose(host) { const s = instances.get(host); if (!s) return; for (const [name, fn] of s.handlers) s.svg.removeEventListener(name, fn); instances.delete(host); }
