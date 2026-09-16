// BirkNext Browser Companion — evidence sanitizer (browser side).
// The backend applies its own BrowserEvidenceSanitizer again before persisting; this layer guarantees that nothing
// credential-shaped ever leaves the browser in the first place. No token, cookie, storage value or form value is read
// anywhere in the companion, so this sanitizer only has to defuse values that appear incidentally in URLs, titles,
// error messages and selectors.
(function (root) {
  'use strict';

  const MASK = '[REDACTED]';
  const MAX_MESSAGE = 300;
  const MAX_TITLE = 200;
  const MAX_URL = 400;
  const MAX_SELECTOR = 160;

  const patterns = [
    /bearer\s+[a-z0-9\-._~+/]+=*/gi,                              // Authorization: Bearer xxx
    /\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}(?:\.[a-z0-9_-]{8,})?/gi,   // JWT (header.payload[.signature])
    /\b(?:access_token|refresh_token|id_token|token|code|client_secret|password|pwd|apikey|api_key|secret|sig|signature)=[^&\s"']+/gi, // token-shaped query/form pairs
    /\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b/gi,                  // e-mail / user identifiers
    /\b[a-f0-9]{32,}\b/gi,                                          // long opaque hex identifiers
    /\b[a-z0-9_-]{48,}\b/gi,                                        // long opaque base64url identifiers
  ];

  function truncate(value, max) {
    if (typeof value !== 'string') return '';
    return value.length > max ? value.slice(0, max - 1) + '…' : value;
  }

  /** Redacts credential-shaped substrings and caps the length. */
  function text(value, max = MAX_MESSAGE) {
    if (value === null || value === undefined) return '';
    let s = String(value);
    for (const p of patterns) s = s.replace(p, MASK);
    return truncate(s, max);
  }

  /** Strips query string, fragment and userinfo from a URL; keeps scheme, host and path only. */
  function url(value) {
    if (!value) return '';
    try {
      const u = new URL(String(value), 'http://invalid.local');
      if (u.protocol === 'data:' || u.protocol === 'blob:') return u.protocol + MASK;
      if (u.protocol !== 'https:' && u.protocol !== 'http:' && u.protocol !== 'wss:' && u.protocol !== 'ws:') return truncate(u.protocol + '//' + MASK, MAX_URL);
      return truncate(`${u.protocol}//${u.host}${text(u.pathname, MAX_URL)}`, MAX_URL);
    } catch {
      return truncate(text(String(value).split('?')[0].split('#')[0], MAX_URL), MAX_URL);
    }
  }

  function title(value) {
    return text(value, MAX_TITLE);
  }

  /** Structural selectors only: tag, safe id, safe classes, role, nth-of-type. Never attribute values that could carry data. */
  function selectorFor(element) {
    if (!element || element.nodeType !== 1) return '';
    const parts = [];
    let node = element;
    let depth = 0;
    while (node && node.nodeType === 1 && depth < 4) {
      let part = node.tagName.toLowerCase();
      const id = node.getAttribute('id');
      if (id && isSafeToken(id)) { parts.unshift(`${part}#${id}`); break; }
      const role = node.getAttribute('role');
      if (role && isSafeToken(role)) part += `[role=${role}]`;
      const classes = Array.from(node.classList || []).filter(isSafeToken).slice(0, 2);
      if (classes.length) part += '.' + classes.join('.');
      const parent = node.parentElement;
      if (parent && !id) {
        const siblings = Array.from(parent.children).filter(c => c.tagName === node.tagName);
        if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(node) + 1})`;
      }
      parts.unshift(part);
      node = parent;
      depth++;
    }
    return truncate(text(parts.join(' > '), MAX_SELECTOR), MAX_SELECTOR);
  }

  // Tokens usable in a selector: short, no digits-only ids that look like record ids, no @ or long opaque values.
  function isSafeToken(token) {
    if (typeof token !== 'string' || token.length === 0 || token.length > 40) return false;
    if (!/^[a-z_][a-z0-9_-]*$/i.test(token)) return false;
    if (/[0-9]{6,}/.test(token)) return false;
    if (/^[a-f0-9]{16,}$/i.test(token)) return false;
    return true;
  }

  const api = { MASK, MAX_MESSAGE, MAX_TITLE, MAX_URL, MAX_SELECTOR, text, url, title, selectorFor, isSafeToken, truncate };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { sanitize: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
