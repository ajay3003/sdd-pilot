// Run against a locally hosted Blazor frontend; NODE_PATH may point to existing global npm modules.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const origin = process.argv[2] || 'http://127.0.0.1:5178';
const output = process.argv[3] || path.resolve('test-results');
const axePath = path.resolve(__dirname, '../../browser-companion/vendor/axe.min.js');
const viewports = [
  { width: 1440, height: 1100 },
  { width: 1100, height: 1000 },
  { width: 768, height: 1000 },
  { width: 390, height: 844 },
];

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    for (const viewport of viewports) {
      const page = await browser.newPage({ viewport });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(`${origin}/user-guide`, { waitUntil: 'domcontentloaded' });
      await page.locator('#overview').waitFor();
      if (viewport.width <= 640) {
        await page.getByRole('button', { name: 'Navigation menu' }).click();
        await page.waitForTimeout(250);
      }
      const dimensions = await page.evaluate(() => ({
        viewport: document.documentElement.clientWidth,
        document: document.documentElement.scrollWidth,
        main: document.querySelector('main')?.clientWidth,
        guide: document.querySelector('.ug-page')?.clientWidth,
      }));
      assert(dimensions.document <= dimensions.viewport,
        `Horizontal overflow at ${viewport.width}px: ${JSON.stringify(dimensions)}`);
      assert.equal(errors.length, 0, `Browser errors at ${viewport.width}px: ${errors.join('; ')}`);
      await page.addScriptTag({ path: axePath });
      const accessibility = await page.evaluate(async () => window.axe.run(document.querySelector('.ug-page'), {
        runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] },
      }));
      assert.equal(accessibility.violations.length, 0,
        `Accessibility violations at ${viewport.width}px: ${accessibility.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => `${n.target.join(' ')} ${n.failureSummary}`).join(' | ')}`).join('; ')}`);
      await page.screenshot({ path: path.join(output, `user-guide-${viewport.width}.png`) });
      console.log(`${viewport.width}px: ${JSON.stringify(dimensions)}; axe A/AA: ${accessibility.passes.length} passes`);
      await page.close();
    }
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
