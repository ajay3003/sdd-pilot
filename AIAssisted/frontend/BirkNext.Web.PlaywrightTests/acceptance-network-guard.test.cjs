// Unit tests for acceptance-network-guard.cjs. No network: the Playwright context and routes are fakes.
// Run: node --test acceptance-network-guard.test.cjs
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const guard = require('./acceptance-network-guard.cjs');

function fakeContext() {
  const handlers = [];
  return {
    handlers,
    async route(pattern, handler) { handlers.push(handler); },
    async send(url, method = 'GET') {
      const calls = [];
      const route = {
        request: () => ({ url: () => url, method: () => method }),
        abort: async reason => calls.push(['abort', reason]),
        continue: async () => calls.push(['continue']),
        fulfill: async () => calls.push(['fulfill']),
      };
      await handlers[0](route);
      return calls;
    },
  };
}

test('the developer ports 5000 and 5173 are always prohibited', () => {
  for (const url of ['http://localhost:5000', 'http://127.0.0.1:5000/api/x', 'http://localhost:5173/', 'https://localhost:5173'])
    assert.match(guard.targetProblem(url), /reserved/);
});

test('external hosts are never acceptable targets', () => {
  assert.match(guard.targetProblem('https://tt02.example.no/api'), /not loopback/);
  assert.match(guard.targetProblem('http://192.168.1.10:5097'), /not loopback/);
  assert.equal(guard.targetProblem('http://localhost:5097'), null);
});

test('validateAcceptanceTargets refuses to start on any prohibited URL', () => {
  assert.throws(() => guard.validateAcceptanceTargets({ web: 'http://localhost:5197', api: 'http://localhost:5000' }), /api: port 5000/);
  assert.deepEqual(guard.validateAcceptanceTargets({ web: 'http://localhost:5197/', api: 'http://localhost:5097' }),
    { web: 'http://localhost:5197', api: 'http://localhost:5097' });
});

test('a published frontend that falls back to :5000 or another API is refused', () => {
  assert.throws(() => guard.validateFrontendConfig({ BackendUrl: 'http://localhost:5000' }, 'http://localhost:5097'), /reserved/);
  assert.throws(() => guard.validateFrontendConfig({}, 'http://localhost:5097'), /no BackendUrl/);
  assert.throws(() => guard.validateFrontendConfig({ BackendUrl: 'http://localhost:5098' }, 'http://localhost:5097'), /not the isolated API/);
  assert.equal(guard.validateFrontendConfig({ BackendUrl: 'http://localhost:5097' }, 'http://localhost:5097'), 'http://localhost:5097');
});

test('a prohibited port cannot be allowlisted', async () => {
  await assert.rejects(guard.installNetworkGuard(fakeContext(), { allowedOrigins: ['http://localhost:5000'] }), /reserved/);
});

test('requests to prohibited or unlisted destinations are aborted before sending and recorded', async () => {
  const context = fakeContext();
  const g = await guard.installNetworkGuard(context, { allowedOrigins: ['http://localhost:5197', 'http://localhost:5097'] });
  assert.deepEqual(await context.send('http://localhost:5097/api/scopes'), [['continue']]);
  assert.deepEqual(await context.send('http://localhost:5197/_framework/blazor.webassembly.js'), [['continue']]);
  assert.deepEqual(await context.send('http://localhost:5000/api/workspace-persistence/auto-save', 'OPTIONS'), [['abort', 'blockedbyclient']]);
  assert.deepEqual(await context.send('http://localhost:5173/'), [['abort', 'blockedbyclient']]);
  assert.deepEqual(await context.send('https://fonts.example.com/x.css'), [['abort', 'blockedbyclient']]);
  assert.deepEqual(await context.send('http://localhost:5099/other'), [['abort', 'blockedbyclient']]);
  assert.equal(g.violations.length, 4);
  assert.equal(g.violations[0].method, 'OPTIONS');
  assert.throws(() => g.assertNoViolations(), /Prohibited network destinations/);
});

test('a request hook only sees allowed requests and must forward them itself', async () => {
  const context = fakeContext();
  const seen = [];
  await guard.installNetworkGuard(context, { allowedOrigins: ['http://localhost:5097'], onRequest: async (route, req) => { seen.push(req.url()); return route.fulfill(); } });
  assert.deepEqual(await context.send('http://localhost:5097/api/x'), [['fulfill']]);
  assert.deepEqual(await context.send('http://localhost:5000/api/x'), [['abort', 'blockedbyclient']]);
  assert.deepEqual(seen, ['http://localhost:5097/api/x']);
});
