// BirkNext Browser Companion — performance summary built from browser-native Performance Timeline entries.
// Pure functions over plain entry-like objects so the calculations are unit-testable without a browser.
// Nothing here fabricates unsupported metrics: a metric the browser did not report stays null, INP is published only
// from real Event Timing interactions above a documented sample minimum, and every list is bounded.
(function (root) {
  'use strict';

  const sanitize = root.BirkNextCompanion && root.BirkNextCompanion.sanitize
    ? root.BirkNextCompanion.sanitize
    : (typeof require === 'function' ? require('./sanitize.js') : null);

  const MAX_LONGEST_RESOURCES = 15;
  const MAX_FAILED_RESOURCES = 20;
  const MAX_DUPLICATE_RESOURCES = 20;
  const MAX_TIMELINE_ENTRIES = 60;
  /** Long Tasks API: a task is "long" above 50 ms; blocking time is the excess over 50 ms (BirkNext main-thread blocking time). */
  const LONG_TASK_THRESHOLD_MS = 50;
  /** BirkNext policy: INP is published only after this many distinct interactions; fewer → "insufficient-samples". */
  const MIN_INP_INTERACTIONS = 3;
  const FRAMEWORK_MARKER = '/_framework/';
  const KINDS = ['js', 'css', 'wasm', 'image', 'font', 'api', 'document', 'framework-data', 'other'];

  function safeUrl(name) {
    return sanitize ? sanitize.url(name) : String(name || '').split('?')[0].split('#')[0];
  }

  function pathOf(entry) { return String(entry.name || '').split('?')[0].split('#')[0]; }

  function classifyResource(entry) {
    const url = pathOf(entry).toLowerCase();
    const type = String(entry.initiatorType || '').toLowerCase();
    if (type === 'navigation') return 'document';
    if (url.endsWith('.wasm')) return 'wasm';
    if (/\.(woff2?|ttf|otf|eot)$/.test(url)) return 'font';   // fonts are usually initiated by CSS, so the extension wins over the initiator
    if (url.endsWith('.js') || url.endsWith('.mjs') || type === 'script') return 'js';
    if (url.endsWith('.css') || type === 'css') return 'css';
    if (type === 'img' || type === 'image' || /\.(png|jpe?g|gif|webp|svg|ico|avif)$/.test(url)) return 'image';
    if (type === 'fetch' || type === 'xmlhttprequest' || type === 'beacon') return 'api';
    if (url.endsWith('.dll') || url.endsWith('.dat') || url.endsWith('.blat') || (url.endsWith('.json') && url.includes(FRAMEWORK_MARKER))) return 'framework-data';
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

  function num(value) { return typeof value === 'number' && isFinite(value) ? value : 0; }

  /**
   * Cache inference from Resource Timing: `deliveryType === 'cache'` is authoritative where supported; otherwise a zero transfer
   * with a non-zero body size means the browser served the response from its HTTP cache. Opaque cross-origin entries expose
   * zeros for everything and therefore stay "unknown" (null) — never counted as cached.
   */
  function deliveryOf(entry) {
    if (entry.deliveryType === 'cache') return 'cache';
    const transfer = num(entry.transferSize), encoded = num(entry.encodedBodySize), decoded = num(entry.decodedBodySize);
    if (transfer > 0) return 'network';
    if (encoded > 0 || decoded > 0) return 'cache';
    return null;
  }

  function sizeOf(entry) {
    const transfer = num(entry.transferSize);
    return transfer > 0 ? transfer : num(entry.encodedBodySize);
  }

  function toEntry(e, originMs) {
    const delivery = deliveryOf(e);
    return {
      url: safeUrl(e.name),
      kind: classifyResource(e),
      durationMs: round(e.duration),
      transferBytes: typeof e.transferSize === 'number' ? e.transferSize : null,
      decodedBytes: typeof e.decodedBodySize === 'number' ? e.decodedBodySize : null,
      status: typeof e.responseStatus === 'number' && e.responseStatus > 0 ? e.responseStatus : null,
      startMs: typeof e.startTime === 'number' ? round(e.startTime - num(originMs)) : null,
      delivery,
      count: 1,
    };
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

  /**
   * Resource timing summary: entries are PerformanceResourceTiming-like objects. `originMs` is the visit start relative to
   * timeOrigin (0 for the initial load), so timeline starts are relative to the observed navigation.
   */
  function resourceSummary(entries, options = {}) {
    const list = Array.isArray(entries) ? entries : [];
    const originMs = num(options.originMs);
    const bytes = {};
    const categories = {};
    for (const k of KINDS) { bytes[k] = 0; categories[k] = { kind: k, count: 0, transferBytes: 0, cachedCount: 0, largest: null, slowest: null }; }
    let transferred = 0, decodedTotal = 0, cached = 0;
    let frameworkBytes = 0, frameworkCount = 0;
    const seen = new Map();
    const failed = [];
    let duplicateFetches = 0;
    let largest = null, slowest = null;

    for (const e of list) {
      const kind = classifyResource(e);
      const transfer = num(e.transferSize);
      const size = sizeOf(e);
      const delivery = deliveryOf(e);
      bytes[kind] += size;
      transferred += transfer;
      decodedTotal += num(e.decodedBodySize);
      const cat = categories[kind];
      cat.count++;
      cat.transferBytes += transfer;
      if (delivery === 'cache') { cached++; cat.cachedCount++; }
      if (isFrameworkResource(e)) { frameworkCount++; frameworkBytes += size; }
      if (!cat.largest || size > sizeOf(cat.largest.raw)) cat.largest = { raw: e };
      if (!cat.slowest || num(e.duration) > num(cat.slowest.raw.duration)) cat.slowest = { raw: e };
      if (!largest || size > sizeOf(largest)) largest = e;
      if (!slowest || num(e.duration) > num(slowest.duration)) slowest = e;
      const key = pathOf(e);
      const record = seen.get(key) || { count: 0, network: 0, kind };
      record.count++;
      if (delivery === 'network') record.network++;
      seen.set(key, record);
      if (record.count > 1) duplicateFetches++;
      // responseStatus is 0 for opaque/cross-origin-without-TAO responses; only an explicit >= 400 is a failure.
      const status = typeof e.responseStatus === 'number' ? e.responseStatus : null;
      if (status !== null && status >= 400 && failed.length < MAX_FAILED_RESOURCES) failed.push(toEntry(e, originMs));
    }

    const longest = list
      .filter(e => typeof e.duration === 'number')
      .sort((a, b) => b.duration - a.duration)
      .slice(0, MAX_LONGEST_RESOURCES)
      .map(e => toEntry(e, originMs));

    const duplicates = Array.from(seen.entries()).filter(([, r]) => r.count > 1).slice(0, MAX_DUPLICATE_RESOURCES)
      .map(([url, r]) => ({ url: safeUrl(url), kind: r.kind, count: r.count, networkCount: r.network }));

    const timeline = list
      .filter(e => typeof e.startTime === 'number')
      .sort((a, b) => a.startTime - b.startTime)
      .slice(0, MAX_TIMELINE_ENTRIES)
      .map(e => toEntry(e, originMs));

    const categoryList = KINDS.map(k => {
      const c = categories[k];
      return {
        kind: k, count: c.count, transferBytes: c.transferBytes, cachedCount: c.cachedCount,
        largest: c.largest ? toEntry(c.largest.raw, originMs) : null,
        slowest: c.slowest ? toEntry(c.slowest.raw, originMs) : null,
      };
    }).filter(c => c.count > 0);

    return {
      resourceCount: list.length,
      transferredBytes: transferred,
      decodedBytes: decodedTotal,
      cachedResourceCount: cached,
      jsBytes: bytes.js, cssBytes: bytes.css, imageBytes: bytes.image, wasmBytes: bytes.wasm, fontBytes: bytes.font,
      apiBytes: bytes.api, frameworkDataBytes: bytes['framework-data'], otherBytes: bytes.other + bytes.document,
      frameworkResourceCount: frameworkCount,
      frameworkBytes,
      categories: categoryList,
      largestResource: largest ? toEntry(largest, originMs) : null,
      slowestResource: slowest ? toEntry(slowest, originMs) : null,
      duplicateFetchCount: duplicateFetches,
      duplicateResources: duplicates,
      failedResourceCount: failed.length,
      failedResources: failed,
      longestResources: longest,
      timeline,
    };
  }

  /**
   * Long tasks for one visit. `stabilizedAtMs` (relative to the same clock as the entries) splits load-phase tasks from
   * runtime-phase tasks. Main-thread blocking time is Σ max(0, duration − 50 ms): BirkNext-specific, deliberately not called TBT.
   */
  function longTaskSummary(longTaskEntries, stabilizedAtMs) {
    const longTasks = Array.isArray(longTaskEntries) ? longTaskEntries : [];
    let total = 0, blocking = 0, longest = 0, after = 0;
    for (const t of longTasks) {
      const d = num(t.duration);
      total += d;
      blocking += Math.max(0, d - LONG_TASK_THRESHOLD_MS);
      if (d > longest) longest = d;
      if (typeof stabilizedAtMs === 'number' && typeof t.startTime === 'number' && t.startTime >= stabilizedAtMs) after++;
    }
    return {
      longTaskCount: longTasks.length,
      longTaskTotalMs: round(total),
      longestTaskMs: longTasks.length ? round(longest) : null,
      mainThreadBlockingMs: longTasks.length ? round(blocking) : null,
      longTasksAfterStabilization: after,
    };
  }

  /**
   * INP from Event Timing entries, following the web-vitals methodology: group entries by interactionId, keep the longest
   * duration per interaction, then take the worst interaction (or the (n/50)th worst for ≥ 50 interactions). Published only
   * when Event Timing is supported AND at least MIN_INP_INTERACTIONS distinct interactions were observed; otherwise the status
   * explains the gap and inpMs stays null. Never estimated from long tasks or other proxies.
   */
  function interactionSummary(eventEntries, firstInputEntries, options = {}) {
    const supported = options.supported !== false;
    const byInteraction = new Map();
    for (const e of Array.isArray(eventEntries) ? eventEntries : []) {
      const id = typeof e.interactionId === 'number' ? e.interactionId : 0;
      if (id <= 0) continue;
      const d = num(e.duration);
      if (d > (byInteraction.get(id) || 0)) byInteraction.set(id, d);
    }
    const durations = Array.from(byInteraction.values()).sort((a, b) => b - a);
    const count = durations.length;
    const firstInput = Array.isArray(firstInputEntries) && firstInputEntries.length ? firstInputEntries[0] : null;
    const firstInputDelayMs = firstInput && typeof firstInput.processingStart === 'number' && typeof firstInput.startTime === 'number'
      ? round(firstInput.processingStart - firstInput.startTime) : null;
    let status = 'not-measured';
    let inpMs = null;
    if (!supported) status = 'not-supported';
    else if (count === 0) status = 'not-measured';
    else if (count < MIN_INP_INTERACTIONS) status = 'insufficient-samples';
    else {
      status = 'measured';
      const index = Math.min(count - 1, Math.floor(count / 50));
      inpMs = round(durations[index]);
    }
    return {
      status,
      interactionCount: count,
      minimumInteractions: MIN_INP_INTERACTIONS,
      inpMs,
      longestInteractionMs: count ? round(durations[0]) : null,
      firstInputDelayMs,
    };
  }

  /** LCP: the last largest-contentful-paint entry wins. CLS: sum of layout-shift values without recent input. */
  function vitalsSummary({ lcpEntries, layoutShiftEntries, longTaskEntries, paintEntries, eventEntries, firstInputEntries, eventTimingSupported, stabilizedAtMs } = {}) {
    const lcp = Array.isArray(lcpEntries) && lcpEntries.length ? lcpEntries[lcpEntries.length - 1] : null;
    let cls = null;
    if (Array.isArray(layoutShiftEntries)) {
      cls = 0;
      for (const s of layoutShiftEntries) if (!s.hadRecentInput && typeof s.value === 'number') cls += s.value;
    }
    const fcp = Array.isArray(paintEntries) ? paintEntries.find(p => p.name === 'first-contentful-paint') : null;
    return Object.assign({
      lcpMs: lcp ? round(lcp.renderTime || lcp.loadTime || lcp.startTime) : null,
      cls: cls === null ? null : round(cls, 4),
      firstContentfulPaintMs: fcp ? round(fcp.startTime) : null,
      interaction: interactionSummary(eventEntries, firstInputEntries, { supported: eventTimingSupported }),
    }, longTaskSummary(longTaskEntries, stabilizedAtMs));
  }

  function isRuntimeResource(path) { return /\/_framework\/dotnet(\.native)?(\.[a-z0-9.-]+)?\.(js|wasm)$/i.test(path) || /\/_framework\/dotnet\.js$/i.test(path); }
  function isFrameworkJs(path) { return /\/_framework\/(blazor\.[a-z.-]*js|dotnet[a-z0-9.-]*\.js)$/i.test(path); }
  function isCultureResource(path) { return /\/_framework\/(icudt[a-z0-9_.-]*\.dat|dotnet\.timezones\.blat)$/i.test(path); }
  function isAssemblyResource(path) { return /\/_framework\/.+\.(dll|wasm)$/i.test(path) && !isRuntimeResource(path); }

  /** Blazor WebAssembly evidence from resource timing (framework folder) and DOM flags supplied by the caller. */
  function blazorSummary(entries, flags = {}, options = {}) {
    const list = Array.isArray(entries) ? entries : [];
    const originMs = num(options.originMs);
    const framework = list.filter(isFrameworkResource);
    const boot = framework.find(e => /blazor\.boot\.json$/i.test(pathOf(e)));
    const failures = framework
      .filter(e => typeof e.responseStatus === 'number' && e.responseStatus >= 400)
      .slice(0, MAX_FAILED_RESOURCES)
      .map(e => toEntry(e, originMs));
    const counts = new Map();
    for (const e of framework) { const k = pathOf(e); counts.set(k, (counts.get(k) || 0) + 1); }
    const repeated = Array.from(counts.entries()).filter(([, c]) => c > 1).length;
    let wasmBytes = 0, runtimeCount = 0, runtimeBytes = 0, assemblyCount = 0, assemblyBytes = 0, cultureCount = 0, cultureBytes = 0, jsCount = 0, cachedCount = 0, networkCount = 0;
    let timezone = false, start = null, end = null, slowest = null;
    for (const e of framework) {
      const p = pathOf(e);
      const size = sizeOf(e);
      if (/\.wasm$/i.test(p)) wasmBytes += size;
      if (isRuntimeResource(p)) { runtimeCount++; runtimeBytes += size; }
      else if (isCultureResource(p)) { cultureCount++; cultureBytes += size; if (/timezones\.blat$/i.test(p)) timezone = true; }
      else if (isAssemblyResource(p)) { assemblyCount++; assemblyBytes += size; }
      if (isFrameworkJs(p)) jsCount++;
      const delivery = deliveryOf(e);
      if (delivery === 'cache') cachedCount++; else if (delivery === 'network') networkCount++;
      if (typeof e.startTime === 'number') { const s = e.startTime - originMs; if (start === null || s < start) start = s; }
      if (typeof e.responseEnd === 'number') { const en = e.responseEnd - originMs; if (end === null || en > end) end = en; }
      if (!slowest || num(e.duration) > num(slowest.duration)) slowest = e;
    }
    const detected = framework.length > 0 || flags.blazorScriptPresent === true;
    const loadKind = framework.length === 0 ? 'none' : networkCount > 0 && cachedCount === 0 ? 'cold' : cachedCount > 0 && networkCount === 0 ? 'warm' : networkCount > 0 && cachedCount > 0 ? 'mixed' : 'unknown';
    return {
      detected,
      bootManifestObserved: Boolean(boot),
      bootManifestFailed: Boolean(boot && typeof boot.responseStatus === 'number' && boot.responseStatus >= 400),
      bootManifestMs: boot ? round(boot.duration) : null,
      frameworkResourceCount: framework.length,
      frameworkBytes: framework.reduce((t, e) => t + sizeOf(e), 0),
      wasmBytes,
      runtimeResourceCount: runtimeCount, runtimeBytes,
      assemblyCount, assemblyBytes,
      cultureResourceCount: cultureCount, cultureBytes, timezoneDataObserved: timezone,
      frameworkJsCount: jsCount,
      cachedFrameworkResourceCount: cachedCount,
      loadKind,
      frameworkLoadStartMs: start === null ? null : round(start),
      frameworkLoadEndMs: end === null ? null : round(end),
      frameworkFailures: failures,
      slowestFrameworkResource: slowest ? toEntry(slowest, originMs) : null,
      repeatedFrameworkDownloads: repeated,
      errorUiVisible: flags.errorUiVisible === true,
      blazorScriptPresent: flags.blazorScriptPresent === true,
    };
  }

  const api = { classifyResource, isFrameworkResource, deliveryOf, navigationSummary, resourceSummary, longTaskSummary, interactionSummary, vitalsSummary, blazorSummary, round,
    MAX_LONGEST_RESOURCES, MAX_FAILED_RESOURCES, MAX_DUPLICATE_RESOURCES, MAX_TIMELINE_ENTRIES, LONG_TASK_THRESHOLD_MS, MIN_INP_INTERACTIONS, KINDS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { perf: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
