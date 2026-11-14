// Network isolation for local browser acceptance runs (Playwright, CommonJS).
//
// Acceptance runs use their own disposable API/web processes on dedicated ports. The developer's own BirkNext (ports 5000 and 5173) and
// every non-loopback host must never be contacted — not even by a CORS preflight. This guard enforces that in code:
//   1. validateAcceptanceTargets() refuses to start when a configured URL is not loopback, uses a prohibited port, or is not explicitly allowed;
//   2. validateFrontendConfig() refuses a published web app whose BackendUrl points anywhere but the isolated API;
//   3. installNetworkGuard() routes every browser request (including preflights and redirects, which Playwright routes as new requests)
//      through an allowlist: allowed origins continue, everything else is aborted before it is sent and recorded as a violation.
// A run must call guard.assertNoViolations() (or read guard.violations) and fail when anything was blocked.

'use strict';

const PROHIBITED_PORTS = Object.freeze([5000, 5173]);
const LOOPBACK_HOSTS = Object.freeze(['localhost', '127.0.0.1', '[::1]', '::1']);

function parse(url) {
  try { return new URL(url); } catch { return null; }
}

function portOf(u) {
  if (u.port) return Number(u.port);
  return u.protocol === 'https:' ? 443 : u.protocol === 'http:' ? 80 : NaN;
}

/** Why a URL is not an acceptable acceptance target (null when acceptable). */
function targetProblem(url) {
  const u = parse(url);
  if (!u) return `not a valid URL: ${url}`;
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return `unsupported scheme ${u.protocol} in ${url}`;
  if (!LOOPBACK_HOSTS.includes(u.hostname)) return `host ${u.hostname} is not loopback (${url})`;
  if (PROHIBITED_PORTS.includes(portOf(u))) return `port ${portOf(u)} is reserved for the developer's own BirkNext (${url})`;
  return null;
}

/** Throws unless every configured acceptance URL is loopback, on an allowed port and distinct from the prohibited ports. */
function validateAcceptanceTargets(urls) {
  const problems = Object.entries(urls).map(([name, url]) => [name, targetProblem(url)]).filter(([, p]) => p);
  if (problems.length) throw new Error(`Refusing to start acceptance: ${problems.map(([n, p]) => `${n}: ${p}`).join('; ')}`);
  return Object.fromEntries(Object.entries(urls).map(([name, url]) => [name, parse(url).origin]));
}

/** Throws unless the published frontend configuration points at the isolated API (never a fallback such as :5000). */
function validateFrontendConfig(appsettings, expectedApiOrigin) {
  const backend = appsettings && (appsettings.BackendUrl ?? appsettings.backendUrl);
  if (!backend) throw new Error('Refusing to start acceptance: the published frontend has no BackendUrl (it would fall back to a default).');
  const problem = targetProblem(backend);
  if (problem) throw new Error(`Refusing to start acceptance: frontend BackendUrl ${problem}`);
  if (parse(backend).origin !== parse(expectedApiOrigin).origin)
    throw new Error(`Refusing to start acceptance: frontend BackendUrl ${backend} is not the isolated API ${expectedApiOrigin}`);
  return backend;
}

/**
 * Decides what to do with one request URL: 'allow' for an allowed loopback origin, otherwise a block reason.
 * Allowed origins must themselves pass targetProblem(), so a prohibited port can never be allowlisted.
 */
function decide(url, allowedOrigins) {
  const u = parse(url);
  if (!u) return { allow: false, reason: 'invalid URL' };
  if (u.protocol === 'data:' || u.protocol === 'blob:' || u.protocol === 'about:') return { allow: true };
  if (!LOOPBACK_HOSTS.includes(u.hostname)) return { allow: false, reason: `external host ${u.hostname}` };
  if (PROHIBITED_PORTS.includes(portOf(u))) return { allow: false, reason: `prohibited port ${portOf(u)}` };
  if (!allowedOrigins.includes(u.origin)) return { allow: false, reason: `origin ${u.origin} is not allowlisted` };
  return { allow: true };
}

/**
 * Installs the allowlist on a Playwright BrowserContext. Options:
 *   allowedOrigins: origins (e.g. 'http://localhost:5197', 'http://localhost:5097') — validated, prohibited ports rejected;
 *   onRequest(route, request): optional hook for allowed requests (e.g. to hold or fail a request in a test); must forward via route.continue/fulfill.
 * Returns { violations, assertNoViolations() }.
 */
async function installNetworkGuard(context, { allowedOrigins, onRequest } = {}) {
  if (!Array.isArray(allowedOrigins) || allowedOrigins.length === 0) throw new Error('installNetworkGuard: allowedOrigins is required');
  const origins = allowedOrigins.map(o => {
    const problem = targetProblem(o);
    if (problem) throw new Error(`installNetworkGuard: ${problem}`);
    return parse(o).origin;
  });
  const violations = [];
  await context.route('**/*', async route => {
    const request = route.request();
    const verdict = decide(request.url(), origins);
    if (!verdict.allow) {
      violations.push({ url: request.url(), method: request.method(), reason: verdict.reason, at: new Date().toISOString() });
      return route.abort('blockedbyclient');
    }
    if (onRequest) return onRequest(route, request);
    return route.continue();
  });
  return {
    violations,
    assertNoViolations() {
      if (violations.length) throw new Error(`Prohibited network destinations were attempted (blocked, not sent): ${JSON.stringify(violations.slice(0, 10))}`);
    },
  };
}

module.exports = { PROHIBITED_PORTS, LOOPBACK_HOSTS, targetProblem, validateAcceptanceTargets, validateFrontendConfig, decide, installNetworkGuard };
