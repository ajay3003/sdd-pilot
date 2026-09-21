import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const origin = 'https://m2lbdev.bufetat.no';
const session = { sessionId: 'session', profileId: 'm2lb', approvedOrigins: [origin], environmentName: 'M2LB' };
function harness({ local = {}, transient = {}, denied = false, registrationError = false, grant = null,
                   respond = null, status = 200, offline = false, storageError = false, evidence = null } = {}) {
  const listeners = {}, posts = [], alarms = new Map();
  let registered = [];
  const event = name => ({ addListener(fn) { listeners[name] = fn; } });
  const storage = (data, canFail) => ({ async get(key) { return { [key]: data[key] }; },
    async set(value) { if (canFail && storageError) throw new Error('QUOTA_BYTES quota exceeded'); Object.assign(data, value); },
    async remove(key) { delete data[key]; } });
  // `grant` models the real optional-permission lifecycle: the origin is not granted until something grants it.
  const granted = grant ? { value: false } : null;
  const chrome = {
    runtime: { id: 'extension', getManifest: () => ({ version: 'test' }), onMessage: event('message'), onStartup: event('startup'), onInstalled: event('installed') },
    storage: { local: storage(local, true), session: storage(transient, false) },
    permissions: {
      contains: async () => (granted ? granted.value : !denied),
      // A service worker has no user gesture, so this is what Chromium actually does when the worker asks.
      request: async () => { throw new Error('This function must be called during a user gesture'); },
      onAdded: event('permissionAdded'),
    },
    scripting: { getRegisteredContentScripts: async () => registered, unregisterContentScripts: async () => { registered = []; },
      registerContentScripts: async scripts => { if (registrationError) throw new Error('policy fixture'); registered = scripts; } },
    alarms: { onAlarm: event('alarm'), get: async name => alarms.get(name), clear: async name => alarms.delete(name), create: async (name, value) => alarms.set(name, value) },
    tabs: { get: async id => ({ id, url: origin + '/?code=DO-NOT-COLLECT#token', incognito: false }) },
  };
  const context = vm.createContext({ chrome, URL, console: { warn() {}, debug() {} }, setTimeout, clearTimeout,
    fetch: async (url, options) => {
      posts.push({ url, route: url.split('/').at(-1), body: JSON.parse(options.body) });
      if (offline) throw new TypeError('Failed to fetch');
      // The evidence route can answer differently from pairing/heartbeat — which is exactly the shape of the live defect.
      const route = url.split('/').at(-1);
      if (evidence && route === 'evidence')
        return { ok: evidence.status < 400, status: evidence.status, text: async () => JSON.stringify(evidence.json) };
      return { ok: status < 400, status, text: async () => JSON.stringify(respond || { accepted: true, ...session }) };
    } });
  context.importScripts = (...files) => files.forEach(file => vm.runInContext(readFileSync(new URL('../' + file, import.meta.url), 'utf8'), context));
  vm.runInContext(readFileSync(new URL('../background.js', import.meta.url), 'utf8'), context);
  const sender = { id: 'extension', frameId: 0, tab: { id: 7 }, url: origin + '/' };
  return { posts, alarms, local, transient, listeners, sender, registered: () => registered,
    // What the popup's own chrome.permissions.request() does on Allow, including the browser's onAdded event.
    allow: async () => { granted.value = true; await listeners.permissionAdded?.({}); await new Promise(setImmediate); },
    message: (message, from = {}) => new Promise(resolve => listeners.message(message, from, resolve)),
    heartbeat: () => vm.runInContext('heartbeat()', context),
    flush: () => vm.runInContext('flush()', context),
    statusNow: () => vm.runInContext('lastStatus', context) };
}

// The live defect: pairing reached the backend, the worker then asked for the approved-origin permission itself,
// Chromium refused it for want of a gesture, and the session was dropped. BirkNext held a paired session that
// nothing could ever report to — "Paired · not reporting", unchanged by refreshing the target page.
test('pairing without the origin permission parks the session instead of losing it', async () => {
  const h = harness({ grant: true });

  const paired = await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });
  assert.equal(paired.state, 'needs-permission');
  assert.deepEqual(Array.from(paired.origins), [`${origin}/*`]);
  assert.match(paired.message, /Allow access to https:\/\/m2lbdev\.bufetat\.no/);
  // Nothing observes the application yet, and nothing pretends to.
  assert.deepEqual(h.registered(), []);
  assert.equal(h.local.session, undefined);
  assert.equal(h.local.pendingSession.sessionId, 'session');
  // The popup — and a reopened popup — sees the actionable state, never "Not paired".
  assert.equal((await h.message({ type: 'popup:status' })).state, 'needs-permission');
});

test('granting the origin starts reporting on the session pairing already created', async () => {
  const h = harness({ grant: true });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  await h.allow();
  const status = await h.message({ type: 'popup:finalize' });

  assert.equal(status.state, 'connected');
  // Same session id: granting later does not require a new pairing code.
  assert.equal(h.local.session.sessionId, 'session');
  assert.equal(h.local.pendingSession, undefined);
  assert.deepEqual(Array.from(h.registered()).flatMap(s => Array.from(s.matches)), [`${origin}/*`, `${origin}/*`]);
  assert.equal(h.alarms.get('birknext-heartbeat').periodInMinutes, 0.5);
  // And a page in that tab now reports, which is what leaves "Paired · not reporting".
  await h.message({ type: 'content:page', page: { origin, path: '/' } }, h.sender);
  assert.equal(h.posts.at(-1).body.currentPageOrigin, origin);
});

test('a grant made outside the popup, or after a worker restart, still activates the parked session', async () => {
  const h = harness({ grant: true });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });
  await h.allow(); // e.g. allowed from edge://extensions while the popup is closed

  const restarted = harness({ local: h.local, transient: h.transient, grant: true });
  await restarted.allow();
  await restarted.listeners.startup();

  assert.equal(restarted.local.session.sessionId, 'session');
  assert.equal(restarted.local.pendingSession, undefined);
  assert.equal(restarted.registered().length, 2);
});

test('unpairing discards a parked session', async () => {
  const h = harness({ grant: true });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  assert.equal((await h.message({ type: 'popup:unpair' })).state, 'not-paired');
  assert.equal(h.local.pendingSession, undefined);
  assert.equal((await h.message({ type: 'popup:status' })).state, 'not-paired');
});

test('worker restart restores missing heartbeat alarm and approved current page without an active UI', async () => {
  const first = harness();
  assert.equal((await first.message({ type: 'popup:pair', pairingCode: 'PAIR' })).state, 'connected');
  await first.message({ type: 'content:page', page: { origin, path: '/' } }, first.sender);
  await new Promise(setImmediate);
  assert.equal(first.posts.at(-1).body.currentPageOrigin, origin);
  const restarted = harness({ local: first.local, transient: first.transient });
  await restarted.listeners.startup();
  assert.equal(restarted.alarms.get('birknext-heartbeat').periodInMinutes, 0.5);
  await restarted.heartbeat();
  assert.equal(restarted.posts.at(-1).body.currentPageOrigin, origin);
  assert.equal(restarted.posts.at(-1).body.currentPagePath, '/');
  assert.ok(!JSON.stringify(restarted.posts).includes('DO-NOT-COLLECT'));
  restarted.alarms.set('birknext-heartbeat', { periodInMinutes: 0.25 });
  await restarted.listeners.startup();
  assert.equal(restarted.alarms.get('birknext-heartbeat').periodInMinutes, 0.5);
});

test('registration failure stays blocked through pair and popup validation', async () => {
  const h = harness({ registrationError: true });
  assert.equal((await h.message({ type: 'popup:pair', pairingCode: 'PAIR' })).state, 'blocked');
  assert.equal((await h.message({ type: 'popup:status' })).state, 'blocked');
});

test('unapproved origins, subdomains, ports, frames, profiles and revoked permissions cannot report', async () => {
  const h = harness({ local: { session } });
  for (const sender of [
    { ...h.sender, url: 'https://unapproved.test/' },
    { ...h.sender, url: 'https://child.m2lbdev.bufetat.no/' },
    { ...h.sender, url: 'https://m2lbdev.bufetat.no:444/' },
    { ...h.sender, frameId: 1 }, { ...h.sender, tab: { id: 7, incognito: true } },
    { ...h.sender, id: 'other-extension' },
  ]) {
    assert.equal(await h.message({ type: 'content:session' }, sender), null);
    assert.equal((await h.message({ type: 'content:page', page: { origin, path: '/' } }, sender)).ok, false);
    assert.equal((await h.message({ type: 'content:evidence', page: { profileId: session.profileId, pageOrigin: origin } }, sender)).queued, false);
  }
  assert.equal((await h.message({ type: 'content:evidence', page: { profileId: 'wrong-profile', pageOrigin: origin } }, h.sender)).queued, false);
  const denied = harness({ local: { session }, denied: true });
  assert.equal((await denied.message({ type: 'content:page' }, denied.sender)).ok, false);
  assert.equal((await denied.message({ type: 'popup:status' })).state, 'blocked');
  assert.equal(h.posts.length, 0);
});

// ── Pairing lifecycle: the two sides must never disagree about whether a session exists ─────────

// The reported failure: pair, see Connected, then find the popup back at "Not paired" while BirkNext still
// showed "Paired · not reporting". A persist that fails has to say so — handing back whatever status was in
// memory means, on a freshly woken worker, handing back the value that describes no pairing at all.
test('a pairing that cannot be stored is reported as a failure, never as a lost pairing', async () => {
  // Storage itself fails, so nothing has set an explanatory status on the way down. The old code answered with
  // whatever was in memory, which on a fresh worker is the value that describes no pairing at all.
  const h = harness({ storageError: true });

  const paired = await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  assert.notEqual(paired.state, 'not-paired');
  assert.notEqual(paired.state, 'connected', 'Connected is only claimed once the session is durably stored');
  assert.equal(paired.state, 'blocked');
  assert.match(paired.message, /could not be stored/);
  assert.match(paired.message, /quota exceeded/);
  assert.equal(h.local.session, undefined);
});

test('a registration blocked by policy keeps its own explanation', async () => {
  const h = harness({ registrationError: true });

  assert.equal((await h.message({ type: 'popup:pair', pairingCode: 'PAIR' })).state, 'blocked');
  assert.match((await h.message({ type: 'popup:status' })).message, /policy/i);
});

test('a freshly woken worker never answers "not paired" from its own uninitialised state', async () => {
  const h = harness({ local: { session } });

  // Nothing has been computed yet in this worker; the answer comes from stored state plus the backend.
  assert.equal((await h.message({ type: 'popup:status' })).state, 'connected');
  assert.equal(h.local.session.sessionId, 'session');
});

test('an explicit backend rejection clears the session so both sides agree', async () => {
  const h = harness({ local: { session }, respond: { accepted: false, message: 'Companion session expired. Pair again in BirkNext.' } });

  const status = await h.message({ type: 'popup:status' });

  assert.equal(status.state, 'stale');
  assert.match(status.message, /expired/);
  // The extension no longer holds a session it has been told is gone.
  assert.equal(h.local.session, undefined);
  assert.equal(h.local.pendingSession, undefined);
  assert.equal(status.session, undefined, 'a revoked status carries no session for the popup to display');
  assert.equal((await h.message({ type: 'popup:status' })).state, 'not-paired');
});

test('a heartbeat rejected by the backend revokes the session too', async () => {
  const h = harness({ local: { session }, status: 403, respond: { accepted: false, message: 'No companion session for this environment.' } });

  await h.heartbeat();

  assert.equal(h.local.session, undefined);
});

// A backend that is merely unreachable is a different case: those credentials are still good, and a transient
// fault must not unpair. This is the distinction §27 turns on.
test('an unreachable backend keeps the session and says so', async () => {
  const h = harness({ local: { session }, offline: true });

  const status = await h.message({ type: 'popup:status' });

  assert.equal(status.state, 'backend-unavailable');
  assert.equal(status.session.sessionId, 'session');
  assert.equal(h.local.session.sessionId, 'session', 'a network fault never discards a valid pairing');

  await h.heartbeat();
  assert.equal(h.local.session.sessionId, 'session');
});

test('the session survives service-worker suspension and is rehydrated on wake', async () => {
  const first = harness();
  assert.equal((await first.message({ type: 'popup:pair', pairingCode: 'PAIR' })).state, 'connected');
  assert.equal(first.local.session.sessionId, 'session', 'stored durably, not in worker memory');
  assert.equal(first.local.backend, undefined, 'the default loopback backend needs no stored override');

  // A new worker with nothing but the persisted storage, exactly as MV3 wakes one.
  const woken = harness({ local: first.local, transient: {} });
  const status = await woken.message({ type: 'popup:status' });

  assert.equal(status.state, 'connected');
  assert.equal(woken.alarms.get('birknext-heartbeat').periodInMinutes, 0.5);
  assert.equal(woken.registered().length, 2, 'reporting is registered again without re-pairing');
});

test('the configured backend is the one pairing, heartbeat and validation all use', async () => {
  const h = harness();
  await h.message({ type: 'popup:setBackend', backend: 'http://localhost:5199' });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });
  await h.heartbeat();

  assert.ok(h.posts.length >= 2);
  for (const post of h.posts) assert.ok(post.url.startsWith('http://localhost:5199/'), post.url);
  // And it survives the worker, so a woken worker cannot silently fall back to a different origin.
  const woken = harness({ local: h.local });
  await woken.message({ type: 'popup:status' });
  assert.ok(woken.posts.at(-1).url.startsWith('http://localhost:5199/'));
});

// ── Evidence delivery is reported, never swallowed ──────────────────────────
// The live failure: BirkNext said Connected with the right current page while every evidence envelope was refused.
// Heartbeats and evidence take different backend paths, so a healthy heartbeat proves nothing about evidence.

test('a refused evidence envelope is reported and keeps the session', async () => {
  const h = harness({ evidence: { status: 403, json: { accepted: false, acceptedPages: 0, rejectedPages: 1,
    message: 'Some pages were rejected (origin not approved for this environment or stale).' } } });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  await h.message({ type: 'content:evidence', page: { profileId: 'm2lb', pageOrigin: origin, pagePath: '/admin/operations' } }, h.sender);
  await h.flush();

  const status = h.statusNow();
  assert.equal(status.state, 'connected', 'a refusal is not a lost session: the pairing is still good');
  assert.match(status.message, /refused the last browser evidence/);
  assert.match(status.message, /Some pages were rejected/);
  // The session survives: only an explicit session/pairing rejection unpairs.
  assert.equal(h.local.session.sessionId, 'session');
});

test('an accepted evidence envelope says so', async () => {
  const h = harness({ evidence: { status: 200, json: { accepted: true, acceptedPages: 1, rejectedPages: 0, message: 'OK' } } });
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  await h.message({ type: 'content:evidence', page: { profileId: 'm2lb', pageOrigin: origin, pagePath: '/admin/operations' } }, h.sender);
  await h.flush();

  assert.equal(h.statusNow().state, 'connected');
  assert.match(h.statusNow().message, /Last evidence accepted/);
});

test('current-page and evidence report under the same session identity', async () => {
  const h = harness();
  await h.message({ type: 'popup:pair', pairingCode: 'PAIR' });

  await h.message({ type: 'content:page', page: { origin, path: '/admin/operations' } }, h.sender);
  await h.message({ type: 'content:evidence', page: { profileId: 'm2lb', pageOrigin: origin, pagePath: '/admin/operations' } }, h.sender);
  await h.flush();

  const heartbeat = h.posts.filter(p => p.route === 'heartbeat').at(-1).body;
  const envelope = h.posts.filter(p => p.route === 'evidence').at(-1).body;
  assert.equal(envelope.sessionId, heartbeat.sessionId);
  assert.equal(envelope.profileId, heartbeat.profileId);
  // Both describe the same approved application. The heartbeat's path is resolved from the reporting tab, never from the
  // message, so it is the tab's current route rather than the route the snapshot was built for.
  assert.equal(heartbeat.currentPageOrigin, envelope.pages[0].pageOrigin);
});
