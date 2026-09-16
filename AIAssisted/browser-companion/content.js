// BirkNext Browser Companion — content script (ISOLATED world, registered only for the paired environment's approved origins).
// Collects safe page evidence per visit: DOM structure counts, BirkNext accessibility rule results, performance timeline metrics,
// runtime error events and Blazor framework evidence. It never reads localStorage/sessionStorage/cookies, form values, request
// bodies or auth headers, and it never sends HTML or text content.
(function () {
  'use strict';
  const C = globalThis.BirkNextCompanion;
  if (!C || !C.pageIdentity || !C.sanitize || !C.perf || !C.dom || !C.a11y || !C.navigation) return;
  if (globalThis.__birkNextCompanionActive) return; // guard against duplicate injection
  globalThis.__birkNextCompanionActive = true;

  const win = window, doc = document;
  let scope = null;           // { profileId, approvedOrigins }
  let visit = null;           // current visit from the navigation tracker
  let snapshotSequence = 0;
  let updateTimer = null;
  let updatesSent = 0;
  let layoutChecks = [];
  let layoutBusy = false;
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (message?.type !== 'wcag:layout') return;
    if (sender.id !== chrome.runtime.id || layoutBusy || !isApprovedVisit(visit)) { respond({ message: 'No approved active page.' }); return; }
    (async () => {
      layoutBusy = true;
      try {
        scope = await send({ type: 'content:session' });
        if (!isApprovedVisit(visit) || !C.wcagInteraction.allowed(scope?.environmentType, true)) { respond({ message: 'Passive checks only for this environment.' }); return; }
        layoutChecks = await C.wcagInteraction.layout(doc, win, { environment: scope.environmentType, approved: true });
        await emit('update');
        respond({ message: 'Layout probes completed on the last visited visible page; original styles restored. Review results in BirkNext.' });
      } finally { layoutBusy = false; }
    })().catch(() => respond({ message: 'Layout probes unavailable; no pass recorded.' }));
    return true;
  });
  const runtime = { errors: new Map(), errorCount: 0, rejectionCount: 0, resourceFailureCount: 0 };
  const lcpEntries = [], layoutShiftEntries = [], longTaskEntries = [];
  const observers = [];
  const unsupported = [];

  function send(message) {
    return new Promise(resolve => {
      try { chrome.runtime.sendMessage(message, response => resolve(chrome.runtime.lastError ? null : response)); }
      catch { resolve(null); }
    });
  }

  // ── runtime error collection (error events only; no console interception) ──
  function recordError(kind, message, source) {
    const msg = C.sanitize.text(message || `${kind} (no message)`);
    const src = source ? C.sanitize.url(source) : null;
    const key = `${kind}|${msg}|${src || ''}`;
    const nowIso = new Date().toISOString();
    const existing = runtime.errors.get(key);
    if (existing) { existing.count++; existing.lastAt = nowIso; }
    else if (runtime.errors.size < 50) runtime.errors.set(key, { kind, message: msg, source: src, count: 1, firstAt: nowIso, lastAt: nowIso });
    if (kind === 'error') runtime.errorCount++; else if (kind === 'unhandledrejection') runtime.rejectionCount++; else runtime.resourceFailureCount++;
    scheduleUpdate();
  }
  // Element load failures are DOM events visible here; script exceptions/unhandled rejections happen in the page's own world and
  // are forwarded by main-world.js (listener only) through postMessage. Everything is sanitized in this isolated world.
  win.addEventListener('error', event => {
    if (event.target && event.target !== win && event.target.tagName) {
      const t = event.target;
      recordError('resource', `${t.tagName.toLowerCase()} failed to load`, t.src || t.href || '');
    }
  }, true);
  win.addEventListener('message', event => {
    if (event.source !== win || !event.data || event.data.__birkNextCompanion !== true) return;
    const kind = event.data.kind === 'unhandledrejection' ? 'unhandledrejection' : 'error';
    recordError(kind, event.data.message, kind === 'error' ? event.data.source : null);
  });

  // ── performance observers (registered once per document; entries are attributed to the current visit at snapshot time) ──
  function observe(type, list, buffered) {
    try {
      if (!('PerformanceObserver' in win) || !PerformanceObserver.supportedEntryTypes || !PerformanceObserver.supportedEntryTypes.includes(type)) { unsupported.push(type); return; }
      const observer = new PerformanceObserver(entries => { for (const e of entries.getEntries()) list.push(e); scheduleUpdate(); });
      observer.observe({ type, buffered: buffered !== false });
      observers.push(observer);
    } catch { unsupported.push(type); }
  }
  observe('largest-contentful-paint', lcpEntries);
  observe('layout-shift', layoutShiftEntries);
  observe('longtask', longTaskEntries);

  function resourceEntriesForVisit() {
    // Resource timing is per document; for SPA visits after the first, only resources started after the visit began count.
    const all = performance.getEntriesByType('resource');
    if (!visit || visit.sequence === 1) return all;
    const visitStart = new Date(visit.startedAt).getTime() - performance.timeOrigin;
    return all.filter(e => e.startTime >= visitStart - 50);
  }

  function blazorFlags() {
    const errorUi = doc.getElementById('blazor-error-ui');
    let visible = false;
    if (errorUi) { const style = win.getComputedStyle(errorUi); visible = style.display !== 'none' && style.visibility !== 'hidden'; }
    return { errorUiVisible: visible, blazorScriptPresent: Boolean(doc.querySelector('script[src*="_framework/blazor."]')) };
  }

  function buildSnapshot(kind) {
    if (!visit || !scope) return null;
    const resources = resourceEntriesForVisit();
    const nav = performance.getEntriesByType('navigation')[0] || null;
    const vitals = C.perf.vitalsSummary({
      lcpEntries: visit.sequence === 1 ? lcpEntries : [],   // LCP/CLS are defined for the initial document load only
      layoutShiftEntries: visit.sequence === 1 ? layoutShiftEntries : [],
      longTaskEntries: longTaskEntries.filter(e => !visit || visit.sequence === 1 || e.startTime >= new Date(visit.startedAt).getTime() - performance.timeOrigin),
      paintEntries: visit.sequence === 1 ? performance.getEntriesByType('paint') : [],
    });
    const navSummary = visit.sequence === 1 ? C.perf.navigationSummary(nav) : { ttfbMs: null, domContentLoadedMs: null, loadEventMs: null, navigationType: 'spa', transferBytes: null };
    const resourceSummary = C.perf.resourceSummary(resources);
    const performanceSummary = Object.assign({}, navSummary, vitals, resourceSummary, {
      unsupportedMetrics: unsupported.concat(visit.sequence === 1 ? [] : ['largest-contentful-paint (SPA route)', 'layout-shift (SPA route)', 'navigation-timing (SPA route)']),
    });
    const a11y = C.a11y.evaluate(doc, win);
    a11y.checks = (a11y.checks || []).concat(layoutChecks);
    return {
      profileId: scope.profileId,
      pageOrigin: visit.origin,
      pagePath: visit.path,
      visitStartedAt: visit.startedAt,
      capturedAt: new Date().toISOString(),
      snapshotKind: kind,
      snapshotSequence: ++snapshotSequence,
      documentTitle: C.sanitize.title(doc.title),
      browserName: browserName(),
      dom: C.dom.summarize(doc, win),
      accessibility: a11y,
      performance: performanceSummary,
      runtime: { errorCount: runtime.errorCount, rejectionCount: runtime.rejectionCount, resourceFailureCount: runtime.resourceFailureCount, errors: Array.from(runtime.errors.values()), consoleCaptured: false },
      blazor: C.perf.blazorSummary(resources, blazorFlags()),
    };
  }

  function browserName() {
    const ua = navigator.userAgent || '';
    if (/Edg\//.test(ua)) return 'Microsoft Edge';
    if (/Chrome\//.test(ua)) return 'Chromium';
    if (/Firefox\//.test(ua)) return 'Firefox';
    return 'Browser';
  }

  function isApprovedVisit(v) { return v && scope && C.pageIdentity.isApprovedOrigin(v.origin, scope.approvedOrigins); }

  async function emit(kind) {
    if (!isApprovedVisit(visit)) return;
    const page = buildSnapshot(kind);
    if (page) await send({ type: 'content:evidence', page });
  }

  function scheduleUpdate() {
    if (!visit || !visit.stabilized || updateTimer || updatesSent >= C.navigation.DEFAULTS.maxUpdates) return;
    updateTimer = setTimeout(async () => { updateTimer = null; updatesSent++; await emit('update'); }, C.navigation.DEFAULTS.updateEveryMs);
  }

  function resetVisitState() {
    layoutChecks = [];
    runtime.errors.clear(); runtime.errorCount = 0; runtime.rejectionCount = 0; runtime.resourceFailureCount = 0;
    updatesSent = 0;
    if (updateTimer) { clearTimeout(updateTimer); updateTimer = null; }
  }

  const tracker = C.navigation.createTracker({
    win, doc,
    onVisit: async v => {
      visit = v;
      if (!isApprovedVisit(v)) return;
      await send({ type: 'content:page', page: { origin: v.origin, path: v.path } });
      await emit('initial');
    },
    onNavigateAway: async previous => {
      if (previous.stabilized && isApprovedVisit(previous)) { visit = previous; await emit('final'); }
      resetVisitState();
      visit = null;
    },
  });

  win.addEventListener('pagehide', () => { if (visit && visit.stabilized) emit('final'); for (const o of observers) { try { o.disconnect(); } catch { } } tracker.dispose(); });

  (async () => {
    scope = await send({ type: 'content:session' });
    if (!scope || !scope.profileId) return; // not paired: stay completely passive
    tracker.start();
  })();
})();
