import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const pageIdentity = require('../lib/page-identity.js');

test('identity is origin + normalized path, never query or fragment (matches Endpoint Discovery)', () => {
  const id = pageIdentity.identityOf('https://m2lbdev.bufetat.no/children/search/?child=123&token=abc#tab');
  assert.deepEqual(id, { origin: 'https://m2lbdev.bufetat.no', path: '/children/search', identity: 'https://m2lbdev.bufetat.no/children/search' });
});

test('default ports are dropped, non-default ports kept', () => {
  assert.equal(pageIdentity.originOf('https://host:443/x'), 'https://host');
  assert.equal(pageIdentity.originOf('http://localhost:5174/x'), 'http://localhost:5174');
  assert.equal(pageIdentity.originOf('http://host:80/'), 'http://host');
});

test('empty path becomes "/" and trailing slashes are trimmed', () => {
  assert.equal(pageIdentity.normalizePath(''), '/');
  assert.equal(pageIdentity.normalizePath('/dashboard///'), '/dashboard');
  assert.equal(pageIdentity.identityOf('https://m2lbdev.bufetat.no').path, '/');
});

test('non-http(s) URLs have no identity', () => {
  assert.equal(pageIdentity.identityOf('chrome-extension://abc/popup.html'), null);
  assert.equal(pageIdentity.identityOf('about:blank'), null);
  assert.equal(pageIdentity.identityOf('not a url'), null);
});

test('origin approval is exact and case-insensitive; no wildcard or suffix matching', () => {
  const approved = ['https://m2lbdev.bufetat.no'];
  assert.equal(pageIdentity.isApprovedOrigin('https://M2LBDEV.bufetat.no', approved), true);
  assert.equal(pageIdentity.isApprovedOrigin('https://evil-m2lbdev.bufetat.no', approved), false);
  assert.equal(pageIdentity.isApprovedOrigin('https://m2lbdev.bufetat.no:8443', approved), false);
  assert.equal(pageIdentity.isApprovedOrigin('https://login.microsoftonline.com', approved), false);
  assert.equal(pageIdentity.isApprovedOrigin(null, approved), false);
});

// The live target, exactly as the user opens it. The approved origin comes from the Target URL with its default
// port dropped, so every spelling of the same page has to land on that one origin — and nothing else may.
test('the M2LB DEV target URL matches its approved origin however it is written', () => {
  const approved = ['https://m2lbdev.bufetat.no'];
  for (const url of [
    'https://m2lbdev.bufetat.no/',
    'https://m2lbdev.bufetat.no',
    'https://m2lbdev.bufetat.no:443/',
    'https://M2LBDEV.bufetat.no/',
    'https://m2lbdev.bufetat.no/barn/1?child=123#tab',
  ]) assert.equal(pageIdentity.isApprovedOrigin(pageIdentity.originOf(url), approved), true, url);

  for (const url of ['http://m2lbdev.bufetat.no/', 'https://m2lbdev.bufetat.no:8443/', 'https://m2lbqa.bufetat.no/'])
    assert.equal(pageIdentity.isApprovedOrigin(pageIdentity.originOf(url), approved), false, url);
});
