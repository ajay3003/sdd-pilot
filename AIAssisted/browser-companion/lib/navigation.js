// BirkNext Browser Companion — SPA navigation tracker + page stabilization model.
// Detects route changes through history.pushState/replaceState (wrapped), popstate/hashchange, and URL polling as a fallback
// (no continuous DOM scanning). Each distinct page identity starts a new "visit" with its own stabilization window:
//   navigation detected → DOM ready → quiet period (no URL change / no burst of mutations) → initial snapshot → passive updates.
// Pure enough to be unit-tested with fake window/history/document objects.
(function (root) {
  'use strict';

  const pageIdentity = root.BirkNextCompanion && root.BirkNextCompanion.pageIdentity
    ? root.BirkNextCompanion.pageIdentity
    : (typeof require === 'function' ? require('./page-identity.js') : null);

  const DEFAULTS = { quietMs: 800, maxWaitMs: 4000, pollMs: 1000, updateEveryMs: 4000, maxUpdates: 6 };

  /**
   * createTracker({ win, doc, onVisit(visit), onNavigateAway(visit), options })
   * visit = { identity, origin, path, startedAt (ISO), sequence, stabilized: false }
   */
  function createTracker({ win, doc, onVisit, onNavigateAway, options, now } = {}) {
    const opts = Object.assign({}, DEFAULTS, options || {});
    const clock = now || (() => Date.now());
    const setTimer = (fn, ms) => win.setTimeout(fn, ms);
    const clearTimer = id => win.clearTimeout(id);
    let current = null;
    let visitSequence = 0;
    let quietTimer = null;
    let maxTimer = null;
    let pollTimer = null;
    let lastMutationAt = 0;
    let observer = null;
    let disposed = false;
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
      current = { identity: id.identity, origin: id.origin, path: id.path, startedAt: new Date(clock()).toISOString(), sequence: ++visitSequence, stabilized: false, reason };
      scheduleStabilization();
    }

    function scheduleStabilization() {
      const visit = current;
      const check = () => {
        if (disposed || current !== visit || visit.stabilized) return;
        const ready = doc.readyState === 'interactive' || doc.readyState === 'complete';
        const quietFor = clock() - lastMutationAt;
        if (ready && quietFor >= opts.quietMs) finish('quiet');
        else quietTimer = setTimer(check, Math.max(50, opts.quietMs - Math.max(0, quietFor)));
      };
      const finish = how => {
        if (disposed || current !== visit || visit.stabilized) return;
        cancelStabilization();
        visit.stabilized = true;
        visit.stabilizedBy = how;
        if (onVisit) onVisit(visit);
      };
      lastMutationAt = clock();
      quietTimer = setTimer(check, opts.quietMs);
      maxTimer = setTimer(() => finish('max-wait'), opts.maxWaitMs);
    }

    function cancelStabilization() {
      if (quietTimer) { clearTimer(quietTimer); quietTimer = null; }
      if (maxTimer) { clearTimer(maxTimer); maxTimer = null; }
    }

    function noteMutation() { lastMutationAt = clock(); }

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
        observer = new win.MutationObserver(noteMutation);
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

    return { start, dispose, current: () => current, noteMutation, options: opts };
  }

  const api = { createTracker, DEFAULTS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { navigation: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
