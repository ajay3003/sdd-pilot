// Stand-in target for the Performance Test Review browser test (no external system is involved).
// Runs inside a local Podman container (node:20-bookworm) published on all interfaces, so the k6 container reaches it through
// host.containers.internal and the test reaches it through localhost. Every response is delayed by the current latency, which the test
// changes between runs to produce a measurable regression:
//   GET /api/items          → 200 JSON after the current delay (the endpoint under load)
//   GET /                   → 200 (reachability checks)
//   GET /__control?delay=N  → sets the delay in milliseconds (test control; not part of the scenario)
//   GET /__control          → reports the delay and how many /api/items requests were served
const http = require('node:http');
let delayMs = Number(process.env.DELAY_MS || 20);
let served = 0;

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://target');
  if (url.pathname === '/__control') {
    if (url.searchParams.has('delay')) delayMs = Math.max(0, Math.min(5000, Number(url.searchParams.get('delay')) || 0));
    if (url.searchParams.has('reset')) served = 0;
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ delayMs, served }));
    return;
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405); res.end(); return; }
  if (url.pathname === '/api/items') {
    served++;
    setTimeout(() => {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ items: [{ id: 1, name: 'Loan' }, { id: 2, name: 'Return' }] }));
    }, delayMs);
    return;
  }
  res.writeHead(200, { 'content-type': 'text/plain' });
  res.end('BirkNext performance stand-in target');
}).listen(8080, '0.0.0.0', () => console.log(`stand-in target on :8080, delay ${delayMs} ms`));
