import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const wcag = require('../lib/wcag.js');
const interaction = require('../lib/wcag-interaction.js');
// Test harness only, matching the existing companion fixtures. The shipped engine has no browser automation dependency.
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');
let browser, page;
const libs = ['sanitize', 'dom', 'wcag', 'wcag-interaction', 'wcag-keyboard', 'a11y'].map(f => readFileSync(new URL(`../lib/${f}.js`, import.meta.url), 'utf8')).join('\n');
before(async () => { browser = await chromium.launch({ headless: true }); page = await browser.newPage(); });
after(async () => { await browser?.close(); });
async function load(body, css = '') {
  await page.setContent(`<!doctype html><html lang="en"><head><title>Fixture</title><style>html{background:white;color:black}${css}</style></head><body><main><h1>Fixture</h1>${body}</main></body></html>`);
  await page.evaluate(libs => { (0, eval)(libs); }, libs);
}
async function collect() { return page.evaluate(() => BirkNextCompanion.a11y.evaluate(document, window)); }
const check = (r, id) => r.checks.find(c => c.checkId === id);

test('WCAG sRGB endpoints, known ratio, alpha and large text thresholds', () => {
  assert.equal(wcag.ratio([0, 0, 0, 1], [255, 255, 255, 1]), 21);
  assert.equal(wcag.ratio([255, 255, 255, 1], [255, 255, 255, 1]), 1);
  assert.ok(Math.abs(wcag.ratio([119, 119, 119, 1], [255, 255, 255, 1]) - 4.478) < .001);
  assert.deepEqual(wcag.composite([0, 0, 0, .5], [255, 255, 255, 1]), [127.5, 127.5, 127.5, 1]);
  assert.equal(wcag.ratio([0, 0, 0, 1], [255, 255, 255, .5]), null);
  assert.equal(wcag.threshold(24, 400), 3); assert.equal(wcag.threshold(19, 700), 3);
  assert.equal(wcag.threshold(18, 700), 4.5); assert.equal(wcag.color('color(display-p3 1 0 0)'), null);
});
test('pass and fail are explicit check outcomes, not inferred from empty finding lists', async () => {
  await load('<img alt="description"><button>Save</button>');
  assert.equal(check(await collect(), 'a11y-image-alt').outcome, 'Pass');
  await load('<img><button aria-label="Delete">Save</button>');
  const r = await collect();
  assert.equal(check(r, 'a11y-image-alt').outcome, 'Fail');
  assert.equal(check(r, 'label-in-name').outcome, 'Fail');
});
test('computed inherited contrast measures normal/large text and excludes disabled controls', async () => {
  await load('<p>Normal</p><p class="large">Large</p><button disabled>Disabled</button>', 'p,button{color:rgb(119,119,119)}.large{font-size:24px}');
  const c = check(await collect(), 'text-contrast');
  assert.equal(c.failed, 1);
});
test('gradient and opacity contrast remain uncertain', async () => {
  await load('<p class="gradient">Text</p><p class="alpha">Alpha</p>', '.gradient{background:linear-gradient(white,black)}.alpha{opacity:.5}');
  const c = check(await collect(), 'text-contrast');
  assert.ok(c.uncertain >= 2); assert.equal(c.failed, 0);
});
test('native media absence and embedded-media scope are distinct', async () => {
  await load('<p>No media</p>'); let r = await collect();
  assert.equal(r.videoCount, 0); assert.equal(r.audioCount, 0); assert.equal(r.mediaScopeComplete, true);
  await load('<iframe title="Embedded content"></iframe>'); r = await collect();
  assert.equal(r.mediaScopeComplete, false);
});
test('caption track absence is a review candidate, never proof of caption failure', async () => {
  await load('<video></video>');
  const c = check(await collect(), 'media-captions');
  assert.equal(c.uncertain, 1); assert.equal(c.failed, 0);
});
test('production and unknown policies never alter layout', async () => {
  for (const env of ['Production', 'Custom', undefined, 'production']) assert.equal(interaction.allowed(env, true), false);
  assert.equal(interaction.allowed('Development', false), false);
  await load('<p>Text</p>');
  const r = await page.evaluate(async () => {
    const before = document.documentElement.outerHTML;
    const checks = await BirkNextCompanion.wcagInteraction.layout(document, window, { environment: 'Production', approved: true });
    return { same: before === document.documentElement.outerHTML, checks };
  });
  assert.equal(r.same, true); assert.ok(r.checks.every(c => c.outcome === 'NotTested'));
});
test('spacing and resize detect clipping candidates and restore all styles; no action handlers run', async () => {
  await load('<form onsubmit="window.actions++"><button onclick="window.actions++">Delete</button><input value="secret-fixture-value"></form><p class="clip">Text that clips after enlargement and spacing</p>', '.clip{height:20px;overflow:hidden;white-space:nowrap;font-size:16px;width:360px}');
  const r = await page.evaluate(async () => {
    window.actions = 0;
    const count = document.querySelectorAll('style').length;
    const checks = await BirkNextCompanion.wcagInteraction.layout(document, window, { environment: 'Development', approved: true });
    return { checks, actions: window.actions, count, after: document.querySelectorAll('style').length, size: getComputedStyle(document.querySelector('.clip')).fontSize };
  });
  assert.equal(r.actions, 0); assert.equal(r.count, r.after); assert.equal(r.size, '16px');
  assert.ok(r.checks.every(c => c.uncertain > 0));
  assert.equal(JSON.stringify(r).includes('secret-fixture-value'), false);
});
test('reflow snapshot is not tested at an untested viewport and requires review at 320px', async () => {
  await load('<div style="width:900px">Overflow</div>');
  assert.equal(check(await collect(), 'reflow-snapshot').outcome, 'NotTested');
  await page.setViewportSize({ width: 320, height: 800 });
  try { const c = check(await collect(), 'reflow-snapshot'); assert.equal(c.uncertain, 1); assert.equal(c.outcome, 'ManualReviewRequired'); }
  finally { await page.setViewportSize({ width: 1280, height: 720 }); }
});
test('safe evidence contains no names, field values, raw DOM or body text', async () => {
  await load('<input value="SENSITIVE-FIXTURE" aria-label="Private person"><button aria-label="wrong">PRIVATE-LABEL</button><p role="status">PRIVATE-STATUS</p>');
  const json = JSON.stringify(await collect());
  for (const s of ['SENSITIVE-FIXTURE', 'Private person', 'PRIVATE-LABEL', 'PRIVATE-STATUS', '<input']) assert.equal(json.includes(s), false);
});
test('navigation/component evidence contains counts only across page fixtures', async () => {
  const results = [];
  for (const body of ['<nav><a href="/a">A</a></nav>', '<nav><a href="/a">A</a><a href="/b">B</a></nav>', '<nav><a href="/c">C</a></nav>']) {
    await load(body); results.push((await collect()).navigationStructure);
  }
  assert.deepEqual(results, [[1], [2], [1]]);
});
async function startKeyboard() {
  await page.evaluate(() => {
    window.observation = BirkNextCompanion.wcagKeyboard.observe(document, window,
      { environment: 'Development', approved: true }, r => { window.observed = r; });
  });
}
async function tab() { await page.keyboard.press('Tab'); await page.evaluate(() => new Promise(requestAnimationFrame)); }
test('trusted keyboard observation is bounded, detects focus candidates and never activates controls', async () => {
  await load('<button onclick="window.actions++">Delete</button><button>Next</button>', 'button:focus{outline:none;box-shadow:none}');
  await page.evaluate(() => { window.actions = 0; });
  await startKeyboard(); await tab(); await tab();
  const r = await page.evaluate(() => ({ checks: window.observation.stop(), actions: window.actions }));
  assert.equal(r.actions, 0); assert.equal(check(r, 'keyboard-traversal').tested, 2);
  assert.equal(check(r, 'focus-indicator').uncertain, 2);
});
test('prevented Tab is a possible trap and valid modal trapping is not failed', async () => {
  await load('<button onkeydown="if(event.key===\'Tab\')event.preventDefault()">Trapped</button>');
  await startKeyboard(); await tab(); await tab();
  let r = await page.evaluate(() => ({ checks: window.observation.stop() }));
  assert.ok(check(r, 'keyboard-traversal').uncertain > 0);
  assert.equal(check(r, 'keyboard-traversal').failed, 0);
  await load('<div role="dialog" aria-modal="true" aria-label="Dialog"><button onkeydown="if(event.key===\'Tab\')event.preventDefault()">Close</button></div>');
  await startKeyboard(); await tab(); await tab();
  r = await page.evaluate(() => ({ checks: window.observation.stop() }));
  assert.equal(check(r, 'keyboard-traversal').uncertain, 0);
  assert.equal(check(r, 'keyboard-traversal').outcome, 'ManualReviewRequired');
});
test('synthetic keys do not produce fabricated traversal evidence; stopped listeners no longer collect', async () => {
  await load('<button>Next</button>'); await startKeyboard();
  const r = await page.evaluate(() => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    return { checks: window.observation.stop() };
  });
  assert.equal(check(r, 'keyboard-traversal').outcome, 'NotTested');
  await tab();
  assert.deepEqual(await page.evaluate(() => window.observation.stop()), []);
});
test('native names, labels, lang, ARIA and contrast agree with axe on deterministic fixtures',
  { skip: !process.env.BIRKNEXT_AXE_SCRIPT && 'Set BIRKNEXT_AXE_SCRIPT to the bundled axe reference script.' }, async () => {
  await load('<button></button><input><img><p aria-labelledby="missing">Text</p><p style="color:#aaa">Poor contrast</p>');
  await page.evaluate(() => document.documentElement.removeAttribute('lang'));
  const native = await collect();
  await page.addScriptTag({ content: readFileSync(process.env.BIRKNEXT_AXE_SCRIPT, 'utf8') });
  const axe = await page.evaluate(async () => (await window.axe.run(document, {
    runOnly: { type: 'rule', values: ['button-name', 'label', 'image-alt', 'html-has-lang', 'color-contrast'] },
  })).violations.map(v => v.id));
  for (const [ours, reference] of [['a11y-button-name', 'button-name'], ['a11y-control-label', 'label'],
    ['a11y-image-alt', 'image-alt'], ['a11y-document-lang', 'html-has-lang'], ['text-contrast', 'color-contrast']]) {
    assert.ok(check(native, ours).failed > 0, ours); assert.ok(axe.includes(reference), reference);
  }
  assert.equal(check(native, 'a11y-aria-reference').failed, 1);
  // axe versions differ in duplicate-id/ARIA-reference treatment; no rule-count equivalence is asserted.
});
