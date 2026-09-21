// End-to-end proof for Critical E2E: the REAL extension — real service worker, real dynamic content-script
// registration, real isolated world — claims a typed command from its own heartbeat, performs it on an approved page,
// and reports the outcome back to the backend.
//
// Playwright here only launches the browser and plays the part of the BirkNext backend; it never drives the page under
// test. Every click and every assertion below is performed by the companion's own content script.
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
// navigates. The delay matters — it is what proves a step waits for a render instead of sleeping.
const fixture = `<!doctype html><html lang="en"><head><title>M2LB fixture</title></head><body><main>
  <h1>Oversikt</h1>
  <nav><a href="/saker" id="nav" data-testid="nav-saker">Saker</a> <a href="/arkiv">Arkiv</a></nav>
  <button id="locked" data-testid="delete" disabled>Slett sak</button>
  <input data-testid="query">
  <div id="view"></div>
  <script>
    document.getElementById('nav').addEventListener('click', event => {
      event.preventDefault();
      history.pushState({}, '', '/saker');
      setTimeout(() => { document.getElementById('view').innerHTML = '<h2 data-testid="status">Aktiv</h2>'; }, 300);
    });
  </script>
</main></body></html>`;

/**
 * Stands up the real extension against a fake BirkNext backend. `queue(command)` makes the next heartbeat hand that
 * command to the worker exactly as the real backend would, and `results` collects what the worker posts back.
 */
async function withPairedCompanion(environmentType, body) {
  const results = [];
  const heartbeats = [];
  let pending = null;
  const server = createServer(async (req, res) => {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    const route = req.url.split('/').at(-1);
    const parsed = raw ? JSON.parse(raw) : {};
    if (route === 'command-result') results.push(parsed);
    if (route === 'heartbeat') heartbeats.push(parsed);
    // The backend hands a command out exactly once. Doing the same here is what makes a retried heartbeat a real test.
    const handOut = route === 'heartbeat' && pending && parsed.currentPageOrigin === origin ? pending : null;
    if (handOut) pending = null;
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({
      accepted: true, sessionId: 'fixture-session', profileId: 'm2lb-dev',
      environmentName: 'M2LB', environmentType, approvedOrigins: [origin],
      pendingCommand: handOut, nextHeartbeatMs: 500,
    }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = mkdtempSync(path.join(tmpdir(), 'companion-e2e-'));
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
    await waitFor(async () => (await worker.evaluate(() => chrome.storage.session.get('reportingPage'))).reportingPage, 15000);

    /** Queues a command and waits for the worker to report its outcome, exactly as a run would. */
    const dispatch = async command => {
      const full = {
        commandId: `cmd-${results.length + 1}-${Date.now()}`, runId: 'run-1', flowId: 'flow-1', stepId: 'step-1',
        profileId: 'm2lb-dev', environmentId: 'env-1', targetOrigin: origin, timeoutMs: 6000, ...command,
      };
      pending = full;
      await worker.evaluate(() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.5 }));
      await waitFor(() => results.some(r => r.result?.commandId === full.commandId), 25000,
        () => JSON.stringify({ heartbeats: heartbeats.length, results }));
      return results.find(r => r.result.commandId === full.commandId).result;
    };

    const redeliver = async command => {
      const before = results.filter(r => r.result.commandId === command.commandId).length;
      pending = command;
      await worker.evaluate(() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.5 }));
      await waitFor(() => results.filter(r => r.result.commandId === command.commandId).length > before, 25000);
    };

    await body({ dispatch, target, errors, results, worker, popup, redeliver });
  } finally {
    await context?.close();
    await new Promise(resolve => server.close(resolve));
    rmSync(profile, { recursive: true, force: true });
  }
}

test('a command queued on the heartbeat is executed by the page and reported back', async () => {
  await withPairedCompanion('Development', async ({ dispatch, target, errors }) => {
    const seen = await dispatch({ action: 'AssertVisible', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(seen.status, 'Passed', JSON.stringify(seen));
    assert.equal(seen.assertionResult, true);
    assert.equal(seen.observedRoute, '/');
    assert.equal(seen.evidenceReference, `${origin}/`, 'a step points at the page evidence that covers it');
    assert.ok(seen.startedAt && seen.completedAt && seen.durationMs >= 0, JSON.stringify(seen));

    const clicked = await dispatch({ action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(clicked.status, 'Passed', JSON.stringify(clicked));

    // The click really navigated the application, and the step waits for the render instead of sleeping.
    const routed = await dispatch({ action: 'WaitForRoute', expected: '/saker' });
    assert.equal(routed.status, 'Passed', JSON.stringify(routed));
    const rendered = await dispatch({ action: 'WaitForText', selector: { kind: 'TestId', value: 'status' }, expected: 'Aktiv' });
    assert.equal(rendered.status, 'Passed', JSON.stringify(rendered));
    assert.equal(await target.evaluate(() => location.pathname), '/saker');

    // A failing assertion is a failure, not an error and not a silent pass.
    const wrong = await dispatch({ action: 'AssertRoute', expected: '/arkiv', timeoutMs: 1000 });
    assert.equal(wrong.status, 'Failed');
    assert.equal(wrong.assertionResult, false);
    assert.match(wrong.sanitizedError, /Expected route \/arkiv, observed \/saker/);

    // The application's own disabled control stays untouched.
    const locked = await dispatch({ action: 'Click', selector: { kind: 'TestId', value: 'delete' }, timeoutMs: 1000 });
    assert.equal(locked.status, 'Failed');
    assert.match(locked.sanitizedError, /disabled/);

    assert.deepEqual(errors, [], 'a step must not raise page errors');
  });
});

test('Fill goes through the real extension and the control keeps the value', async () => {
  await withPairedCompanion('Development', async ({ dispatch, target }) => {
    const filled = await dispatch({ action: 'Fill', selector: { kind: 'TestId', value: 'query' }, value: 'M2LB-E2E-7' });
    assert.equal(filled.status, 'Passed', JSON.stringify(filled));
    assert.equal(await target.inputValue('[data-testid=query]'), 'M2LB-E2E-7');
    assert.equal((await dispatch({ action: 'AssertValue', selector: { kind: 'TestId', value: 'query' }, expected: 'M2LB-E2E-7' })).status, 'Passed');
  });
});

test('a redelivered command is answered from what already happened, never performed twice', async () => {
  await withPairedCompanion('Development', async ({ dispatch, target, results, redeliver }) => {
    await dispatch({ commandId: 'fixed-id', action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(await target.evaluate(() => location.pathname), '/saker');

    // Put the page back, then hand the worker the same command again — a redelivery after a restart, or a backend that
    // did not see the first result.
    await target.evaluate(() => history.pushState({}, '', '/'));
    const before = results.filter(r => r.result.commandId === 'fixed-id').length;
    await redeliver({ commandId: 'fixed-id', action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' },
      runId: 'run-1', flowId: 'flow-1', stepId: 'step-1', profileId: 'm2lb-dev', environmentId: 'env-1',
      targetOrigin: origin, timeoutMs: 6000 });

    assert.ok(results.filter(r => r.result.commandId === 'fixed-id').length > before, 'the redelivery is still answered');
    assert.equal(await target.evaluate(() => location.pathname), '/',
      'the second delivery must not click again');
  });
});

test('an action outside the allow-list is refused by the page runner', async () => {
  await withPairedCompanion('Development', async ({ dispatch }) => {
    for (const action of ['eval', 'navigate', 'setValue', 'submit']) {
      const r = await dispatch({ action, selector: { kind: 'TestId', value: 'nav-saker' } });
      assert.equal(r.status, 'Blocked', JSON.stringify(r));
      assert.match(r.sanitizedError, /Action not allowed/);
    }
  });
});

test('a command for another environment or another origin never reaches the page', async () => {
  await withPairedCompanion('Development', async ({ dispatch, target }) => {
    const wrongProfile = await dispatch({ profileId: 'someone-else', action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(wrongProfile.status, 'Blocked');
    assert.match(wrongProfile.sanitizedError, /different Target Environment/);

    const wrongOrigin = await dispatch({ targetOrigin: 'https://elsewhere.example', action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(wrongOrigin.status, 'Blocked');
    assert.match(wrongOrigin.sanitizedError, /has not approved/);

    assert.equal(await target.evaluate(() => location.pathname), '/', 'neither command touched the page');
  });
});

test('a production environment is never driven, however the command arrives', async () => {
  await withPairedCompanion('Production', async ({ dispatch, target }) => {
    const r = await dispatch({ action: 'Click', selector: { kind: 'TestId', value: 'nav-saker' } });
    assert.equal(r.status, 'Blocked', JSON.stringify(r));
    assert.match(r.sanitizedError, /non-production/);
    assert.equal(await target.evaluate(() => location.pathname), '/');
  });
});

async function waitFor(predicate, timeout, detail = () => '') {
  const end = Date.now() + timeout;
  let value = await predicate();
  while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 100)); value = await predicate(); }
  assert.ok(value, `Timed out: ${detail()}`);
}
