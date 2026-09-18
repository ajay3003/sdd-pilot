// BirkNext Browser Companion — popup. Talks only to the service worker; shows pairing state and takes the pairing code.
(function () {
  'use strict';
  const $ = id => document.getElementById(id);

  function send(message) {
    return new Promise(resolve => chrome.runtime.sendMessage(message, response => resolve(chrome.runtime.lastError ? { state: 'backend-unavailable', message: chrome.runtime.lastError.message } : response)));
  }

  let pendingOrigins = [];

  // The popup renders the service worker's answer and decides nothing about pairing itself. The worker reads
  // the stored session; this file only says what that answer looks like.
  const LABELS = {
    connected: 'Connected',
    'not-paired': 'Not paired',
    stale: 'Session invalid',
    blocked: 'Blocked by policy',
    'needs-permission': 'Access required',
    'backend-unavailable': 'BirkNext not reachable',
    checking: 'Checking connection…',
  };

  function render(status) {
    const state = (status && status.state) || 'checking';
    // Anything unrecognised is still being resolved; it is never reported as an absent pairing.
    const checking = !(state in LABELS) || state === 'checking';
    const el = $('state');
    el.textContent = checking ? LABELS.checking : LABELS[state];
    el.className = `state state-${checking ? 'checking' : state}`;
    $('message').textContent = (status && status.message) || '';
    const paired = Boolean(status && status.session) && state !== 'not-paired';
    pendingOrigins = (status && status.origins) || [];
    $('paired').hidden = !paired;
    $('grant').hidden = state !== 'needs-permission';
    $('unpair').hidden = state === 'needs-permission';
    // While the worker is still reading its stored session nothing is claimed either way. Offering the pairing
    // form during that moment is what made a perfectly good pairing look as though it had been lost.
    $('unpaired').hidden = checking || (paired && (state === 'connected' || state === 'needs-permission'));
    if (paired) {
      $('environment').textContent = status.session.environmentName || status.session.profileId;
      $('origins').textContent = (status.session.approvedOrigins || []).join(', ');
    }
  }

  async function refresh() { render(await send({ type: 'popup:status' })); }

  // The permission prompt belongs here and nowhere else: this is the only context with a user gesture. The
  // service worker cannot ask, so it parks the session and waits for the outcome of this call.
  async function requestAccess(origins) {
    if (!origins || origins.length === 0) return;
    let granted = false;
    let failure = null;
    try { granted = await chrome.permissions.request({ origins }); }
    catch (e) { failure = e && e.message; }
    render(await send({ type: 'popup:finalize' }));
    if (failure) $('message').textContent = `Access could not be requested (${failure}). Open the extension's details in edge://extensions and allow the site there.`;
    else if (!granted) $('message').textContent = 'Access was not granted, so no page is observed. Choose Allow access, then Allow in the browser prompt.';
  }

  $('pair').addEventListener('click', async () => {
    const code = $('code').value.trim();
    if (!code) return;
    $('pair').disabled = true;
    try {
      const status = await send({ type: 'popup:pair', pairingCode: code });
      render(status);
      // Still inside the click handler, so the activation from that click is what carries the prompt.
      if (status && status.state === 'needs-permission') await requestAccess(status.origins);
    }
    finally { $('pair').disabled = false; }
  });
  $('grant').addEventListener('click', async () => {
    $('grant').disabled = true;
    try { await requestAccess(pendingOrigins); }
    finally { $('grant').disabled = false; }
  });
  $('code').addEventListener('keydown', e => { if (e.key === 'Enter') $('pair').click(); });
  $('unpair').addEventListener('click', async () => render(await send({ type: 'popup:unpair' })));
  $('wcagLayout').addEventListener('click', async () => {
    $('wcagLayout').disabled = true;
    try { const result = await send({ type: 'popup:wcag-layout' }); $('message').textContent = result?.message || 'Layout probes unavailable.'; }
    finally { $('wcagLayout').disabled = false; }
  });
  $('wcagKeyboard').addEventListener('click', async () => {
    const result = await send({ type: 'popup:wcag-keyboard' });
    $('message').textContent = result?.message || 'Keyboard observation unavailable.';
  });
  $('saveBackend').addEventListener('click', async () => {
    const value = $('backend').value.trim();
    if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(value)) { $('message').textContent = 'Only a loopback backend (http://127.0.0.1:port or http://localhost:port) is allowed.'; return; }
    await send({ type: 'popup:setBackend', backend: value });
    await refresh();
  });

  chrome.storage.local.get('backend').then(({ backend }) => { $('backend').value = backend || 'http://127.0.0.1:5000'; });
  // Resolve from the worker, never from a blank slate that reads as "Not paired".
  render({ state: 'checking' });
  refresh();
})();
