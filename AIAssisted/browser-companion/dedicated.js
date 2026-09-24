// Only BirkNext's managed copy contains dedicated-launch.json. Never a page-visible URL or storage value.
let dedicatedBusy = false;
async function dedicatedBootstrap() {
  if (dedicatedBusy) return;
  dedicatedBusy = true;
  try {
    const configResponse = await fetch(chrome.runtime.getURL('dedicated-launch.json'));
    if (!configResponse.ok) return;
    const config = await configResponse.json();
    const build = await (await fetch(chrome.runtime.getURL('build-info.json'))).json();
    globalThis.birkNextBuildId = build.buildId;
    if (!BACKEND_CANDIDATES.includes(config.backend)) return;
    await chrome.alarms.create('birknext-dedicated', { periodInMinutes: 0.5 });
    const response = await fetch(`${config.backend}/api/browser-companion/extension/dedicated`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, cache: 'no-store',
      body: JSON.stringify({ launchToken: config.launchToken, extensionVersion: EXTENSION_VERSION, buildId: build.buildId }),
    });
    if (!response.ok) return;
    const r = await response.json();
    if (!r.accepted) { lastStatus = { state: 'blocked', message: r.message }; return; }
    await chrome.storage.local.set({ backend: config.backend });
    const existing = await getSession();
    if (existing?.sessionId === r.sessionId) { await heartbeat(); return; }
    const session = { sessionId: r.sessionId, profileId: r.profileId, environmentName: r.environmentName,
      environmentType: r.environmentType, approvedOrigins: r.approvedOrigins, expiresAt: r.expiresAt };
    if (await hasOriginPermissions(session.approvedOrigins)) { await activate(session); await heartbeat(); }
    else { await chrome.storage.local.set({ pendingSession: session }); lastStatus = needsPermission(session); }
  } catch { /* Missing config in normal Edge, or backend unavailable; never log the bootstrap token. */ }
  finally { dedicatedBusy = false; }
}
dedicatedBootstrap();
