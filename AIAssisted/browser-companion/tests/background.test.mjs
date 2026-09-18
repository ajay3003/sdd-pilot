import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const origin = 'https://m2lbdev.bufetat.no';
const session = { sessionId: 'session', profileId: 'm2lb', approvedOrigins: [origin], environmentName: 'M2LB' };
function harness({ local = {}, transient = {}, denied = false, registrationError = false, grant = null } = {}) {
  const listeners = {}, posts = [], alarms = new Map();
  let registered = [];
  const event = name => ({ addListener(fn) { listeners[name] = fn; } });
  const storage = data => ({ async get(key) { return { [key]: data[key] }; }, async set(value) { Object.assign(data, value); }, async remove(key) { delete data[key]; } });
  // `grant` models the real optional-permission lifecycle: the origin is not granted until something grants it.
  const granted = grant ? { value: false } : null;
  const chrome = {
    runtime: { id: 'extension', getManifest: () => ({ version: 'test' }), onMessage: event('message'), onStartup: event('startup'), onInstalled: event('installed') },
    storage: { local: storage(local), session: storage(transient) },
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
  const context = vm.createContext({ chrome, URL, console: { warn() {} }, setTimeout, clearTimeout,
    fetch: async (url, options) => { posts.push({ route: url.split('/').at(-1), body: JSON.parse(options.body) });
      return { ok: true, status: 200, text: async () => JSON.stringify({ accepted: true, ...session }) }; } });
  context.importScripts = (...files) => files.forEach(file => vm.runInContext(readFileSync(new URL('../' + file, import.meta.url), 'utf8'), context));
  vm.runInContext(readFileSync(new URL('../background.js', import.meta.url), 'utf8'), context);
  const sender = { id: 'extension', frameId: 0, tab: { id: 7 }, url: origin + '/' };
  return { posts, alarms, local, transient, listeners, sender, registered: () => registered,
    // What the popup's own chrome.permissions.request() does on Allow, including the browser's onAdded event.
    allow: async () => { granted.value = true; await listeners.permissionAdded?.({}); await new Promise(setImmediate); },
    message: (message, from = {}) => new Promise(resolve => listeners.message(message, from, resolve)),
    heartbeat: () => vm.runInContext('heartbeat()', context) };
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
