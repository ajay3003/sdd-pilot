// Evidence after navigation, in the REAL extension.
//
// Liveness following a route is covered by live-page.test.mjs; this is about evidence. Browser Discovery records a page
// only when its evidence arrives, so every kind of navigation — SPA route, full navigation, reload, Back/Forward and a
// round trip through an unapproved sign-in origin — must end with evidence for the page the user is now on.
//
// `slowAxe` delays the bundled axe run in the test copy of the extension only. On a large application page an axe run
// outlasts the next route's stabilization window; that ordering is what used to lose every SPA route after the first.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtempSync, rmSync, cpSync, readFileSync, writeFileSync, appendFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';
const require = createRequire(import.meta.url);
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');
const root = fileURLToPath(new URL('..', import.meta.url));
const origin = 'https://m2lbdev.bufetat.no';
const signIn = 'https://login.microsoftonline.com';

const fixture = `<!doctype html><html lang="en"><head><title>M2LB fixture</title></head><body><main>
  <h1>Oversikt</h1>
  <a href="/saker" data-spa data-testid="spa-saker">Saker</a>
  <a href="/roller" data-spa data-testid="spa-roller">Roller</a>
  <a href="/arkiv" data-testid="full-arkiv">Arkiv</a>
  <script>
    for (const a of document.querySelectorAll('[data-spa]')) a.addEventListener('click', event => {
      event.preventDefault();
      history.pushState({}, '', a.getAttribute('href'));
      document.querySelector('h1').textContent = a.textContent;
    });
  </script>
</main></body></html>`;

async function withCompanion({ slowAxe = false } = {}, body) {
  const envelopes = [];
  const server = createServer(async (req, res) => {
    let raw = '';
    for await (const chunk of req) raw += chunk;
    if (req.url.split('/').at(-1) === 'evidence') envelopes.push({ at: Date.now(), pages: JSON.parse(raw || '{}').pages ?? [] });
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ accepted: true, sessionId: 'fixture-session', profileId: 'm2lb-dev',
      environmentName: 'M2LB', environmentType: 'Development', approvedOrigins: [origin] }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = mkdtempSync(path.join(tmpdir(), 'companion-nav-'));
  const extension = path.join(profile, 'extension');
  cpSync(root, extension, { recursive: true, filter: source => !['dist', 'tests'].includes(path.basename(source)) });
  const manifest = JSON.parse(readFileSync(path.join(extension, 'manifest.json'), 'utf8'));
  manifest.host_permissions.push(`${origin}/*`);
  writeFileSync(path.join(extension, 'manifest.json'), JSON.stringify(manifest));
  if (slowAxe) appendFileSync(path.join(extension, 'vendor', 'axe.min.js'),
    '\n;(function(){const run=axe.run.bind(axe);axe.run=async(...a)=>{await new Promise(r=>setTimeout(r,2500));return run(...a);};})();\n');
  let context;
  try {
    context = await chromium.launchPersistentContext(profile, { channel: 'chromium', headless: true,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`] });
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
    const popup = await context.newPage();
    await popup.goto(`chrome-extension://${new URL(worker.url()).host}/popup.html`);
    await popup.evaluate(backend => chrome.storage.local.set({ backend }), `http://127.0.0.1:${server.address().port}`);
    const status = await popup.evaluate(() => chrome.runtime.sendMessage({ type: 'popup:pair', pairingCode: 'FIXTURE' }));
    assert.equal(status.state, 'connected', JSON.stringify(status));
    await popup.close();
    await context.route(`${origin}/**`, route => route.fulfill({ contentType: 'text/html', body: fixture }));
    await context.route(`${signIn}/**`, route => route.fulfill({ contentType: 'text/html',
      body: `<!doctype html><html lang="en"><head><title>Sign in</title></head><body><main><h1>Sign in</h1><a href="${origin}/roller" data-testid="back-to-app">Continue</a></main></body></html>` }));

    const pages = () => envelopes.flatMap(e => e.pages);
    const pathsSeen = () => [...new Set(pages().map(p => p.pagePath))];
    /** Waits until evidence for `pagePath` exists that was produced by a visit starting after `since`. */
    const evidenceFor = (pagePath, since = 0) => waitFor(
      () => pages().some(p => p.pagePath === pagePath && Date.parse(p.visitStartedAt) >= since), 30000,
      () => `no evidence for ${pagePath} since ${new Date(since).toISOString()}; saw ${JSON.stringify(pages().map(p => [p.pagePath, p.snapshotKind, p.visitStartedAt]))}`);
    await body({ context, envelopes, pages, pathsSeen, evidenceFor });
  } finally {
    await context?.close();
    await new Promise(resolve => server.close(resolve));
    rmSync(profile, { recursive: true, force: true });
  }
}

test('the first page is captured', async () => {
  await withCompanion({}, async ({ context, evidenceFor }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await evidenceFor('/');
  });
});

test('every SPA route is captured, even when the previous page is still being assessed', async () => {
  await withCompanion({ slowAxe: true }, async ({ context, evidenceFor, pages }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await evidenceFor('/');

    let since = Date.now();
    await target.click('[data-testid=spa-saker]');
    await evidenceFor('/saker', since);

    since = Date.now();
    await target.click('[data-testid=spa-roller]');
    await evidenceFor('/roller', since);
    // No route borrowed another route's snapshot, and nothing was attributed to the wrong origin.
    assert.ok(pages().every(p => p.pageOrigin === origin));
  });
});

test('a full navigation, a reload and Back/Forward each produce evidence for the page now shown', async () => {
  await withCompanion({}, async ({ context, evidenceFor }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await evidenceFor('/');

    let since = Date.now();
    await target.click('[data-testid=full-arkiv]');
    await target.waitForURL(`${origin}/arkiv`);
    await evidenceFor('/arkiv', since);

    since = Date.now();
    await target.reload();
    await evidenceFor('/arkiv', since);

    since = Date.now();
    await target.goBack();
    await target.waitForURL(`${origin}/`);
    await evidenceFor('/', since);

    since = Date.now();
    await target.goForward();
    await target.waitForURL(`${origin}/arkiv`);
    await evidenceFor('/arkiv', since);
  });
});

test('a sign-in detour is never captured, and capture resumes on returning to the application', async () => {
  await withCompanion({}, async ({ context, evidenceFor, pages }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await evidenceFor('/');

    await target.goto(`${signIn}/common/oauth2/authorize`);
    const since = Date.now();
    await target.click('[data-testid=back-to-app]');
    await target.waitForURL(`${origin}/roller`);
    await evidenceFor('/roller', since);
    assert.ok(pages().every(p => p.pageOrigin === origin), 'the sign-in origin is not an application page');
  });
});

test('an idle page does not flood evidence', async () => {
  await withCompanion({}, async ({ context, envelopes, evidenceFor }) => {
    const target = await context.newPage();
    await target.goto(origin);
    await evidenceFor('/');
    await new Promise(resolve => setTimeout(resolve, 6000));
    const settled = envelopes.length;
    await new Promise(resolve => setTimeout(resolve, 10000));
    // A quiet page sends nothing further on its own: the 500 ms liveness poll and the 15 s page beat carry no evidence.
    assert.equal(envelopes.length, settled, JSON.stringify(envelopes.map(e => e.pages.map(p => p.snapshotKind))));
  });
});

async function waitFor(predicate, timeout, detail = () => '') {
  const end = Date.now() + timeout;
  let value = await predicate();
  while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 150)); value = await predicate(); }
  assert.ok(value, `Timed out: ${detail()}`);
}
