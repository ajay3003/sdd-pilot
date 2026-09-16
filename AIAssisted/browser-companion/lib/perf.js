// BirkNext Browser Companion — performance summary built from browser-native Performance Timeline entries.
// Pure functions over plain entry-like objects so the calculations are unit-testable without a browser.
// Nothing here fabricates unsupported metrics: a metric the browser did not report stays null.
(function (root) {
  'use strict';

  const sanitize = root.BirkNextCompanion && root.BirkNextCompanion.sanitize
    ? root.BirkNextCompanion.sanitize
    : (typeof require === 'function' ? require('./sanitize.js') : null);

  const MAX_LONGEST_RESOURCES = 15;
  const MAX_FAILED_RESOURCES = 20;
  const FRAMEWORK_MARKER = '/_framework/';

  function classifyResource(entry) {
    const url = String(entry.name || '').toLowerCase().split('?')[0];
    const type = String(entry.initiatorType || '').toLowerCase();
    if (url.endsWith('.wasm')) return 'wasm';
    if (url.endsWith('.js') || url.endsWith('.mjs') || type === 'script') return 'js';
    if (url.endsWith('.css') || type === 'css') return 'css';
    if (type === 'img' || type === 'image' || /\.(png|jpe?g|gif|webp|svg|ico|avif)$/.test(url)) return 'image';
    if (/\.(woff2?|ttf|otf|eot)$/.test(url)) return 'font';
    if (type === 'fetch' || type === 'xmlhttprequest') return 'api';
    if (url.endsWith('.dll') || url.endsWith('.dat') || url.endsWith('.blat') || url.endsWith('.json') && url.includes(FRAMEWORK_MARKER)) return 'framework-data';
    return 'other';
  }

  function isFrameworkResource(entry) {
    return String(entry.name || '').toLowerCase().includes(FRAMEWORK_MARKER);
  }

  function round(value, digits = 0) {
    if (typeof value !== 'number' || !isFinite(value)) return null;
    const f = Math.pow(10, digits);
    return Math.round(value * f) / f;
  }

  /** Navigation timing: entry is a PerformanceNavigationTiming-like object (all values relative to timeOrigin). */
  function navigationSummary(nav) {
    if (!nav) return { ttfbMs: null, domContentLoadedMs: null, loadEventMs: null, navigationType: null, transferBytes: null };
    return {
      ttfbMs: round(nav.responseStart),
      domContentLoadedMs: round(nav.domContentLoadedEventEnd),
      loadEventMs: nav.loadEventEnd > 0 ? round(nav.loadEventEnd) : null,
      navigationType: nav.type || null,
      transferBytes: typeof nav.transferSize === 'number' ? nav.transferSize : null,
    };
  }

  /** Resource timing summary: entries are PerformanceResourceTiming-like objects. */
  function resourceSummary(entries) {
    const list = Array.isArray(entries) ? entries : [];
    const bytes = { js: 0, css: 0, image: 0, wasm: 0, font: 0, api: 0, 'framework-data': 0, other: 0 };
    let transferred = 0;
    let frameworkBytes = 0;
    let frameworkCount = 0;
    const seen = new Map();
    const failed = [];
    let duplicateFetches = 0;

    for (const e of list) {
      const kind = classifyResource(e);
      const transfer = typeof e.transferSize === 'number' ? e.transferSize : 0;
      const size = transfer > 0 ? transfer : (typeof e.encodedBodySize === 'number' ? e.encodedBodySize : 0);
      bytes[kind] += size;
      transferred += transfer;
      if (isFrameworkResource(e)) { frameworkCount++; frameworkBytes += size; }
      const key = String(e.name || '').split('?')[0].split('#')[0];
      const count = (seen.get(key) || 0) + 1;
      seen.set(key, count);
      if (count > 1) duplicateFetches++;
      // responseStatus is 0 for opaque/cross-origin-without-TAO responses; only treat explicit >= 400 (and 0 with no
      // body and no transfer for same-origin) as failures.
      const status = typeof e.responseStatus === 'number' ? e.responseStatus : null;
      if (status !== null && status >= 400 && failed.length < MAX_FAILED_RESOURCES) {
        failed.push({ url: sanitize ? sanitize.url(e.name) : key, kind, status });
      }
    }

    const longest = list
      .filter(e => typeof e.duration === 'number')
      .sort((a, b) => b.duration - a.duration)
      .slice(0, MAX_LONGEST_RESOURCES)
      .map(e => ({
        url: sanitize ? sanitize.url(e.name) : String(e.name || '').split('?')[0],
        kind: classifyResource(e),
        durationMs: round(e.duration),
        transferBytes: typeof e.transferSize === 'number' ? e.transferSize : null,
      }));

    const duplicates = Array.from(seen.entries()).filter(([, c]) => c > 1).slice(0, 20)
      .map(([url, count]) => ({ url: sanitize ? sanitize.url(url) : url, count }));

    return {
      resourceCount: list.length,
      transferredBytes: transferred,
      jsBytes: bytes.js, cssBytes: bytes.css, imageBytes: bytes.image, wasmBytes: bytes.wasm, fontBytes: bytes.font,
      apiBytes: bytes.api, frameworkDataBytes: bytes['framework-data'], otherBytes: bytes.other,
      frameworkResourceCount: frameworkCount,
      frameworkBytes,
      duplicateFetchCount: duplicateFetches,
      duplicateResources: duplicates,
      failedResourceCount: failed.length,
      failedResources: failed,
      longestResources: longest,
    };
  }

  /** LCP: the last largest-contentful-paint entry wins. CLS: sum of layout-shift values without recent input. */
  function vitalsSummary({ lcpEntries, layoutShiftEntries, longTaskEntries, paintEntries } = {}) {
    const lcp = Array.isArray(lcpEntries) && lcpEntries.length ? lcpEntries[lcpEntries.length - 1] : null;
    let cls = null;
    if (Array.isArray(layoutShiftEntries)) {
      cls = 0;
      for (const s of layoutShiftEntries) if (!s.hadRecentInput && typeof s.value === 'number') cls += s.value;
    }
    const longTasks = Array.isArray(longTaskEntries) ? longTaskEntries : [];
    const longTaskTotal = longTasks.reduce((t, e) => t + (typeof e.duration === 'number' ? e.duration : 0), 0);
    const fcp = Array.isArray(paintEntries) ? paintEntries.find(p => p.name === 'first-contentful-paint') : null;
    return {
      lcpMs: lcp ? round(lcp.renderTime || lcp.loadTime || lcp.startTime) : null,
      cls: cls === null ? null : round(cls, 4),
      longTaskCount: longTasks.length,
      longTaskTotalMs: round(longTaskTotal),
      longestTaskMs: longTasks.length ? round(Math.max(...longTasks.map(e => e.duration || 0))) : null,
      firstContentfulPaintMs: fcp ? round(fcp.startTime) : null,
    };
  }

  /** Blazor WebAssembly evidence from resource timing (framework folder) and DOM flags supplied by the caller. */
  function blazorSummary(entries, flags = {}) {
    const list = Array.isArray(entries) ? entries : [];
    const framework = list.filter(isFrameworkResource);
    const boot = framework.find(e => /blazor\.boot\.json$/i.test(String(e.name).split('?')[0]));
    const failures = framework
      .filter(e => typeof e.responseStatus === 'number' && e.responseStatus >= 400)
      .slice(0, MAX_FAILED_RESOURCES)
      .map(e => ({ url: sanitize ? sanitize.url(e.name) : String(e.name).split('?')[0], status: e.responseStatus }));
    const counts = new Map();
    for (const e of framework) { const k = String(e.name).split('?')[0]; counts.set(k, (counts.get(k) || 0) + 1); }
    const repeated = Array.from(counts.entries()).filter(([, c]) => c > 1).length;
    const wasmBytes = framework.filter(e => /\.wasm$/i.test(String(e.name).split('?')[0]))
      .reduce((t, e) => t + (e.transferSize || e.encodedBodySize || 0), 0);
    const detected = framework.length > 0 || flags.blazorScriptPresent === true;
    return {
      detected,
      bootManifestObserved: Boolean(boot),
      bootManifestFailed: Boolean(boot && typeof boot.responseStatus === 'number' && boot.responseStatus >= 400),
      frameworkResourceCount: framework.length,
      frameworkBytes: framework.reduce((t, e) => t + (e.transferSize || e.encodedBodySize || 0), 0),
      wasmBytes,
      frameworkFailures: failures,
      repeatedFrameworkDownloads: repeated,
      errorUiVisible: flags.errorUiVisible === true,
      blazorScriptPresent: flags.blazorScriptPresent === true,
    };
  }

  const api = { classifyResource, isFrameworkResource, navigationSummary, resourceSummary, vitalsSummary, blazorSummary, round,
    MAX_LONGEST_RESOURCES, MAX_FAILED_RESOURCES };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { perf: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
