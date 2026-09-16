import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
require('../lib/page-identity.js');
const navigation = require('../lib/navigation.js');

// Deterministic fake browser: manual clock, manual timers, fake history/location. No wall-clock waits.
function fakeBrowser(startUrl) {
  let now = 0;
  const timers = new Map();
  let nextId = 1;
  const listeners = {};
  const location = { href: startUrl };
  const win = {
    location,
    history: {
      pushState(_s, _t, url) { location.href = new URL(url, location.href).href; },
      replaceState(_s, _t, url) { location.href = new URL(url, location.href).href; },
    },
    setTimeout(fn, ms) { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; },
    clearTimeout(id) { timers.delete(id); },
    setInterval() { return 0; },
    clearInterval() {},
    addEventListener(type, fn) { (listeners[type] ||= []).push(fn); },
    dispatch(type) { for (const fn of listeners[type] || []) fn({}); },
  };
  const doc = { readyState: 'complete', documentElement: {} };
  function advance(ms) {
    const target = now + ms;
    while (true) {
      const due = Array.from(timers.entries()).filter(([, t]) => t.at <= target).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      now = due[1].at; timers.delete(due[0]); due[1].fn();
    }
    now = target;
  }
  return { win, doc, advance, now: () => now };
}

test('SPA navigation /dashboard → /children → /placements yields three isolated visits with stable identities', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/dashboard');
  const visits = [], left = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v), onNavigateAway: v => left.push(v) });
  tracker.start();
  b.advance(1000);                                  // quiet period elapsed → initial visit stabilizes
  b.win.history.pushState({}, '', '/children?filter=active');
  b.advance(1000);
  b.win.history.pushState({}, '', '/placements/');
  b.advance(1000);

  assert.deepEqual(visits.map(v => v.identity), ['https://m2lbdev.bufetat.no/dashboard', 'https://m2lbdev.bufetat.no/children', 'https://m2lbdev.bufetat.no/placements']);
  assert.deepEqual(visits.map(v => v.sequence), [1, 2, 3]);
  assert.deepEqual(left.map(v => v.identity), ['https://m2lbdev.bufetat.no/dashboard', 'https://m2lbdev.bufetat.no/children']);
  assert.ok(visits.every(v => v.stabilized));
  tracker.dispose();
});

test('query/hash-only changes do not start a new visit; replaceState to a new path does', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/children');
  const visits = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v) });
  tracker.start(); b.advance(1000);
  b.win.history.pushState({}, '', '/children?page=2'); b.advance(1000);
  b.win.history.replaceState({}, '', '/children#top'); b.advance(1000);
  assert.equal(visits.length, 1);
  b.win.history.replaceState({}, '', '/users'); b.advance(1000);
  assert.equal(visits.length, 2);
  assert.equal(visits[1].path, '/users');
  tracker.dispose();
});

test('stabilization waits for a quiet period but never longer than maxWait; mutations delay it', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/dashboard');
  const visits = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v), options: { quietMs: 800, maxWaitMs: 3000 } });
  tracker.start();
  for (let i = 0; i < 10; i++) { b.advance(500); tracker.noteMutation(); }   // constant mutations for 5 s
  assert.equal(visits.length, 1, 'max-wait cap fires even while the DOM keeps changing');
  assert.equal(visits[0].stabilizedBy, 'max-wait');
  tracker.dispose();
});

test('popstate (back/forward) starts a new visit for the restored route', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/a');
  const visits = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v) });
  tracker.start(); b.advance(1000);
  b.win.location.href = 'https://m2lbdev.bufetat.no/b'; b.win.dispatch('popstate'); b.advance(1000);
  assert.deepEqual(visits.map(v => v.path), ['/a', '/b']);
  assert.equal(visits[1].reason, 'popstate');
  tracker.dispose();
});

test('dispose restores the original history methods (no leak into the host application)', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/a');
  const originalPush = b.win.history.pushState;
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: () => {} });
  tracker.start();
  assert.notEqual(b.win.history.pushState, originalPush);
  tracker.dispose();
  assert.equal(b.win.history.pushState, originalPush);
});

test('page stabilization records BirkNext stabilization time and how it ended; network activity delays quiet, polling does not', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/children');
  const visits = [], started = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v), onVisitStart: v => started.push(v), options: { quietMs: 800, maxWaitMs: 6000 } });
  tracker.start();
  assert.equal(started.length, 1, 'visit start is announced before stabilization');
  assert.equal(started[0].stabilized, false);
  b.advance(500); assert.equal(tracker.noteNetwork('https://api/children?page=1'), true);   // new request → quiet restarts
  b.advance(500); assert.equal(tracker.noteNetwork('https://api/roles'), true);
  b.advance(500); tracker.noteMutation(120);
  assert.equal(visits.length, 0, 'quiet window keeps moving while new activity arrives');
  b.advance(800);
  assert.equal(visits.length, 1);
  assert.equal(visits[0].stabilizedBy, 'quiet');
  assert.equal(visits[0].stabilizationMs, 2300, '1500 ms of activity + 800 ms quiet');
  assert.equal(visits[0].mutations.batchCount, 1);
  assert.equal(visits[0].mutations.mutationCount, 120);
  assert.equal(visits[0].mutations.largestBatch, 120);
  assert.equal(visits[0].mutations.lastMutationMs, 1500);
  assert.equal(started[0], visits[0], 'the same visit object is shared from start to stabilization');

  // A polling endpoint on the next page: the first two hits count, the rest never reset the quiet window.
  b.win.history.pushState({}, '', '/dashboard');
  for (let i = 0; i < 12; i++) { b.advance(300); tracker.noteNetwork('https://api/poll?tick=' + i); }
  assert.equal(visits.length, 2, 'stabilized despite continuous polling');
  assert.equal(visits[1].stabilizedBy, 'quiet');
  assert.ok(visits[1].stabilizationMs < 6000, 'well before the max-wait bound');
  tracker.dispose();
});

test('mutation batches after stabilization are counted as churn only when large; network after stabilization is ignored', () => {
  const b = fakeBrowser('https://m2lbdev.bufetat.no/a');
  const visits = [];
  const tracker = navigation.createTracker({ win: b.win, doc: b.doc, now: b.now, onVisit: v => visits.push(v), options: { quietMs: 800, maxWaitMs: 3000, largeBatch: 50 } });
  tracker.start(); b.advance(1000);
  assert.equal(visits.length, 1);
  tracker.noteMutation(3); tracker.noteMutation(80); tracker.noteMutation(200);
  assert.equal(visits[0].mutations.largeBatchesAfterStabilization, 2);
  assert.equal(visits[0].mutations.batchCount, 3);
  assert.equal(tracker.noteNetwork('https://api/x'), false);
  tracker.dispose();
});
