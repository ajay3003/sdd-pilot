// BirkNext Browser Companion — service worker.
// Responsibilities: pairing with the local BirkNext backend (loopback only), holding the session (extension storage, never a
// credential of the target application), registering the content script ONLY for the environment's approved origins, batching
// evidence from content scripts and posting it to the backend, heartbeats. It never reads cookies, storage or headers of any page.

importScripts('lib/page-identity.js', 'lib/sanitize.js');
const { pageIdentity, sanitize } = globalThis.BirkNextCompanion;
const BACKEND_CANDIDATES = ['http://127.0.0.1:5000', 'http://localhost:5000'];
const EXTENSION_VERSION = chrome.runtime.getManifest().version;
/** What this build can do, reported on every heartbeat so BirkNext never offers a feature an older build lacks. */
const CAPABILITIES = ['element-pick'];
const CONTENT_SCRIPT_ID = 'birknext-companion-content';
const MAIN_WORLD_SCRIPT_ID = 'birknext-companion-main';
const CONTENT_FILES = ['lib/sanitize.js', 'lib/page-identity.js', 'lib/automation.js', 'lib/picker.js', 'lib/dom.js', 'lib/wcag.js', 'lib/wcag-interaction.js', 'lib/wcag-keyboard.js', 'lib/a11y.js', 'vendor/axe.min.js', 'lib/axe-evidence.js', 'lib/perf.js', 'lib/navigation.js', 'content.js'];
const HEARTBEAT_ALARM = 'birknext-heartbeat';
const FLUSH_DELAY_MS = 1500;
const MAX_PAGES_PER_ENVELOPE = 20;

let pendingByIdentity = new Map();
let flushTimer = null;
// Until storage has been read this is genuinely unknown; it must never read as "not paired", because that is
// a claim about a session nobody has looked for yet.
let lastStatus = { state: 'checking', message: 'Checking pairing…' };

// Safe lifecycle trace: event names only. Never a session id, pairing code or any page content.
function trace(event) { console.debug('BirkNext companion: ' + event); }

async function getSession() {
  const { session } = await chrome.storage.local.get('session');
  return session || null;
}

async function setSession(session) {
  await chrome.storage.session.remove('reportingPage');
  wcagTab = null;
  if (session) await chrome.storage.local.set({ session });
  else await chrome.storage.local.remove('session');
  await chrome.alarms.clear(HEARTBEAT_ALARM);
  await ensureHeartbeatAlarm(session);
  await updateContentScriptRegistration(session);
}

async function ensureHeartbeatAlarm(session) {
  if (session && (await chrome.alarms.get(HEARTBEAT_ALARM))?.periodInMinutes !== 0.5)
    await chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 0.5 });
}

function approved(session, url) {
  return pageIdentity.isApprovedOrigin(pageIdentity.originOf(url), session?.approvedOrigins);
}

async function approvedSender(session, sender) {
  return Boolean(session && sender.id === chrome.runtime.id && sender.tab && !sender.tab.incognito &&
    sender.frameId === 0 && approved(session, sender.url) &&
    await chrome.permissions.contains({ origins: [`${pageIdentity.originOf(sender.url)}/*`] }));
}

async function backendBase() {
  const { backend } = await chrome.storage.local.get('backend');
  return backend || BACKEND_CANDIDATES[0];
}

async function post(path, body) {
  const base = await backendBase();
  const response = await fetch(`${base}/api/browser-companion/extension/${path}`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), cache: 'no-store',
  });
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  return { ok: response.ok, status: response.status, json };
}

// ── Pairing ──────────────────────────────────────────────────────────────────

async function pair(pairingCode) {
  const result = await post('pair', { pairingCode: String(pairingCode || '').trim(), extensionVersion: EXTENSION_VERSION });
  if (!result.ok || !result.json || !result.json.accepted) {
    lastStatus = { state: 'not-paired', message: (result.json && result.json.message) || `Pairing failed (HTTP ${result.status}).` };
    return lastStatus;
  }
  const r = result.json;
  const session = { sessionId: r.sessionId, profileId: r.profileId, environmentName: r.environmentName, environmentType: r.environmentType, approvedOrigins: r.approvedOrigins || [], expiresAt: r.expiresAt, pairedAt: new Date().toISOString() };
  // The host permission for the approved origins CANNOT be requested from here. chrome.permissions.request()
  // requires a user gesture, and an MV3 service worker never has one — the popup's click does not carry over a
  // sendMessage. Asking here fails silently and the session is lost while BirkNext already holds a paired
  // session, which reads as "Paired · not reporting" forever. So the session is parked and the popup asks.
  if (await hasOriginPermissions(session.approvedOrigins)) return await activate(session);
  await chrome.storage.local.set({ pendingSession: session });
  lastStatus = needsPermission(session);
  return lastStatus;
}

// Scope patterns for exactly the approved origins — never a broader host pattern.
function originPatterns(origins) { return (origins || []).map(o => `${o}/*`); }

async function hasOriginPermissions(origins) {
  const patterns = originPatterns(origins);
  if (patterns.length === 0) return false;
  try { return await chrome.permissions.contains({ origins: patterns }); }
  catch (e) { console.warn('BirkNext companion: permission check failed', e && e.message); return false; }
}

function needsPermission(session) {
  return {
    state: 'needs-permission', session, origins: originPatterns(session.approvedOrigins),
    message: `BirkNext paired with ${session.environmentName}. Allow access to ${session.approvedOrigins.join(', ')} to start reporting; nothing is observed until you do.`,
  };
}

async function activate(session) {
  try { await setSession(session); }
  catch (e) {
    // Connected is only ever reported once the session is durably stored. Handing back whatever happened to be
    // in memory is what made a failure here read as "Not paired": on a freshly woken worker that in-memory
    // value is the initial one, which describes no pairing at all.
    if (lastStatus.state !== 'blocked')
      lastStatus = { state: 'blocked', message: `This pairing could not be stored (${(e && e.message) || 'unknown error'}). Pair again.`, session };
    trace('PairingStateNotPersisted');
    return lastStatus;
  }
  trace('PairingStatePersisted');
  lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
  return lastStatus;
}

// The backend has discarded this session, so the extension stops claiming it too. Only an explicit rejection
// gets here: a backend that is merely unreachable keeps the session, because those credentials are still good.
async function revokeSession(message) {
  try { await setSession(null); } catch { /* registration teardown is best effort; the session is gone either way */ }
  await chrome.storage.local.remove('pendingSession');
  trace('SessionRevoked');
  lastStatus = { state: 'stale', message: message || 'Session no longer valid. Pair again in BirkNext.' };
  return lastStatus;
}

// Turn a parked session into the live one as soon as the permission exists, whether the popup reported the
// grant or the browser did. The session id from pairing is kept, so granting later needs no new pairing code.
async function finalizePending() {
  const { pendingSession } = await chrome.storage.local.get('pendingSession');
  if (!pendingSession) return null;
  if (!await hasOriginPermissions(pendingSession.approvedOrigins)) return null;
  await chrome.storage.local.remove('pendingSession');
  return await activate(pendingSession);
}

async function unpair() {
  await setSession(null);
  await chrome.storage.local.remove('pendingSession');
  pendingByIdentity = new Map();
  lastStatus = { state: 'not-paired', message: 'Unpaired.' };
  return lastStatus;
}

async function validate() {
  const session = await getSession();
  if (!session) {
    // A session parked for want of the host permission is not "not paired": BirkNext holds it, and it starts
    // reporting the moment access is granted. Saying "Not paired" here is what hid the real state.
    const finalized = await finalizePending();
    if (finalized) return finalized;
    const { pendingSession } = await chrome.storage.local.get('pendingSession');
    if (pendingSession) { lastStatus = needsPermission(pendingSession); return lastStatus; }
    lastStatus = { state: 'not-paired', message: 'Not paired. Generate a pairing code in BirkNext.' };
    return lastStatus;
  }
  try {
    const result = await post('validate', { sessionId: session.sessionId, profileId: session.profileId, currentPageOrigin: null, currentPagePath: null, extensionVersion: EXTENSION_VERSION });
    if (result.ok && result.json && result.json.accepted) {
      await updateContentScriptRegistration(session);
      await ensureHeartbeatAlarm(session);
      lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
    } else if (result.status === 403 || (result.json && result.json.accepted === false)) {
      // An explicit rejection, not a network fault: the session is gone on the backend, so it goes here too.
      return await revokeSession(result.json && result.json.message);
    } else {
      lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable on loopback.', session };
    }
  } catch {
    if (lastStatus.state === 'blocked') return lastStatus;
    lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable on loopback (is BirkNext running?).', session };
  }
  return lastStatus;
}

// ── Content script registration limited to approved origins ─────────────────

async function updateContentScriptRegistration(session) {
  try {
    const existing = await chrome.scripting.getRegisteredContentScripts({ ids: [CONTENT_SCRIPT_ID, MAIN_WORLD_SCRIPT_ID] });
    if (existing.length) await chrome.scripting.unregisterContentScripts({ ids: existing.map(s => s.id) });
  } catch { /* none registered */ }
  if (!session || !session.approvedOrigins || session.approvedOrigins.length === 0) return;
  const matches = session.approvedOrigins.map(o => `${o}/*`);
  try {
    if (!await chrome.permissions.contains({ origins: matches })) throw new Error('Permission for approved origins is missing');
    await chrome.scripting.registerContentScripts([
      { id: CONTENT_SCRIPT_ID, js: CONTENT_FILES, matches, runAt: 'document_start', persistAcrossSessions: true, world: 'ISOLATED' },
      // Listener-only forwarder for uncaught exceptions / unhandled rejections (they are not observable from the isolated world).
      { id: MAIN_WORLD_SCRIPT_ID, js: ['main-world.js'], matches, runAt: 'document_start', persistAcrossSessions: true, world: 'MAIN' },
    ]);
  } catch (e) {
    console.warn('BirkNext companion: content script registration failed', e && e.message);
    lastStatus = { state: 'blocked', message: `Content script could not be registered (${e && e.message}). Managed browser policy may block the companion.`, session };
    throw e;
  }
}

// ── Evidence batching ────────────────────────────────────────────────────────

function queueEvidence(page) {
  // Coalesce per page identity: the latest snapshot wins, so a flood of updates becomes one message.
  pendingByIdentity.set(`${page.pageOrigin}${page.pagePath}|${page.visitStartedAt}`, page);
  if (!flushTimer) flushTimer = setTimeout(flush, FLUSH_DELAY_MS);
}

async function flush() {
  flushTimer = null;
  const session = await getSession();
  if (!session || pendingByIdentity.size === 0) { pendingByIdentity = new Map(); return; }
  const pages = Array.from(pendingByIdentity.values()).slice(0, MAX_PAGES_PER_ENVELOPE);
  pendingByIdentity = new Map();
  try {
    const result = await post('evidence', { sessionId: session.sessionId, profileId: session.profileId, extensionVersion: EXTENSION_VERSION, pages });
    if (result.status === 403 && result.json && /session|pair/i.test(result.json.message || '')) {
      return await revokeSession(result.json.message);
    } else if (result.ok) {
      lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}. Last evidence accepted ${new Date().toLocaleTimeString()}.`, session };
      trace('EvidenceAccepted');
    } else {
      // A backend that answers "no" is not an unreachable backend and not a lost session, so the pairing stands and the
      // heartbeat keeps running. But swallowing this is what made a broken pipeline look like a healthy one: BirkNext said
      // Connected with the current page, and nothing anywhere said the evidence had been refused.
      lastStatus = { state: 'connected', session, message: `Paired with ${session.environmentName}, but BirkNext refused the last browser evidence (${(result.json && result.json.message) || `HTTP ${result.status}`}).` };
      trace('EvidenceRejected');
    }
  } catch {
    lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable; evidence will be retried on the next page snapshot.', session };
  }
}

async function heartbeat() {
  const session = await getSession();
  if (!session) return;
  const live = await livePagesFor(session);
  // The single-page fields stay for a backend that predates live page reporting, and are meaningful only when exactly
  // one page is open — they cannot describe two tabs, which is the whole reason livePages exists.
  const only = live.length === 1 ? live[0] : null;
  try {
    const result = await post('heartbeat', {
      sessionId: session.sessionId, profileId: session.profileId, extensionVersion: EXTENSION_VERSION,
      currentPageOrigin: only ? only.origin : null, currentPagePath: only ? only.route : null,
      livePages: live,
      capabilities: CAPABILITIES,
    });
    if (result.ok && result.json && result.json.accepted && lastStatus.state !== 'connected') lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
    if (result.status === 403) { await revokeSession(result.json && result.json.message); return; }
    trace(result.ok ? 'HeartbeatSucceeded' : 'HeartbeatRejected');
    if (result.ok && result.json) await afterHeartbeat(session, result.json);
    // A backend that answers otherwise is still a backend: these credentials remain good, and a transient
    // fault must never unpair.
  } catch {
    trace('HeartbeatFailed');
    lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable on loopback.', session };
  }
}

// Commands already executed by this worker. A redelivered command is answered from here, never performed twice.
const executedCommands = new Map();
let pollTimer = null;

/**
 * Runs one queued Critical E2E step, then comes straight back for the next one. Between steps the worker polls at the
 * cadence the backend asks for, so a flow does not advance at heartbeat speed; when no run is in progress the backend
 * asks for nothing and the ordinary 30-second alarm is the only traffic.
 */
async function afterHeartbeat(session, body) {
  if (body.pendingCommand) await runCommand(session, body.pendingCommand);
  if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
  const next = body.pendingCommand ? 250 : Number(body.nextHeartbeatMs) || 0;
  if (next > 0) pollTimer = setTimeout(() => heartbeat(), Math.max(next, 250));
}

async function runCommand(session, command) {
  const id = command && command.commandId;
  if (!id) return;
  if (executedCommands.has(id)) { await reportCommand(session, executedCommands.get(id)); return; }

  const started = new Date().toISOString();
  const refuse = reason => ({
    commandId: id, stepId: command.stepId || '', status: 'Blocked', startedAt: started,
    completedAt: new Date().toISOString(), durationMs: 0, sanitizedError: reason,
  });

  let outcome;
  if (command.profileId !== session.profileId) outcome = refuse('The step belongs to a different Target Environment.');
  else if (!PROBE_ENVIRONMENTS.includes(session.environmentType)) outcome = refuse('Steps run only against non-production environments.');
  else if (!approved(session, command.targetOrigin)) outcome = refuse('The step targets an origin this session has not approved.');
  else if (!wcagTab || wcagTab.profileId !== session.profileId) outcome = refuse('No approved reporting page is open in this browser.');
  else if (command.pageId && ![...livePages.values()].some(p => p.pageId === command.pageId)) outcome = refuse('The page this step was bound to is no longer open.');
  else {
    // Deliver to the tab the command is bound to. The last page that announced itself is only a fallback for a command
    // that names no page; with two tabs open it is not necessarily the one the command was aimed at.
    const bound = command.pageId ? [...livePages.values()].find(p => p.pageId === command.pageId) : null;
    const tabId = bound ? bound.tabId : wcagTab.id;
    // Claim before dispatching: if the page never answers, the command is still spent, so a retry cannot click again.
    executedCommands.set(id, refuse('The page did not report a result.'));
    try {
      outcome = await chrome.tabs.sendMessage(tabId, { type: 'e2e:command', command });
    } catch {
      outcome = refuse('The approved page could not be reached; it may have been closed.');
    }
  }
  executedCommands.set(id, outcome);
  if (executedCommands.size > 200) executedCommands.delete(executedCommands.keys().next().value);
  await reportCommand(session, outcome);
}

async function reportCommand(session, result) {
  try {
    await post('command-result', { sessionId: session.sessionId, profileId: session.profileId, extensionVersion: EXTENSION_VERSION, result });
    trace('CommandResultReported');
  } catch { trace('CommandResultFailed'); }
}

/**
 * Approved pages with a live content script, keyed by tab id.
 *
 * This is deliberately NOT derived from evidence. A page becomes live the moment its content script starts and stays
 * live until the tab closes, the script is replaced by a reload, or it stops saying it is there — none of which has
 * anything to do with whether a DOM snapshot has been collected yet. Evidence arriving used to be what made BirkNext
 * believe a page was open, which is how a closed tab could still be reported as the current page.
 */
const livePages = new Map();

/** Written through so the registry survives the service worker being suspended mid-session. */
async function restoreLivePages() {
  if (livePages.size > 0) return;
  const { livePages: stored } = await chrome.storage.session.get('livePages');
  for (const page of stored ?? []) livePages.set(page.tabId, page);
}

async function persistLivePages() {
  try { await chrome.storage.session.set({ livePages: [...livePages.values()] }); } catch { /* session storage is best effort */ }
}

/** The live pages that still belong to this session and whose tabs still exist. */
async function livePagesFor(session) {
  await restoreLivePages();
  const out = [];
  for (const [tabId, page] of [...livePages.entries()]) {
    if (page.profileId !== session.profileId || !approved(session, page.origin)) { livePages.delete(tabId); continue; }
    // A tab that has gone is not live, whatever it last reported. onRemoved normally gets here first; this is the
    // fallback for a worker that was asleep when the tab closed.
    try { await chrome.tabs.get(tabId); } catch { livePages.delete(tabId); continue; }
    out.push({ pageId: page.pageId, origin: page.origin, route: page.route, contentScriptInstanceId: page.instanceId });
  }
  await persistLivePages();
  return out;
}

// Immediate cleanup when a tab closes. onRemoved needs no "tabs" permission — it carries only the id of a tab we
// already knew about.
chrome.tabs.onRemoved.addListener(async tabId => {
  await restoreLivePages();
  if (livePages.delete(tabId)) { await persistLivePages(); heartbeat(); }
});

let wcagTab = null;
// Environments an interactive probe may drive. Production is deliberately absent and must stay absent.
const PROBE_ENVIRONMENTS = ['Local', 'Development', 'QA', 'Test', 'RC'];
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === HEARTBEAT_ALARM) heartbeat(); });

// ── Messages from popup and content scripts ────────────────────────────────

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  (async () => {
    switch (message && message.type) {
      case 'popup:status': sendResponse(await validate()); break;
      case 'popup:wcag-keyboard':
      case 'popup:wcag-layout': {
        if (sender.tab) { sendResponse({ message: 'Popup action required.' }); break; }
        const status = await validate();
        if (status.state !== 'connected' || !wcagTab || wcagTab.profileId !== status.session.profileId ||
            !PROBE_ENVIRONMENTS.includes(status.session.environmentType)) {
          sendResponse({ message: 'Layout probes require a paired non-production page. Re-pair after changing environment policy.' }); break;
        }
        sendResponse(await chrome.tabs.sendMessage(wcagTab.id, { type: message.type === 'popup:wcag-keyboard' ? 'wcag:keyboard' : 'wcag:layout' }));
        break;
      }
      // A Critical E2E probe: BirkNext names an allow-listed ACTION and describes an element; no code crosses this
      // boundary. Same gate as the layout probes — paired session, the extension's own approved reporting tab, and a
      // non-production environment. Production is never driven.
      case 'popup:e2e-probe': {
        // Only the extension's own UI may ask for a probe. A page that somehow reached this worker is not the
        // extension, whether or not it happens to live in a tab.
        if (!sender.url || !sender.url.startsWith(chrome.runtime.getURL(''))) { sendResponse({ status: 'blocked', error: 'Probes are requested from the BirkNext companion, not from a page.' }); break; }
        const probeStatus = await validate();
        if (probeStatus.state !== 'connected' || !wcagTab || wcagTab.profileId !== probeStatus.session.profileId ||
            !PROBE_ENVIRONMENTS.includes(probeStatus.session.environmentType)) {
          sendResponse({ status: 'blocked', error: 'Probes require a paired non-production reporting page.' }); break;
        }
        sendResponse(await chrome.tabs.sendMessage(wcagTab.id, { type: 'e2e:command', command: { ...message.command, commandId: message.commandId ?? null } }));
        break;
      }
      case 'popup:pair': sendResponse(await pair(message.pairingCode)); break;
      // The popup owns the permission prompt (it has the gesture); this is where it hands back the outcome.
      case 'popup:finalize': sendResponse(await finalizePending() ?? await validate()); break;
      case 'popup:unpair': sendResponse(await unpair()); break;
      case 'popup:setBackend': await chrome.storage.local.set({ backend: message.backend }); sendResponse({ ok: true }); break;
      case 'content:session': {
        // Content scripts only receive scope information (profile id + approved origins), never the session id.
        const session = await getSession();
        sendResponse(await approvedSender(session, sender) ? { profileId: session.profileId, approvedOrigins: session.approvedOrigins, environmentType: session.environmentType } : null);
        break;
      }
      case 'content:evidence': {
        const session = await getSession();
        const allowed = await approvedSender(session, sender) && message.page?.profileId === session.profileId &&
          message.page.pageOrigin === pageIdentity.originOf(sender.url);
        if (allowed) queueEvidence(message.page);
        sendResponse({ queued: Boolean(allowed) });
        break;
      }
      // A content script announcing that it is alive on an approved page. Independent of evidence: it arrives at
      // document_start, long before any DOM snapshot exists, and repeats on every route change.
      case 'content:live': {
        const session = await getSession();
        if (!await approvedSender(session, sender) || !sender.tab) { sendResponse({ ok: false }); break; }
        await restoreLivePages();
        const tabId = sender.tab.id;
        const instanceId = String(message.instanceId || '').slice(0, 32);
        const origin = pageIdentity.originOf(sender.url);
        if (!instanceId || !origin) { sendResponse({ ok: false }); break; }
        livePages.set(tabId, {
          tabId, pageId: `t${tabId}-${instanceId}`, profileId: session.profileId, origin,
          // Origin comes from sender.url, which the browser controls; the route comes from the page, which is the
          // only side that knows where an SPA has navigated to.
          route: pageIdentity.identityOf(`${origin}${String(message.route || '/')}`)?.path ?? '/', instanceId,
        });
        await persistLivePages();
        wcagTab = { id: tabId, profileId: session.profileId };
        sendResponse({ ok: true, pageId: `t${tabId}-${instanceId}` });
        // Tell BirkNext straight away rather than at the next scheduled beat: a page that just opened should not look
        // closed for another half minute.
        heartbeat();
        break;
      }
      case 'content:page': {
        const session = await getSession();
        if (!await approvedSender(session, sender)) { sendResponse({ ok: false }); break; }
        // Store only the tab/environment association, never a raw URL or page credentials.
        await chrome.storage.session.set({ reportingPage: { tabId: sender.tab.id, profileId: session.profileId } });
        wcagTab = { id: sender.tab.id, profileId: session.profileId };
        await heartbeat(); sendResponse({ ok: true }); break;
      }
      default: sendResponse({ error: 'unknown message' });
    }
  })().catch(e => sendResponse({ error: String(e && e.message) }));
  return true; // async response
});

async function restoreReporting() {
  trace('ServiceWorkerRehydrated');
  const session = (await getSession()) ?? ((await finalizePending()) && await getSession());
  await ensureHeartbeatAlarm(session);
  try { await updateContentScriptRegistration(session); } catch { /* Preserve the actionable blocked status. */ }
}
// The grant can also arrive from edge://extensions or a later prompt; either way the parked session goes live.
chrome.permissions.onAdded?.addListener(() => { finalizePending().catch(() => {}); });
chrome.runtime.onInstalled.addListener(restoreReporting);
chrome.runtime.onStartup.addListener(restoreReporting);
// MV3 wakes a fresh worker for messages/alarms too, without firing onStartup.
getSession().then(ensureHeartbeatAlarm).catch(() => {});
