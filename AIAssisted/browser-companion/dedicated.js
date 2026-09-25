// Only BirkNext's managed copy contains dedicated-launch.json. Never a page-visible URL or storage value.
//
// Lifecycle: every worker start and every 30-second 'birknext-dedicated' alarm presents the launch token. The backend
// answers with the same session for as long as that launch is current (asking counts as activity, so a parked session
// is not replaced by a new identity every idle window). With the approved-origin permission present, which the
// managed copy declares for exactly the current origin, the session goes live and heartbeats; without it, the session
// stays parked under pendingSession, stable, and BirkNext is told why. The file is re-read on every attempt, so a new
// Dedicated Edge launch after a backend restart is picked up without a pairing code.
let dedicatedBusy = false;
let dedicatedBusySince = 0;
// A bootstrap never legitimately takes this long. One that has (a request that never settled while the backend was
// being killed) must not hold the guard forever, or the browser can never re-pair on its own.
const DEDICATED_STALE_MS = 20000;
const DEDICATED_REQUEST_TIMEOUT_MS = 10000;
const requestTimeout = () => (typeof AbortSignal !== 'undefined' && AbortSignal.timeout) ? AbortSignal.timeout(DEDICATED_REQUEST_TIMEOUT_MS) : undefined;

async function dedicatedPost(config, build, permissionPending) {
  const response = await fetch(`${config.backend}/api/browser-companion/extension/dedicated`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, cache: 'no-store', signal: requestTimeout(),
    body: JSON.stringify({ launchToken: config.launchToken, extensionVersion: EXTENSION_VERSION, buildId: build.buildId, permissionPending }),
  });
  return response.ok ? await response.json() : null;
}

async function dedicatedBootstrap() {
  if (dedicatedBusy && Date.now() - dedicatedBusySince < DEDICATED_STALE_MS) return;
  dedicatedBusy = true;
  dedicatedBusySince = Date.now();
  try {
    const configResponse = await fetch(chrome.runtime.getURL('dedicated-launch.json'));
    if (!configResponse.ok) return;
    const config = await configResponse.json();
    const build = await (await fetch(chrome.runtime.getURL('build-info.json'))).json();
    globalThis.birkNextBuildId = build.buildId;
    if (!BACKEND_CANDIDATES.includes(config.backend)) return;
    await chrome.alarms.create('birknext-dedicated', { periodInMinutes: 0.5 });

    const { pendingSession: parked } = await chrome.storage.local.get('pendingSession');
    const reportedPending = Boolean(parked) && !await hasOriginPermissions(parked.approvedOrigins);
    const r = await dedicatedPost(config, build, reportedPending);
    if (!r) return;
    if (!r.accepted) { lastStatus = { state: 'blocked', message: r.message }; return; }
    await chrome.storage.local.set({ backend: config.backend });

    const existing = await getSession();
    if (existing?.sessionId === r.sessionId) { await heartbeat(); return; }
    const session = { sessionId: r.sessionId, profileId: r.profileId, environmentName: r.environmentName,
      environmentType: r.environmentType, approvedOrigins: r.approvedOrigins, expiresAt: r.expiresAt };
    // activate() starts the heartbeat itself; a session from an earlier launch or backend is simply replaced.
    if (await hasOriginPermissions(session.approvedOrigins)) { await chrome.storage.local.remove('pendingSession'); await activate(session); return; }

    // Parked: keep one stable pending session (the backend hands back this same id while the launch is current).
    await chrome.storage.local.set({ pendingSession: session });
    lastStatus = needsPermission(session);
    trace('DedicatedPermissionRequired');
    // Say so now rather than at the next alarm, so BirkNext shows "permission required" instead of "waiting".
    if (!reportedPending || parked?.sessionId !== session.sessionId) await dedicatedPost(config, build, true);
  } catch { /* Missing config in normal Edge, or backend unavailable; never log the bootstrap token. */ }
  finally { dedicatedBusy = false; }
}
dedicatedBootstrap();
