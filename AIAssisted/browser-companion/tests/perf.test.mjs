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

test('LCP takes the last entry, CLS sums shifts without recent input, long tasks are counted and blocking time excludes the first 50 ms', () => {
  const v = perf.vitalsSummary({
    lcpEntries: [{ startTime: 900, renderTime: 900 }, { startTime: 2600, renderTime: 2600 }],
    layoutShiftEntries: [{ value: 0.05, hadRecentInput: false }, { value: 0.2, hadRecentInput: true }, { value: 0.07, hadRecentInput: false }],
    longTaskEntries: [{ duration: 80, startTime: 100 }, { duration: 120, startTime: 3000 }],
    paintEntries: [{ name: 'first-paint', startTime: 500 }, { name: 'first-contentful-paint', startTime: 650 }],
    stabilizedAtMs: 2000,
  });
  assert.equal(v.lcpMs, 2600);
  assert.equal(v.cls, 0.12);
  assert.equal(v.longTaskCount, 2);
  assert.equal(v.longTaskTotalMs, 200);
  assert.equal(v.longestTaskMs, 120);
  assert.equal(v.mainThreadBlockingMs, 100, '(80-50) + (120-50)');
  assert.equal(v.longTasksAfterStabilization, 1);
  assert.equal(v.firstContentfulPaintMs, 650);
});

test('unsupported metrics stay null, never fabricated', () => {
  const v = perf.vitalsSummary({ lcpEntries: [], layoutShiftEntries: undefined, longTaskEntries: [], paintEntries: [] });
  assert.equal(v.lcpMs, null);
  assert.equal(v.cls, null);
  assert.equal(v.firstContentfulPaintMs, null);
  assert.equal(v.mainThreadBlockingMs, null);
  assert.equal(v.interaction.inpMs, null);
  assert.equal(v.interaction.status, 'not-measured');
});

test('INP: not-supported without Event Timing, insufficient-samples below the minimum, measured from the worst interaction otherwise', () => {
  const ev = (id, duration) => ({ interactionId: id, duration, startTime: 1000 });
  assert.equal(perf.interactionSummary([ev(1, 300)], [], { supported: false }).status, 'not-supported');
  const two = perf.interactionSummary([ev(1, 120), ev(1, 300), ev(2, 80), ev(0, 900)], []);
  assert.equal(two.status, 'insufficient-samples');
  assert.equal(two.interactionCount, 2, 'entries without an interactionId never count');
  assert.equal(two.inpMs, null, 'no INP value is published below the sample minimum');
  assert.equal(two.longestInteractionMs, 300);
  assert.equal(two.minimumInteractions, perf.MIN_INP_INTERACTIONS);
  const three = perf.interactionSummary([ev(1, 120), ev(1, 300), ev(2, 80), ev(3, 240)], [{ startTime: 500, processingStart: 530 }]);
  assert.equal(three.status, 'measured');
  assert.equal(three.inpMs, 300, 'longest duration per interaction, worst interaction wins below 50 samples');
  assert.equal(three.firstInputDelayMs, 30);
  const many = perf.interactionSummary(Array.from({ length: 60 }, (_, i) => ev(i + 1, 1000 - i * 10)), []);
  assert.equal(many.inpMs, 990, 'with >= 50 interactions the (n/50)th worst is reported (index 1)');
});

test('resource summary classifies bytes and categories, infers cache hits, detects duplicates and failures, and sanitizes URLs', () => {
  const entries = [
    res('https://h/app.js', { initiatorType: 'script', transferSize: 30000, startTime: 5 }),
    res('https://h/styles.css?v=1', { initiatorType: 'link', transferSize: 4000, startTime: 6 }),
    res('https://h/logo.png', { initiatorType: 'img', transferSize: 2000 }),
    res('https://h/font.woff2', { initiatorType: 'css', transferSize: 0, encodedBodySize: 8000, decodedBodySize: 8000 }),
    res('https://h/_framework/dotnet.wasm', { transferSize: 1500000, duration: 900 }),
    res('https://h/_framework/blazor.boot.json', { transferSize: 3000 }),
    res('https://api.h/children?token=SECRET', { initiatorType: 'fetch', transferSize: 500, duration: 1800, startTime: 40 }),
    res('https://api.h/children?token=SECRET2', { initiatorType: 'fetch', transferSize: 0, encodedBodySize: 500, duration: 200, startTime: 60 }),
    res('https://h/missing.js', { initiatorType: 'script', responseStatus: 404, transferSize: 0, encodedBodySize: 0 }),
    res('https://cdn.h/opaque.js', { initiatorType: 'script', responseStatus: 0, transferSize: 0, encodedBodySize: 0, decodedBodySize: 0 }),
  ];
  const s = perf.resourceSummary(entries, { originMs: 0 });
  assert.equal(s.resourceCount, 10);
  assert.equal(s.jsBytes, 30000);
  assert.equal(s.cssBytes, 4000);
  assert.equal(s.imageBytes, 2000);
  assert.equal(s.fontBytes, 8000);
  assert.equal(s.wasmBytes, 1500000);
  assert.equal(s.apiBytes, 1000);
  assert.equal(s.frameworkResourceCount, 2);
  assert.equal(s.cachedResourceCount, 2, 'font + second api call served from cache; the opaque entry stays unknown');
  assert.equal(s.duplicateFetchCount, 1);
  assert.deepEqual(s.duplicateResources, [{ url: 'https://api.h/children', kind: 'api', count: 2, networkCount: 1 }]);
  assert.equal(s.failedResourceCount, 1);
  assert.equal(s.failedResources[0].url, 'https://h/missing.js');
  assert.equal(s.longestResources[0].url, 'https://api.h/children');
  assert.equal(s.largestResource.url, 'https://h/_framework/dotnet.wasm');
  assert.equal(s.slowestResource.url, 'https://api.h/children');
  assert.equal(s.slowestResource.durationMs, 1800);
  const js = s.categories.find(c => c.kind === 'js');
  assert.equal(js.count, 3);
  assert.equal(js.largest.url, 'https://h/app.js');
  assert.equal(s.categories.find(c => c.kind === 'font').cachedCount, 1);
  assert.deepEqual(s.timeline.slice(0, 2).map(t => t.url), ['https://h/app.js', 'https://h/styles.css']);
  assert.equal(s.timeline.find(t => t.url === 'https://api.h/children').delivery, 'network');
  assert.equal(JSON.stringify(s).includes('SECRET'), false);
});

test('blazor summary detects framework, boot manifest failure, repeated downloads, wasm bytes and the runtime/assembly/culture breakdown', () => {
  const entries = [
    res('https://h/_framework/blazor.boot.json', { responseStatus: 500, duration: 40, startTime: 100, responseEnd: 140 }),
    res('https://h/_framework/blazor.webassembly.js', { transferSize: 60000, startTime: 90, responseEnd: 150 }),
    res('https://h/_framework/dotnet.js', { transferSize: 40000, startTime: 150, responseEnd: 200 }),
    res('https://h/_framework/dotnet.native.wasm', { transferSize: 2000000, duration: 700, startTime: 200, responseEnd: 900 }),
    res('https://h/_framework/dotnet.native.wasm', { transferSize: 2000000, startTime: 950, responseEnd: 1600 }),
    res('https://h/_framework/System.Text.Json.wasm', { transferSize: 300000, startTime: 300, responseEnd: 500 }),
    res('https://h/_framework/MyApp.dll', { transferSize: 120000, startTime: 320, responseEnd: 520 }),
    res('https://h/_framework/icudt_EFIGS.dat', { transferSize: 500000, startTime: 330, responseEnd: 600 }),
    res('https://h/_framework/dotnet.timezones.blat', { transferSize: 0, encodedBodySize: 80000, startTime: 340, responseEnd: 400 }),
  ];
  const b = perf.blazorSummary(entries, { errorUiVisible: true, blazorScriptPresent: true });
  assert.equal(b.detected, true);
  assert.equal(b.bootManifestObserved, true);
  assert.equal(b.bootManifestFailed, true);
  assert.equal(b.bootManifestMs, 40);
  assert.equal(b.frameworkResourceCount, 9);
  assert.equal(b.wasmBytes, 4300000);
  assert.equal(b.runtimeResourceCount, 3, 'dotnet.js + two dotnet.native.wasm downloads');
  assert.equal(b.assemblyCount, 2, 'System.Text.Json.wasm and MyApp.dll');
  assert.equal(b.assemblyBytes, 420000);
  assert.equal(b.cultureResourceCount, 2);
  assert.equal(b.timezoneDataObserved, true);
  assert.equal(b.frameworkJsCount, 2);
  assert.equal(b.cachedFrameworkResourceCount, 1);
  assert.equal(b.loadKind, 'mixed');
  assert.equal(b.frameworkLoadStartMs, 90);
  assert.equal(b.frameworkLoadEndMs, 1600);
  assert.equal(b.repeatedFrameworkDownloads, 1);
  assert.equal(b.frameworkFailures.length, 1);
  assert.equal(b.slowestFrameworkResource.url, 'https://h/_framework/dotnet.native.wasm');
  assert.equal(b.errorUiVisible, true);
  assert.equal(perf.blazorSummary([], {}).detected, false);
  assert.equal(perf.blazorSummary([], {}).loadKind, 'none');
});

test('blazor warm navigation: framework served from cache is classified warm, cold when transferred', () => {
  const warm = perf.blazorSummary([res('https://h/_framework/dotnet.native.wasm', { transferSize: 0, encodedBodySize: 2000000 })], {});
  assert.equal(warm.loadKind, 'warm');
  const cold = perf.blazorSummary([res('https://h/_framework/dotnet.native.wasm', { transferSize: 2000000 })], {});
  assert.equal(cold.loadKind, 'cold');
});

test('resource lists are bounded', () => {
  const many = Array.from({ length: 100 }, (_, i) => res(`https://h/r${i}.js`, { duration: i, startTime: i }));
  const s = perf.resourceSummary(many);
  assert.equal(s.longestResources.length, perf.MAX_LONGEST_RESOURCES);
  assert.equal(s.longestResources[0].durationMs, 99);
  assert.equal(s.timeline.length, perf.MAX_TIMELINE_ENTRIES);
  assert.equal(s.timeline[0].startMs, 0);
});

test('cache inference: deliveryType wins, zero transfer with body means cache, opaque zeros stay unknown', () => {
  assert.equal(perf.deliveryOf({ deliveryType: 'cache', transferSize: 300 }), 'cache');
  assert.equal(perf.deliveryOf({ transferSize: 0, encodedBodySize: 10 }), 'cache');
  assert.equal(perf.deliveryOf({ transferSize: 300, encodedBodySize: 10 }), 'network');
  assert.equal(perf.deliveryOf({ transferSize: 0, encodedBodySize: 0, decodedBodySize: 0 }), null);
});
