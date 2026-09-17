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

test('Pair → Connected → approved M2LB navigation and refresh keep reporting without a BirkNext tab', async () => {
  const reports = [];
  const server = createServer(async (req, res) => {
    let body = '';
    for await (const chunk of req) body += chunk;
    reports.push({ route: req.url.split('/').at(-1), body: JSON.parse(body) });
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ accepted: true, sessionId: 'fixture-session', profileId: 'm2lb-dev',
      environmentName: 'M2LB', environmentType: 'Development', approvedOrigins: [origin] }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = mkdtempSync(path.join(tmpdir(), 'companion-reporting-'));
  const extension = path.join(profile, 'extension');
  cpSync(root, extension, { recursive: true, filter: source => !['dist', 'tests'].includes(path.basename(source)) });
  // Headless cannot accept the native optional-host permission dialog. Pregrant ONLY the fixture origin;
  // keep the real worker, registration, isolated-world startup and reporting path unchanged.
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
    await popup.evaluate(async ({ backend, origin }) => {
      await chrome.storage.local.set({ backend });
      if (!await chrome.permissions.contains({ origins: [`${origin}/*`] })) throw new Error('Fixture origin not granted');
    }, { backend: `http://127.0.0.1:${server.address().port}`, origin });
    const status = await popup.evaluate(() => chrome.runtime.sendMessage({ type: 'popup:pair', pairingCode: 'FIXTURE' }));
    assert.equal(status.state, 'connected', JSON.stringify(status));
    const registrations = await worker.evaluate(() => chrome.scripting.getRegisteredContentScripts());
    assert.equal(registrations.length, 2);
    await popup.close();
    await context.route(`${origin}/**`, route => route.fulfill({ contentType: 'text/html', body:
      '<!doctype html><html lang="en"><head><title>Fixture</title></head><body><main><h1>M2LB fixture</h1></main></body></html>' }));
    const target = await context.newPage();
    const errors = [];
    target.on('pageerror', error => errors.push(error.message));
    await target.goto(origin);
    await waitFor(() => reports.some(r => r.route === 'evidence'), 15000, () => JSON.stringify({ reports, errors }));
    assert.ok(reports.some(r => r.route === 'heartbeat' && r.body.currentPageOrigin === origin));
    const evidence = reports.find(r => r.route === 'evidence').body.pages[0];
    assert.equal(evidence.pageOrigin, origin);
    assert.equal(evidence.pagePath, '/');
    assert.ok(evidence.dom && evidence.accessibility && evidence.performance);
    const before = reports.filter(r => r.route === 'evidence').length;
    await target.reload();
    await waitFor(() => reports.filter(r => r.route === 'evidence').length > before, 15000);
    // A real MV3 worker stop discards globals. Wake it from a popup, then close the popup again.
    const cdp = await context.newCDPSession(target);
    await cdp.send('ServiceWorker.enable');
    await cdp.send('ServiceWorker.stopAllWorkers');
    const wake = await context.newPage();
    await wake.goto(`${extensionOrigin}/popup.html`);
    await wake.evaluate(() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.5 }));
    const heartbeatCount = reports.filter(r => r.route === 'heartbeat').length;
    await wake.close();
    await waitFor(() => reports.filter(r => r.route === 'heartbeat').length > heartbeatCount, 10000);
    assert.equal(reports.filter(r => r.route === 'heartbeat').at(-1).body.currentPageOrigin, origin);
    await context.route('https://unapproved.test/**', route => route.fulfill({ contentType: 'text/html', body: '<h1>Unapproved</h1>' }));
    const other = await context.newPage();
    await other.goto('https://unapproved.test/');
    await other.bringToFront();
    const started = Date.now();
    await waitFor(() => Date.now() - started > 50000, 55000);
    const heartbeats = reports.filter(r => r.route === 'heartbeat');
    assert.ok(heartbeats.length > heartbeatCount + 2, 'Reporting continues beyond the 45-second connected window');
    assert.equal(heartbeats.at(-1).body.currentPageOrigin, origin);
    assert.ok(reports.filter(r => r.route === 'evidence').every(r => r.body.pages.every(p => p.pageOrigin === origin)));
    assert.deepEqual(errors, []);
  } finally {
    await context?.close();
    await new Promise(resolve => server.close(resolve));
    rmSync(profile, { recursive: true, force: true });
  }
});

async function waitFor(predicate, timeout, detail = () => '') {
  const end = Date.now() + timeout;
  while (!predicate() && Date.now() < end) await new Promise(resolve => setTimeout(resolve, 100));
  assert.ok(predicate(), `Timed out: ${detail()}`);
}
