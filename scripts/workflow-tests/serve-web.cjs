// Static server for a published BirkNext web app (wwwroot) with the SPA fallback, for the workflow tests only.
// Usage: node serve-web.cjs <wwwroot> <port>. Listens on 127.0.0.1 only; files outside wwwroot are never served.
const http = require('http'), fs = require('fs'), path = require('path');
const root = path.resolve(process.argv[2]);
const port = Number(process.argv[3]);
if (!fs.existsSync(path.join(root, 'index.html')) || !port) { console.error('usage: node serve-web.cjs <wwwroot> <port>'); process.exit(2); }
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm',
  '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.pdb': 'application/octet-stream', '.blat': 'application/octet-stream',
  '.png': 'image/png', '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.webmanifest': 'application/manifest+json' };
http.createServer((req, res) => {
  const p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  let file = path.normalize(path.join(root, p));
  if (!file.startsWith(root)) { res.writeHead(403); return res.end(); }
  if (!fs.existsSync(file) || fs.statSync(file).isDirectory()) file = path.join(root, 'index.html');
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
  fs.createReadStream(file).pipe(res);
}).listen(port, '127.0.0.1', () => console.log(`BirkNext web on http://localhost:${port}`));
