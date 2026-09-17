// BirkNext Browser Companion — service worker.
// Responsibilities: pairing with the local BirkNext backend (loopback only), holding the session (extension storage, never a
// credential of the target application), registering the content script ONLY for the environment's approved origins, batching
// evidence from content scripts and posting it to the backend, heartbeats. It never reads cookies, storage or headers of any page.

importScripts('lib/page-identity.js', 'lib/sanitize.js');
const { pageIdentity, sanitize } = globalThis.BirkNextCompanion;
const BACKEND_CANDIDATES = ['http://127.0.0.1:5000', 'http://localhost:5000'];
const EXTENSION_VERSION = chrome.runtime.getManifest().version;
const CONTENT_SCRIPT_ID = 'birknext-companion-content';
const MAIN_WORLD_SCRIPT_ID = 'birknext-companion-main';
const CONTENT_FILES = ['lib/sanitize.js', 'lib/page-identity.js', 'lib/dom.js', 'lib/wcag.js', 'lib/wcag-interaction.js', 'lib/wcag-keyboard.js', 'lib/a11y.js', 'vendor/axe.min.js', 'lib/axe-evidence.js', 'lib/perf.js', 'lib/navigation.js', 'content.js'];
const HEARTBEAT_ALARM = 'birknext-heartbeat';
const FLUSH_DELAY_MS = 1500;
const MAX_PAGES_PER_ENVELOPE = 20;

let pendingByIdentity = new Map();
let flushTimer = null;
let lastStatus = { state: 'unknown', message: 'Not paired.' };

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
  // Ask for host permission on exactly the approved origins (user gesture comes from the popup click that triggered pairing).
  const granted = await requestOriginPermissions(session.approvedOrigins);
  if (!granted) {
    lastStatus = { state: 'not-paired', message: 'Permission for the approved origins was not granted; the companion cannot observe the application.' };
    return lastStatus;
  }
  try { await setSession(session); }
  catch { return lastStatus; }
  lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
  return lastStatus;
}

async function requestOriginPermissions(origins) {
  const patterns = origins.map(o => `${o}/*`);
  if (patterns.length === 0) return false;
  try {
    const has = await chrome.permissions.contains({ origins: patterns });
    if (has) return true;
    return await chrome.permissions.request({ origins: patterns });
  } catch (e) {
    console.warn('BirkNext companion: permission request failed', e && e.message);
    return false;
  }
}

async function unpair() {
  await setSession(null);
  pendingByIdentity = new Map();
  lastStatus = { state: 'not-paired', message: 'Unpaired.' };
  return lastStatus;
}

async function validate() {
  const session = await getSession();
  if (!session) { lastStatus = { state: 'not-paired', message: 'Not paired. Generate a pairing code in BirkNext.' }; return lastStatus; }
  try {
    const result = await post('validate', { sessionId: session.sessionId, profileId: session.profileId, currentPageOrigin: null, currentPagePath: null, extensionVersion: EXTENSION_VERSION });
    if (result.ok && result.json && result.json.accepted) {
      await updateContentScriptRegistration(session);
      await ensureHeartbeatAlarm(session);
      lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
    } else if (result.status === 403 || (result.json && result.json.accepted === false)) {
      lastStatus = { state: 'stale', message: (result.json && result.json.message) || 'Session no longer valid. Pair again in BirkNext.', session };
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
      lastStatus = { state: 'stale', message: result.json.message, session };
    } else if (result.ok) {
      lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}. Last evidence accepted ${new Date().toLocaleTimeString()}.`, session };
    }
  } catch {
    lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable; evidence will be retried on the next page snapshot.', session };
  }
}

async function heartbeat() {
  const session = await getSession();
  if (!session) return;
  let currentPage = null;
  const { reportingPage } = await chrome.storage.session.get('reportingPage');
  if (reportingPage?.profileId === session.profileId) {
    try {
      const tab = await chrome.tabs.get(reportingPage.tabId);
      if (!tab.incognito && approved(session, tab.url) &&
          await chrome.permissions.contains({ origins: [`${pageIdentity.originOf(tab.url)}/*`] })) {
        const identity = pageIdentity.identityOf(sanitize.url(tab.url));
        currentPage = identity;
      }
    } catch { /* The reporting tab was closed or its host permission was removed. */ }
  }
  try {
    const result = await post('heartbeat', {
      sessionId: session.sessionId, profileId: session.profileId, extensionVersion: EXTENSION_VERSION,
      currentPageOrigin: currentPage ? currentPage.origin : null, currentPagePath: currentPage ? currentPage.path : null,
    });
    if (result.ok && result.json && result.json.accepted && lastStatus.state !== 'connected') lastStatus = { state: 'connected', message: `Paired with ${session.environmentName}.`, session };
    if (result.status === 403) lastStatus = { state: 'stale', message: (result.json && result.json.message) || 'Session no longer valid.', session };
  } catch { lastStatus = { state: 'backend-unavailable', message: 'BirkNext backend not reachable on loopback.', session }; }
}

let wcagTab = null;
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
            !['Local', 'Development', 'QA', 'Test', 'RC'].includes(status.session.environmentType)) {
          sendResponse({ message: 'Layout probes require a paired non-production page. Re-pair after changing environment policy.' }); break;
        }
        sendResponse(await chrome.tabs.sendMessage(wcagTab.id, { type: message.type === 'popup:wcag-keyboard' ? 'wcag:keyboard' : 'wcag:layout' }));
        break;
      }
      case 'popup:pair': sendResponse(await pair(message.pairingCode)); break;
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
  const session = await getSession();
  await ensureHeartbeatAlarm(session);
  try { await updateContentScriptRegistration(session); } catch { /* Preserve the actionable blocked status. */ }
}
chrome.runtime.onInstalled.addListener(restoreReporting);
chrome.runtime.onStartup.addListener(restoreReporting);
// MV3 wakes a fresh worker for messages/alarms too, without firing onStartup.
getSession().then(ensureHeartbeatAlarm).catch(() => {});
