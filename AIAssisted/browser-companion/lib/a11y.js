// BirkNext Accessibility Checks — a conservative, deterministic native rule set (Phase 1).
// This is NOT full WCAG conformance testing and NOT an axe replacement. Every rule reports only structural evidence
// (rule id, count, sanitized selectors) — never element text, attribute values or user data.
(function (root) {
  'use strict';

  const sanitize = root.BirkNextCompanion && root.BirkNextCompanion.sanitize
    ? root.BirkNextCompanion.sanitize
    : (typeof require === 'function' ? require('./sanitize.js') : null);
  const dom = root.BirkNextCompanion && root.BirkNextCompanion.dom
    ? root.BirkNextCompanion.dom
    : (typeof require === 'function' ? require('./dom.js') : null);

  const MAX_SELECTORS_PER_RULE = 5;
  const MAX_FINDINGS_PER_RULE = 200;

  // Rule catalogue: severity and WCAG mapping are fixed per rule (no per-run inflation).
  const RULES = {
    'a11y-document-lang': { severity: 'Medium', wcag: '3.1.1', title: 'Document language missing', guidance: 'Set lang on the <html> element (for example lang="nb").' },
    'a11y-page-title': { severity: 'Medium', wcag: '2.4.2', title: 'Page title missing or empty', guidance: 'Provide a descriptive <title> for every page/route.' },
    'a11y-image-alt': { severity: 'High', wcag: '1.1.1', title: 'Image without accessible alternative', guidance: 'Add alt text, or alt="" / role="presentation" for decorative images.' },
    'a11y-button-name': { severity: 'High', wcag: '4.1.2', title: 'Button without accessible name', guidance: 'Give the button visible text, aria-label or aria-labelledby.' },
    'a11y-link-name': { severity: 'High', wcag: '2.4.4', title: 'Link without accessible name', guidance: 'Give the link text content, aria-label or an image with alt text.' },
    'a11y-control-label': { severity: 'High', wcag: '1.3.1', title: 'Form control without associated label', guidance: 'Associate a <label for>, wrap in <label>, or use aria-label/aria-labelledby.' },
    'a11y-aria-reference': { severity: 'Medium', wcag: '4.1.2', title: 'ARIA reference to a missing id', guidance: 'aria-labelledby / aria-describedby / aria-controls must reference existing element ids.' },
    'a11y-duplicate-id': { severity: 'Medium', wcag: '4.1.1', title: 'Duplicate element id', guidance: 'Ids must be unique; duplicates break label and ARIA associations.' },
    'a11y-heading-order': { severity: 'Low', wcag: '1.3.1', title: 'Heading level skipped', guidance: 'Do not skip heading levels (for example h2 directly to h4).' },
    'a11y-heading-h1': { severity: 'Low', wcag: '1.3.1', title: 'No h1 heading on the page', guidance: 'Provide one h1 that names the page content.' },
    'a11y-main-landmark': { severity: 'Low', wcag: '1.3.1', title: 'Main landmark missing', guidance: 'Wrap the primary content in <main> or role="main".' },
    'a11y-dialog-name': { severity: 'Medium', wcag: '4.1.2', title: 'Dialog without accessible name', guidance: 'Give dialogs aria-label or aria-labelledby pointing to the dialog title.' },
    'a11y-positive-tabindex': { severity: 'Low', wcag: '2.4.3', title: 'Positive tabindex used', guidance: 'Avoid tabindex > 0; rely on DOM order for focus order.' },
    'a11y-hidden-focusable': { severity: 'Low', wcag: '2.4.3', title: 'Focusable element inside hidden content', guidance: 'Remove hidden controls from the tab order (tabindex="-1" or disabled) while they are not shown.' },
  };

  function hasText(value) { return typeof value === 'string' && value.trim().length > 0; }

  function isPresentational(el) {
    const role = el.getAttribute('role');
    return role === 'presentation' || role === 'none' || el.getAttribute('aria-hidden') === 'true';
  }

  function referencedText(doc, idList) {
    if (!hasText(idList)) return false;
    return idList.trim().split(/\s+/).some(id => { const t = doc.getElementById(id); return t && hasText(t.textContent); });
  }

  /** Accessible-name approximation: aria-label, aria-labelledby, text content, title, img alt, value for inputs. */
  function hasAccessibleName(doc, el) {
    if (hasText(el.getAttribute('aria-label'))) return true;
    if (referencedText(doc, el.getAttribute('aria-labelledby'))) return true;
    if (hasText(el.textContent)) return true;
    if (hasText(el.getAttribute('title'))) return true;
    for (const img of el.querySelectorAll('img, svg')) {
      if (hasText(img.getAttribute('alt')) || hasText(img.getAttribute('aria-label')) || hasText(img.getAttribute('title'))) return true;
      if (img.tagName.toLowerCase() === 'svg' && img.querySelector('title') && hasText(img.querySelector('title').textContent)) return true;
    }
    if (el.tagName.toLowerCase() === 'input') {
      const type = (el.getAttribute('type') || '').toLowerCase();
      if ((type === 'submit' || type === 'reset' || type === 'button') && hasText(el.getAttribute('value'))) return true;
      if (type === 'image' && hasText(el.getAttribute('alt'))) return true;
    }
    return false;
  }

  function hasLabel(doc, control) {
    if (hasText(control.getAttribute('aria-label'))) return true;
    if (referencedText(doc, control.getAttribute('aria-labelledby'))) return true;
    if (hasText(control.getAttribute('title'))) return true;
    const id = control.getAttribute('id');
    if (id) {
      for (const label of doc.querySelectorAll('label[for]')) if (label.getAttribute('for') === id && hasText(label.textContent)) return true;
    }
    let parent = control.parentElement;
    while (parent) { if (parent.tagName.toLowerCase() === 'label' && hasText(parent.textContent)) return true; parent = parent.parentElement; }
    return false;
  }

  function collector() {
    const byRule = new Map();
    return {
      add(ruleId, element) {
        let f = byRule.get(ruleId);
        if (!f) { f = { ruleId, count: 0, selectors: [] }; byRule.set(ruleId, f); }
        if (f.count >= MAX_FINDINGS_PER_RULE) return;
        f.count++;
        if (element && f.selectors.length < MAX_SELECTORS_PER_RULE && sanitize) {
          const s = sanitize.selectorFor(element);
          if (s && !f.selectors.includes(s)) f.selectors.push(s);
        }
      },
      results() {
        return Array.from(byRule.values()).map(f => ({ ...f, ...describe(f.ruleId) }));
      },
    };
  }

  function describe(ruleId) {
    const r = RULES[ruleId] || { severity: 'Low', wcag: null, title: ruleId, guidance: '' };
    return { severity: r.severity, wcag: r.wcag, title: r.title, guidance: r.guidance };
  }

  /** Evaluates all Phase 1 rules against a Document. `win` is optional (hidden-element detection). */
  function evaluate(doc, win) {
    const out = collector();
    const html = doc.documentElement;

    if (!html || !hasText(html.getAttribute('lang'))) out.add('a11y-document-lang', html);
    if (!hasText(doc.title)) out.add('a11y-page-title', null);

    for (const img of doc.querySelectorAll('img')) {
      if (isPresentational(img)) continue;
      if (!img.hasAttribute('alt') && !hasText(img.getAttribute('aria-label')) && !referencedText(doc, img.getAttribute('aria-labelledby'))) out.add('a11y-image-alt', img);
    }

    for (const btn of doc.querySelectorAll('button, [role=button], input[type=button], input[type=submit], input[type=reset], input[type=image]')) {
      if (isPresentational(btn)) continue;
      if (!hasAccessibleName(doc, btn)) out.add('a11y-button-name', btn);
    }

    for (const link of doc.querySelectorAll('a[href], [role=link]')) {
      if (isPresentational(link)) continue;
      if (!hasAccessibleName(doc, link)) out.add('a11y-link-name', link);
    }

    for (const control of doc.querySelectorAll('input:not([type=hidden]):not([type=button]):not([type=submit]):not([type=reset]):not([type=image]), select, textarea')) {
      if (isPresentational(control)) continue;
      if (!hasLabel(doc, control)) out.add('a11y-control-label', control);
    }

    for (const el of doc.querySelectorAll('[aria-labelledby], [aria-describedby], [aria-controls]')) {
      for (const attr of ['aria-labelledby', 'aria-describedby', 'aria-controls']) {
        const ids = el.getAttribute(attr);
        if (!hasText(ids)) continue;
        if (ids.trim().split(/\s+/).some(id => !doc.getElementById(id))) { out.add('a11y-aria-reference', el); break; }
      }
    }

    if (dom) for (const d of dom.duplicateIds(doc)) { for (let i = 0; i < d.count; i++) out.add('a11y-duplicate-id', doc.getElementById(d.id)); }

    const headings = dom ? dom.headingSummary(doc) : { counts: {}, order: [] };
    let previous = 0;
    for (const level of headings.order) {
      if (previous > 0 && level > previous + 1) out.add('a11y-heading-order', null);
      previous = level;
    }
    if (headings.order.length > 0 && !headings.order.includes(1)) out.add('a11y-heading-h1', null);

    if (doc.body && doc.querySelectorAll('main, [role=main]').length === 0) out.add('a11y-main-landmark', doc.body);

    for (const dialog of doc.querySelectorAll('dialog, [role=dialog], [role=alertdialog]')) {
      if (!hasText(dialog.getAttribute('aria-label')) && !referencedText(doc, dialog.getAttribute('aria-labelledby'))) out.add('a11y-dialog-name', dialog);
    }

    for (const el of doc.querySelectorAll('[tabindex]')) {
      if (parseInt(el.getAttribute('tabindex'), 10) > 0) out.add('a11y-positive-tabindex', el);
    }

    if (dom) {
      for (const el of doc.querySelectorAll(dom.INTERACTIVE + ', [tabindex]')) {
        const tabindex = el.getAttribute('tabindex');
        if (tabindex !== null && parseInt(tabindex, 10) < 0) continue;
        // Only count elements hidden through an ancestor/explicit hidden state that we can detect reliably.
        if (dom.isHidden(el, win)) out.add('a11y-hidden-focusable', el);
      }
    }

    return { engine: 'BirkNext Accessibility Checks', rulesEvaluated: Object.keys(RULES).length, findings: out.results() };
  }

  const api = { RULES, evaluate, hasAccessibleName, hasLabel, describe, MAX_SELECTORS_PER_RULE, MAX_FINDINGS_PER_RULE };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { a11y: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
