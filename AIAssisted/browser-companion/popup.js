// BirkNext Browser Companion — popup. Talks only to the service worker; shows pairing state and takes the pairing code.
(function () {
  'use strict';
  const $ = id => document.getElementById(id);

  function send(message) {
    return new Promise(resolve => chrome.runtime.sendMessage(message, response => resolve(chrome.runtime.lastError ? { state: 'backend-unavailable', message: chrome.runtime.lastError.message } : response)));
  }

  function render(status) {
    const state = (status && status.state) || 'unknown';
    const el = $('state');
    el.textContent = ({ connected: 'Connected', 'not-paired': 'Not paired', stale: 'Session invalid', blocked: 'Blocked by policy', 'backend-unavailable': 'BirkNext not reachable' })[state] || state;
    el.className = `state state-${state}`;
    $('message').textContent = (status && status.message) || '';
    const paired = status && status.session && state !== 'not-paired';
    $('paired').hidden = !paired;
    $('unpaired').hidden = Boolean(paired) && state === 'connected';
    if (paired) {
      $('environment').textContent = status.session.environmentName || status.session.profileId;
      $('origins').textContent = (status.session.approvedOrigins || []).join(', ');
    }
  }

  async function refresh() { render(await send({ type: 'popup:status' })); }

  $('pair').addEventListener('click', async () => {
    const code = $('code').value.trim();
    if (!code) return;
    $('pair').disabled = true;
    try { render(await send({ type: 'popup:pair', pairingCode: code })); }
    finally { $('pair').disabled = false; }
  });
  $('code').addEventListener('keydown', e => { if (e.key === 'Enter') $('pair').click(); });
  $('unpair').addEventListener('click', async () => render(await send({ type: 'popup:unpair' })));
  $('saveBackend').addEventListener('click', async () => {
    const value = $('backend').value.trim();
    if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(value)) { $('message').textContent = 'Only a loopback backend (http://127.0.0.1:port or http://localhost:port) is allowed.'; return; }
    await send({ type: 'popup:setBackend', backend: value });
    await refresh();
  });

  chrome.storage.local.get('backend').then(({ backend }) => { $('backend').value = backend || 'http://127.0.0.1:5000'; });
  refresh();
})();
