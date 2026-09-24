// Element picker for Critical E2E authoring. Runs the shipped libraries in a real Chromium page: selector candidates are
// validated by the replay resolver, the application never sees the picking click, and nothing is left behind.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');

const libs = ['sanitize.js', 'page-identity.js', 'automation.js', 'picker.js']
  .map(f => readFileSync(new URL(`../lib/${f}`, import.meta.url), 'utf8')).join('\n;\n');
let browser, page;
before(async () => { browser = await chromium.launch({ headless: true }); page = await browser.newPage(); });
after(async () => { await browser?.close(); });

async function load(body) {
  await page.route('https://app.example.test/**', r => r.fulfill({ status: 200, contentType: 'text/html',
    body: `<!doctype html><html lang="nb"><head><title>M02</title></head><body><main>${body}</main></body></html>` }));
  await page.goto('https://app.example.test/plassering/42?tab=ny');
  await page.evaluate(src => { (0, eval)(src); }, libs);
  await page.evaluate(() => { window.__appClicks = 0; document.addEventListener('click', () => { window.__appClicks++; }); });
}
const describe = sel => page.evaluate(s => {
  const r = BirkNextCompanion.picker.describe(document, window, BirkNextCompanion.picker.targetOf(document.querySelector(s)));
  return r.descriptor ?? r;
}, sel);
const replayResolves = (selector, sel) => page.evaluate(([selector, s]) =>
  BirkNextCompanion.automation.find(document, window, selector).element === BirkNextCompanion.picker.targetOf(document.querySelector(s)), [selector, sel]);

test('a test id is the recommended selector, and replay resolves it to the same element', async () => {
  await load('<button data-testid="create-placement">Ny plassering</button>');
  const d = await describe('button');
  assert.deepEqual(d.recommended, { kind: 'TestId', value: 'create-placement' });
  assert.deepEqual(d.candidates.map(c => [c.selector.kind, c.unique]), [['TestId', true], ['Role', true], ['Text', true], ['Css', true]]);
  assert.equal(d.tagName, 'button'); assert.equal(d.role, 'button'); assert.equal(d.accessibleName, 'Ny plassering');
  assert.equal(d.pageOrigin, 'https://app.example.test'); assert.equal(d.pageRoute, '/plassering/42', 'no query string');
  assert.equal(await replayResolves(d.recommended, 'button'), true);
});

test('without a test id, a unique role and accessible name is recommended', async () => {
  await load('<nav><a href="/plassering?x=1">Plassering</a></nav><button>Ny plassering</button>');
  const d = await describe('button');
  assert.deepEqual(d.recommended, { kind: 'Role', value: '', role: 'button', name: 'Ny plassering' });
  const link = await describe('a');
  assert.equal(link.href, 'https://app.example.test/plassering', 'link target keeps origin and path only');
});

test('a form control is identified by its label; its value never leaves the page', async () => {
  await load('<label for="fra">Fra dato</label><input id="fra" type="date"><label>Kommentar <textarea></textarea></label>');
  await page.fill('#fra', '2026-09-24');
  await page.fill('textarea', 'Ola Nordmann ola@example.no');
  const date = await describe('#fra');
  assert.deepEqual(date.recommended, { kind: 'Label', value: 'Fra dato' });
  assert.equal(date.inputType, 'date'); assert.equal(date.label, 'Fra dato');
  const note = await describe('textarea');
  assert.equal(note.recommended.kind, 'Label');
  for (const d of [date, note]) assert.ok(!JSON.stringify(d).includes('2026-09-24') && !JSON.stringify(d).includes('Ola'), 'no field value in the descriptor');
});

test('an ambiguous name is not recommended; structural CSS is the last resort', async () => {
  await load('<section><button>Lagre</button></section><section><button>Lagre</button></section>');
  const d = await describe('section:nth-of-type(2) button');
  const role = d.candidates.find(c => c.selector.kind === 'Role');
  assert.equal(role.unique, false); assert.equal(role.matchCount, 2);
  assert.equal(d.recommended.kind, 'Css');
  assert.equal(await replayResolves(d.recommended, 'section:nth-of-type(2) button'), true);
});

test('nothing unique means no recommendation rather than a guess', async () => {
  // Two identical nameless subtrees deeper than the structural selector reaches: no strategy can tell them apart.
  const deep = '<div><div><div><div><div><button aria-label=""></button></div></div></div></div></div>';
  await load(`<div>${deep}</div><div>${deep}</div>`.replace(/<div><div>/, '<div class="a"><div>'));
  const d = await page.evaluate(() => {
    const target = document.querySelectorAll('button')[1];
    return BirkNextCompanion.picker.describe(document, window, target).descriptor;
  });
  assert.ok(d.candidates.length > 0 && d.candidates.every(c => !c.unique), JSON.stringify(d.candidates));
  assert.equal(d.recommended, null);
});

test('a name that looks like personal data is not turned into a selector', async () => {
  await load('<button>ola.nordmann@example.no</button>');
  const d = await describe('button');
  assert.ok(d.candidates.every(c => !['Role', 'Text'].includes(c.selector.kind)), 'e-mail-shaped names are not offered');
  assert.ok(!JSON.stringify(d).includes('ola.nordmann@example.no'));
});

test('a click on a child resolves to its control; hidden and disabled state is reported', async () => {
  await load('<button disabled data-testid="lagre"><span>Lagre</span></button><button data-testid="skjult" style="display:none">Skjult</button>');
  const d = await describe('span');
  assert.equal(d.tagName, 'button'); assert.equal(d.enabled, false); assert.equal(d.visible, true);
  const hidden = await describe('[data-testid=skjult]');
  assert.equal(hidden.visible, false);
});

async function startPick(timeoutMs = 5000) {
  await page.evaluate(ms => {
    window.__picked = BirkNextCompanion.picker.pick(document, window, { timeoutMs: ms })
      .then(r => ({ status: r.status, tag: r.element?.tagName.toLowerCase() ?? null, text: r.element?.textContent.trim() ?? null }));
  }, timeoutMs);
}
const picked = () => page.evaluate(() => window.__picked);
const leftovers = () => page.evaluate(() => document.querySelectorAll('[data-birknext-picker]').length);

test('hover highlights, click selects, and the application never receives the click', async () => {
  await load('<button id="b" onclick="window.__saved = true"><span>Ny plassering</span></button><a href="/annen">Annen</a>');
  await startPick();
  assert.equal(await page.evaluate(() => document.querySelector('[data-birknext-picker=banner]')?.getAttribute('role')), 'status');
  await page.hover('#b span');
  const box = await page.evaluate(() => getComputedStyle(document.querySelector('[data-birknext-picker=highlight]')).display);
  assert.equal(box, 'block', 'the hovered element is outlined');
  await page.click('#b span');
  assert.deepEqual(await picked(), { status: 'picked', tag: 'button', text: 'Ny plassering' });
  assert.equal(await page.evaluate(() => window.__saved ?? false), false, 'the button handler did not run');
  assert.equal(await page.evaluate(() => window.__appClicks), 0, 'no click reached the application');
  assert.equal(await leftovers(), 0, 'overlay and banner removed');
  assert.equal(page.url(), 'https://app.example.test/plassering/42?tab=ny');
  // After picking, the page behaves normally again.
  await page.click('#b');
  assert.equal(await page.evaluate(() => window.__saved), true);
});

test('a picked link does not navigate', async () => {
  await load('<a href="/annen">Annen side</a>');
  await startPick();
  await page.click('a');
  assert.equal((await picked()).tag, 'a');
  assert.equal(page.url(), 'https://app.example.test/plassering/42?tab=ny');
});

test('Esc cancels and restores the page; Enter selects the focused element', async () => {
  await load('<input aria-label="Søk"><button>Søk nå</button>');
  await startPick();
  await page.keyboard.press('Escape');
  assert.deepEqual(await picked(), { status: 'cancelled', tag: null, text: null });
  assert.equal(await leftovers(), 0);

  await startPick();
  await page.keyboard.press('Tab');   // focus moves normally: no trap
  await page.keyboard.press('Enter');
  assert.equal((await picked()).tag, 'input');
  assert.equal(await page.evaluate(() => window.__appClicks), 0);
});

test('an unanswered pick times out and cleans up', async () => {
  await load('<button>Lagre</button>');
  await startPick(1000);
  assert.equal((await picked()).status, 'timeout');
  assert.equal(await leftovers(), 0);
});

test('shadow DOM: a pick inside an open shadow root identifies its host (document-level resolution only)', async () => {
  await load('<x-card data-testid="kort"></x-card>');
  await page.evaluate(() => { document.querySelector('x-card').attachShadow({ mode: 'open' }).innerHTML = '<button>Inne</button>'; });
  await startPick();
  await page.click('x-card >> button');
  assert.equal((await picked()).tag, 'x-card', 'events retarget to the host; replay cannot reach inside the shadow root');
});
