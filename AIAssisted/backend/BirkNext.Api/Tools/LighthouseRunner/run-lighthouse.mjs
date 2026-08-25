import lighthouse from 'lighthouse';
import * as chromeLauncher from 'chrome-launcher';
import { chromium } from 'playwright-core';
import { createRequire } from 'node:module';
import { mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';

const require = createRequire(import.meta.url);
const lighthouseVersion = require('lighthouse/package.json').version;
const playwrightVersion = require('playwright-core/package.json').version;
const args = process.argv.slice(2);
const readiness = args.includes('--readiness');
const targetArg = args.find(a => a.startsWith('--url='));
let chrome;
let profileDirectory;

const configuration = {
  extends: 'lighthouse:default',
  settings: {
    onlyCategories: ['performance'],
    formFactor: 'desktop',
    screenEmulation: { mobile: false, width: 1350, height: 940, deviceScaleFactor: 1, disabled: false },
    throttlingMethod: 'simulate',
    throttling: { rttMs: 40, throughputKbps: 10240, requestLatencyMs: 0, downloadThroughputKbps: 0, uploadThroughputKbps: 0, cpuSlowdownMultiplier: 1 },
    locale: 'en-US',
    disableStorageReset: false,
    maxWaitForLoad: 45000
  }
};

function metric(lhr, id, name, unit) {
  const audit = lhr.audits[id];
  if (!audit || audit.numericValue == null) return { name, auditId: id, status: 'NotAvailable', source: 'Lighthouse', measurementType: 'Lab' };
  return { name, auditId: id, observedValue: audit.numericValue, unit, status: 'Measured', source: 'Lighthouse', measurementType: 'Lab' };
}

try {
  profileDirectory = await mkdtemp(path.join(process.cwd(), '.lighthouse-profile-'));
  chrome = await chromeLauncher.launch({
    chromePath: chromium.executablePath(),
    userDataDir: profileDirectory,
    chromeFlags: ['--headless=new', '--no-sandbox', '--disable-dev-shm-usage', '--disable-gpu']
  });
  const versionResponse = await fetch(`http://127.0.0.1:${chrome.port}/json/version`);
  const browserInfo = await versionResponse.json();
  const browserVersion = String(browserInfo.Browser || '').replace(/^[^/]+\//, '');

  if (readiness) {
    console.log(JSON.stringify({ available: true, lighthouseVersion, nodeVersion: process.version, playwrightVersion, browserName: 'Chromium', browserVersion }));
    process.exitCode = 0;
  } else {
    if (!targetArg) throw new Error('Missing --url argument.');
    const targetUrl = targetArg.slice('--url='.length);
    const result = await lighthouse(targetUrl, { port: chrome.port, output: 'json', logLevel: 'error' }, configuration);
    if (!result?.lhr) throw new Error('Lighthouse returned no LHR.');
    const lhr = result.lhr;
    const auditIds = ['render-blocking-resources', 'unused-javascript', 'unused-css-rules', 'uses-optimized-images', 'uses-text-compression', 'uses-long-cache-ttl', 'mainthread-work-breakdown', 'long-tasks', 'bootup-time', 'total-byte-weight', 'third-party-summary', 'server-response-time'];
    const audits = auditIds.map(id => lhr.audits[id]).filter(Boolean).filter(a => a.score !== null && a.score < 1).slice(0, 12).map(a => ({ auditId: a.id, title: a.title, description: a.description, score: a.score, displayValue: a.displayValue }));
    console.log(JSON.stringify({
      lighthouseVersion: lhr.lighthouseVersion || lighthouseVersion,
      nodeVersion: process.version,
      playwrightVersion,
      browserName: 'Chromium', browserVersion,
      requestedUrl: lhr.requestedUrl, finalUrl: lhr.finalDisplayedUrl || lhr.finalUrl,
      performanceScore: lhr.categories.performance.score == null ? null : Math.round(lhr.categories.performance.score * 100),
      metrics: [
        metric(lhr, 'first-contentful-paint', 'FCP', 'ms'),
        metric(lhr, 'largest-contentful-paint', 'LCP', 'ms'),
        metric(lhr, 'cumulative-layout-shift', 'CLS', 'score'),
        metric(lhr, 'speed-index', 'Speed Index', 'ms'),
        metric(lhr, 'total-blocking-time', 'TBT', 'ms'),
        metric(lhr, 'interactive', 'Time to Interactive', 'ms'),
        metric(lhr, 'server-response-time', 'Server Response Time', 'ms'),
        { name: 'INP', auditId: 'interaction-to-next-paint', status: 'FieldDataRequired', source: 'Lighthouse', measurementType: 'Lab' }
      ],
      audits,
      effectiveConfiguration: configuration.settings
    }));
  }
} catch (error) {
  console.error(error?.stack || String(error));
  process.exitCode = 1;
} finally {
  if (chrome) {
    try { await chrome.kill(); } catch (error) { console.error(`Chromium cleanup warning: ${error.message}`); }
  }
  if (profileDirectory) await rm(profileDirectory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
}
