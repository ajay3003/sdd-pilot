import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

// Browser Companion collects evidence from approved pages. It does not, and must not, configure the browser.
//
// That is what makes the cleanup question answerable: BirkNext never owns an Edge proxy setting, so it can
// never leave a stale one behind. Taking proxy control would reverse that — it would make BirkNext responsible
// for restoring settings across crashes, browser restarts and enterprise policy, in a managed browser. This
// test exists so that capability cannot be added quietly; adding it is a decision, not a refactor.

const root = new URL('..', import.meta.url);
const manifest = JSON.parse(readFileSync(new URL('manifest.json', root), 'utf8'));

function sources(dir = '') {
  const here = new URL(dir, root);
  return readdirSync(here, { withFileTypes: true }).flatMap(entry => {
    if (['dist', 'tests', 'vendor', 'node_modules'].includes(entry.name)) return [];
    const child = dir + entry.name;
    return entry.isDirectory() ? sources(child + '/') : child.endsWith('.js') ? [child] : [];
  });
}

test('the companion holds no permission that could change browser settings', () => {
  for (const forbidden of ['proxy', 'privacy', 'management', 'webRequest', 'webRequestBlocking', 'declarativeNetRequest'])
    assert.ok(!(manifest.permissions || []).includes(forbidden), `manifest must not request "${forbidden}"`);
  for (const forbidden of ['proxy', 'privacy'])
    assert.ok(!(manifest.optional_permissions || []).includes(forbidden), `optional permissions must not include "${forbidden}"`);
});

test('no script reaches for a proxy or settings API', () => {
  const offenders = [];
  for (const file of sources()) {
    const text = readFileSync(new URL(file, root), 'utf8');
    for (const api of ['chrome.proxy', 'browser.proxy', 'chrome.privacy', 'browser.privacy', 'proxySettings', 'pacScript'])
      if (text.includes(api)) offenders.push(`${file}: ${api}`);
  }
  assert.deepEqual(offenders, [], 'proxy configuration is the user\u2019s, not the extension\u2019s');
});

test('the extension can only reach loopback and the origins it is granted', () => {
  assert.deepEqual(manifest.host_permissions, ['http://127.0.0.1/*', 'http://localhost/*']);
  assert.ok(!(manifest.optional_host_permissions || []).includes('<all_urls>'));
});
