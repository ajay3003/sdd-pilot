// Stand-in website for the Frontend Quality Review browser test (no external system is involved).
// Runs inside a local Podman container (node:20-bookworm) published on all interfaces, so BirkNext reaches it through localhost and the ZAP
// container through host.containers.internal. The mode decides whether the site has deliberate, recognisable defects:
//   broken: no security headers; CSS/JS without any caching header; a script that logs a console error and throws a TypeError; a script
//           reference that answers 404; an image without alt text, an unlabeled input, an empty button, faint text, no lang, no title;
//           a session cookie without HttpOnly/Secure/SameSite
//   fixed:  all of the above corrected
//   GET /__control?mode=broken|fixed → sets the mode;  GET /__control → reports the mode and how many page loads were served
// Every response body is static; nothing echoes the request.
const http = require('node:http');
let mode = process.env.MODE || 'broken';
let pages = 0;

const broken = {
  page: `<!doctype html>
<html><head><meta charset="utf-8"><link rel="stylesheet" href="/site.css"></head>
<body><main><h1>Children's services</h1>
<img src="/logo.svg" width="120" height="40">
<p class="faint">Opening hours: weekdays 08-16</p>
<form action="/search"><input type="text" name="q"><button type="submit"></button></form>
</main>
<script src="/app.js"></script>
<script src="/js/widgets.js"></script>
</body></html>`,
  css: `body{font-family:sans-serif;margin:2rem;background:#fff;color:#222}.faint{color:#c8c8c8}`,
  js: `console.error('Child list request failed: 500 Internal Server Error');
document.addEventListener('DOMContentLoaded', function () { var widget; widget.render(); });`,
};

const fixed = {
  page: `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Children's services</title><link rel="stylesheet" href="/site.css"></head>
<body><main><h1>Children's services</h1>
<img src="/logo.svg" width="120" height="40" alt="Children's services logo">
<p class="faint">Opening hours: weekdays 08-16</p>
<form action="/search"><label for="q">Search</label> <input type="text" id="q" name="q"><button type="submit">Search</button></form>
</main>
<script src="/app.js"></script>
</body></html>`,
  css: `body{font-family:sans-serif;margin:2rem;background:#fff;color:#222}.faint{color:#555}`,
  js: `document.addEventListener('DOMContentLoaded', function () { document.body.dataset.ready = 'true'; });`,
};

const logo = `<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"><rect width="120" height="40" fill="#1d4e89"/></svg>`;

const securityHeaders = {
  'content-security-policy': "default-src 'self'; img-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'",
  'x-content-type-options': 'nosniff',
  'referrer-policy': 'no-referrer',
  'permissions-policy': 'geolocation=(), camera=(), microphone=()',
  'x-frame-options': 'DENY',
  'cross-origin-opener-policy': 'same-origin',
  'cross-origin-resource-policy': 'same-origin',
};

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://target');
  if (url.pathname === '/__control') {
    const requested = url.searchParams.get('mode');
    if (requested === 'broken' || requested === 'fixed') { mode = requested; pages = 0; }
    res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
    res.end(JSON.stringify({ mode, pages }));
    return;
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405); res.end(); return; }
  const isFixed = mode === 'fixed';
  const site = isFixed ? fixed : broken;
  const headers = isFixed ? { ...securityHeaders } : {};
  const asset = (type, body) => {
    // Fixed: long-lived, versionless static assets are cacheable; broken: no Cache-Control, no ETag, no Last-Modified.
    if (isFixed) { headers['cache-control'] = 'public, max-age=31536000, immutable'; headers['etag'] = `"${mode}-${body.length}"`; }
    res.writeHead(200, { ...headers, 'content-type': type });
    res.end(req.method === 'HEAD' ? undefined : body);
  };
  switch (url.pathname) {
    case '/':
    case '/index.html':
      pages++;
      if (isFixed) headers['cache-control'] = 'no-cache';
      else headers['set-cookie'] = 'session=standin-session; Path=/';
      res.writeHead(200, { ...headers, 'content-type': 'text/html; charset=utf-8' });
      res.end(req.method === 'HEAD' ? undefined : site.page);
      return;
    case '/site.css': return asset('text/css; charset=utf-8', site.css);
    case '/app.js': return asset('text/javascript; charset=utf-8', site.js);
    case '/logo.svg': return asset('image/svg+xml', logo);
    default:
      res.writeHead(404, { ...headers, 'content-type': 'text/plain; charset=utf-8' });
      res.end(req.method === 'HEAD' ? undefined : 'Not found');
  }
}).listen(8080, '0.0.0.0', () => console.log(`frontend stand-in on :8080, mode ${mode}`));
