// End-to-end smoke of the runtime security checks against the loopback fixture (security-runtime-fixture.cjs) and an isolated
// BirkNext backend whose SecurityTesting:TrustedTargets registers the fixture profile. Checks real runs plus responsive layout
// (1440/1100/768/390), keyboard focus and axe (WCAG A/AA) on the AQR security section and the FQR cookie section.
// Usage: NODE_PATH=<global node_modules> node security-runtime-smoke.cjs <frontendOrigin> <backendOrigin> <outputDir>
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const frontend = process.argv[2] || 'http://localhost:5198';
const backend = process.argv[3] || 'http://localhost:5098';
const output = process.argv[4] || path.resolve('test-results/security-runtime');
const configuredBackend = 'http://localhost:5000';
const axePath = path.resolve(__dirname, '../../browser-companion/vendor/axe.min.js');
const cookieSentinel = 'SENTINEL-COOKIE-VALUE-5097';
const viewports = [{ width: 1440, height: 1100 }, { width: 1100, height: 1000 }, { width: 768, height: 1000 }, { width: 390, height: 844 }];

const profile = {
  profiles: [{
    id: 'fixture-dev', name: 'Fixture DEV', environmentType: 'Development', targetUrl: 'http://127.0.0.1:5097/',
    restBaseUrl: 'http://127.0.0.1:5099/api/children', swaggerUrl: 'http://127.0.0.1:5099/swagger/v1/swagger.json', isActive: true,
    security: {
      expectedSecurityHeaders: ['X-Content-Type-Options'],
      runtimeSecurity: {
        apiDocumentationExposure: 'ExpectedProtected', apiDocumentationPaths: [],
        cors: { allowedOrigins: ['http://127.0.0.1:5097', 'http://127.0.0.1:5096'], allowCredentials: null, allowedMethods: [], allowedHeaders: [] },
        cookies: { authCookieNames: ['Session'], requireSecure: true, requireHttpOnly: true, allowedSameSite: [], allowedDomains: [], allowPersistentAuthCookies: false },
        authorizationScenarios: [{
          scenarioId: 'children', displayName: 'Children list', apiType: 'Rest', url: 'http://127.0.0.1:5099/api/children', method: 'GET', successStatusCodes: [],
          identities: [{ alias: 'anonymous', role: 'Visitor', expected: 'Deny', notFoundMeansDeny: false }, { alias: 'supervisor', role: 'Supervisor', expected: 'Allow', notFoundMeansDeny: false }],
        }],
        bodyFuzzOperations: [{ method: 'POST', path: '/api/search', policy: 'ReadOnlyBodySafe', cleanupStrategyId: null }],
      },
    },
  }],
  activeProfileId: 'fixture-dev',
};

const results = { checks: [], overflow: [], axe: [], keyboard: [], sentinel: [] };
function check(name, condition, detail = '') { results.checks.push({ name, ok: !!condition, detail }); if (!condition) console.error(`FAIL ${name} ${detail}`); }

async function routeBackend(context) {
  const frontendOrigin = new URL(frontend).origin;
  await context.route(`${configuredBackend}/**`, async route => {
    const headers = { 'access-control-allow-origin': frontendOrigin, 'access-control-allow-credentials': 'true', 'access-control-allow-methods': 'GET, POST, PUT, PATCH, DELETE, OPTIONS', 'access-control-allow-headers': route.request().headers()['access-control-request-headers'] || '*' };
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers });
    const response = await route.fetch({ url: backend + route.request().url().slice(configuredBackend.length), timeout: 600000 });
    await route.fulfill({ response, headers: { ...response.headers(), ...headers } });
  });
}

async function overflow(page, label) {
  const [client, scroll, culprits] = await page.evaluate(() => {
    const w = document.documentElement.clientWidth, out = [];
    for (const el of document.querySelectorAll('body *')) {
      const r = el.getBoundingClientRect();
      if (r.right > w + 1 && r.width > 0 && !(el.parentElement && el.parentElement.getBoundingClientRect().right > w + 1)) out.push(el.tagName.toLowerCase() + '.' + String(el.className).trim().split(/\s+/).join('.') + ' ' + (el.getAttribute('data-testid') || '') + ' ' + Math.round(r.right));
      if (out.length >= 3) break;
    }
    return [w, document.documentElement.scrollWidth, out]; });
  results.overflow.push({ label, client, scroll, ok: scroll <= client + 1, culprits });
  check(`no horizontal overflow · ${label}`, scroll <= client + 1, `${scroll} > ${client}: ${culprits.join(' | ')}`);
}

async function axe(page, label, selector) {
  await page.waitForTimeout(400);   // let state transitions (opacity/colour) settle before measuring contrast
  if (!(await page.evaluate(() => !!window.axe))) await page.addScriptTag({ path: axePath });
  const violations = await page.evaluate(async sel => (await axe.run({ include: [[sel]] }, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] } })).violations
    .map(v => `${v.id} (${v.impact}, ${v.nodes.length}): ${v.nodes.slice(0, 2).map(n => n.target.join(' ') + ' ' + JSON.stringify(n.any[0] && n.any[0].data || '')).join('; ')}`), selector);
  results.axe.push({ label, violations });
  check(`axe A/AA · ${label}`, violations.length === 0, violations.join(' | '));
}

async function focusVisible(page, selector, label) {
  // Keyboard-initiated focus (programmatic focus after a mouse click does not trigger :focus-visible): focus, step back, Tab forward.
  const target = page.locator(selector).first();
  await target.focus();
  await page.keyboard.press('Shift+Tab');
  await page.keyboard.press('Tab');
  const visible = await target.evaluate(el => {
    const s = getComputedStyle(el);
    return el === document.activeElement && (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 || s.boxShadow !== 'none');
  });
  results.keyboard.push({ label, visible });
  check(`visible keyboard focus · ${label}`, visible);
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: viewports[0] });
    await routeBackend(context);
    await context.addInitScript(json => { if (!localStorage.getItem('birknext:frontend-analysis-settings')) localStorage.setItem('birknext:frontend-analysis-settings', json); }, JSON.stringify(profile));
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', e => pageErrors.push(e.message));

    // ── AQR: setup panels (readiness) ──────────────────────────────────────────────────────────
    await page.goto(`${frontend}/api-quality-review`, { waitUntil: 'domcontentloaded' });
    await page.locator('[data-testid=aqr-authorization]').waitFor({ timeout: 120000 });
    await page.waitForFunction(() => document.querySelector('[data-testid=aqr-authorization-trust]')?.getAttribute('data-state') === 'Trusted', null, { timeout: 60000 });
    check('authorization panel states the bounded-check warning', (await page.locator('[data-testid=aqr-authorization-warning]').innerText()).includes('not a penetration test'));
    await axe(page, 'AQR security idle (authorization + fuzzing panels)', '[data-testid=aqr-authorization]');
    await focusVisible(page, '[data-testid=aqr-authorization-run]', 'authorization run');

    // Authorization scenarios: anonymous expected Deny gets 200 (UnexpectedAllow); 'supervisor' has no server provider (ExecutionUnavailable).
    await page.locator('[data-testid=aqr-authorization-run]').click();
    await page.locator('[data-testid=aqr-authorization-result-row]').first().waitFor({ timeout: 60000 });
    const outcomes = await page.locator('[data-testid=aqr-authorization-result-row]').evaluateAll(rows => rows.map(r => r.getAttribute('data-outcome')));
    check('authorization outcomes from explicit expectations', JSON.stringify(outcomes) === JSON.stringify(['UnexpectedAllow', 'ExecutionUnavailable']), JSON.stringify(outcomes));
    await axe(page, 'authorization scenario results', '[data-testid=aqr-authorization]');

    // Safe fuzzing with request-body cases.
    await page.locator('[data-testid=aqr-fuzz-level-contractfuzzing]').check();
    await page.locator('[data-testid=aqr-fuzzing-body-toggle]').check();
    await focusVisible(page, '[data-testid=aqr-fuzzing-body-toggle]', 'body fuzz toggle');
    await page.locator('[data-testid=aqr-fuzzing-analyze]').click();
    await page.waitForFunction(() => !document.querySelector('[data-testid=aqr-fuzzing-run]')?.disabled, null, { timeout: 120000 });
    await page.locator('[data-testid=aqr-fuzzing-run]').click();
    await page.waitForFunction(() => /finished/i.test(document.querySelector('[data-testid=aqr-fuzzing-status]')?.innerText || ''), null, { timeout: 300000 });
    const fuzzText = await page.locator('[data-testid=aqr-fuzzing]').innerText();
    check('body fuzz eligibility lists the opted-in operation and DELETE skipped', fuzzText.includes('POST /api/search · request body') && fuzzText.includes('DELETE /api/items/{id} · request body'));
    check('body fuzz produced outcomes', /Unexpected 5xx|Handled validation/.test(fuzzText));
    await axe(page, 'body fuzz results', '[data-testid=aqr-fuzzing]');

    // AQR review: documentation exposure + CORS matrix in the Security tab.
    await page.locator('[data-testid=aqr-run]').click();
    await page.locator('[data-testid=aqr-tab-security]').waitFor({ timeout: 300000 });
    await page.locator('[data-testid=aqr-tab-security]').click();
    await page.locator('[data-testid=aqr-docs-exposure-row]').waitFor();
    check('documentation exposure is UnexpectedExposure (expected protected, served publicly)', (await page.locator('[data-testid=aqr-docs-exposure-row]').getAttribute('data-assessment')) === 'UnexpectedExposure');
    const corsKinds = await page.locator('[data-testid=aqr-cors-row]').evaluateAll(rows => rows.map(r => `${r.getAttribute('data-kind')}:${r.getAttribute('data-assessment')}`));
    check('CORS matrix shows frontend, allowed and foreign probes', corsKinds.some(k => k.startsWith('ConfiguredAllowedOrigin')) && corsKinds.includes('ForeignOrigin:UnexpectedAllow'), corsKinds.join(','));
    await focusVisible(page, '[data-testid=aqr-tab-security]', 'results tab');
    await axe(page, 'AQR completed results (security tab)', '[data-testid=aqr-tabpanel]');
    await page.screenshot({ path: path.join(output, 'aqr-security-1440.png'), fullPage: true });

    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await page.waitForTimeout(300);
      await overflow(page, `AQR security results @${viewport.width}`);
    }
    await page.setViewportSize(viewports[0]);

    // ── FQR: cookie section (Static Security of the fixture frontend) ─────────────────────────────
    await page.goto(`${frontend}/frontend-quality-review`, { waitUntil: 'domcontentloaded' });
    await page.locator('[data-testid=fqr-run]').waitFor({ timeout: 120000 });
    await page.waitForFunction(() => !document.querySelector('[data-testid=fqr-run]')?.disabled, null, { timeout: 120000 });
    await page.locator('[data-testid=fqr-run]').click();
    await page.locator('[data-testid=fqr-cookie-security]').waitFor({ timeout: 600000 });
    const toggle = page.locator('[data-testid=fqr-cookie-security] .disclosure-toggle').first();
    await toggle.focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-testid=fqr-cookie-row], [data-testid=fqr-cookies-not-recorded], [data-testid=fqr-cookies-none]'), null, { timeout: 30000 });
    await page.screenshot({ path: path.join(output, 'fqr-cookies-open.png'), fullPage: true });
    console.log('cookie section:', (await page.locator('[data-testid=fqr-cookie-security]').innerText()).slice(0, 600));
    const cookieRows = await page.locator('[data-testid=fqr-cookies-document] [data-testid=fqr-cookie-row]').evaluateAll(rows => rows.map(r => r.getAttribute('data-cookie')));
    check('FQR cookie metadata shows both fixture cookies', cookieRows.includes('Session') && cookieRows.includes('Theme'), cookieRows.join(','));
    const rules = await page.locator('[data-testid=fqr-cookie-finding]').evaluateAll(f => f.map(x => x.getAttribute('data-rule')));
    check('declared auth cookie evaluated strictly', rules.includes('cookie-auth-missing-secure') && rules.includes('cookie-samesite-none-insecure'), rules.join(','));
    await focusVisible(page, '[data-testid=fqr-cookies-proxy-load]', 'proxy cookie load');
    await axe(page, 'FQR cookie results', '[data-testid=fqr-cookie-security]');
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await page.waitForTimeout(300);
      await overflow(page, `FQR cookie section @${viewport.width}`);
    }
    await page.setViewportSize(viewports[0]);
    await page.screenshot({ path: path.join(output, 'fqr-cookies-1440.png'), fullPage: true });

    // ── Sentinel: the cookie value never reaches the DOM or browser storage ─────────────────────────
    const dom = await page.content();
    const storage = await page.evaluate(() => JSON.stringify(Object.fromEntries(Object.keys(localStorage).map(k => [k, localStorage.getItem(k)]))));
    results.sentinel.push({ dom: dom.includes(cookieSentinel), storage: storage.includes(cookieSentinel) });
    check('cookie value absent from DOM and storage', !dom.includes(cookieSentinel) && !storage.includes(cookieSentinel));

    // ── Production: protected and active runs blocked ─────────────────────────────────────────────
    const prod = JSON.parse(JSON.stringify(profile));
    prod.profiles[0].environmentType = 'Production';
    await page.evaluate(json => localStorage.setItem('birknext:frontend-analysis-settings', json), JSON.stringify(prod));
    await page.goto(`${frontend}/api-quality-review`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => document.querySelector('[data-testid=aqr-back], [data-testid=aqr-authorization-state]'), null, { timeout: 120000 });
    await page.waitForTimeout(1500);
    if (await page.locator('[data-testid=aqr-back]').count() > 0) await page.locator('[data-testid=aqr-back]').click();   // the last report is restored; return to setup
    await page.locator('[data-testid=aqr-authorization-state]').waitFor({ timeout: 120000 });
    check('production: authorization blocked', (await page.locator('[data-testid=aqr-authorization-state]').innerText()) === 'Blocked');
    check('production: fuzzing blocked', await page.locator('[data-testid=aqr-fuzzing-blocked]').count() === 1);
    await axe(page, 'production blocked state', '[data-testid=aqr-authorization]');
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await page.waitForTimeout(300);
      await overflow(page, `AQR production blocked @${viewport.width}`);
    }

    check('no page errors', pageErrors.length === 0, pageErrors.join(' | '));
  } finally {
    await browser.close();
    fs.writeFileSync(path.join(output, 'security-runtime-smoke.json'), JSON.stringify(results, null, 2));
  }
  const failed = results.checks.filter(c => !c.ok);
  console.log(`${results.checks.length - failed.length}/${results.checks.length} checks passed`);
  process.exit(failed.length === 0 ? 0 : 1);
})().catch(e => { console.error(e); fs.writeFileSync(path.join(output, 'security-runtime-smoke.json'), JSON.stringify(results, null, 2)); process.exit(2); });
