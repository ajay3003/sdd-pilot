// BirkNext Accessibility Checks — deterministic rule tests over fixed HTML fixtures, evaluated in a real DOM (Playwright Chromium,
// no navigation, no network, no wall-clock timing). Each fixture is loaded with page.setContent and the rule library is injected.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('C:/Users/ajaan/AppData/Roaming/npm/node_modules/playwright');

const libDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'lib');
const libs = ['sanitize.js', 'dom.js', 'a11y.js'].map(f => readFileSync(path.join(libDir, f), 'utf8')).join('\n');

let browser, page;
before(async () => { browser = await chromium.launch({ headless: true }); page = await browser.newPage(); });
after(async () => { await browser.close(); });

async function evaluate(html) {
  await page.setContent(html, { waitUntil: 'domcontentloaded' });
  return page.evaluate(src => { eval(src); return globalThis.BirkNextCompanion.a11y.evaluate(document, window); }, libs);
}
const ids = result => result.findings.map(f => f.ruleId).sort();
const find = (result, id) => result.findings.find(f => f.ruleId === id);

const VALID = `<!doctype html><html lang="nb"><head><title>Barn — søk</title></head><body>
<header><h1>Søk etter barn</h1></header>
<main>
  <form><label for="q">Navn</label><input id="q" type="text" /><button type="submit">Søk</button></form>
  <h2>Resultater</h2><img src="a.png" alt="Barn" /><a href="/barn/1">Åpne barn</a>
  <img src="deco.png" alt="" />
</main></body></html>`;

test('valid page produces no findings', async () => {
  const r = await evaluate(VALID);
  assert.deepEqual(ids(r), []);
  assert.equal(r.engine, 'BirkNext Accessibility Checks');
  assert.ok(r.rulesEvaluated >= 14);
});

test('missing button name', async () => {
  const r = await evaluate(VALID.replace('<button type="submit">Søk</button>', '<button type="submit"><span class="icon"></span></button><button aria-label="Lukk"></button>'));
  const f = find(r, 'a11y-button-name');
  assert.equal(f.count, 1, 'the aria-labelled button is fine');
  assert.equal(f.severity, 'High');
  assert.equal(f.wcag, '4.1.2');
  assert.ok(f.selectors[0].startsWith('button') || f.selectors[0].includes('button'));
});

test('missing form label (label association, wrapping label and aria-label all count as labelled)', async () => {
  const r = await evaluate(VALID.replace('<h2>Resultater</h2>', '<h2>Resultater</h2><input type="text" id="unlabelled" /><label>Alder <input type="number" /></label><select aria-label="Type"><option>A</option></select>'));
  assert.equal(find(r, 'a11y-control-label').count, 1);
  assert.deepEqual(find(r, 'a11y-control-label').selectors, ['input#unlabelled']);
});

test('duplicate id and dangling ARIA reference', async () => {
  const r = await evaluate(VALID.replace('<h2>Resultater</h2>', '<h2>Resultater</h2><div id="q">dup</div><p aria-describedby="nope">x</p>'));
  assert.equal(find(r, 'a11y-duplicate-id').count, 2, 'both elements sharing the id are reported');
  assert.equal(find(r, 'a11y-aria-reference').count, 1);
});

test('bad heading order and missing h1', async () => {
  const r = await evaluate(VALID.replace('<h1>Søk etter barn</h1>', '<h2>Søk</h2>').replace('<h2>Resultater</h2>', '<h4>Resultater</h4>'));
  assert.equal(find(r, 'a11y-heading-order').count, 1);
  assert.equal(find(r, 'a11y-heading-h1').count, 1);
});

test('missing lang, missing title, missing main landmark, image without alt, link without name', async () => {
  const r = await evaluate('<!doctype html><html><head></head><body><h1>x</h1><img src="a.png" /><a href="/x"></a><a href="/y" aria-label="Til y"></a></body></html>');
  assert.deepEqual(ids(r), ['a11y-document-lang', 'a11y-image-alt', 'a11y-link-name', 'a11y-main-landmark', 'a11y-page-title']);
  assert.equal(find(r, 'a11y-link-name').count, 1);
  assert.equal(find(r, 'a11y-document-lang').severity, 'Medium');
});

test('dialog name, positive tabindex, hidden focusable', async () => {
  const r = await evaluate(VALID.replace('<h2>Resultater</h2>', '<h2>Resultater</h2><div role="dialog">x</div><dialog aria-label="Ok">y</dialog><button tabindex="3">T</button><div hidden><button>Hidden</button></div>'));
  assert.equal(find(r, 'a11y-dialog-name').count, 1);
  assert.equal(find(r, 'a11y-positive-tabindex').count, 1);
  assert.equal(find(r, 'a11y-hidden-focusable').count, 1);
});

test('selectors never contain attribute values or text; findings carry no page text', async () => {
  const r = await evaluate(VALID.replace('<h2>Resultater</h2>', '<h2>Resultater</h2><input type="text" value="ola.nordmann@bufetat.no" class="user-9876543" /><button data-user="kari@x.no"></button>'));
  const json = JSON.stringify(r);
  assert.equal(json.includes('ola.nordmann'), false);
  assert.equal(json.includes('kari@x.no'), false);
  assert.equal(json.includes('9876543'), false);
});

test('rule catalogue has fixed severity and WCAG mapping per rule id', () => {
  const a11y = require('../lib/a11y.js');
  for (const [id, rule] of Object.entries(a11y.RULES)) {
    assert.ok(['Critical', 'High', 'Medium', 'Low', 'Info'].includes(rule.severity), id);
    assert.ok(rule.wcag === null || /^\d\.\d+\.\d+$/.test(rule.wcag), id);
    assert.ok(rule.guidance.length > 10, id);
  }
});
