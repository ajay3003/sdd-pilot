// BirkNext Browser Companion — page identity.
// Must produce exactly the identity Endpoint Discovery derives from proxy traffic
// (NetworkTrafficClassifier.NormalizePath / origin without default ports): origin + normalized pathname,
// never query string or fragment, trailing slash trimmed, empty path => "/".
(function (root) {
  'use strict';

  function normalizePath(pathname) {
    let path = (pathname || '').split('?')[0].split('#')[0];
    while (path.length > 1 && path.endsWith('/')) path = path.slice(0, -1);
    return path.length === 0 ? '/' : path;
  }

  function originOf(url) {
    try {
      const u = new URL(url);
      if (u.protocol !== 'https:' && u.protocol !== 'http:') return null;
      const isDefault = (u.protocol === 'https:' && (u.port === '' || u.port === '443')) || (u.protocol === 'http:' && (u.port === '' || u.port === '80'));
      return `${u.protocol}//${u.hostname}${isDefault ? '' : ':' + u.port}`;
    } catch {
      return null;
    }
  }

  /** Returns { origin, path, identity } or null for non-http(s) URLs. */
  function identityOf(url) {
    const origin = originOf(url);
    if (!origin) return null;
    const path = normalizePath(new URL(url).pathname);
    return { origin, path, identity: origin + path };
  }

  /** Origin approval: exact origin match against the environment's approved origins (no wildcards). */
  function isApprovedOrigin(origin, approvedOrigins) {
    if (!origin || !Array.isArray(approvedOrigins)) return false;
    const o = origin.toLowerCase();
    return approvedOrigins.some(a => typeof a === 'string' && a.toLowerCase() === o);
  }

  const api = { normalizePath, originOf, identityOf, isApprovedOrigin };
  root.BirkNextCompanion = Object.assign(root.BirkNextCompanion || {}, { pageIdentity: api });
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
