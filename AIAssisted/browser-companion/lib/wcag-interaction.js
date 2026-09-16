// Explicit, bounded layout probes only. Never click, submit, dispatch keys or change field values.
(function (root) {
  'use strict';
  const MAX_ELEMENTS = 1500;
  const ALLOWED = new Set(['Local', 'Development', 'QA', 'Test', 'RC']);
  function allowed(environment, approved) { return approved === true && ALLOWED.has(environment); }
  function metrics(el, win) {
    const s = win.getComputedStyle(el), r = el.getBoundingClientRect();
    return { visible: r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none',
      clipped: (s.overflowX === 'hidden' || s.overflowX === 'clip') && el.scrollWidth > el.clientWidth + 1 ||
        (s.overflowY === 'hidden' || s.overflowY === 'clip') && el.scrollHeight > el.clientHeight + 1 };
  }
  async function layout(doc, win, options) {
    const ids = ['text-spacing', 'resize-text'];
    const empty = () => ids.map(checkId => ({ checkId, outcome: 'NotTested', tested: 0, failed: 0, uncertain: 0, selectors: [] }));
    if (!allowed(options.environment, options.approved) || doc.visibilityState === 'hidden') return empty();
    const nodes = Array.from(doc.querySelectorAll('body *')).slice(0, MAX_ELEMENTS);
    const total = doc.querySelectorAll('body *').length;
    const base = nodes.map(el => metrics(el, win));
    const sanitize = root.BirkNextCompanion.sanitize;
    const result = [];
    const scroll = [win.scrollX, win.scrollY];
    for (const checkId of ids) {
      let sheet;
      const originals = [];
      try {
        if (checkId === 'text-spacing') {
          sheet = doc.createElement('style');
          sheet.textContent = '* { line-height:1.5 !important; letter-spacing:.12em !important; word-spacing:.16em !important; } p { margin-bottom:2em !important; }';
          doc.head.appendChild(sheet);
        } else {
          const sizes = nodes.map(el => parseFloat(win.getComputedStyle(el).fontSize));
          nodes.forEach((el, i) => {
            if (!Number.isFinite(sizes[i])) return;
            originals.push([el, el.style.getPropertyValue('font-size'), el.style.getPropertyPriority('font-size')]);
            el.style.setProperty('font-size', `${sizes[i] * 2}px`, 'important');
          });
        }
        // Reading layout synchronously makes cleanup deterministic even if the tab is backgrounded.
        const suspicious = nodes.filter((el, i) => {
          const after = metrics(el, win);
          return base[i].visible && ((!base[i].clipped && after.clipped) || !after.visible);
        });
        result.push({ checkId, outcome: suspicious.length || total > MAX_ELEMENTS ? 'ManualReviewRequired' : 'Pass',
          tested: nodes.length, failed: 0, uncertain: suspicious.length + (total > MAX_ELEMENTS ? 1 : 0),
          selectors: suspicious.slice(0, 5).map(sanitize.selectorFor).filter(Boolean) });
      } catch {
        result.push({ checkId, outcome: 'NotTested', tested: 0, failed: 0, uncertain: 1, selectors: [] });
      } finally {
        sheet?.remove();
        for (const [el, value, priority] of originals) {
          if (value) el.style.setProperty('font-size', value, priority); else el.style.removeProperty('font-size');
        }
        win.scrollTo(...scroll);
      }
    }
    return result;
  }
  const api = { layout, allowed, MAX_ELEMENTS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { wcagInteraction: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
