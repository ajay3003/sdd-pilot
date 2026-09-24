// BirkNext Browser Companion — element picker for Critical E2E flow authoring.
//
// The tester clicks one element on the live, approved page; the companion returns that element's IDENTITY — never its
// HTML, its value or the page's text. Selector candidates are built from the same five strategies replay uses and are
// validated with the replay resolver itself (automation.matches), so a candidate called unique here is one a step will
// resolve to exactly this element. Nothing in pick mode activates the application: every pointer and activation event
// is swallowed until the pick ends, and the only DOM added is one overlay and one banner, both removed on exit.
(function (root) {
  'use strict';

  const automation = root.BirkNextCompanion && root.BirkNextCompanion.automation
    ? root.BirkNextCompanion.automation : (typeof require === 'function' ? require('./automation.js') : null);
  const sanitize = root.BirkNextCompanion && root.BirkNextCompanion.sanitize
    ? root.BirkNextCompanion.sanitize : (typeof require === 'function' ? require('./sanitize.js') : null);
  const pageIdentity = root.BirkNextCompanion && root.BirkNextCompanion.pageIdentity
    ? root.BirkNextCompanion.pageIdentity : (typeof require === 'function' ? require('./page-identity.js') : null);

  /** What a click inside an element means: its nearest control, test-id carrier or heading — not the <span> under the pointer. */
  const TARGET = 'a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=link], [role=checkbox], '
    + '[role=radio], [role=tab], [role=menuitem], [role=switch], [role=combobox], [role=textbox], [contenteditable=true], '
    + '[data-testid], h1, h2, h3, h4, h5, h6, [role=heading], [role=status], output';
  const MARKER = 'data-birknext-picker';
  const DEFAULT_TIMEOUT_MS = 45000;
  const NAME_MAX = 80;

  function ours(el) { return el && el.nodeType === 1 && el.hasAttribute(MARKER); }

  function targetOf(el) {
    if (!el || el.nodeType !== 1 || ours(el)) return null;
    return el.closest(TARGET) || el;
  }

  /** The role the replay resolver will look the element up by: an explicit supported role first, then the native one. */
  function roleOf(el) {
    const explicit = (el.getAttribute('role') || '').trim().toLowerCase();
    if (explicit && automation.ROLE_TAGS[explicit]) return explicit;
    for (const [role, css] of Object.entries(automation.ROLE_TAGS)) {
      try { if (el.matches(css)) return role; } catch { /* unsupported selector in this engine */ }
    }
    return null;
  }

  /** The label a person reads for a form control, by the same three rules the Label resolver applies. */
  function labelOf(doc, el) {
    if (!el.matches('input, select, textarea')) return null;
    const aria = (el.getAttribute('aria-label') || '').trim();
    if (aria) return aria;
    const id = el.getAttribute('id');
    if (id) {
      const byFor = doc.querySelector(`label[for="${CSS.escape(id)}"]`);
      if (byFor && (byFor.textContent || '').trim()) return byFor.textContent.trim();
    }
    const wrapping = el.closest('label');
    return wrapping && (wrapping.textContent || '').trim() ? wrapping.textContent.trim() : null;
  }

  function short(value) {
    if (value == null) return null;
    const collapsed = String(value).replace(/\s+/g, ' ').trim();
    return collapsed ? sanitize.text(collapsed, NAME_MAX) : null;
  }

  /**
   * Ranked candidates, each counted against the live page with the replay resolver. A name or label that does not fit
   * in a selector without truncation is not offered: a truncated value would never match on replay.
   */
  function candidates(doc, win, el) {
    const out = [];
    // A value the sanitizer would redact (an e-mail, a token-shaped or long opaque id) is data, not identity; a selector
    // made of it would carry personal data into a saved flow, so it is not offered at all.
    const clean = value => typeof value === 'string' && sanitize.text(value, 1000) === value;
    const offer = selector => {
      if (![selector.value, selector.name].filter(v => v).every(clean)) return;
      const resolved = automation.matches(doc, win, selector);
      if (resolved.error) return;
      out.push({ selector, matchCount: resolved.pool.length, unique: resolved.pool.length === 1 && resolved.pool[0] === el });
    };
    const testId = el.getAttribute('data-testid');
    if (testId && testId.length <= 160) offer({ kind: 'TestId', value: testId });

    const role = roleOf(el);
    // Exactly the name the resolver compares (trimmed, not collapsed), so the candidate is checked as replay will see it.
    const name = automation.accessibleName(el).trim();
    if (role && name && name.length <= NAME_MAX) offer({ kind: 'Role', value: '', role, name });

    const label = labelOf(doc, el);
    if (label && label.length <= NAME_MAX) offer({ kind: 'Label', value: label });

    if (name && name.length <= NAME_MAX && !el.matches('input, select, textarea')) offer({ kind: 'Text', value: name });

    const css = sanitize.selectorFor(el);
    if (css) offer({ kind: 'Css', value: css });
    return out;
  }

  /**
   * The element's identity for a deterministic step. Values are never read: a picked input reports its type and label,
   * not what is typed in it. Link targets keep origin and path only.
   */
  function describe(doc, win, el) {
    if (!el || !el.isConnected) return { error: 'The selected element is no longer on the page.' };
    const page = pageIdentity.identityOf(win.location.href) || { origin: '', path: '/' };
    const ranked = candidates(doc, win, el);
    const unique = ranked.filter(c => c.unique);
    // CSS is the last resort: recommended only when nothing a person would recognise identifies the element.
    const recommended = (unique.find(c => c.selector.kind !== 'Css') || unique[0] || null);
    const tag = el.tagName.toLowerCase();
    return {
      descriptor: {
        pageOrigin: page.origin,
        pageRoute: page.path,
        tagName: tag,
        role: roleOf(el),
        accessibleName: short(automation.accessibleName(el)),
        label: short(labelOf(doc, el)),
        testId: el.getAttribute('data-testid') ? short(el.getAttribute('data-testid')) : null,
        inputType: tag === 'input' ? (el.getAttribute('type') || 'text').toLowerCase() : null,
        href: tag === 'a' && el.getAttribute('href') ? sanitize.url(el.href) : null,
        visible: automation.visible(el, win),
        enabled: automation.enabled(el),
        candidates: ranked,
        recommended: recommended ? recommended.selector : null,
      },
    };
  }

  function place(box, el) {
    const r = el.getBoundingClientRect();
    box.style.display = 'block';
    box.style.top = `${Math.max(0, r.top - 2)}px`;
    box.style.left = `${Math.max(0, r.left - 2)}px`;
    box.style.width = `${r.width + 4}px`;
    box.style.height = `${r.height + 4}px`;
  }

  /**
   * Enters pick mode and resolves once: { status: 'picked', element } | { status: 'cancelled' } | { status: 'timeout' }.
   * Esc cancels; Enter picks the focused element (the keyboard path); Tab is left alone, so focus is never trapped.
   */
  function pick(doc, win, { timeoutMs = DEFAULT_TIMEOUT_MS, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
    return new Promise(resolve => {
      const box = doc.createElement('div');
      box.setAttribute(MARKER, 'highlight');
      box.setAttribute('aria-hidden', 'true');
      box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;display:none;box-sizing:border-box;'
        + 'border:3px solid #c2410c;outline:2px dashed #ffffff;background:rgba(194,65,12,0.08);';
      const banner = doc.createElement('div');
      banner.setAttribute(MARKER, 'banner');
      banner.setAttribute('role', 'status');
      banner.textContent = 'BirkNext: click an element to select it for the test step. Enter selects the focused element. Esc cancels.';
      banner.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;top:8px;left:50%;transform:translateX(-50%);'
        + 'max-width:90vw;padding:6px 12px;border:2px solid #c2410c;border-radius:6px;background:#fff7ed;color:#431407;'
        + 'font:600 13px/1.4 system-ui,sans-serif;box-shadow:0 2px 8px rgba(0,0,0,0.2);';
      (doc.body || doc.documentElement).append(box, banner);

      let done = false;
      const swallow = e => { e.preventDefault(); e.stopImmediatePropagation(); };
      const onMove = e => { const t = targetOf(e.target); if (t) place(box, t); };
      const onPointer = e => { if (!done) swallow(e); };
      const onClick = e => {
        if (done) return;
        swallow(e);
        const t = targetOf(e.target);
        if (t) finish({ status: 'picked', element: t });
      };
      const onKey = e => {
        if (done) return;
        if (e.key === 'Escape') { swallow(e); finish({ status: 'cancelled' }); return; }
        if (e.key === 'Enter') {
          swallow(e);
          const t = targetOf(doc.activeElement);
          if (t && t !== doc.body && t !== doc.documentElement) finish({ status: 'picked', element: t });
        }
      };
      const onFocus = e => { const t = targetOf(e.target); if (t) place(box, t); };
      const listeners = [
        ['pointermove', onMove], ['mousemove', onMove], ['focusin', onFocus],
        ['pointerdown', onPointer], ['mousedown', onPointer], ['pointerup', onPointer], ['mouseup', onPointer],
        ['touchstart', onPointer], ['touchend', onPointer], ['auxclick', onPointer], ['dblclick', onPointer],
        ['click', onClick], ['keydown', onKey], ['keypress', onPointer], ['keyup', e => { if (!done && (e.key === 'Escape' || e.key === 'Enter')) swallow(e); }],
      ];
      for (const [type, fn] of listeners) win.addEventListener(type, fn, { capture: true, passive: false });
      const timer = setTimer(() => finish({ status: 'timeout' }), Math.min(Math.max(Number(timeoutMs) || DEFAULT_TIMEOUT_MS, 1000), 60000));

      function finish(result) {
        if (done) return;
        done = true;
        clearTimer(timer);
        // Listeners come off after the current event finishes dispatching, so the rest of the picking click is swallowed too.
        setTimer(() => { for (const [type, fn] of listeners) win.removeEventListener(type, fn, { capture: true }); }, 0);
        box.remove();
        banner.remove();
        resolve(result);
      }
    });
  }

  const api = { pick, describe, candidates, roleOf, labelOf, targetOf, MARKER, DEFAULT_TIMEOUT_MS };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { picker: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
