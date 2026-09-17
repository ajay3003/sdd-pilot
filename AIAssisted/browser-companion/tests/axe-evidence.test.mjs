import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
const require = createRequire(import.meta.url);
const { summarize, collector } = require('../lib/axe-evidence.js');
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');

test('axe outcomes retain typed meaning and transport no HTML or node data', () => {
  const rule = { id: 'image-alt', tags: ['wcag2a', 'wcag111'], nodes: [{ html: '<img src="secret">', target: ['#personal-data'] }] };
  const result = summarize({ testEngine: { version: '4.11.1' }, violations: [rule], incomplete: [rule], passes: [rule], inapplicable: [rule] }, 'visit-1');
  assert.deepEqual(result.rules.map(r => r.outcome), ['Fail', 'ManualReviewRequired', 'Pass', 'NotApplicable']);
  assert.deepEqual(result.rules[0].criterionIds, ['1.1.1']);
  assert.ok(!JSON.stringify(result).includes('secret')); assert.ok(!JSON.stringify(result).includes('personal-data'));
});

test('unchanged evidence executes once; new evidence revision executes again', async () => {
  let runs = 0;
  const collect = collector({ run: async () => { runs++; return {}; } });
  await Promise.all([collect({}, 'one'), collect({}, 'one')]);
  assert.equal(runs, 1);
  await collect({}, 'two'); assert.equal(runs, 2);
});

test('missing or failed axe execution stays unavailable, never a clean result', async () => {
  assert.equal((await collector(null)({}, 'one')).state, 'Unavailable');
  assert.equal((await collector({ run: async () => { throw new Error('unavailable'); } })({}, 'one')).state, 'Unavailable');
});

test('bundled axe executes automatically in Companion content flow and emits safe criterion evidence', async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.route('http://app.test/**', route => route.fulfill({ contentType: 'text/html', body:
      '<!doctype html><html lang="en"><head><title>Fixture</title></head><body><main><h1>Fixture</h1><img src="broken.png"><button></button></main></body></html>' }));
    await page.goto('http://app.test/dashboard');
    await page.evaluate(() => {
      window.sentEvidence = [];
      window.chrome = { runtime: { id: 'fixture', lastError: null, onMessage: { addListener() {} }, sendMessage(message, callback) {
        if (message.type === 'content:session') callback({ profileId: 'dev', approvedOrigins: ['http://app.test'], environmentType: 'Development' });
        else { if (message.type === 'content:evidence') window.sentEvidence.push(message.page); callback({ accepted: true }); }
      } } };
    });
    for (const script of ['lib/sanitize.js', 'lib/page-identity.js', 'lib/dom.js', 'lib/wcag.js', 'lib/wcag-interaction.js',
      'lib/wcag-keyboard.js', 'lib/a11y.js', 'lib/perf.js', 'lib/navigation.js', 'vendor/axe.min.js', 'lib/axe-evidence.js', 'content.js'])
      await page.addScriptTag({ content: readFileSync(new URL('../' + script, import.meta.url), 'utf8') });
    await page.waitForFunction(() => sentEvidence.some(p => p.accessibility?.axe?.state === 'Completed'), { timeout: 30000 });
    const evidence = await page.evaluate(() => sentEvidence.find(p => p.accessibility?.axe?.state === 'Completed'));
    assert.equal(evidence.pagePath, '/dashboard');
    assert.ok(evidence.accessibility.axe.rules.some(r => r.ruleId === 'image-alt' && r.outcome === 'Fail' && r.criterionIds.includes('1.1.1')));
    assert.ok(evidence.accessibility.axe.rules.some(r => r.ruleId === 'button-name' && r.outcome === 'Fail'));
    assert.ok(!JSON.stringify(evidence.accessibility.axe).includes('<img'));
  } finally { await browser.close(); }
});
