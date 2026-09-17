// Production Blazor DOM, isolated synthetic storage and mocked loopback responses. Never contacts M2LB.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.BIRKNEXT_UI_URL || 'http://localhost:5197';
const out = path.resolve(__dirname, '../test-results/browser-quality');
const legal = 'no-public-wcag21-48-v1', extended = 'extended-wcag22-aa-v1';
fs.mkdirSync(out, { recursive: true });

(async () => {
  const browser = await chromium.launch({ headless: true });
  let checks = 0;
  const check = (condition, message) => { assert.ok(condition, message); checks++; };
  try {
    for (const scenario of ['empty', 'saved', 'saved-version']) {
      const populated = scenario === 'saved';
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', e => errors.push(e.message));
      // An old physical scoped-CSS bundle exists on this workstation. Use the current generated build artifact.
      const css = path.resolve(__dirname, '../AIAssisted/frontend/BirkNext.Web/obj/Debug/net8.0/scopedcss/bundle/BirkNext.Web.styles.css');
      await page.route('**/BirkNext.Web.styles.css', route => route.fulfill({ path: css, contentType: 'text/css' }));
      await page.route('**/api/**', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify({
        state: 'NotPaired', profileId: 'quality-fixture', pages: [], message: 'Fixture: disconnected'
      }) }));
      await page.addInitScript(({ populated, scenario, legal }) => {
        if (sessionStorage.getItem('quality-fixture-seeded')) return;
        sessionStorage.setItem('quality-fixture-seeded', 'true');
        localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'quality-fixture', profiles: [{
          id: 'quality-fixture', name: 'Browser Quality fixture', environmentType: 'Development', targetUrl: 'https://m2lbdev.bufetat.no'
        }] }));
        const at = '2026-09-17T08:00:00Z';
        localStorage.setItem('birknext:endpoint-discovery', JSON.stringify({ 'quality-fixture': {
          wcag: scenario === 'saved-version' ? { version: 'Wcag22', level: 'AA' } : { profileId: legal },
          pages: populated ? ['/dashboard', '/children'].map((p, i) => ({ origin: 'https://m2lbdev.bufetat.no', path: p,
            firstObservedAt: at, lastObservedAt: at, browserEvidence: { pageOrigin: 'https://m2lbdev.bufetat.no', pagePath: p, visitStartedAt: at, capturedAt: at,
              accessibility: { checks: [{ checkId: 'a11y-image-alt', outcome: i === 0 ? 'Fail' : 'Pass', tested: 1, failed: i === 0 ? 1 : 0 }] },
              performance: { observationType: 'initial-load', lcpMs: 1800, cls: 0, interaction: { status: 'insufficient-samples' } }
            } })) : []
        } }));
      }, { populated, scenario, legal });
      await page.goto(base + '/admin/system-settings?section=target-environments');
      // The route may mount before the query-selected settings section is applied.
      await page.getByRole('button', { name: 'Target Environments', exact: true }).click();
      await page.getByRole('button', { name: 'Endpoint Discovery', exact: true }).click().catch(async error => {
        fs.writeFileSync(path.join(out, 'navigation-debug.txt'), await page.locator('body').innerText());
        await page.screenshot({ path: path.join(out, 'navigation-debug.png'), fullPage: true });
        throw error;
      });
      await page.locator('[data-testid=browser-quality-open]').click();
      const workspace = page.locator('.bq-workspace');
      await workspace.waitFor();
      const selector = page.getByLabel('Assessment profile', { exact: true });
      const expectedId = scenario === 'saved-version' ? 'legacy-22-AA' : legal;
      check(await selector.inputValue() === expectedId, 'Selected profile is restored');
      check(await page.locator('[data-testid=wcag-coverage]').getAttribute('data-profile') === expectedId, 'Rendered assessment matches selection');
      check((await page.locator('[data-testid=wcag-assessment-title]').innerText()).includes(scenario === 'saved-version' ? 'WCAG 2.2' : 'WCAG 2.1'), 'Version matches stored selection');
      check(!(await workspace.innerText()).includes('Legacy'), 'No implementation-history profile label');
      const wcag = page.getByRole('tab', { name: 'WCAG', exact: true });
      const perf = page.getByRole('tab', { name: 'Performance', exact: true });
      check(await wcag.getAttribute('aria-selected') === 'true', 'WCAG is default');
      check(await page.locator('.wcag-cards .metric-card').count() === 5, 'Shared summary cards');
      if (!populated) {
        check(await page.getByText('No browser evidence collected yet', { exact: true }).isVisible(), 'Empty evidence panel visible');
        check(await page.locator('[data-testid=wcag-criterion-row]').count() === 0, 'No initial matrix');
        check(await page.locator('[data-testid=wcag-toggle-criteria]').getAttribute('aria-expanded') === 'false', 'Disclosure state is collapsed');
      }
      await workspace.screenshot({ path: path.join(out, `${scenario}-wcag.png`) });
      await wcag.focus();
      await page.keyboard.press('ArrowRight');
      check(await perf.getAttribute('aria-selected') === 'true', 'ArrowRight activates Performance');
      check(await perf.evaluate(e => e === document.activeElement), 'Tab focus follows selection');
      check(!await page.locator('[data-testid=wcag-coverage]').isVisible(), 'Matrix does not precede Performance');
      if (populated) {
        check(await page.locator('[data-testid=bq-core-inp] .metric-value').first().innerText() === 'Not available', 'Unavailable INP is not zero');
        check(await page.locator('[data-testid=bq-core-ttfb] .metric-value').first().innerText() === 'Not available', 'Unavailable timing is not zero');
      } else check(await page.getByText('No browser performance evidence has been collected yet', { exact: true }).isVisible(), 'Performance empty state');
      await workspace.screenshot({ path: path.join(out, `${scenario}-performance.png`) });
      await page.keyboard.press('Home');
      check(await wcag.getAttribute('aria-selected') === 'true', 'Home returns to WCAG');
      const toggle = page.locator('[data-testid=wcag-toggle-criteria]');
      if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click();
      check(await page.locator('[data-testid=wcag-criterion-row]').count() === (scenario === 'saved-version' ? 56 : 48), 'Correct configured matrix size');
      check(await page.locator('.wcag-principle > summary').count() === 4, 'Four accessible principle disclosures');
      await page.locator('.wcag-principle > summary').first().focus();
      await page.keyboard.press('Enter');
      check(!await page.locator('.wcag-principle').first().evaluate(e => e.open), 'Principle collapses by keyboard');
      await page.keyboard.press('Enter');
      for (const name of ['Status', 'Level', 'Automation', 'Search']) check(await page.getByLabel(name, { exact: true }).count() > 0, `Labelled ${name} filter`);
      await selector.selectOption(extended);
      await page.waitForFunction(id => document.querySelector('[data-testid=wcag-coverage]')?.dataset.profile === id, extended);
      if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click();
      check(await page.locator('[data-testid=wcag-criterion-row]').count() === 55, 'Extended profile recalculates 55 criteria');
      check((await page.locator('[data-testid=wcag-assessment-title]').innerText()).includes('WCAG 2.2'), 'Extended heading updates');
      const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('birknext:endpoint-discovery')));
      check(stored['quality-fixture'].wcag.profileId === extended && !stored['Profile.Id'], 'Change persists under real environment id');
      await selector.selectOption(legal);
      await page.waitForFunction(id => document.querySelector('[data-testid=wcag-coverage]')?.dataset.profile === id, legal);
      if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click();
      check(await page.locator('[data-testid=wcag-criterion-row]').count() === 48, 'Legal membership restored');
      check(await page.locator('[data-criterion="3.3.8"]').count() === 0, 'WCAG 2.2-only criterion removed');
      await page.getByLabel('Search', { exact: true }).fill('1.1.1');
      await page.waitForFunction(() => document.querySelectorAll('[data-testid=wcag-criterion-row]').length === 1);
      check(await page.locator('tbody .status-chip').innerText() === (populated ? 'Failed' : 'Not tested'), 'Textual exact status, not color alone');
      await page.getByLabel('Search', { exact: true }).fill('');
      await workspace.screenshot({ path: path.join(out, `${scenario}-criteria.png`) });
      await page.setViewportSize({ width: 1024, height: 900 });
      check(await workspace.evaluate(e => e.scrollWidth <= e.clientWidth + 1), 'Laptop layout contains overflow');
      await workspace.screenshot({ path: path.join(out, `${scenario}-laptop.png`) });
      check(await toggle.evaluate(e => e.classList.contains('btn-secondary') && getComputedStyle(e).borderRadius !== '0px'), 'Action uses shared rendered styling');
      check(errors.length === 0, 'No browser runtime errors: ' + errors.join('; '));
      await context.close();
    }
    fs.writeFileSync(path.join(out, 'verification.json'), JSON.stringify({ scenarios: 3, checks, passed: true, source: 'Synthetic evidence; isolated browser; production Blazor UI' }, null, 2));
    console.log(`PASS: 3 production-UI scenarios, ${checks} assertions. Screenshots: ${out}`);
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
