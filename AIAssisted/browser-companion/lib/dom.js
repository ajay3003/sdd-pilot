// BirkNext Browser Companion — DOM structural summary. Counts and structure only: no HTML, no text content,
// no attribute values other than structural ones (tag, role, id/class tokens that pass the sanitizer).
(function (root) {
  'use strict';

  const INTERACTIVE = 'a[href], button, input:not([type=hidden]), select, textarea, [role=button], [role=link], [role=checkbox], [role=radio], [role=tab], [role=menuitem], [role=switch], [contenteditable=true]';
  const FORM_CONTROLS = 'input:not([type=hidden]), select, textarea';
  const LANDMARK_ROLES = ['banner', 'navigation', 'main', 'contentinfo', 'complementary', 'search', 'form', 'region'];

  function maxDepth(rootElement) {
    let max = 0;
    const stack = [[rootElement, 1]];
    while (stack.length) {
      const [node, depth] = stack.pop();
      if (depth > max) max = depth;
      const children = node.children;
      for (let i = 0; i < children.length; i++) stack.push([children[i], depth + 1]);
    }
    return max;
  }

  function landmarkSummary(doc) {
    const result = {};
    for (const role of LANDMARK_ROLES) result[role] = doc.querySelectorAll(`[role=${role}]`).length;
    result.main += doc.querySelectorAll('main:not([role])').length;
    result.navigation += doc.querySelectorAll('nav:not([role])').length;
    result.banner += doc.querySelectorAll('body > header:not([role])').length;
    result.contentinfo += doc.querySelectorAll('body > footer:not([role])').length;
    result.complementary += doc.querySelectorAll('aside:not([role])').length;
    result.form += doc.querySelectorAll('form:not([role])').length;
    return result;
  }

  function headingSummary(doc) {
    const counts = { h1: 0, h2: 0, h3: 0, h4: 0, h5: 0, h6: 0 };
    const order = [];
    for (const h of doc.querySelectorAll('h1, h2, h3, h4, h5, h6, [role=heading][aria-level]')) {
      const level = h.hasAttribute('aria-level') ? parseInt(h.getAttribute('aria-level'), 10) : parseInt(h.tagName.slice(1), 10);
      if (level >= 1 && level <= 6) { counts['h' + level]++; order.push(level); }
    }
    return { counts, order: order.slice(0, 200) };
  }

  function duplicateIds(doc) {
    const seen = new Map();
    for (const el of doc.querySelectorAll('[id]')) {
      const id = el.getAttribute('id');
      if (!id) continue;
      seen.set(id, (seen.get(id) || 0) + 1);
    }
    return Array.from(seen.entries()).filter(([, c]) => c > 1).map(([id, count]) => ({ id, count }));
  }

  function isHidden(el, win) {
    if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
    if (!win || typeof win.getComputedStyle !== 'function') return false;
    const style = win.getComputedStyle(el);
    return style.display === 'none' || style.visibility === 'hidden';
  }

  function hiddenFocusable(doc, win) {
    let count = 0;
    for (const el of doc.querySelectorAll(INTERACTIVE + ', [tabindex]')) {
      const tabindex = el.getAttribute('tabindex');
      if (tabindex !== null && parseInt(tabindex, 10) < 0) continue;
      if (isHidden(el, win)) count++;
    }
    return count;
  }

  /** Full structural summary. `win` is optional (computed styles for hidden detection). */
  function summarize(doc, win) {
    const body = doc.body || doc.documentElement;
    const headings = headingSummary(doc);
    const dups = duplicateIds(doc);
    return {
      nodeCount: doc.getElementsByTagName('*').length,
      maxDepth: body ? maxDepth(body) : 0,
      interactiveCount: doc.querySelectorAll(INTERACTIVE).length,
      formControlCount: doc.querySelectorAll(FORM_CONTROLS).length,
      iframeCount: doc.querySelectorAll('iframe').length,
      imageCount: doc.querySelectorAll('img').length,
      headingCounts: headings.counts,
      headingOrder: headings.order,
      landmarks: landmarkSummary(doc),
      duplicateIdCount: dups.length,
      hiddenFocusableCount: hiddenFocusable(doc, win),
      dialogCount: doc.querySelectorAll('dialog, [role=dialog], [role=alertdialog]').length,
      positiveTabIndexCount: Array.from(doc.querySelectorAll('[tabindex]')).filter(el => parseInt(el.getAttribute('tabindex'), 10) > 0).length,
    };
  }

  const api = { summarize, maxDepth, landmarkSummary, headingSummary, duplicateIds, hiddenFocusable, isHidden, INTERACTIVE, FORM_CONTROLS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { dom: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
