// Isolated real-stack acceptance. Requires dotnet builds and Playwright on NODE_PATH.
// Uses its own browser storage and ports; never reads or changes a user's browser profile.
const { chromium } = require('playwright');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const evidence = path.join(root, 'TestResults', 'active-target-runtime');
fs.mkdirSync(evidence, { recursive: true });
const origin = 'http://localhost:5174';
const api = 'http://localhost:5002';
const storageKey = 'birknext:frontend-analysis-settings';
const profiles = {
  activeProfileId: 'qa',
  profiles: [
    { id: 'qa', name: 'QA', environmentType: 'QA', targetUrl: 'https://example-qa.local' },
    { id: 'dev', name: 'M2LB DEV', environmentType: 'Development', targetUrl: 'https://m2lbdev.bufetat.no/' }
  ]
};
let servers = [], browser, activePage;
const results = { fixture: profiles, steps: [], reviewRequests: [] };
function startServers() {
  const configs = [
    ['backend', path.resolve(root, '../backend/BirkNext.Api/BirkNext.Api.csproj'), 'Debug', api],
    ['frontend', path.join(root, 'BirkNext.Web/BirkNext.Web.csproj'), 'Release', origin]
  ];
  for (const [name, project, configuration, url] of configs) {
    const stream = fs.createWriteStream(path.join(evidence, `${name}.log`), { flags: 'a' });
    const child = spawn('dotnet', ['run', '--no-build', '-c', configuration, '--project', project, '--urls', url],
      { cwd: path.dirname(project), windowsHide: true, env: { ...process.env, FRONTEND_ORIGIN: origin } });
    child.stdout.pipe(stream); child.stderr.pipe(stream);
    servers.push({ child, stream });
  }
}
function stopServers() {
  for (const { child, stream } of servers) {
    if (child.exitCode === null) spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
    stream.end();
  }
  servers = [];
}
async function ready(url) {
  for (let i = 0; i < 90; i++) {
    try { await fetch(url, { signal: AbortSignal.timeout(2000) }); return; } catch { }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(`Server did not start: ${url}`);
}
async function newContext(storageState) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, storageState });
  context.setDefaultTimeout(120000);
  // Only app configuration is redirected to the isolated real API instance. Review requests/results are never mocked.
  await context.route('**/appsettings.json', async route => {
    const response = await route.fetch();
    await route.fulfill({ response, json: { ...await response.json(), BackendUrl: api } });
  });
  // AdminApiService still hard-codes port 5000. Forward to this run's actual backend,
  // preserving method, payload and response (no API mocks).
  await context.route('http://localhost:5000/**', route =>
    route.continue({ url: route.request().url().replace('http://localhost:5000', api) }));
  return context;
}
async function settings(page, name, activate) {
  await page.goto(`${origin}/admin/system-settings?section=target-environments`);
  const tab = page.getByRole('button', { name: 'Target Environments', exact: true });
  await tab.waitFor();
  await tab.click();
  await page.locator('.fa-profile-chip').filter({ hasText: name }).click();
  assert.equal((await page.locator('.fa-detail-name').innerText()).trim(), name);
  if (activate && (await page.locator('.fa-active-card-name').allTextContents())[0] !== name) {
    await page.getByRole('button', { name: 'Set as Active', exact: true }).click();
    await page.waitForFunction(expected => document.querySelector('.fa-active-card-name')?.textContent === expected, name);
  }
  const active = await page.evaluate(key => JSON.parse(localStorage.getItem(key)).activeProfileId, storageKey);
  results.steps.push({ action: activate ? 'Set as Active' : 'Select only', selected: name, active });
  console.log(`Settings: selected ${name}, active ${active}`);
}
async function target(page, name, url) {
  await page.goto(`${origin}/frontend-quality-review`);
  await page.locator('[data-testid=fqr-access-target]').waitFor();
  assert.ok((await page.locator('[data-testid=fqr-access-target]').innerText()).includes(name));
  assert.equal(await page.locator('[data-testid=fqr-access-url]').innerText(), url);
  results.steps.push({ action: 'FQR target', name, url });
  console.log(`FQR: ${name} ${url}`);
}
(async () => {
  try {
    startServers();
    await Promise.all([ready(origin), ready(api)]);
    browser = await chromium.launch({ headless: true });
    let context = await newContext();
    let page = await context.newPage();
    activePage = page;
    await page.goto(origin);
    await page.evaluate(({ key, value }) => localStorage.setItem(key, JSON.stringify(value)), { key: storageKey, value: profiles });
    await settings(page, 'M2LB DEV', true);
    await target(page, 'M2LB DEV', profiles.profiles[1].targetUrl);
    await page.screenshot({ path: path.join(evidence, 'dev-active.png'), fullPage: true });
    page.on('request', request => {
      const body = (request.postData() || '') + decodeURIComponent(request.url());
      if (body.includes('m2lbdev.bufetat.no') || body.includes('example-qa.local')) {
        results.reviewRequests.push({ endpoint: new URL(request.url()).pathname,
          dev: body.includes('m2lbdev.bufetat.no'), qa: body.includes('example-qa.local') });
      }
    });
    await page.getByRole('button', { name: 'Run Frontend Quality Review', exact: true }).click();
    await page.locator('[data-testid=fqr-result-target]').waitFor({ timeout: 180000 });
    const resultTarget = await page.locator('[data-testid=fqr-result-target]').innerText();
    assert.ok(resultTarget.includes('M2LB DEV'));
    assert.ok(resultTarget.includes('https://m2lbdev.bufetat.no/'));
    const text = await page.locator('body').innerText();
    assert.ok(!text.includes('example-qa.local'));
    assert.ok(results.reviewRequests.length > 0);
    assert.ok(results.reviewRequests.every(request => request.dev && !request.qa));
    results.review = { target: resultTarget, preflight: await page.locator('[data-testid=fqr-preflight-banner]').allTextContents() };
    await page.screenshot({ path: path.join(evidence, 'dev-review.png'), fullPage: true });
    // A full document navigation creates a new WASM instance each time; settings persist in this isolated browser.
    await settings(page, 'QA', false);
    await target(page, 'M2LB DEV', profiles.profiles[1].targetUrl);
    await settings(page, 'QA', true);
    await target(page, 'QA', profiles.profiles[0].targetUrl);
    await settings(page, 'M2LB DEV', true);
    const statePath = path.join(evidence, 'browser-state.json');
    await context.storageState({ path: statePath });
    await context.close();
    stopServers();
    startServers();
    await Promise.all([ready(origin), ready(api)]);
    context = await newContext(statePath);
    page = await context.newPage();
    activePage = page;
    await target(page, 'M2LB DEV', profiles.profiles[1].targetUrl);
    results.restart = 'Both server processes and browser context restarted; DEV remains active';
    await page.screenshot({ path: path.join(evidence, 'dev-after-restart.png'), fullPage: true });
    results.passed = true;
  } catch (error) {
    if (activePage && !activePage.isClosed()) {
      await activePage.screenshot({ path: path.join(evidence, 'failure.png'), fullPage: true }).catch(() => {});
      results.pageText = await activePage.locator('body').innerText().catch(() => 'Page unavailable');
    }
    results.passed = false;
    results.error = String(error.stack || error);
    process.exitCode = 1;
  } finally {
    if (browser) await browser.close();
    stopServers();
    fs.writeFileSync(path.join(evidence, 'acceptance.json'), JSON.stringify(results, null, 2));
    console.log(JSON.stringify(results, null, 2));
  }
})();
