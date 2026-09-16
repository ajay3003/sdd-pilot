import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const sanitize = require('../lib/sanitize.js');

const JWT = 'eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c';

test('Bearer values, JWTs, e-mails and token query pairs are redacted from text', () => {
  const out = sanitize.text(`Request failed: Authorization: Bearer ${JWT} for ola.nordmann@bufetat.no ?access_token=SECRET1&code=SECRET2`);
  assert.equal(out.includes(JWT), false);
  assert.equal(out.includes('SECRET1'), false);
  assert.equal(out.includes('SECRET2'), false);
  assert.equal(out.includes('ola.nordmann@bufetat.no'), false);
  assert.ok(out.includes(sanitize.MASK));
});

test('long opaque identifiers are redacted, ordinary words survive', () => {
  const out = sanitize.text('user 0123456789abcdef0123456789abcdef0123456789abcdef failed to load component');
  assert.equal(out.includes('0123456789abcdef'), false);
  assert.ok(out.includes('failed to load component'));
});

test('text is length-capped', () => {
  assert.ok(sanitize.text('x'.repeat(2000)).length <= sanitize.MAX_MESSAGE);
});

test('URLs lose query string, fragment and userinfo', () => {
  assert.equal(sanitize.url('https://user:pw@api.example.test/children?token=abc#frag'), 'https://api.example.test/children');
  assert.equal(sanitize.url('data:text/html;base64,AAAA'), 'data:' + sanitize.MASK);
  assert.equal(sanitize.url('https://m2lbdev.bufetat.no/_framework/blazor.boot.json'), 'https://m2lbdev.bufetat.no/_framework/blazor.boot.json');
});

test('selector tokens reject values that could carry data', () => {
  assert.equal(sanitize.isSafeToken('sp-btn-primary'), true);
  assert.equal(sanitize.isSafeToken('user-1234567'), false);
  assert.equal(sanitize.isSafeToken('kari@bufetat.no'), false);
  assert.equal(sanitize.isSafeToken('0123456789abcdef0123'), false);
  assert.equal(sanitize.isSafeToken('x'.repeat(41)), false);
});
