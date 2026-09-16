import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const perf = require('../lib/perf.js');

const res = (name, extra = {}) => Object.assign({ name, initiatorType: 'other', duration: 100, transferSize: 1000, encodedBodySize: 900, responseStatus: 200, startTime: 10 }, extra);

test('navigation timing maps TTFB, DOMContentLoaded, load and type; unfinished load stays null', () => {
  const nav = perf.navigationSummary({ responseStart: 123.4, domContentLoadedEventEnd: 800.6, loadEventEnd: 0, type: 'navigate', transferSize: 5000 });
  assert.deepEqual(nav, { ttfbMs: 123, domContentLoadedMs: 801, loadEventMs: null, navigationType: 'navigate', transferBytes: 5000 });
  assert.equal(perf.navigationSummary(null).ttfbMs, null);
});

test('LCP takes the last entry, CLS sums shifts without recent input, long tasks are counted', () => {
  const v = perf.vitalsSummary({
    lcpEntries: [{ startTime: 900, renderTime: 900 }, { startTime: 2600, renderTime: 2600 }],
    layoutShiftEntries: [{ value: 0.05, hadRecentInput: false }, { value: 0.2, hadRecentInput: true }, { value: 0.07, hadRecentInput: false }],
    longTaskEntries: [{ duration: 80 }, { duration: 120 }],
    paintEntries: [{ name: 'first-paint', startTime: 500 }, { name: 'first-contentful-paint', startTime: 650 }],
  });
  assert.equal(v.lcpMs, 2600);
  assert.equal(v.cls, 0.12);
  assert.equal(v.longTaskCount, 2);
  assert.equal(v.longTaskTotalMs, 200);
  assert.equal(v.longestTaskMs, 120);
  assert.equal(v.firstContentfulPaintMs, 650);
});

test('unsupported metrics stay null, never fabricated', () => {
  const v = perf.vitalsSummary({ lcpEntries: [], layoutShiftEntries: undefined, longTaskEntries: [], paintEntries: [] });
  assert.equal(v.lcpMs, null);
  assert.equal(v.cls, null);
  assert.equal(v.firstContentfulPaintMs, null);
});

test('resource summary classifies bytes, detects duplicates and failures, and sanitizes URLs', () => {
  const entries = [
    res('https://h/app.js', { initiatorType: 'script', transferSize: 30000 }),
    res('https://h/styles.css?v=1', { initiatorType: 'link', transferSize: 4000 }),
    res('https://h/logo.png', { initiatorType: 'img', transferSize: 2000 }),
    res('https://h/_framework/dotnet.wasm', { transferSize: 1500000, duration: 900 }),
    res('https://h/_framework/blazor.boot.json', { transferSize: 3000 }),
    res('https://api.h/children?token=SECRET', { initiatorType: 'fetch', transferSize: 500, duration: 1800 }),
    res('https://api.h/children?token=SECRET2', { initiatorType: 'fetch', transferSize: 500, duration: 200 }),
    res('https://h/missing.js', { initiatorType: 'script', responseStatus: 404, transferSize: 0, encodedBodySize: 0 }),
  ];
  const s = perf.resourceSummary(entries);
  assert.equal(s.resourceCount, 8);
  assert.equal(s.jsBytes, 30000);
  assert.equal(s.cssBytes, 4000);
  assert.equal(s.imageBytes, 2000);
  assert.equal(s.wasmBytes, 1500000);
  assert.equal(s.apiBytes, 1000);
  assert.equal(s.frameworkResourceCount, 2);
  assert.equal(s.duplicateFetchCount, 1);
  assert.deepEqual(s.duplicateResources, [{ url: 'https://api.h/children', count: 2 }]);
  assert.equal(s.failedResourceCount, 1);
  assert.equal(s.failedResources[0].url, 'https://h/missing.js');
  assert.equal(s.longestResources[0].url, 'https://api.h/children');
  assert.equal(JSON.stringify(s).includes('SECRET'), false);
});

test('blazor summary detects framework, boot manifest failure, repeated downloads and wasm bytes', () => {
  const entries = [
    res('https://h/_framework/blazor.boot.json', { responseStatus: 500 }),
    res('https://h/_framework/dotnet.native.wasm', { transferSize: 2000000 }),
    res('https://h/_framework/dotnet.native.wasm', { transferSize: 2000000 }),
    res('https://h/_framework/System.Text.Json.wasm', { transferSize: 300000 }),
  ];
  const b = perf.blazorSummary(entries, { errorUiVisible: true, blazorScriptPresent: true });
  assert.equal(b.detected, true);
  assert.equal(b.bootManifestObserved, true);
  assert.equal(b.bootManifestFailed, true);
  assert.equal(b.frameworkResourceCount, 4);
  assert.equal(b.wasmBytes, 4300000);
  assert.equal(b.repeatedFrameworkDownloads, 1);
  assert.equal(b.frameworkFailures.length, 1);
  assert.equal(b.errorUiVisible, true);
  assert.equal(perf.blazorSummary([], {}).detected, false);
});

test('resource lists are bounded', () => {
  const many = Array.from({ length: 100 }, (_, i) => res(`https://h/r${i}.js`, { duration: i }));
  const s = perf.resourceSummary(many);
  assert.equal(s.longestResources.length, perf.MAX_LONGEST_RESOURCES);
  assert.equal(s.longestResources[0].durationMs, 99);
});
