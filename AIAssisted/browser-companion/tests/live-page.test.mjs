// Live page registration in the REAL extension.
//
// Page liveness is what BirkNext uses to decide whether a browser step can run, so it has to be true about the browser
// rather than about what has been collected from it. These tests watch the heartbeats the worker actually sends and
// assert that a page becomes live before any evidence exists, survives SPA navigation, and stops being live the moment
// its tab does.
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

const fixture = `<!doctype html><html lang="en"><head><title>M2LB fixture</title></head><body><main>
  <h1>Oversikt</h1>
  <a href="/saker" id="nav" data-testid="nav-saker">Saker</a>
  <script>
    document.getElementById('nav').addEventListener('click', event => {
      event.preventDefault();
      history.pushState({}, '', '/saker');
    });
  </script>
</main></body></html>`;

async function withCompanion(body) {
  const heartbeats = [];
  const server = createServer(async (req, res) => {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    const route = req.url.split('/').at(-1);
    if (route === 'heartbeat') heartbeats.push(JSON.parse(raw || '{}'));
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ accepted: true, sessionId: 'fixture-session', profileId: 'm2lb-dev',
      environmentName: 'M2LB', environmentType: 'Development', approvedOrigins: [origin] }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = mkdtempSync(path.join(tmpdir(), 'companion-live-'));
  const extension = path.join(profile, 'extension');
  cpSync(root, extension, { recursive: true, filter: source => !['dist', 'tests'].includes(path.basename(source)) });
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

    /** Forces a heartbeat and returns the live pages it carried. */
    const beat = async () => {
      const before = heartbeats.length;
      await worker.evaluate(() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.5 }));
      await waitFor(() => heartbeats.length > before, 15000, () => JSON.stringify(heartbeats.at(-1)));
      return heartbeats.at(-1).livePages ?? [];
    };

    await body({ context, worker, beat, heartbeats });
  } finally {
    await context?.close();
    await new Promise(resolve => server.close(resolve));
    rmSync(profile, { recursive: true, force: true });
  }
}

test('an approved page is live as soon as its content script starts, before any evidence exists', async () => {
  await withCompanion(async ({ context, heartbeats }) => {
    await context.newPage().then(page => page.goto(origin));
    // Registration is announced immediately, so the page does not look closed until the next scheduled beat.
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000, () => JSON.stringify(heartbeats));

    const live = heartbeats.findLast(h => (h.livePages ?? []).length > 0).livePages;
    assert.equal(live.length, 1);
    assert.equal(live[0].origin, origin);
    assert.equal(live[0].route, '/');
    assert.ok(live[0].contentScriptInstanceId, 'a live page names the content script instance it belongs to');
    assert.ok(live[0].pageId.includes(live[0].contentScriptInstanceId), 'page identity is tab plus instance, not a URL');
  });
});

test('an SPA route change moves the route and never makes the page disappear', async () => {
  await withCompanion(async ({ context, beat, heartbeats }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000);
    const first = heartbeats.findLast(h => (h.livePages ?? []).length > 0).livePages[0];

    await target.click('[data-testid=nav-saker]');
    await waitFor(async () => (await target.evaluate(() => location.pathname)) === '/saker', 5000);
    const after = await beat();

    assert.equal(after.length, 1, 'the page stays alive across a route change');
    assert.equal(after[0].route, '/saker');
    assert.equal(after[0].pageId, first.pageId, 'a route change is not a new page');
    // The whole point: no heartbeat in between ever reported zero live pages.
    const zeroAfterRegistration = heartbeats
      .slice(heartbeats.findIndex(h => (h.livePages ?? []).length > 0))
      .filter(h => (h.livePages ?? []).length === 0);
    assert.deepEqual(zeroAfterRegistration, [], 'the live page must not flicker out mid-navigation');
  });
});

test('closing the tab takes the live page away', async () => {
  await withCompanion(async ({ context, beat, heartbeats }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000);

    await target.close();
    await waitFor(async () => (await beat()).length === 0, 20000, () => JSON.stringify(heartbeats.at(-1)));
    assert.deepEqual(await beat(), []);
  });
});

test('two approved tabs are two live pages', async () => {
  await withCompanion(async ({ context, beat, heartbeats }) => {
    const first = await context.newPage();
    await first.goto(origin);
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000);
    const second = await context.newPage();
    await second.goto(`${origin}/arkiv`);

    await waitFor(async () => (await beat()).length === 2, 20000, () => JSON.stringify(heartbeats.at(-1)));
    const live = await beat();
    assert.equal(live.length, 2);
    assert.equal(new Set(live.map(p => p.pageId)).size, 2, 'two tabs on the same origin are two identities');
  });
});

test('a reload replaces the live page instead of adding a second one', async () => {
  await withCompanion(async ({ context, beat, heartbeats }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000);
    const before = (await beat())[0];

    await target.reload();
    await waitFor(async () => {
      const live = await beat();
      return live.length === 1 && live[0].contentScriptInstanceId !== before.contentScriptInstanceId;
    }, 20000, () => JSON.stringify(heartbeats.at(-1)));

    const after = await beat();
    assert.equal(after.length, 1, 'one tab is one live page, however many times it reloads');
    assert.notEqual(after[0].contentScriptInstanceId, before.contentScriptInstanceId);
  });
});

test('live state survives the service worker being stopped', async () => {
  await withCompanion(async ({ context, worker, beat, heartbeats }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await waitFor(() => heartbeats.some(h => (h.livePages ?? []).length === 1), 15000);

    // A real MV3 worker stop discards globals; the registry is written through to session storage for exactly this.
    const cdp = await context.newCDPSession(target);
    await cdp.send('ServiceWorker.enable');
    await cdp.send('ServiceWorker.stopAllWorkers');

    const wake = await context.newPage();
    await wake.goto(`chrome-extension://${new URL(worker.url()).host}/popup.html`);
    await wake.evaluate(() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.5 }));
    await wake.close();

    await waitFor(async () => (await beat()).length === 1, 25000, () => JSON.stringify(heartbeats.at(-1)));
    assert.equal((await beat())[0].route, '/');
  });
});

async function waitFor(predicate, timeout, detail = () => '') {
  const end = Date.now() + timeout;
  let value = await predicate();
  while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 150)); value = await predicate(); }
  assert.ok(value, `Timed out: ${detail()}`);
}
