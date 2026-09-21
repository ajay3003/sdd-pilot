// End-to-end proof for the Critical E2E spike: the REAL extension — real service worker, real dynamic content-script
// registration, real isolated world — receives a typed command, performs it on an approved page and reports the outcome.
// Playwright here only launches the browser and serves the fixture; it never drives the page under test. Every click and
// every assertion below is performed by the companion's own content script.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtempSync, rmSync, cpSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';
const require = createRequire(import.meta.url);
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');
const root = fileURLToPath(new URL('..', import.meta.url));
const origin = 'https://m2lbdev.bufetat.no';

// A small SPA: clicking "Saker" pushes a route and renders a heading a moment later, the way the real application
// navigates. The delay matters — it is what proves the probe waits for a render instead of sleeping.
const fixture = `<!doctype html><html lang="en"><head><title>M2LB fixture</title></head><body><main>
  <h1>Oversikt</h1>
  <nav><a href="/saker" id="nav">Saker</a> <a href="/arkiv">Arkiv</a></nav>
  <button id="locked" disabled>Slett sak</button>
  <div id="view"></div>
  <script>
    document.getElementById('nav').addEventListener('click', event => {
      event.preventDefault();
      history.pushState({}, '', '/saker');
      setTimeout(() => { document.getElementById('view').innerHTML = '<h2 data-testid="saker">Saker (3)</h2>'; }, 300);
    });
  </script>
</main></body></html>`;

async function withPairedCompanion(environmentType, body) {
  const server = createServer(async (req, res) => {
    for await (const chunk of req) void chunk;
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ accepted: true, sessionId: 'fixture-session', profileId: 'm2lb-dev',
      environmentName: 'M2LB', environmentType, approvedOrigins: [origin] }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = mkdtempSync(path.join(tmpdir(), 'companion-probe-'));
  const extension = path.join(profile, 'extension');
  cpSync(root, extension, { recursive: true, filter: source => !['dist', 'tests'].includes(path.basename(source)) });
  // Headless cannot accept the native optional-host permission dialog. Pregrant ONLY the fixture origin; the worker,
  // the registration, the isolated-world startup and the command path stay exactly as shipped.
  const manifest = JSON.parse(readFileSync(path.join(extension, 'manifest.json'), 'utf8'));
  manifest.host_permissions.push(`${origin}/*`);
  writeFileSync(path.join(extension, 'manifest.json'), JSON.stringify(manifest));
  let context;
  try {
    context = await chromium.launchPersistentContext(profile, { channel: 'chromium', headless: true,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`] });
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
    const extensionOrigin = `chrome-extension://${new URL(worker.url()).host}`;
    const popup = await context.newPage();
    await popup.goto(`${extensionOrigin}/popup.html`);
    await popup.evaluate(backend => chrome.storage.local.set({ backend }), `http://127.0.0.1:${server.address().port}`);
    const status = await popup.evaluate(() => chrome.runtime.sendMessage({ type: 'popup:pair', pairingCode: 'FIXTURE' }));
    assert.equal(status.state, 'connected', JSON.stringify(status));

    await context.route(`${origin}/**`, route => route.fulfill({ contentType: 'text/html', body: fixture }));
    const target = await context.newPage();
    const errors = [];
    target.on('pageerror', error => errors.push(error.message));
    await target.goto(origin);
    // The probe path depends on the worker knowing the reporting tab, which the content script reports on startup.
    await waitFor(async () => (await worker.evaluate(() => chrome.storage.session.get('reportingPage'))).reportingPage, 15000);

    // Exactly what BirkNext would send: an action name and a described element. No code crosses this boundary.
    const probe = command => popup.evaluate(
      c => chrome.runtime.sendMessage({ type: 'popup:e2e-probe', commandId: 'test', command: c }), command);
    await body({ probe, target, errors });
  } finally {
    await context?.close();
    await new Promise(resolve => server.close(resolve));
    rmSync(profile, { recursive: true, force: true });
  }
}

test('the companion sees a link, clicks it and confirms the route it landed on', async () => {
  await withPairedCompanion('Development', async ({ probe, target, errors }) => {
    const seen = await probe({ action: 'assertVisible', selector: { kind: 'text', value: 'Saker' } });
    assert.equal(seen.status, 'passed', JSON.stringify(seen));
    assert.match(seen.summary, /a "Saker" is visible/);
    assert.equal(seen.observedRoute, '/');
    assert.ok(seen.startedAt && seen.completedAt && seen.durationMs >= 0, JSON.stringify(seen));

    const clicked = await probe({ action: 'click', selector: { kind: 'text', value: 'Saker' } });
    assert.equal(clicked.status, 'passed', JSON.stringify(clicked));

    // The click really navigated the application, and the probe waits for the render instead of sleeping.
    const routed = await probe({ action: 'assertRoute', expected: '/saker' });
    assert.equal(routed.status, 'passed', JSON.stringify(routed));
    const rendered = await probe({ action: 'assertText', selector: { kind: 'testid', value: 'saker' }, expected: 'Saker (3)', timeoutMs: 5000 });
    assert.equal(rendered.status, 'passed', JSON.stringify(rendered));
    assert.equal(await target.evaluate(() => location.pathname), '/saker');

    // A failing assertion is reported as a failure, not as an error and not as a silent pass.
    const wrong = await probe({ action: 'assertRoute', expected: '/arkiv', timeoutMs: 1000 });
    assert.equal(wrong.status, 'failed');
    assert.match(wrong.error, /Expected route \/arkiv, observed \/saker/);

    // The application's own disabled control stays untouched.
    const locked = await probe({ action: 'click', selector: { kind: 'css', value: '#locked' }, timeoutMs: 1000 });
    assert.equal(locked.status, 'failed');
    assert.match(locked.error, /disabled/);

    assert.deepEqual(errors, [], 'the probe must not raise page errors');
  });
});

test('an action outside the allow-list is refused by the page runner', async () => {
  await withPairedCompanion('Development', async ({ probe }) => {
    for (const action of ['eval', 'navigate', 'setValue', 'submit']) {
      const r = await probe({ action, selector: { kind: 'text', value: 'Saker' } });
      assert.equal(r.status, 'blocked', JSON.stringify(r));
      assert.match(r.error, /Action not allowed/);
    }
  });
});

test('a production environment is never driven, however the command arrives', async () => {
  await withPairedCompanion('Production', async ({ probe, target }) => {
    const r = await probe({ action: 'click', selector: { kind: 'text', value: 'Saker' } });
    assert.equal(r.status, 'blocked', JSON.stringify(r));
    assert.equal(await target.evaluate(() => location.pathname), '/');
  });
});

async function waitFor(predicate, timeout, detail = () => '') {
  const end = Date.now() + timeout;
  let value = await predicate();
  while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 100)); value = await predicate(); }
  assert.ok(value, `Timed out: ${detail()}`);
}
