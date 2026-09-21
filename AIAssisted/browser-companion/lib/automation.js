// BirkNext Browser Companion — typed page actions for Critical E2E probes.
//
// This is the same mechanism the companion already uses for WCAG layout probes: an ISOLATED-world content script acting
// on the DOM of a page whose origin the user approved. Nothing here uses CDP, remote debugging or the debugger
// permission, and nothing here evaluates code: BirkNext sends an allow-listed ACTION NAME and a described selector,
// never JavaScript. eval/Function are never reached because no code string ever crosses the boundary.
//
// Actions are deliberately user-like. A disabled control is not clicked, a hidden element is not revealed, an overlay is
// not removed — a probe that cannot be performed the way a person would perform it is a failure to report, not an
// obstacle to work around.
(function (root) {
  'use strict';

  /** Every action BirkNext may ask for. Anything else is rejected before it reaches the page. */
  const ACTIONS = ['click', 'assertVisible', 'assertText', 'assertRoute'];

  /** Selector kinds, in the order a test author should prefer them. */
  const SELECTORS = ['role', 'testid', 'text', 'label', 'css'];

  function visible(el, win) {
    if (!el || el.nodeType !== 1) return false;
    const style = win.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
    if (el.hidden || el.getAttribute('aria-hidden') === 'true') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  /** Disabled means disabled: a probe never clicks a control the application is refusing to offer. */
  function enabled(el) {
    if (el.disabled) return false;
    if (el.getAttribute('aria-disabled') === 'true') return false;
    return !el.closest('[inert]');
  }

  function accessibleName(el) {
    const label = el.getAttribute('aria-label');
    if (label) return label.trim();
    const labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      const parts = labelledBy.split(/\s+/).map(id => el.ownerDocument.getElementById(id)).filter(Boolean);
      if (parts.length) return parts.map(p => (p.textContent || '').trim()).join(' ').trim();
    }
    return (el.textContent || '').trim();
  }

  const ROLE_TAGS = {
    button: 'button, input[type=button], input[type=submit], [role=button]',
    link: 'a[href], [role=link]',
    tab: '[role=tab]',
    menuitem: '[role=menuitem]',
    heading: 'h1, h2, h3, h4, h5, h6, [role=heading]',
    textbox: 'input[type=text], input:not([type]), textarea, [role=textbox]',
  };

  /**
   * Resolves the described selector to at most one element. Ambiguity is an error, never a guess: clicking "whichever
   * matched first" is how an automated flow silently exercises the wrong control.
   */
  function find(doc, win, selector) {
    if (!selector || !SELECTORS.includes(selector.kind)) return { error: `Unsupported selector kind: ${selector && selector.kind}` };
    let candidates = [];
    try {
      switch (selector.kind) {
        case 'role': {
          const css = ROLE_TAGS[String(selector.role || '').toLowerCase()];
          if (!css) return { error: `Unsupported role: ${selector.role}` };
          candidates = Array.from(doc.querySelectorAll(css));
          if (selector.name) {
            const wanted = String(selector.name).trim().toLowerCase();
            candidates = candidates.filter(el => accessibleName(el).toLowerCase() === wanted);
          }
          break;
        }
        case 'testid':
          candidates = Array.from(doc.querySelectorAll(`[data-testid="${CSS.escape(String(selector.value || ''))}"]`));
          break;
        case 'label': {
          const wanted = String(selector.value || '').trim().toLowerCase();
          candidates = Array.from(doc.querySelectorAll('input, select, textarea')).filter(el => {
            const byAria = (el.getAttribute('aria-label') || '').trim().toLowerCase();
            if (byAria === wanted) return true;
            const id = el.getAttribute('id');
            if (!id) return false;
            const lbl = doc.querySelector(`label[for="${CSS.escape(id)}"]`);
            return lbl ? (lbl.textContent || '').trim().toLowerCase() === wanted : false;
          });
          break;
        }
        case 'text': {
          const wanted = String(selector.value || '').trim().toLowerCase();
          candidates = Array.from(doc.querySelectorAll('a, button, [role=button], [role=link], [role=tab], [role=menuitem]'))
            .filter(el => accessibleName(el).toLowerCase() === wanted);
          break;
        }
        case 'css':
          candidates = Array.from(doc.querySelectorAll(String(selector.value || '')));
          break;
      }
    } catch (e) {
      return { error: `Selector could not be evaluated: ${e && e.message}` };
    }

    const shown = candidates.filter(el => visible(el, win));
    const pool = shown.length > 0 ? shown : candidates;
    if (pool.length === 0) return { error: 'No element matched the selector.' };
    if (pool.length > 1) return { error: `Selector matched ${pool.length} elements; it must identify exactly one.` };
    return { element: pool[0] };
  }

  /** A one-line description of what was acted on. Never element text beyond a short accessible name. */
  function describe(el) {
    const name = accessibleName(el);
    return `${el.tagName.toLowerCase()}${name ? ` "${name.slice(0, 60)}"` : ''}`;
  }

  /**
   * Performs one allow-listed action. Returns { status, summary, error }, never throws into the page.
   * status: 'passed' | 'failed'  — a prerequisite problem is the caller's to classify as blocked.
   */
  function perform(doc, win, command) {
    const action = command && command.action;
    if (!ACTIONS.includes(action)) return { status: 'failed', error: `Unsupported action: ${action}` };

    if (action === 'assertRoute') {
      const expected = String(command.expected || '');
      const actual = win.location.pathname;
      return actual === expected
        ? { status: 'passed', summary: `Route is ${actual}` }
        : { status: 'failed', error: `Expected route ${expected}, observed ${actual}`, summary: `Route is ${actual}` };
    }

    const found = find(doc, win, command.selector);
    if (found.error) return { status: 'failed', error: found.error };
    const el = found.element;

    switch (action) {
      case 'assertVisible':
        return visible(el, win)
          ? { status: 'passed', summary: `${describe(el)} is visible` }
          : { status: 'failed', error: `${describe(el)} is not visible` };

      case 'assertText': {
        const expected = String(command.expected || '').trim();
        const actual = accessibleName(el);
        return actual.includes(expected)
          ? { status: 'passed', summary: `${describe(el)} contains the expected text` }
          : { status: 'failed', error: `Expected text "${expected}" not found`, summary: `Observed "${actual.slice(0, 80)}"` };
      }

      case 'click': {
        if (!visible(el, win)) return { status: 'failed', error: `${describe(el)} is not visible; a probe does not click what a user cannot see.` };
        if (!enabled(el)) return { status: 'failed', error: `${describe(el)} is disabled; a probe does not click a control the application has disabled.` };
        try {
          el.click();   // the element's own activation behaviour, exactly as a user click produces
          return { status: 'passed', summary: `Clicked ${describe(el)}` };
        } catch (e) {
          return { status: 'failed', error: `Click was not accepted: ${e && e.message}` };
        }
      }
    }
    return { status: 'failed', error: `Unsupported action: ${action}` };
  }

  /**
   * Waits for a condition rather than sleeping: an SPA renders when it renders, and a fixed delay is either a flake or
   * wasted time. Polls the same predicate until it holds or the bounded timeout expires.
   */
  function waitFor(predicate, { timeoutMs = 8000, intervalMs = 100, now = () => Date.now(), setTimer = setTimeout } = {}) {
    const deadline = now() + timeoutMs;
    let attempts = 0;
    return new Promise(resolve => {
      const tick = () => {
        attempts++;
        let held = false;
        try { held = Boolean(predicate()); } catch { held = false; }
        if (held) return resolve({ ok: true, attempts });
        if (now() >= deadline) return resolve({ ok: false, attempts, timedOut: true });
        setTimer(tick, intervalMs);
      };
      tick();
    });
  }

  const api = { ACTIONS, SELECTORS, find, perform, waitFor, visible, enabled, accessibleName, describe };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { automation: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
