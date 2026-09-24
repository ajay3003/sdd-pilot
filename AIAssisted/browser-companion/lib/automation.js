// BirkNext Browser Companion — typed page actions for Critical E2E regression.
//
// This is the same mechanism the companion already uses for WCAG layout probes: an ISOLATED-world content script acting
// on the DOM of a page whose origin the user approved. Nothing here uses CDP, remote debugging or the debugger
// permission, and nothing here evaluates code: BirkNext sends an allow-listed ACTION NAME and a described selector,
// never JavaScript. eval/Function are never reached because no code string ever crosses the boundary.
//
// Actions are deliberately user-like. A disabled control is not clicked, a hidden element is not revealed, an overlay is
// not removed, a read-only field is not written — a step that cannot be performed the way a person would perform it is
// a failure to report, not an obstacle to work around.
//
// Action and selector names are the shared contract's names (CompanionActionKind / CompanionSelectorKind), so a command
// travels from C# to the page without a translation layer that could drift.
(function (root) {
  'use strict';

  /** Every action BirkNext may ask for. Anything else is rejected before it reaches the page. */
  const ACTIONS = ['Navigate', 'Click', 'Fill', 'Select', 'WaitForVisible', 'WaitForText', 'WaitForRoute',
    'AssertVisible', 'AssertHidden', 'AssertText', 'AssertValue', 'AssertRoute', 'ReadValue'];

  /** Actions that change the page. Everything else only observes. */
  const MUTATING = ['Navigate', 'Click', 'Fill', 'Select'];

  /** Selector kinds, in the order a flow author should prefer them. */
  const SELECTORS = ['TestId', 'Role', 'Label', 'Text', 'Css'];

  function visible(el, win) {
    if (!el || el.nodeType !== 1) return false;
    const style = win.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
    if (el.hidden || el.getAttribute('aria-hidden') === 'true') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  /** Disabled means disabled: a step never operates a control the application is refusing to offer. */
  function enabled(el) {
    if (el.disabled) return false;
    if (el.getAttribute('aria-disabled') === 'true') return false;
    return !el.closest('[inert]');
  }

  function writable(el) {
    if (!enabled(el)) return false;
    return !el.readOnly && el.getAttribute('aria-readonly') !== 'true';
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
    combobox: 'select, [role=combobox]',
    checkbox: 'input[type=checkbox], [role=checkbox]',
    row: 'tr, [role=row]',
    cell: 'td, th, [role=cell], [role=gridcell]',
    status: '[role=status], output',
  };

  /**
   * Resolves the described selector to at most one element. Ambiguity is an error, never a guess: acting on "whichever
   * matched first" is how an automated flow silently exercises the wrong control and still reports green.
   */
  function find(doc, win, selector) {
    const resolved = matches(doc, win, selector);
    if (resolved.error) return resolved;
    const pool = resolved.pool;
    if (pool.length === 0) return { error: 'No element matched the selector.' };
    if (pool.length > 1) return { error: `Selector matched ${pool.length} elements; it must identify exactly one.` };
    return { element: pool[0] };
  }

  /**
   * Every element the selector matches, under the same rule replay uses: visible matches when there are any, otherwise
   * all of them. The element picker counts uniqueness with this, so a candidate it calls unique is one `find` resolves.
   */
  function matches(doc, win, selector) {
    if (!selector || !SELECTORS.includes(selector.kind)) return { error: `Unsupported selector kind: ${selector && selector.kind}` };
    let candidates = [];
    try {
      switch (selector.kind) {
        case 'Role': {
          const css = ROLE_TAGS[String(selector.role || '').toLowerCase()];
          if (!css) return { error: `Unsupported role: ${selector.role}` };
          candidates = Array.from(doc.querySelectorAll(css));
          if (selector.name) {
            const wanted = String(selector.name).trim().toLowerCase();
            candidates = candidates.filter(el => accessibleName(el).toLowerCase() === wanted);
          }
          break;
        }
        case 'TestId':
          candidates = Array.from(doc.querySelectorAll(`[data-testid="${CSS.escape(String(selector.value || ''))}"]`));
          break;
        case 'Label': {
          const wanted = String(selector.value || '').trim().toLowerCase();
          candidates = Array.from(doc.querySelectorAll('input, select, textarea')).filter(el => {
            const byAria = (el.getAttribute('aria-label') || '').trim().toLowerCase();
            if (byAria === wanted) return true;
            const wrapping = el.closest('label');
            if (wrapping && (wrapping.textContent || '').trim().toLowerCase() === wanted) return true;
            const id = el.getAttribute('id');
            if (!id) return false;
            const lbl = doc.querySelector(`label[for="${CSS.escape(id)}"]`);
            return lbl ? (lbl.textContent || '').trim().toLowerCase() === wanted : false;
          });
          break;
        }
        case 'Text': {
          const wanted = String(selector.value || '').trim().toLowerCase();
          candidates = Array.from(doc.querySelectorAll('a, button, [role=button], [role=link], [role=tab], [role=menuitem], h1, h2, h3, [role=heading], [role=status]'))
            .filter(el => accessibleName(el).toLowerCase() === wanted);
          break;
        }
        case 'Css':
          candidates = Array.from(doc.querySelectorAll(String(selector.value || '')));
          break;
      }
    } catch (e) {
      return { error: `Selector could not be evaluated: ${e && e.message}` };
    }

    const shown = candidates.filter(el => visible(el, win));
    return { pool: shown.length > 0 ? shown : candidates };
  }

  /** A one-line description of what was acted on. Never element text beyond a short accessible name. */
  function describe(el) {
    const name = accessibleName(el);
    return `${el.tagName.toLowerCase()}${name ? ` "${name.slice(0, 60)}"` : ''}`;
  }

  /** The value a control currently holds, for ReadValue and AssertValue. */
  function valueOf(el) {
    if ('value' in el) return String(el.value ?? '');
    return accessibleName(el);
  }

  /**
   * Sets a control's value the way a user's typing reaches a component framework.
   *
   * Assigning `el.value` alone is invisible to Blazor: its change detection listens for input/change events, and
   * React-style frameworks additionally track the value through the prototype's setter. So the value goes in through the
   * native prototype setter (which keeps any framework value-tracker in step) and is then announced with bubbling input
   * and change events. A field that merely *looks* right in the DOM while the model still holds the old value is the
   * single most dangerous false pass in UI automation, which is why the caller verifies the value afterwards rather
   * than trusting that this returned.
   */
  function setValue(el, win, value) {
    el.focus();
    const proto = el instanceof win.HTMLTextAreaElement ? win.HTMLTextAreaElement.prototype
      : el instanceof win.HTMLSelectElement ? win.HTMLSelectElement.prototype
        : win.HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value');
    if (setter && setter.set) setter.set.call(el, value); else el.value = value;
    el.dispatchEvent(new win.Event('input', { bubbles: true }));
    el.dispatchEvent(new win.Event('change', { bubbles: true }));
  }

  function textMatches(actual, expected, match) {
    const a = String(actual || '').trim(), e = String(expected || '').trim();
    return match === 'Contains' ? a.includes(e) : a === e;
  }

  /**
   * Performs one allow-listed action. Returns { status, summary, error, observedValue, assertionResult } and never
   * throws into the page. `status` is 'passed' or 'failed'; a missing prerequisite is the caller's to report as blocked.
   */
  function perform(doc, win, command) {
    const action = command && command.action;
    if (!ACTIONS.includes(action)) return { status: 'failed', error: `Unsupported action: ${action}` };
    const match = command.match === 'Contains' ? 'Contains' : 'Equals';

    // Route actions look at the page, not at an element.
    if (action === 'AssertRoute' || action === 'WaitForRoute') {
      const expected = String(command.expected ?? command.value ?? '');
      const actual = win.location.pathname;
      return actual === expected
        ? { status: 'passed', assertionResult: true, summary: `Route is ${actual}` }
        : { status: 'failed', assertionResult: false, error: `Expected route ${expected}, observed ${actual}`, summary: `Route is ${actual}` };
    }

    // Navigate uses the application's own navigation control. Same-origin location changes are allowed only when the
    // flow explicitly asks for a route, and never to another origin — a command must not be able to send the
    // authenticated browser somewhere the session never approved.
    if (action === 'Navigate') {
      if (command.selector) {
        const found = find(doc, win, command.selector);
        if (found.error) return { status: 'failed', error: found.error };
        return clickElement(found.element, win);
      }
      const route = String(command.value || '');
      if (!route.startsWith('/') || route.startsWith('//')) return { status: 'failed', error: 'Navigate takes an application navigation control, or a same-origin path beginning with "/".' };
      win.location.assign(new win.URL(route, win.location.origin).toString());
      return { status: 'passed', summary: `Navigating to ${route}` };
    }

    const found = find(doc, win, command.selector);
    if (action === 'AssertHidden') {
      // "Not there" and "there but hidden" are both hidden; only an ambiguous selector is a problem worth reporting.
      if (found.error) return found.error.startsWith('Selector matched')
        ? { status: 'failed', error: found.error }
        : { status: 'passed', assertionResult: true, summary: 'Element is not present' };
      return visible(found.element, win)
        ? { status: 'failed', assertionResult: false, error: `${describe(found.element)} is visible` }
        : { status: 'passed', assertionResult: true, summary: `${describe(found.element)} is hidden` };
    }
    if (found.error) return { status: 'failed', error: found.error };
    const el = found.element;

    switch (action) {
      case 'AssertVisible':
      case 'WaitForVisible':
        return visible(el, win)
          ? { status: 'passed', assertionResult: true, summary: `${describe(el)} is visible` }
          : { status: 'failed', assertionResult: false, error: `${describe(el)} is not visible` };

      case 'AssertText':
      case 'WaitForText': {
        const actual = accessibleName(el);
        return textMatches(actual, command.expected, match)
          ? { status: 'passed', assertionResult: true, observedValue: actual, summary: `${describe(el)} has the expected text` }
          : { status: 'failed', assertionResult: false, observedValue: actual, error: `Expected text "${command.expected}" (${match.toLowerCase()}) not found` };
      }

      case 'AssertValue': {
        const actual = valueOf(el);
        return textMatches(actual, command.expected, match)
          ? { status: 'passed', assertionResult: true, observedValue: actual, summary: `${describe(el)} has the expected value` }
          : { status: 'failed', assertionResult: false, observedValue: actual, error: `Expected value "${command.expected}" (${match.toLowerCase()}) not found` };
      }

      case 'ReadValue':
        return { status: 'passed', observedValue: valueOf(el), summary: `Read ${describe(el)}` };

      case 'Click':
        return clickElement(el, win);

      case 'Fill': {
        if (!visible(el, win)) return { status: 'failed', error: `${describe(el)} is not visible; a step does not type into what a user cannot see.` };
        if (!writable(el)) return { status: 'failed', error: `${describe(el)} is disabled or read-only; a step does not force a value into it.` };
        const wanted = String(command.value ?? '');
        try { setValue(el, win, wanted); } catch (e) { return { status: 'failed', error: `Value was not accepted: ${e && e.message}` }; }
        // Verify rather than assume. A framework that rejected or reformatted the input must not read as a pass.
        const actual = valueOf(el);
        return actual === wanted
          ? { status: 'passed', observedValue: actual, summary: `Filled ${describe(el)}` }
          : { status: 'failed', observedValue: actual, error: `The control did not take the value; it now holds a different one.` };
      }

      case 'Select': {
        if (!visible(el, win)) return { status: 'failed', error: `${describe(el)} is not visible.` };
        if (!writable(el)) return { status: 'failed', error: `${describe(el)} is disabled; a step does not force a selection.` };
        if (!(el instanceof win.HTMLSelectElement)) return { status: 'failed', error: 'Select targets a native <select>. Operate a custom dropdown with Click steps instead.' };
        const wanted = String(command.value ?? '');
        const option = Array.from(el.options).find(o => o.value === wanted || (o.textContent || '').trim() === wanted);
        if (!option) return { status: 'failed', error: `No option "${wanted}" in ${describe(el)}.` };
        if (option.disabled) return { status: 'failed', error: `Option "${wanted}" is disabled.` };
        try { setValue(el, win, option.value); } catch (e) { return { status: 'failed', error: `Selection was not accepted: ${e && e.message}` }; }
        return el.value === option.value
          ? { status: 'passed', observedValue: el.value, summary: `Selected "${wanted}" in ${describe(el)}` }
          : { status: 'failed', observedValue: el.value, error: 'The control did not take the selection.' };
      }
    }
    return { status: 'failed', error: `Unsupported action: ${action}` };
  }

  function clickElement(el, win) {
    if (!visible(el, win)) return { status: 'failed', error: `${describe(el)} is not visible; a step does not click what a user cannot see.` };
    if (!enabled(el)) return { status: 'failed', error: `${describe(el)} is disabled; a step does not click a control the application has disabled.` };
    try {
      el.click();   // the element's own activation behaviour, exactly as a user click produces
      return { status: 'passed', summary: `Clicked ${describe(el)}` };
    } catch (e) {
      return { status: 'failed', error: `Click was not accepted: ${e && e.message}` };
    }
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

  const api = { ACTIONS, MUTATING, SELECTORS, ROLE_TAGS, find, matches, perform, waitFor, visible, enabled, writable, accessibleName, describe, valueOf, setValue };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { automation: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
