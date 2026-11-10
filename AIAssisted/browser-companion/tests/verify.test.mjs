// Selector verification for suggested E2E tests (authoring only) and the password-field guard. The fixture browser is test harness only.
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

async function load(body) {
  await page.setContent(`<!doctype html><html lang="en"><body><main>${body}</main></body></html>`);
  await page.evaluate(src => { (0, eval)(src); }, libs);
}
const verify = (selectors, max) => page.evaluate(([s, m]) => BirkNextCompanion.automation.verify(document, window, s, m), [selectors, max]);
const run = command => page.evaluate(c => BirkNextCompanion.automation.perform(document, window, c), command);

test('VerifySelectors is authoring only: not an executable action', () => {
  assert.ok(!automation.ACTIONS.includes('VerifySelectors'));
  assert.ok(!automation.MUTATING.includes('VerifySelectors'));
});

test('each selector reports its match count and the first match identity — unique, ambiguous, missing', async () => {
  await load(`<form><label for="n">Name</label><input id="n" data-testid="child-name" />
    <button type="submit" data-testid="save">Save</button><button type="button">Cancel</button><button type="button">Cancel</button>
    <button disabled data-testid="off">Off</button><span style="display:none" data-testid="hidden">x</span></form>`);
  const r = await verify([
    { kind: 'TestId', value: 'child-name' }, { kind: 'Label', value: 'Name' }, { kind: 'Role', role: 'button', name: 'Save' },
    { kind: 'Text', value: 'Cancel' }, { kind: 'TestId', value: 'missing' }, { kind: 'TestId', value: 'off' }, { kind: 'TestId', value: 'hidden' },
  ]);
  const by = r.checks.map(c => [c.matchCount, c.visible, c.enabled, c.role]);
  assert.deepEqual(by[0], [1, true, true, 'textbox']);
  assert.deepEqual(by[1], [1, true, true, 'textbox']);
  assert.deepEqual(by[2], [1, true, true, 'button']);
  assert.equal(r.checks[3].matchCount, 2, 'ambiguity is reported, never resolved to the first match');
  assert.equal(r.checks[4].matchCount, 0);
  assert.deepEqual([r.checks[5].visible, r.checks[5].enabled], [true, false]);
  assert.deepEqual([r.checks[6].matchCount, r.checks[6].visible], [1, false]);
});

test('no value, no HTML, no text beyond a short accessible name; password fields report no name', async () => {
  await load('<label>Password <input type="password" aria-label="Password" value="hunter2-secret" data-testid="pw"></label><input data-testid="t" value="typed-value">');
  const r = await verify([{ kind: 'TestId', value: 'pw' }, { kind: 'TestId', value: 't' }]);
  const json = JSON.stringify(r);
  assert.ok(!json.includes('hunter2-secret') && !json.includes('typed-value') && !json.includes('<'), json);
  assert.equal(r.checks[0].accessibleName, null);
});

test('bounded: too many selectors, none, or an unsupported kind are refused or reported', async () => {
  await load('<button>Go</button>');
  assert.match((await verify(Array.from({ length: 17 }, () => ({ kind: 'Text', value: 'Go' })), 16)).error, /At most 16/);
  assert.match((await verify([], 16)).error, /No selectors/);
  const bad = await verify([{ kind: 'Script', value: 'alert(1)' }], 16);
  assert.match(bad.checks[0].limitation, /Unsupported selector kind/);
});

test('a frame on the page is stated as not searched when a selector is not found in the top frame', async () => {
  await load('<iframe srcdoc="<button data-testid=inner>x</button>"></iframe>');
  const r = await verify([{ kind: 'TestId', value: 'inner' }], 16);
  assert.equal(r.checks[0].matchCount, 0);
  assert.match(r.checks[0].limitation, /1 frame\(s\) on the page are not searched/);
});

test('password fields are never read or compared by ReadValue / AssertValue, and Fill never echoes the secret', async () => {
  await load('<input type="password" data-testid="pw" value="s3cret">');
  const read = await run({ action: 'ReadValue', selector: { kind: 'TestId', value: 'pw' } });
  assert.equal(read.status, 'failed');
  assert.equal(read.observedValue, undefined);
  const cmp = await run({ action: 'AssertValue', selector: { kind: 'TestId', value: 'pw' }, expected: 's3cret' });
  assert.equal(cmp.status, 'failed');
  assert.ok(!JSON.stringify(cmp).includes('s3cret'));
  const fill = await run({ action: 'Fill', selector: { kind: 'TestId', value: 'pw' }, value: 'new-secret' });
  assert.equal(fill.status, 'passed');
  assert.ok(!JSON.stringify(fill).includes('new-secret'));
});
