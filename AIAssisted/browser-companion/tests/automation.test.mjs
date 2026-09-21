// Typed page actions for Critical E2E probes. The fixture browser is test harness only — the shipped runner is the
// extension's own content script, which is what the end-to-end case in reporting.test.mjs exercises.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const automation = require('../lib/automation.js');
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');

let browser, page;
const libs = readFileSync(new URL('../lib/automation.js', import.meta.url), 'utf8');
before(async () => { browser = await chromium.launch({ headless: true }); page = await browser.newPage(); });
after(async () => { await browser?.close(); });

async function load(body, css = '') {
  await page.setContent(`<!doctype html><html lang="en"><head><style>${css}</style></head><body><main>${body}</main></body></html>`);
  await page.evaluate(src => { (0, eval)(src); }, libs);
}
const run = command => page.evaluate(c => BirkNextCompanion.automation.perform(document, window, c), command);

test('an action outside the allow-list is refused before the page is touched', async () => {
  await load('<button>Save</button>');
  for (const action of ['eval', 'navigate', 'type', 'submit', undefined]) {
    const r = await run({ action, selector: { kind: 'text', value: 'Save' } });
    assert.equal(r.status, 'failed');
    assert.match(r.error, /Unsupported action/);
  }
  assert.deepEqual(automation.ACTIONS, ['click', 'assertVisible', 'assertText', 'assertRoute']);
});

test('an ambiguous selector fails instead of picking one, so a probe cannot silently exercise the wrong control', async () => {
  await load('<a href="#a">Open</a><a href="#b">Open</a>');
  const r = await run({ action: 'click', selector: { kind: 'text', value: 'Open' } });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /matched 2 elements/);
});

test('a click reaches the element and its own activation behaviour runs', async () => {
  await load('<button data-testid="go" onclick="document.title=\'clicked\'">Go</button>');
  const r = await run({ action: 'click', selector: { kind: 'testid', value: 'go' } });
  assert.equal(r.status, 'passed', JSON.stringify(r));
  assert.equal(await page.title(), 'clicked');
  assert.match(r.summary, /Clicked button "Go"/);
});

test('a disabled or invisible control is reported, never forced', async () => {
  await load('<button disabled data-testid="d">Delete</button><button data-testid="h" style="display:none">Hidden</button>' +
    '<button data-testid="a" aria-disabled="true">Archive</button>');
  for (const [id, expected] of [['d', /disabled/], ['h', /not visible/], ['a', /disabled/]]) {
    const r = await run({ action: 'click', selector: { kind: 'testid', value: id } });
    assert.equal(r.status, 'failed');
    assert.match(r.error, expected);
  }
  assert.equal(await page.evaluate(() => document.title), '');
});

test('role and label selectors resolve by accessible name, including aria-labelledby', async () => {
  await load('<h2 id="t">Cases</h2><button aria-labelledby="t">x</button><label for="q">Search</label><input id="q">');
  assert.equal((await run({ action: 'assertVisible', selector: { kind: 'role', role: 'button', name: 'Cases' } })).status, 'passed');
  assert.equal((await run({ action: 'assertVisible', selector: { kind: 'label', value: 'Search' } })).status, 'passed');
  assert.equal((await run({ action: 'assertVisible', selector: { kind: 'role', role: 'button', name: 'Missing' } })).status, 'failed');
});

test('assertions distinguish a real outcome from a missing element', async () => {
  await load('<a href="#x" data-testid="link">Saker (12)</a>');
  assert.equal((await run({ action: 'assertText', selector: { kind: 'testid', value: 'link' }, expected: 'Saker' })).status, 'passed');
  const wrong = await run({ action: 'assertText', selector: { kind: 'testid', value: 'link' }, expected: 'Arkiv' });
  assert.equal(wrong.status, 'failed');
  assert.match(wrong.error, /not found/);
  const missing = await run({ action: 'assertVisible', selector: { kind: 'testid', value: 'nope' } });
  assert.match(missing.error, /No element matched/);
});

test('assertRoute compares the path only, so query strings and ids never leak into an assertion', async () => {
  await page.goto('data:text/html,<body>x</body>');
  await page.evaluate(src => { (0, eval)(src); }, libs);
  const r = await page.evaluate(() => BirkNextCompanion.automation.perform(document, window, { action: 'assertRoute', expected: window.location.pathname }));
  assert.equal(r.status, 'passed');
  const bad = await page.evaluate(() => BirkNextCompanion.automation.perform(document, window, { action: 'assertRoute', expected: '/elsewhere' }));
  assert.equal(bad.status, 'failed');
  assert.match(bad.error, /Expected route \/elsewhere/);
});

test('waitFor polls until the condition holds and gives up at the bounded timeout', async () => {
  let clock = 0, calls = 0;
  const timers = [];
  const opts = { timeoutMs: 1000, intervalMs: 100, now: () => clock, setTimer: fn => timers.push(fn) };
  const drain = async () => { while (timers.length) { clock += 100; timers.shift()(); await null; } };

  const eventual = automation.waitFor(() => ++calls >= 3, opts);
  await drain();
  assert.deepEqual(await eventual, { ok: true, attempts: 3 });

  clock = 0;
  const never = automation.waitFor(() => false, opts);
  await drain();
  const result = await never;
  assert.equal(result.ok, false);
  assert.equal(result.timedOut, true);
  assert.ok(result.attempts <= 11, `bounded, got ${result.attempts}`);
});

test('a selector that cannot be evaluated is an error, not an exception into the page', async () => {
  await load('<p>x</p>');
  const r = await run({ action: 'assertVisible', selector: { kind: 'css', value: ':::bad' } });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /could not be evaluated/);
});
