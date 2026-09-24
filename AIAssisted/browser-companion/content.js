// BirkNext Browser Companion — content script (ISOLATED world, registered only for the paired environment's approved origins).
// Collects safe page evidence per visit: DOM structure counts, BirkNext accessibility rule results, performance timeline metrics
// (navigation/resource timing, LCP/CLS, Event Timing interactions, long tasks, page stabilization), runtime error events and Blazor
// framework evidence. It never reads localStorage/sessionStorage/cookies, form values, request bodies or auth headers, and it never
// sends HTML or text content. Evidence is batched (one snapshot per quiet window, bounded updates) so the collector stays cheap.
(function () {
  'use strict';
  const C = globalThis.BirkNextCompanion;
  if (!C || !C.pageIdentity || !C.sanitize || !C.perf || !C.dom || !C.a11y || !C.navigation) return;
  if (globalThis.__birkNextCompanionActive) return; // guard against duplicate injection
  globalThis.__birkNextCompanionActive = true;

  const win = window, doc = document;
  // Identifies THIS content script instance. A full page load makes a new one, which is how the backend tells a reload
  // apart from a second tab instead of accumulating phantom live pages.
  const instanceId = Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
  let livePageId = null;

  /**
   * Announces that this page is alive on an approved origin.
   *
   * Deliberately separate from evidence reporting. Page liveness is continuous — it starts when the script starts and
   * ends when the tab does — while an evidence visit begins and ends with every SPA route change. Deriving one from the
   * other made the browser appear to vanish mid-navigation, and made a closed tab appear open as long as its last
   * snapshot was still arriving.
   */
  let announcedRoute = null;
  async function announceLive() {
    if (!scope) return;
    announcedRoute = C.pageIdentity.identityOf(win.location.href)?.path ?? '/';
    // The page reports its own route: sender.url is the frame's committed URL and does not follow a same-document
    // pushState, so trusting it would freeze the route at whatever the tab first loaded.
    lastAnnouncedAt = Date.now();
    const response = await send({ type: 'content:live', instanceId, route: announcedRoute });
    if (response?.pageId) livePageId = response.pageId;
  }
  let scope = null;           // { profileId, approvedOrigins }
  let visit = null;           // current visit from the navigation tracker
  let snapshotSequence = 0;
  let updateTimer = null;
  let updatesSent = 0;
  let layoutChecks = [];
  let keyboardChecks = [];
  let keyboardObservation = null;
  let layoutBusy = false;
  let observerCallbacks = 0;
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (!['wcag:layout', 'wcag:keyboard'].includes(message?.type)) return;
    if (sender.id !== chrome.runtime.id || layoutBusy || !isApprovedVisit(visit)) { respond({ message: 'No approved active page.' }); return; }
    (async () => {
      layoutBusy = true;
      try {
        scope = await send({ type: 'content:session' });
    // Live before any evidence: a page the user just opened is open, whether or not anything has been observed on it.
    if (scope && C.pageIdentity.isApprovedOrigin(C.pageIdentity.originOf(win.location.href), scope.approvedOrigins)) await announceLive();
        if (!isApprovedVisit(visit) || !C.wcagInteraction.allowed(scope?.environmentType, true)) { respond({ message: 'Passive checks only for this environment.' }); return; }
        if (message.type === 'wcag:keyboard') {
          keyboardObservation?.stop();
          const observedVisit = visit;
          keyboardObservation = C.wcagKeyboard.observe(doc, win, { environment: scope.environmentType, approved: true }, checks => {
            if (visit !== observedVisit) return;
            keyboardChecks = checks;
            emit('update');
          });
          respond({ message: 'Close this popup and use Tab / Shift+Tab for 30 seconds. Focus observations require review; no controls are activated automatically.' });
          return;
        }
        layoutChecks = await C.wcagInteraction.layout(doc, win, { environment: scope.environmentType, approved: true });
        await emit('update');
        respond({ message: 'Layout probes completed on the last visited visible page; original styles restored. Review results in BirkNext.' });
      } finally { layoutBusy = false; }
    })().catch(() => respond({ message: 'Layout probes unavailable; no pass recorded.' }));
    return true;
  });
  // ── Critical E2E step runner ───────────────────────────────────────────────
  // BirkNext asks for one allow-listed action against a described element and gets back an outcome. It never sends
  // JavaScript: the message carries an action name and a selector description, so there is nothing here to eval. The
  // action is performed the way a person would perform it — a hidden, disabled or read-only control is reported, not
  // forced. Statuses are the shared contract's (Passed / Failed / Blocked), so no mapping layer can drift.
  let probeBusy = false;
  const finishedCommands = new Map();   // commandId -> result, so a redelivered command is answered, not re-executed
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (message?.type !== 'e2e:command') return;
    const command = message.command || {};
    const startedAt = new Date().toISOString(), started = Date.now();
    const finish = result => {
      const full = {
        commandId: command.commandId ?? null, stepId: command.stepId ?? '', startedAt,
        completedAt: new Date().toISOString(), durationMs: Date.now() - started,
        observedRoute: win.location.pathname, observedValue: null, assertionResult: null,
        evidenceReference: visit && isApprovedVisit(visit) ? `${visit.origin}${visit.path}` : null,
        safeSummary: null, sanitizedError: null, ...result,
      };
      if (command.commandId) finishedCommands.set(command.commandId, full);
      respond(full);
    };
    if (sender.id !== chrome.runtime.id || !C.automation) { finish({ status: 'Blocked', sanitizedError: 'Step runner unavailable.' }); return; }
    // The same command arriving twice is answered with what already happened. A worker restart mid-flight must not turn
    // one click into two.
    if (command.commandId && finishedCommands.has(command.commandId)) { respond(finishedCommands.get(command.commandId)); return; }
    if (probeBusy) { finish({ status: 'Blocked', sanitizedError: 'A step is already running on this page.' }); return; }
    (async () => {
      probeBusy = true;
      try {
        scope = await send({ type: 'content:session' });
        const timeoutMs = Math.min(Math.max(Number(command.timeoutMs) || 5000, 500), 60000);
        // A step often arrives during a route change, and the navigation tracker has no current visit for the moment
        // between leaving one route and starting the next. Refusing then would report "no approved page" for a page the
        // user is looking at, so wait for the visit the same way everything else here waits for the application.
        if (!isApprovedVisit(visit)) await C.automation.waitFor(() => isApprovedVisit(visit), { timeoutMs, intervalMs: 100 });
        if (!isApprovedVisit(visit)) { finish({ status: 'Blocked', sanitizedError: 'No approved active page.' }); return; }
        // The same non-production gate the worker applied, re-checked in the page: the worker's answer is not the only
        // thing standing between a step and a production page.
        if (!isApprovedVisit(visit) || !C.wcagInteraction.allowed(scope?.environmentType, true)) {
          finish({ status: 'Blocked', sanitizedError: 'Steps run only on approved non-production pages.' }); return;
        }
        // The page must still be the page the command was aimed at.
        if (command.targetOrigin && command.targetOrigin !== visit.origin) {
          finish({ status: 'Blocked', sanitizedError: 'The browser page changed before the step ran.' }); return;
        }
        // Authoring: the tester picks one element. Bound to this exact content-script instance, because a reload in
        // between would mean describing an element of a page the command was never aimed at.
        if (command.action === 'PickElement') {
          if (!C.picker) { finish({ status: 'Blocked', sanitizedError: 'Element picking is unavailable in this page.' }); return; }
          if (command.contentScriptInstanceId && command.contentScriptInstanceId !== instanceId) {
            finish({ status: 'Blocked', sanitizedError: 'The page was reloaded after picking was requested.' }); return;
          }
          qualityObserver.disconnect();   // the picker's overlay is not page evidence
          let picked;
          try { picked = await C.picker.pick(doc, win, { timeoutMs }); } finally { observeQuality(); }
          if (picked.status === 'cancelled') { finish({ status: 'Cancelled', sanitizedError: 'Selection cancelled.' }); return; }
          if (picked.status !== 'picked') { finish({ status: 'Blocked', sanitizedError: 'No element was selected before the picker timed out.' }); return; }
          if (!isApprovedVisit(visit)) { finish({ status: 'Blocked', sanitizedError: 'The page changed while picking.' }); return; }
          const described = C.picker.describe(doc, win, picked.element);
          if (described.error) { finish({ status: 'Blocked', sanitizedError: described.error }); return; }
          const d = described.descriptor;
          finish({
            status: 'Passed', element: d,
            safeSummary: C.sanitize.text(`Picked ${d.tagName}${d.accessibleName ? ` "${d.accessibleName}"` : ''}`),
          });
          return;
        }
        if (!C.automation.ACTIONS.includes(command.action)) {
          finish({ status: 'Blocked', sanitizedError: `Action not allowed: ${C.sanitize.text(String(command.action))}` }); return;
        }
        // Wait for the application to render rather than sleeping: an SPA settles when it settles, and a fixed delay is
        // either a flake or wasted time. A mutating action runs once; only observations are retried.
        let outcome = { status: 'failed', error: 'Step did not run.' };
        const once = C.automation.MUTATING.includes(command.action);
        await C.automation.waitFor(() => {
          if (once && outcome.status === 'passed') return true;
          outcome = C.automation.perform(doc, win, command);
          return once || outcome.status === 'passed';
        }, { timeoutMs, intervalMs: 150 });
        finish({
          status: outcome.status === 'passed' ? 'Passed' : 'Failed',
          assertionResult: outcome.assertionResult ?? null,
          observedValue: outcome.observedValue == null ? null : C.sanitize.text(String(outcome.observedValue)),
          safeSummary: outcome.summary ? C.sanitize.text(outcome.summary) : null,
          sanitizedError: outcome.error ? C.sanitize.text(outcome.error) : null,
        });
      } catch (e) {
        finish({ status: 'Blocked', sanitizedError: C.sanitize.text(`Step could not run: ${e && e.message}`) });
      } finally { probeBusy = false; }
    })();
    return true;
  });

  const runtime = { errors: new Map(), errorCount: 0, rejectionCount: 0, resourceFailureCount: 0, errorsBeforeStabilization: 0 };
  const lcpEntries = [], layoutShiftEntries = [], longTaskEntries = [], eventEntries = [], firstInputEntries = [];
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
    if (visit && !visit.stabilized && kind !== 'resource') runtime.errorsBeforeStabilization++;
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
  function observe(type, list, options) {
    try {
      if (!('PerformanceObserver' in win) || !PerformanceObserver.supportedEntryTypes || !PerformanceObserver.supportedEntryTypes.includes(type)) { unsupported.push(type); return false; }
      const observer = new PerformanceObserver(entries => {
        observerCallbacks++;
        for (const e of entries.getEntries()) { if (list) list.push(e); if (options && options.onEntry) options.onEntry(e); }
        if (!options || options.schedule !== false) scheduleUpdate();
      });
      observer.observe(Object.assign({ type, buffered: true }, options && options.observe ? options.observe : {}));
      observers.push(observer);
      return true;
    } catch { unsupported.push(type); return false; }
  }
  observe('largest-contentful-paint', lcpEntries);
  observe('layout-shift', layoutShiftEntries);
  observe('longtask', longTaskEntries);
  // Event Timing: durationThreshold 40 ms (web-vitals convention) so short interactions are still counted toward the sample minimum.
  const eventTimingSupported = observe('event', eventEntries, { observe: { durationThreshold: 40 } });
  observe('first-input', firstInputEntries);
  // Resource entries feed the network-quiet stabilization signal only (the summary re-reads the buffer at snapshot time).
  observe('resource', null, { schedule: false, onEntry: e => { if (tracker) tracker.noteNetwork(e.name); } });

  function visitStartRelMs() {
    return visit ? new Date(visit.startedAt).getTime() - performance.timeOrigin : 0;
  }

  function resourceEntriesForVisit() {
    // Resource timing is per document; for SPA visits after the first, only resources started after the visit began count.
    const all = performance.getEntriesByType('resource');
    if (!visit || visit.sequence === 1) return all;
    const visitStart = visitStartRelMs();
    return all.filter(e => e.startTime >= visitStart - 50);
  }

  function blazorFlags() {
    const errorUi = doc.getElementById('blazor-error-ui');
    let visible = false;
    if (errorUi) { const style = win.getComputedStyle(errorUi); visible = style.display !== 'none' && style.visibility !== 'hidden'; }
    return { errorUiVisible: visible, blazorScriptPresent: Boolean(doc.querySelector('script[src*="_framework/blazor."]')) };
  }

  function memorySnapshot() {
    try {
      const m = performance.memory;
      return m && typeof m.usedJSHeapSize === 'number' ? m.usedJSHeapSize : null;
    } catch { return null; }
  }

  function buildSnapshot(kind) {
    if (!visit || !scope) return null;
    const buildStart = performance.now();
    const initial = visit.sequence === 1;
    const originMs = initial ? 0 : visitStartRelMs();
    const stabilizedAtMs = visit.stabilizedAt ? new Date(visit.stabilizedAt).getTime() - performance.timeOrigin : null;
    const resources = resourceEntriesForVisit();
    const nav = performance.getEntriesByType('navigation')[0] || null;
    const inVisit = e => initial || (typeof e.startTime === 'number' && e.startTime >= originMs);
    const vitals = C.perf.vitalsSummary({
      lcpEntries: initial ? lcpEntries : [],   // LCP/CLS are defined for the initial document load only
      layoutShiftEntries: initial ? layoutShiftEntries : [],
      longTaskEntries: longTaskEntries.filter(inVisit),
      paintEntries: initial ? performance.getEntriesByType('paint') : [],
      eventEntries: eventEntries.filter(inVisit),
      firstInputEntries: initial ? firstInputEntries : [],
      eventTimingSupported,
      stabilizedAtMs,
    });
    const navSummary = initial ? C.perf.navigationSummary(nav) : { ttfbMs: null, domContentLoadedMs: null, loadEventMs: null, navigationType: 'spa', transferBytes: null };
    const resourceSummary = C.perf.resourceSummary(resources, { originMs });
    const performanceSummary = Object.assign({ observationType: initial ? 'initial-load' : 'spa-navigation' }, navSummary, vitals, resourceSummary, {
      stabilizationMs: visit.stabilized ? visit.stabilizationMs : null,
      stabilizedBy: visit.stabilized ? visit.stabilizedBy : null,
      mutations: Object.assign({}, visit.mutations),
      jsHeapUsedBytes: memorySnapshot(),
      unsupportedMetrics: unsupported.concat(initial ? [] : ['largest-contentful-paint (SPA route)', 'layout-shift (SPA route)', 'navigation-timing (SPA route)']),
    });
    const a11y = C.a11y.evaluate(doc, win);
    a11y.checks = (a11y.checks || []).concat(layoutChecks, keyboardChecks);
    const page = {
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
      runtime: { errorCount: runtime.errorCount, rejectionCount: runtime.rejectionCount, resourceFailureCount: runtime.resourceFailureCount, errors: Array.from(runtime.errors.values()), consoleCaptured: false, errorsBeforeStabilization: runtime.errorsBeforeStabilization },
      blazor: C.perf.blazorSummary(resources, blazorFlags(), { originMs }),
    };
    // Collector overhead is part of the evidence so the observer can be audited against the page it measures.
    const payloadBytes = JSON.stringify(page).length;
    performanceSummary.collector = {
      snapshotBuildMs: C.perf.round(performance.now() - buildStart, 1),
      observerCallbacks,
      snapshotsSent: updatesSent + 1,
      payloadBytes,
      entriesExamined: resources.length,
    };
    return page;
  }

  function browserName() {
    const ua = navigator.userAgent || '';
    if (/Edg\//.test(ua)) return 'Microsoft Edge';
    if (/Chrome\//.test(ua)) return 'Chromium';
    if (/Firefox\//.test(ua)) return 'Firefox';
    return 'Browser';
  }

  function isApprovedVisit(v) { return v && scope && C.pageIdentity.isApprovedOrigin(v.origin, scope.approvedOrigins); }

  let qualityRevision = 0;
  const qualityObserver = new MutationObserver(() => { qualityRevision++; scheduleUpdate(); });
  function observeQuality() { qualityObserver.observe(doc, { subtree: true, childList: true, attributes: true, characterData: true }); }
  observeQuality();
  const collectAxe = C.axeEvidence?.collector(globalThis.axe, {
    beforeRun: () => qualityObserver.disconnect(), // axe's temporary DOM probes must not retrigger themselves
    afterRun: observeQuality,
  });
  async function emit(kind) {
    if (!isApprovedVisit(visit)) return;
    const observedVisit = visit;
    const page = buildSnapshot(kind);
    // Per visit and DOM revision; unchanged metric snapshots reuse execution. Existing update limits bound automatic work.
    if (page && collectAxe) page.accessibility.axe = await collectAxe(doc, `${observedVisit.origin}${observedVisit.path}|${observedVisit.startedAt}|${qualityRevision}`);
    if (visit !== observedVisit || !isApprovedVisit(visit)) return;
    if (page) await send({ type: 'content:evidence', page });
  }

  function scheduleUpdate() {
    if (!visit || !visit.stabilized || updateTimer || updatesSent >= C.navigation.DEFAULTS.maxUpdates) return;
    updateTimer = setTimeout(async () => { updateTimer = null; updatesSent++; await emit('update'); }, C.navigation.DEFAULTS.updateEveryMs);
  }

  function resetVisitState() {
    keyboardObservation?.stop(); keyboardObservation = null; keyboardChecks = [];
    layoutChecks = [];
    runtime.errors.clear(); runtime.errorCount = 0; runtime.rejectionCount = 0; runtime.resourceFailureCount = 0; runtime.errorsBeforeStabilization = 0;
    updatesSent = 0;
    observerCallbacks = 0;
    if (updateTimer) { clearTimeout(updateTimer); updateTimer = null; }
  }

  const tracker = C.navigation.createTracker({
    win, doc,
    // The visit object is shared from route change on, so errors and interactions before stabilization are attributed to it.
    onVisitStart: v => {
      visit = v;
      // The route moved; the page did not. Re-announcing keeps the route current without the page ever being absent.
      if (isApprovedVisit(v)) { announceLive(); send({ type: 'content:page' }); }
    },
    onVisit: async v => {
      visit = v;
      if (!isApprovedVisit(v)) return;
      await send({ type: 'content:page', page: { origin: v.origin, path: v.path } });
      await emit('initial');
    },
    // Synchronous on purpose. The tracker starts the next visit straight after this returns; awaiting the final snapshot
    // here (it waits for the serialized axe run) let this reset land after the NEXT route had stabilized, which cleared
    // that route's visit and dropped its evidence. emit() takes its snapshot before its first await, so it still sees
    // the previous visit, and its own visit check keeps anything late from being reported as the new route.
    onNavigateAway: previous => {
      if (previous.stabilized && isApprovedVisit(previous)) { visit = previous; emit('final'); }
      resetVisitState();
      visit = null;
    },
  });

  let pageHeartbeat = null, livenessHeartbeat = null, lastAnnouncedAt = 0;
  win.addEventListener('pagehide', () => { clearInterval(pageHeartbeat); clearInterval(livenessHeartbeat); if (visit && visit.stabilized) emit('final'); for (const o of observers) { try { o.disconnect(); } catch { } } tracker.dispose(); });

  (async () => {
    scope = await send({ type: 'content:session' });
    if (!scope || !scope.profileId) return; // not paired: stay completely passive
    tracker.start();
    // Reporting is independent of evidence stabilization, update limits and which browser tab is active.
    // The worker rechecks sender origin, profile and current permissions on every message.
    pageHeartbeat = setInterval(() => {
      if (isApprovedVisit(visit)) send({ type: 'content:page' });
    }, 15000);
    // Liveness is its own path, deliberately not gated on an evidence visit: this page is open whether or not anything
    // has been observed on it, and it stays open across the gap between one visit ending and the next beginning.
    //
    // It also re-announces whenever the route moves. A pushState from the application runs in the MAIN world, which an
    // isolated-world history patch cannot see, so comparing the path is the only way the route stays current without
    // waiting for the evidence tracker to notice on its own poll.
    livenessHeartbeat = setInterval(() => {
      if (!scope || !C.pageIdentity.isApprovedOrigin(C.pageIdentity.originOf(win.location.href), scope.approvedOrigins)) return;
      const route = C.pageIdentity.identityOf(win.location.href)?.path ?? '/';
      if (route !== announcedRoute || Date.now() - lastAnnouncedAt > 15000) announceLive();
    }, 500);
  })();
})();
