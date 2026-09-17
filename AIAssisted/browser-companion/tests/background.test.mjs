import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const origin = 'https://m2lbdev.bufetat.no';
const session = { sessionId: 'session', profileId: 'm2lb', approvedOrigins: [origin], environmentName: 'M2LB' };
function harness({ local = {}, transient = {}, denied = false, registrationError = false } = {}) {
  const listeners = {}, posts = [], alarms = new Map();
  let registered = [];
  const event = name => ({ addListener(fn) { listeners[name] = fn; } });
  const storage = data => ({ async get(key) { return { [key]: data[key] }; }, async set(value) { Object.assign(data, value); }, async remove(key) { delete data[key]; } });
  const chrome = {
    runtime: { id: 'extension', getManifest: () => ({ version: 'test' }), onMessage: event('message'), onStartup: event('startup'), onInstalled: event('installed') },
    storage: { local: storage(local), session: storage(transient) },
    permissions: { contains: async () => !denied, request: async () => !denied },
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
  return { posts, alarms, local, transient, listeners, sender,
    message: (message, from = {}) => new Promise(resolve => listeners.message(message, from, resolve)),
    heartbeat: () => vm.runInContext('heartbeat()', context) };
}

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
