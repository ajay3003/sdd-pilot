// Isolated production-UI verification with synthetic saved evidence. Never connects to the target application.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.BIRKNEXT_UI_URL || 'http://localhost:5187';
const out = path.resolve(__dirname, '../test-results/browser-quality');
fs.mkdirSync(out, { recursive: true });

(async () => {
  const browser = await chromium.launch({ headless: true });
  let checks = 0;
  const check = (condition, message) => { assert.ok(condition, message); checks++; };
  try {
    for (const populated of [false, true]) {
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      await page.route('http://localhost:5000/**', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({
        state: 'NotPaired', profileId: 'quality-fixture', pages: [], message: 'Fixture: disconnected'
      }) }));
      await page.addInitScript(({ populated }) => {
        if (sessionStorage.getItem('quality-fixture-seeded')) return;
        sessionStorage.setItem('quality-fixture-seeded', 'true');
        localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'quality-fixture', profiles: [{
          id: 'quality-fixture', name: 'Browser Quality fixture', environmentType: 'Development', targetUrl: 'https://m2lbdev.bufetat.no'
        }] }));
        const timestamp = '2026-09-17T08:00:00Z';
        localStorage.setItem('birknext:endpoint-discovery', JSON.stringify({ 'quality-fixture': {
          wcag: { profileId: 'no-public-wcag21-48-v1' }, pages: populated ? ['/dashboard', '/children'].map(p => ({
            origin: 'https://m2lbdev.bufetat.no', path: p, firstObservedAt: timestamp, lastObservedAt: timestamp,
            browserEvidence: { pageOrigin: 'https://m2lbdev.bufetat.no', pagePath: p, visitStartedAt: timestamp, capturedAt: timestamp,
              accessibility: { checks: [{ checkId: 'a11y-page-title', outcome: 'Pass', tested: 1 }] } }
          })).concat([{ origin: 'https://samhandlingsrom-onmicrosoft-com.access.mcas.ms', path: '/', endpoints: [] }]) : []
        } }));
      }, { populated });
      await page.goto(base + '/admin/system-settings?section=target-environments');
      await page.getByRole('button', { name: 'Endpoint Discovery', exact: true }).click();
      await page.locator('[data-testid=browser-quality-summary]').waitFor();
      check(await page.locator('[data-testid=wcag-coverage]').count() === 0, 'Overview has no criterion matrix');
      check(await page.locator('[data-testid=discovery-pages-count]').innerText() === (populated ? '2' : '0'), 'Only application pages counted');
      check(await page.locator('[data-testid=discovery-companion]').innerText() === 'Not connected', 'Live companion state is disconnected');
      await page.screenshot({ path: path.join(out, populated ? 'overview-saved.png' : 'overview-empty.png'), fullPage: true });
      await page.locator('[data-testid=discovery-nav-quality]').click();
      check(await page.locator('[data-testid=wcag-criterion-row]').count() === 48, 'Norwegian profile has 48 top-level rows');
      check((await page.locator('.wcag-panel h3').innerText()).includes('Norwegian legal requirements'), 'Heading names authoritative profile');
      check(!(await page.locator('.wcag-panel').innerText()).includes('Automated assessment'), 'No unconditional automated-assessment label');
      if (populated) {
        await page.locator('[data-testid=wcag-criterion-row] button').first().click();
        check(await page.locator('[data-testid=wcag-criterion-detail] article').count() === 2, 'Criterion detail exposes two pages');
      } else check(await page.locator('[data-testid=wcag-awaiting]').count() === 1, 'Unavailable evidence gives useful instructions');
      const selector = page.locator('.wcag-workspace > label select');
      await selector.selectOption('extended-wcag22-aa-v1');
      await page.waitForFunction(() => document.querySelectorAll('[data-testid=wcag-criterion-row]').length === 55);
      check((await page.locator('.wcag-panel h3').innerText()).includes('WCAG 2.2'), 'Switch updates heading and membership');
      await selector.selectOption('no-public-wcag21-48-v1');
      await page.waitForFunction(() => document.querySelectorAll('[data-testid=wcag-criterion-row]').length === 48);
      check(!(await page.locator('.wcag-table tbody').innerText()).includes('3.3.8'), '2.2-only criterion removed');
      await page.screenshot({ path: path.join(out, populated ? 'assessment-saved.png' : 'assessment-empty.png'), fullPage: true });
      await page.reload();
      await page.getByRole('button', { name: 'Endpoint Discovery', exact: true }).click();
      check(await page.locator('[data-testid=discovery-pages-count]').innerText() === (populated ? '2' : '0'), 'Saved evidence survives reload');
      check(errors.length === 0, 'No browser runtime errors: ' + errors.join('; '));
      await context.close();
    }
    fs.writeFileSync(path.join(out, 'verification.json'), JSON.stringify({ scenarios: 2, checks, passed: true, source: 'Synthetic evidence in isolated browser storage; production Blazor UI' }, null, 2));
    console.log(`PASS: 2 production-UI scenarios, ${checks} assertions. Screenshots: ${out}`);
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
