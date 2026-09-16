// Bounded observation of the tester's trusted Tab/Shift+Tab events. Synthetic events cannot prove browser traversal.
// This engine never sends a key, focuses a control, clicks or submits. Possible traps/indicators require human confirmation.
(function (root) {
  'use strict';
  const MAX_STEPS = 100;
  function observe(doc, win, options, completed) {
    const C = root.BirkNextCompanion;
    if (!C.wcagInteraction.allowed(options.environment, options.approved)) return { stop() { return []; } };
    const sequence = [], candidates = [];
    let pending = false, stopped = false, repeated = 0, last = null, timer;
    function end() {
      if (stopped) return [];
      stopped = true;
      clearTimeout(timer);
      doc.removeEventListener('keydown', key, true);
      doc.removeEventListener('focusin', focus, true);
      const result = [
        { checkId: 'keyboard-traversal', outcome: sequence.length ? 'ManualReviewRequired' : 'NotTested', tested: sequence.length,
          failed: 0, uncertain: repeated, selectors: sequence.slice(0, 5) },
        { checkId: 'focus-indicator', outcome: sequence.length ? 'ManualReviewRequired' : 'NotTested', tested: sequence.length,
          failed: 0, uncertain: candidates.length, selectors: candidates.slice(0, 5) },
      ];
      completed(result);
      return result;
    }
    function capture() {
      if (!pending || stopped) return;
      pending = false;
      const el = doc.activeElement;
      if (!el || el === doc.body || el === doc.documentElement) return;
      const selector = C.wcag.selectorFor(el);
      if (selector) sequence.push(selector);
      // Valid modal traps are not reported as failures. Repetition elsewhere is only a candidate.
      if (el === last && !el.closest('dialog[open], [role=dialog][aria-modal=true]')) repeated++;
      last = el;
      const style = win.getComputedStyle(el);
      const outline = style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
      if (!outline && style.boxShadow === 'none' && selector) candidates.push(selector);
      if (sequence.length >= MAX_STEPS) end();
    }
    function key(event) {
      if (!event.isTrusted || event.key !== 'Tab') return;
      pending = true;
      // Traps that prevent a focus event still need an observation after the browser handles Tab.
      win.requestAnimationFrame(capture);
    }
    function focus() { capture(); }
    doc.addEventListener('keydown', key, true);
    doc.addEventListener('focusin', focus, true);
    timer = setTimeout(end, Math.min(options.durationMs || 30000, 30000));
    return { stop: end };
  }
  const api = { observe, MAX_STEPS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { wcagKeyboard: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
