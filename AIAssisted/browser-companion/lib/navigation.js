// BirkNext Browser Companion — SPA navigation tracker + page stabilization model.
// Detects route changes through history.pushState/replaceState (wrapped), popstate/hashchange, and URL polling as a fallback
// (no continuous DOM scanning). Each distinct page identity starts a new "visit" with its own stabilization window:
//   navigation detected → DOM ready → quiet period (no URL change, no burst of mutations, no NEW relevant network activity)
//   → initial snapshot → passive updates.
// Stabilization is bounded by maxWaitMs, so background polling, WebSockets or endless DOM churn can never keep a page
// "unstable" forever; the visit records whether it stabilized by quiet or by the bound ("BirkNext Page Stabilization Time").
// Pure enough to be unit-tested with fake window/history/document objects.
(function (root) {
  'use strict';

  const pageIdentity = root.BirkNextCompanion && root.BirkNextCompanion.pageIdentity
    ? root.BirkNextCompanion.pageIdentity
    : (typeof require === 'function' ? require('./page-identity.js') : null);

  const DEFAULTS = { quietMs: 800, maxWaitMs: 4000, pollMs: 1000, updateEveryMs: 4000, maxUpdates: 6, pollingRepeatThreshold: 2, largeBatch: 50 };

  /**
   * createTracker({ win, doc, onVisit(visit), onVisitStart(visit), onNavigateAway(visit), options })
   * visit = { identity, origin, path, startedAt (ISO), startedMs, sequence, stabilized: false, stabilizedAt, stabilizationMs,
   *           stabilizedBy, mutations: { batchCount, mutationCount, largestBatch, lastMutationMs, largeBatchesAfterStabilization } }
   */
  function createTracker({ win, doc, onVisit, onVisitStart, onNavigateAway, options, now } = {}) {
    const opts = Object.assign({}, DEFAULTS, options || {});
    const clock = now || (() => Date.now());
    const setTimer = (fn, ms) => win.setTimeout(fn, ms);
    const clearTimer = id => win.clearTimeout(id);
    let current = null;
    let visitSequence = 0;
    let quietTimer = null;
    let maxTimer = null;
    let pollTimer = null;
    let lastActivityAt = 0;
    let observer = null;
    let disposed = false;
    let networkSeen = new Map();
    const original = {};

    function currentIdentity() {
      return pageIdentity.identityOf(win.location.href);
    }

    function startVisit(reason) {
      const id = currentIdentity();
      if (!id) return;
      if (current && current.identity === id.identity) return; // same page (query/hash change) — not a new visit
      if (current && onNavigateAway) onNavigateAway(current, reason);
      cancelStabilization();
      const startedMs = clock();
      current = {
        identity: id.identity, origin: id.origin, path: id.path, startedAt: new Date(startedMs).toISOString(), startedMs,
        sequence: ++visitSequence, stabilized: false, stabilizedAt: null, stabilizationMs: null, stabilizedBy: null, reason,
        mutations: { batchCount: 0, mutationCount: 0, largestBatch: 0, lastMutationMs: null, largeBatchesAfterStabilization: 0 },
      };
      networkSeen = new Map();
      if (onVisitStart) { try { onVisitStart(current); } catch { /* observer errors never break tracking */ } }
      scheduleStabilization();
    }

    function scheduleStabilization() {
      const visit = current;
      const check = () => {
        if (disposed || current !== visit || visit.stabilized) return;
        const ready = doc.readyState === 'interactive' || doc.readyState === 'complete';
        const quietFor = clock() - lastActivityAt;
        if (ready && quietFor >= opts.quietMs) finish('quiet');
        else quietTimer = setTimer(check, Math.max(50, opts.quietMs - Math.max(0, quietFor)));
      };
      const finish = how => {
        if (disposed || current !== visit || visit.stabilized) return;
        cancelStabilization();
        const at = clock();
        visit.stabilized = true;
        visit.stabilizedBy = how;
        visit.stabilizedAt = new Date(at).toISOString();
        visit.stabilizationMs = Math.max(0, at - visit.startedMs);
        if (onVisit) onVisit(visit);
      };
      lastActivityAt = clock();
      quietTimer = setTimer(check, opts.quietMs);
      maxTimer = setTimer(() => finish('max-wait'), opts.maxWaitMs);
    }

    function cancelStabilization() {
      if (quietTimer) { clearTimer(quietTimer); quietTimer = null; }
      if (maxTimer) { clearTimer(maxTimer); maxTimer = null; }
    }

    /** DOM mutation batch (records = number of MutationRecords in the callback; defaults to 1). Counts only, never content. */
    function noteMutation(records) {
      const n = typeof records === 'number' && records > 0 ? records : 1;
      lastActivityAt = clock();
      if (!current) return;
      const m = current.mutations;
      m.batchCount++;
      m.mutationCount += n;
      if (n > m.largestBatch) m.largestBatch = n;
      if (current.stabilized) { if (n >= opts.largeBatch) m.largeBatchesAfterStabilization++; }
      else m.lastMutationMs = Math.max(0, clock() - current.startedMs);
    }

    /**
     * Network activity for a (query-stripped) URL. Only NEW or rarely repeated URLs delay stabilization: a URL fetched more than
     * `pollingRepeatThreshold` times within the visit is treated as polling-like and no longer resets the quiet window, so
     * continuous polling cannot hold the page open until the max-wait bound.
     */
    function noteNetwork(url) {
      if (!current || current.stabilized) return false;
      const key = String(url || '').split('?')[0].split('#')[0];
      const count = (networkSeen.get(key) || 0) + 1;
      networkSeen.set(key, count);
      if (count > opts.pollingRepeatThreshold) return false;
      lastActivityAt = clock();
      return true;
    }

    function wrapHistory(method) {
      const history = win.history;
      if (!history || typeof history[method] !== 'function') return;
      original[method] = history[method];
      history[method] = function () {
        const result = original[method].apply(this, arguments);
        try { startVisit(method); } catch { /* never break the host application */ }
        return result;
      };
    }

    function start() {
      wrapHistory('pushState');
      wrapHistory('replaceState');
      win.addEventListener('popstate', () => startVisit('popstate'));
      win.addEventListener('hashchange', () => startVisit('hashchange'));
      if (typeof win.MutationObserver === 'function' && doc.documentElement) {
        observer = new win.MutationObserver(records => noteMutation(records ? records.length : 1));
        observer.observe(doc.documentElement, { childList: true, subtree: true });
      }
      pollTimer = win.setInterval(() => { const id = currentIdentity(); if (id && (!current || current.identity !== id.identity)) startVisit('poll'); }, opts.pollMs);
      startVisit('initial');
    }

    function dispose() {
      disposed = true;
      cancelStabilization();
      if (observer) observer.disconnect();
      if (pollTimer) win.clearInterval(pollTimer);
      for (const method of Object.keys(original)) win.history[method] = original[method];
    }

    return { start, dispose, current: () => current, noteMutation, noteNetwork, options: opts };
  }

  const api = { createTracker, DEFAULTS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { navigation: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
