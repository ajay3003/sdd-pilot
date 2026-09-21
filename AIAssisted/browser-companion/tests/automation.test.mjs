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
  for (const action of ['eval', 'script', 'type', 'submit', 'click', undefined]) {
    const r = await run({ action, selector: { kind: 'Text', value: 'Save' } });
    assert.equal(r.status, 'failed');
    assert.match(r.error, /Unsupported action/);
  }
  assert.ok(automation.ACTIONS.every(a => /^[A-Z]/.test(a)), 'actions use the shared contract names');
});

test('an ambiguous selector fails instead of picking one, so a probe cannot silently exercise the wrong control', async () => {
  await load('<a href="#a">Open</a><a href="#b">Open</a>');
  const r = await run({ action: 'Click', selector: { kind: 'Text', value: 'Open' } });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /matched 2 elements/);
});

test('a click reaches the element and its own activation behaviour runs', async () => {
  await load('<button data-testid="go" onclick="document.title=\'clicked\'">Go</button>');
  const r = await run({ action: 'Click', selector: { kind: 'TestId', value: 'go' } });
  assert.equal(r.status, 'passed', JSON.stringify(r));
  assert.equal(await page.title(), 'clicked');
  assert.match(r.summary, /Clicked button "Go"/);
});

test('a disabled or invisible control is reported, never forced', async () => {
  await load('<button disabled data-testid="d">Delete</button><button data-testid="h" style="display:none">Hidden</button>' +
    '<button data-testid="a" aria-disabled="true">Archive</button>');
  for (const [id, expected] of [['d', /disabled/], ['h', /not visible/], ['a', /disabled/]]) {
    const r = await run({ action: 'Click', selector: { kind: 'TestId', value: id } });
    assert.equal(r.status, 'failed');
    assert.match(r.error, expected);
  }
  assert.equal(await page.evaluate(() => document.title), '');
});

test('role and label selectors resolve by accessible name, including aria-labelledby', async () => {
  await load('<h2 id="t">Cases</h2><button aria-labelledby="t">x</button><label for="q">Search</label><input id="q">');
  assert.equal((await run({ action: 'AssertVisible', selector: { kind: 'Role', role: 'button', name: 'Cases' } })).status, 'passed');
  assert.equal((await run({ action: 'AssertVisible', selector: { kind: 'Label', value: 'Search' } })).status, 'passed');
  assert.equal((await run({ action: 'AssertVisible', selector: { kind: 'Role', role: 'button', name: 'Missing' } })).status, 'failed');
});

test('assertions distinguish a real outcome from a missing element', async () => {
  await load('<a href="#x" data-testid="link">Saker (12)</a>');
  assert.equal((await run({ action: 'AssertText', selector: { kind: 'TestId', value: 'link' }, expected: 'Saker (12)' })).status, 'passed');
  assert.equal((await run({ action: 'AssertText', selector: { kind: 'TestId', value: 'link' }, expected: 'Saker', match: 'Contains' })).status, 'passed');
  assert.equal((await run({ action: 'AssertText', selector: { kind: 'TestId', value: 'link' }, expected: 'Saker' })).status, 'failed', 'Equals is exact; a partial match needs Contains');
  const wrong = await run({ action: 'AssertText', selector: { kind: 'TestId', value: 'link' }, expected: 'Arkiv' });
  assert.equal(wrong.status, 'failed');
  assert.match(wrong.error, /not found/);
  const missing = await run({ action: 'AssertVisible', selector: { kind: 'TestId', value: 'nope' } });
  assert.match(missing.error, /No element matched/);
});

test('assertRoute compares the path only, so query strings and ids never leak into an assertion', async () => {
  await page.goto('data:text/html,<body>x</body>');
  await page.evaluate(src => { (0, eval)(src); }, libs);
  const r = await page.evaluate(() => BirkNextCompanion.automation.perform(document, window, { action: 'AssertRoute', expected: window.location.pathname }));
  assert.equal(r.status, 'passed');
  const bad = await page.evaluate(() => BirkNextCompanion.automation.perform(document, window, { action: 'AssertRoute', expected: '/elsewhere' }));
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
  const r = await run({ action: 'AssertVisible', selector: { kind: 'Css', value: ':::bad' } });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /could not be evaluated/);
});

// ── Fill / Select: the actions that must survive a component framework ────────

test('Fill reaches a framework that listens for input and change, not just the DOM property', async () => {
  // Blazor and React-style frameworks never see a bare value assignment. This fixture records exactly what the
  // application would observe, so a fill that only mutated the property would show up as a missing event.
  await load('<input data-testid="q"><p id="model"></p>');
  await page.evaluate(() => {
    const el = document.querySelector('[data-testid=q]');
    window.__events = [];
    for (const type of ['input', 'change']) el.addEventListener(type, e => {
      window.__events.push({ type, bubbles: e.bubbles, value: e.target.value });
      document.getElementById('model').textContent = e.target.value;   // stands in for the bound model
    });
  });

  const r = await run({ action: 'Fill', selector: { kind: 'TestId', value: 'q' }, value: 'M2LB-E2E-42' });
  assert.equal(r.status, 'passed', JSON.stringify(r));
  const events = await page.evaluate(() => window.__events);
  assert.deepEqual(events.map(e => e.type), ['input', 'change']);
  assert.ok(events.every(e => e.bubbles), 'a non-bubbling event never reaches a framework listening higher up');
  assert.equal(await page.textContent('#model'), 'M2LB-E2E-42', 'the bound model must hold the value, not just the input');
  assert.equal(r.observedValue, 'M2LB-E2E-42');
});

test('Fill verifies the control kept the value rather than trusting that the assignment returned', async () => {
  // A control that normalises or rejects input must not read as a pass. This one refuses anything but digits.
  await load('<input data-testid="n">');
  await page.evaluate(() => {
    const el = document.querySelector('[data-testid=n]');
    el.addEventListener('input', () => { el.value = el.value.replace(/\D/g, ''); });
  });
  const r = await run({ action: 'Fill', selector: { kind: 'TestId', value: 'n' }, value: 'SAK-42' });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /did not take the value/);
  assert.equal(r.observedValue, '42');
});

test('Fill refuses a disabled or read-only control rather than forcing a value in', async () => {
  await load('<input data-testid="d" disabled><input data-testid="r" readonly><input data-testid="a" aria-readonly="true">');
  for (const id of ['d', 'r', 'a']) {
    const r = await run({ action: 'Fill', selector: { kind: 'TestId', value: id }, value: 'x' });
    assert.equal(r.status, 'failed', id);
    assert.match(r.error, /disabled or read-only/);
  }
  assert.deepEqual(await page.evaluate(() => Array.from(document.querySelectorAll('input')).map(i => i.value)), ['', '', '']);
});

test('Select operates a native select by value or by option text and announces the change', async () => {
  await load('<select data-testid="s"><option value="">--</option><option value="a">Aktiv</option><option value="b" disabled>Avsluttet</option></select>');
  await page.evaluate(() => {
    window.__changes = 0;
    document.querySelector('[data-testid=s]').addEventListener('change', () => window.__changes++);
  });

  assert.equal((await run({ action: 'Select', selector: { kind: 'TestId', value: 's' }, value: 'a' })).status, 'passed');
  assert.equal(await page.evaluate(() => window.__changes), 1);
  assert.equal((await run({ action: 'Select', selector: { kind: 'TestId', value: 's' }, value: 'Aktiv' })).status, 'passed');

  const missing = await run({ action: 'Select', selector: { kind: 'TestId', value: 's' }, value: 'Nonsense' });
  assert.equal(missing.status, 'failed');
  assert.match(missing.error, /No option/);

  const locked = await run({ action: 'Select', selector: { kind: 'TestId', value: 's' }, value: 'b' });
  assert.equal(locked.status, 'failed');
  assert.match(locked.error, /disabled/);
});

test('Select refuses a custom dropdown rather than reaching into its hidden state', async () => {
  await load('<div data-testid="combo" role="combobox">Velg</div>');
  const r = await run({ action: 'Select', selector: { kind: 'TestId', value: 'combo' }, value: 'x' });
  assert.equal(r.status, 'failed');
  assert.match(r.error, /Click steps/, 'a component is operated the way a user operates it');
});

// ── The remaining V1 actions ──────────────────────────────────────────────────

test('AssertHidden accepts both absent and present-but-hidden, and rejects visible', async () => {
  await load('<p data-testid="shown">x</p><p data-testid="gone" style="display:none">y</p>');
  assert.equal((await run({ action: 'AssertHidden', selector: { kind: 'TestId', value: 'gone' } })).status, 'passed');
  assert.equal((await run({ action: 'AssertHidden', selector: { kind: 'TestId', value: 'missing' } })).status, 'passed');
  assert.equal((await run({ action: 'AssertHidden', selector: { kind: 'TestId', value: 'shown' } })).status, 'failed');
});

test('AssertValue and ReadValue observe the control, and ReadValue feeds a later step', async () => {
  await load('<input data-testid="id" value="SAK-42">');
  assert.equal((await run({ action: 'AssertValue', selector: { kind: 'TestId', value: 'id' }, expected: 'SAK-42' })).status, 'passed');
  const read = await run({ action: 'ReadValue', selector: { kind: 'TestId', value: 'id' } });
  assert.equal(read.status, 'passed');
  assert.equal(read.observedValue, 'SAK-42');
});

test('Navigate prefers the application own control and refuses to leave the origin', async () => {
  await load('<a data-testid="nav" href="#x" onclick="document.title=&quot;navigated&quot;">Saker</a>');
  assert.equal((await run({ action: 'Navigate', selector: { kind: 'TestId', value: 'nav' } })).status, 'passed');
  assert.equal(await page.title(), 'navigated');

  for (const value of ['https://elsewhere.example/x', '//elsewhere.example/x', 'javascript:alert(1)', 'saker']) {
    const r = await run({ action: 'Navigate', value });
    assert.equal(r.status, 'failed', value);
    assert.match(r.error, /same-origin path/);
  }
});

test('an assertion carries a typed assertionResult so a false assertion is never read as an error', async () => {
  await load('<p data-testid="p">x</p>');
  assert.equal((await run({ action: 'AssertVisible', selector: { kind: 'TestId', value: 'p' } })).assertionResult, true);
  assert.equal((await run({ action: 'AssertHidden', selector: { kind: 'TestId', value: 'p' } })).assertionResult, false);
  assert.equal((await run({ action: 'Click', selector: { kind: 'TestId', value: 'p' } })).assertionResult, undefined,
    'an action makes no assertion');
});
