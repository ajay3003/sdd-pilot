import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Dedicated Edge lifecycle: launch token → pending session → approved-origin permission → active session → heartbeat.
// The permission model is exact: contains() is true only when every asked-for pattern has been granted, so a grant for
// one Target Environment origin never stands in for another.
const dev = 'https://m2lbdev.bufetat.no';
const qa = 'https://m2lbqa.bufetat.no';
const TOKEN = 'a'.repeat(64);

function harness({ local = {}, transient = {}, granted = [], bootstrap, heartbeat } = {}) {
  const listeners = {}, posts = [], alarms = new Map();
  const grants = new Set(granted.map(o => `${o}/*`));
  let registered = [];
  const event = name => ({ addListener(fn) { listeners[name] = fn; } });
  const storage = data => ({ async get(key) { return { [key]: data[key] }; },
    async set(value) { Object.assign(data, value); }, async remove(key) { delete data[key]; } });
  const chrome = {
    runtime: { id: 'extension', getManifest: () => ({ version: '0.1.0' }), getURL: path => `chrome-extension://extension/${path}`,
      onMessage: event('message'), onStartup: event('startup'), onInstalled: event('installed') },
    storage: { local: storage(local), session: storage(transient) },
    permissions: { contains: async ({ origins }) => origins.every(o => grants.has(o)), onAdded: event('permissionAdded') },
    scripting: { getRegisteredContentScripts: async () => registered, unregisterContentScripts: async () => { registered = []; },
      registerContentScripts: async scripts => { registered = scripts; } },
    alarms: { onAlarm: event('alarm'), get: async name => alarms.get(name), clear: async name => alarms.delete(name), create: async (name, value) => alarms.set(name, value) },
    tabs: { get: async id => ({ id }), onRemoved: event('tabRemoved') },
  };
  const json = (value, status = 200) => ({ ok: status < 400, status, json: async () => value, text: async () => JSON.stringify(value) });
  const context = vm.createContext({ chrome, URL, console: { warn() {}, debug() {} }, setTimeout, clearTimeout,
    fetch: async (url, options) => {
      if (url.endsWith('/dedicated-launch.json')) return json({ launchToken: TOKEN, backend: 'http://127.0.0.1:5000' });
      if (url.endsWith('/build-info.json')) return json({ buildId: 'b'.repeat(64) });
      const route = url.split('/').at(-1);
      const body = JSON.parse(options.body);
      posts.push({ route, body });
      if (route === 'dedicated') {
        const value = bootstrap(body, posts.filter(p => p.route === 'dedicated').length);
        return value === 'hang' ? new Promise(() => {}) : json(value);
      }
      if (route === 'heartbeat') { const [value, status] = (heartbeat ?? (() => [{ accepted: true }]))(body); return json(value, status); }
      return json({ accepted: true });
    } });
  context.importScripts = (...files) => files.forEach(file => vm.runInContext(readFileSync(new URL('../' + file, import.meta.url), 'utf8'), context));
  vm.runInContext(readFileSync(new URL('../background.js', import.meta.url), 'utf8'), context);
  const idle = async () => { for (let i = 0; i < 200 && vm.runInContext('dedicatedBusy', context); i++) await new Promise(setImmediate); await new Promise(setImmediate); };
  return {
    posts, local, transient, alarms, listeners, idle, registered: () => registered,
    routes: route => posts.filter(p => p.route === route),
    grant: async origin => { grants.add(`${origin}/*`); await listeners.permissionAdded?.({}); await new Promise(setImmediate); },
    // The 30-second 'birknext-dedicated' alarm firing.
    tick: async () => { await idle(); await listeners.alarm({ name: 'birknext-dedicated' }); await idle(); },
    status: () => vm.runInContext('lastStatus', context),
    message: message => new Promise(resolve => listeners.message(message, {}, resolve)),
    run: code => vm.runInContext(code, context),
  };
}

const paired = (sessionId, origin = dev) => ({ accepted: true, sessionId, profileId: 'dev', environmentName: 'M2LB DEV',
  environmentType: 'Development', approvedOrigins: [origin], expiresAt: '2099-01-01T00:00:00Z' });

test('missing origin permission parks one stable pending session, reports it, and never heartbeats', async () => {
  const h = harness({ bootstrap: () => paired('s1') });
  await h.idle();

  assert.equal(h.local.session, undefined);
  assert.equal(h.local.pendingSession.sessionId, 's1');
  assert.equal(h.status().state, 'needs-permission');
  assert.deepEqual(Array.from(h.status().origins), [`${dev}/*`]);
  assert.equal(h.routes('heartbeat').length, 0, 'no active session, so no heartbeat');
  // BirkNext is told straight away that the session is waiting for a permission, not merely silent.
  assert.deepEqual(h.routes('dedicated').map(p => p.body.permissionPending), [false, true]);
  assert.ok(!JSON.stringify(h.posts).includes('undefined'));

  // Later alarms: the same session is presented as pending; no new identity is manufactured.
  await h.tick(); await h.tick();
  assert.equal(h.local.pendingSession.sessionId, 's1');
  assert.ok(h.routes('dedicated').slice(2).every(p => p.body.permissionPending === true));
  assert.equal(h.routes('heartbeat').length, 0);
});

test('a declared or already granted origin promotes pending to active once and heartbeats immediately', async () => {
  const h = harness({ granted: [dev], bootstrap: () => paired('s1') });
  await h.idle();

  assert.equal(h.local.session.sessionId, 's1');
  assert.equal(h.local.pendingSession, undefined);
  assert.equal(h.status().state, 'connected');
  assert.equal(h.routes('heartbeat').length, 1, 'one heartbeat on activation, not two');
  assert.equal(h.routes('heartbeat')[0].body.sessionId, 's1');
  assert.equal(h.alarms.get('birknext-heartbeat').periodInMinutes, 0.5, 'the alarm keeps it alive across worker suspension');
  assert.equal(h.registered().length, 2);

  // The next bootstrap for the same session only heartbeats; it never re-activates or re-pairs.
  await h.tick();
  assert.equal(h.local.session.sessionId, 's1');
  assert.equal(h.routes('heartbeat').length, 2);
});

test('granting the permission later promotes the parked session and starts the heartbeat', async () => {
  const h = harness({ bootstrap: () => paired('s1') });
  await h.idle();
  assert.equal(h.routes('heartbeat').length, 0);

  await h.grant(dev);

  assert.equal(h.local.session.sessionId, 's1');
  assert.equal(h.local.pendingSession, undefined);
  assert.equal(h.routes('heartbeat').length, 1);
});

test('reopening Dedicated Edge reconnects on the stored session without a pairing code', async () => {
  const first = harness({ granted: [dev], bootstrap: () => paired('s1') });
  await first.idle();

  const reopened = harness({ local: first.local, granted: [dev], bootstrap: () => paired('s1') });
  await reopened.idle();
  await reopened.listeners.startup();

  assert.equal(reopened.local.session.sessionId, 's1');
  assert.ok(reopened.routes('heartbeat').length >= 1);
  assert.equal(reopened.routes('pair').length, 0);
});

test('a session the backend no longer knows is replaced by the next bootstrap, and a late rejection cannot undo that', async () => {
  // Backend restarted: it rejects the stored session s1 and the bootstrap hands out s2.
  const h = harness({ local: { session: paired('s1'), backend: 'http://127.0.0.1:5000' }, granted: [dev],
    bootstrap: () => paired('s2'),
    heartbeat: body => body.sessionId === 's1' ? [{ accepted: false, message: 'No companion session for this environment.' }, 403] : [{ accepted: true }] });
  await h.idle();
  assert.equal(h.local.session.sessionId, 's2');

  // A heartbeat for s1 that was already in flight comes back 403 after s2 went live: the worker's revoke path runs
  // with the id that request carried.
  await h.run("revokeSession('No companion session for this environment.', 's1')");
  assert.equal(h.local.session.sessionId, 's2', 'the rejection answered s1, so s2 stays');
  assert.equal(h.status().state, 'connected');
});

test('a rejected stored session is revoked when nothing replaced it', async () => {
  const h = harness({ local: { session: paired('s1'), backend: 'http://127.0.0.1:5000' }, granted: [dev],
    bootstrap: () => ({ accepted: false, message: 'Dedicated browser launch is no longer current.' }),
    heartbeat: () => [{ accepted: false, message: 'No companion session for this environment.' }, 403] });
  await h.idle();
  await h.listeners.alarm({ name: 'birknext-heartbeat' });
  await new Promise(setImmediate); await new Promise(setImmediate);

  assert.equal(h.local.session, undefined, 'no stale "paired" claim survives an explicit rejection');
  assert.equal(h.local.pendingSession, undefined);
});

test('a permission for the DEV origin does not authorise a QA session', async () => {
  const h = harness({ granted: [dev], bootstrap: () => paired('s-qa', qa) });
  await h.idle();

  assert.equal(h.local.session, undefined);
  assert.equal(h.local.pendingSession.sessionId, 's-qa');
  assert.deepEqual(Array.from(h.status().origins), [`${qa}/*`]);
  assert.equal(h.routes('heartbeat').length, 0);
});

test('a launch the backend no longer recognises is reported, and manufactures nothing', async () => {
  const h = harness({ bootstrap: () => ({ accepted: false, message: 'Dedicated browser launch is no longer current.' }) });
  await h.idle();
  await h.tick();

  assert.equal(h.status().state, 'blocked');
  assert.equal(h.local.session, undefined);
  assert.equal(h.local.pendingSession, undefined);
  assert.equal(h.routes('heartbeat').length, 0);
});

test('the dedicated bootstrap never sends the launch token anywhere but the loopback bootstrap route', async () => {
  const h = harness({ granted: [dev], bootstrap: () => paired('s1') });
  await h.idle();
  for (const post of h.posts.filter(p => p.route !== 'dedicated')) assert.ok(!JSON.stringify(post.body).includes(TOKEN));
  assert.ok(!JSON.stringify(h.local).includes(TOKEN), 'never stored in extension storage');
});

test('a heartbeat answered accepted:false revokes that session and re-bootstraps without a pairing code', async () => {
  // Backend restarted with Edge left open: the old session is unknown (HTTP 200, accepted:false), the launch is not.
  let issued = 's1';
  const h = harness({ granted: [dev], bootstrap: () => paired(issued),
    heartbeat: body => body.sessionId === 's1' && issued === 's2' ? [{ accepted: false, message: 'No companion session for this environment. Pair again in BirkNext.' }] : [{ accepted: true }] });
  await h.idle();
  assert.equal(h.local.session.sessionId, 's1');

  issued = 's2';
  await h.listeners.alarm({ name: 'birknext-heartbeat' });
  await h.idle();

  assert.equal(h.local.session.sessionId, 's2', 'the next session came from the launch, not from a pairing code');
  assert.equal(h.routes('pair').length, 0);
  assert.equal(h.routes('heartbeat').at(-1).body.sessionId, 's2');
});

test('a bootstrap request that never settles cannot block re-pairing forever', async () => {
  let hang = true;
  const h = harness({ granted: [dev], bootstrap: () => hang ? 'hang' : paired('s1') });
  await new Promise(setImmediate);
  assert.equal(h.routes('dedicated').length, 1);

  hang = false;
  await h.listeners.alarm({ name: 'birknext-dedicated' });
  await new Promise(setImmediate);
  assert.equal(h.routes('dedicated').length, 1, 'still inside the guard window');

  h.run('dedicatedBusySince = Date.now() - DEDICATED_STALE_MS - 1');
  await h.listeners.alarm({ name: 'birknext-dedicated' });
  await h.idle();
  assert.equal(h.routes('dedicated').length, 2);
  assert.equal(h.local.session.sessionId, 's1');
});
