// BirkNext Browser Companion — MAIN-world listener (registered only for approved origins).
// Uncaught exceptions and unhandled rejections of the application are only observable in the page's own JavaScript world, so this
// tiny script forwards them (message, script URL, kind) to the isolated-world content script via postMessage. It adds listeners only:
// no monkey-patching of console/fetch/XHR, no reads of storage, cookies, headers or form values, and the strings are truncated here
// and sanitized again in the isolated world before anything leaves the browser.
(function () {
  'use strict';
  if (window.__birkNextCompanionMainHook) return;
  window.__birkNextCompanionMainHook = true;
  const MAX = 500;
  function forward(kind, message, source) {
    try {
      window.postMessage({ __birkNextCompanion: true, kind, message: String(message || '').slice(0, MAX), source: String(source || '').slice(0, MAX) }, window.location.origin);
    } catch { /* never disturb the host application */ }
  }
  window.addEventListener('error', function (event) {
    // Element load failures (img/script/link) are DOM events the isolated world already sees; only forward script exceptions.
    if (event && event.target && event.target !== window && event.target.tagName) return;
    forward('error', event && event.message, event && event.filename);
  });
  window.addEventListener('unhandledrejection', function (event) {
    let message = 'Unhandled promise rejection';
    try { message = event.reason && event.reason.message ? event.reason.message : String(event.reason || message); } catch { /* opaque reason */ }
    forward('unhandledrejection', message, '');
  });
})();
