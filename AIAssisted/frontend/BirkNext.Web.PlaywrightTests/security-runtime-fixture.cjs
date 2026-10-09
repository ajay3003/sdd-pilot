// Loopback fixture for the runtime security checks (API documentation exposure, CORS, cookies, authorization scenarios, body fuzzing).
// Synthetic responses only; binds to 127.0.0.1. Usage: node security-runtime-fixture.cjs [apiPort=5099] [frontendPort=5097]
const http = require('node:http');

const apiPort = Number(process.argv[2] || 5099);
const frontendPort = Number(process.argv[3] || 5097);
// Sentinel cookie value: the BirkNext UI, report, export and storage must never contain it.
const cookieSentinel = 'SENTINEL-COOKIE-VALUE-5097';

const openApi = {
  openapi: '3.0.1',
  info: { title: 'Fixture API', version: '1' },
  paths: {
    '/api/children': { get: { parameters: [{ name: 'status', in: 'query', schema: { type: 'string', enum: ['Active', 'Closed'] } }], responses: { 200: { description: 'ok' }, 400: { description: 'bad' } } } },
    '/api/search': {
      post: {
        requestBody: { required: true, content: { 'application/json': { schema: { $ref: '#/components/schemas/Search' } } } },
        responses: { 200: { description: 'ok' }, 400: { description: 'bad' } },
      },
    },
    '/api/items/{id}': { delete: { responses: { 204: { description: 'gone' } } } },
  },
  components: { schemas: { Search: { type: 'object', required: ['term'], additionalProperties: false, properties: { term: { type: 'string', maxLength: 20 }, page: { type: 'integer', minimum: 1, maximum: 50 } } } } },
};

function send(res, status, body, headers = {}) {
  res.writeHead(status, { 'Content-Type': 'application/json', ...headers });
  res.end(typeof body === 'string' ? body : JSON.stringify(body));
}

http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${apiPort}`);
  let body = '';
  req.on('data', chunk => { body += chunk; if (body.length > 65536) req.destroy(); });
  req.on('end', () => {
    if (req.method === 'OPTIONS') {
      // Reflects any origin with credentials: the foreign-origin probe must report it.
      res.writeHead(204, { 'Access-Control-Allow-Origin': req.headers.origin || '*', 'Access-Control-Allow-Credentials': 'true', 'Access-Control-Allow-Methods': 'GET, POST, DELETE', 'Vary': 'Origin' });
      return res.end();
    }
    if (url.pathname === '/swagger') { res.writeHead(200, { 'Content-Type': 'text/html' }); return res.end('<html><body><div id="swagger-ui"></div></body></html>'); }
    if (url.pathname === '/swagger/v1/swagger.json') return send(res, 200, openApi);
    if (url.pathname === '/api/children' && req.method === 'GET') return send(res, 200, { items: [], totalCount: 0 });
    if (url.pathname === '/api/search' && req.method === 'POST') {
      if (!/json/.test(req.headers['content-type'] || '')) return send(res, 415, { title: 'Unsupported Media Type', status: 415 });
      try {
        const doc = JSON.parse(body);
        if (typeof doc.term !== 'string') return send(res, 400, { title: 'Bad Request', status: 400 });
        if (doc.page !== undefined && typeof doc.page !== 'number') return send(res, 500, { error: 'System.InvalidCastException at Fixture.Search() in C:\\src\\Search.cs:line 12' });
        return send(res, 200, { items: [] });
      } catch {
        return send(res, 400, { title: 'Bad Request', status: 400 });
      }
    }
    return send(res, 404, { title: 'Not Found', status: 404 });
  });
}).listen(apiPort, '127.0.0.1', () => console.log(`fixture api http://127.0.0.1:${apiPort}`));

http.createServer((req, res) => {
  res.writeHead(200, {
    'Content-Type': 'text/html',
    'Set-Cookie': [`Session=${cookieSentinel}; Path=/; SameSite=None`, 'Theme=dark; Path=/; Max-Age=3600; SameSite=Lax'],
  });
  res.end('<!doctype html><html><head><title>Fixture app</title></head><body><main>Fixture app</main></body></html>');
}).listen(frontendPort, '127.0.0.1', () => console.log(`fixture frontend http://127.0.0.1:${frontendPort}`));
