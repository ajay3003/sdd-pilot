# Browser Companion automation feasibility spike

**Question.** Can the Browser Companion perform one benign automated action on authenticated M2LB and assert the result,
without CDP, without Playwright, without weakening Defender or Edge policy?

**Verdict: GO.** One confirmation on the real authenticated application is outstanding and is a manual step (§"Outstanding
confirmation" below). Nothing in the mechanism depends on that confirmation succeeding for the *architecture* decision —
it would only tell us whether M2LB's own DOM is selector-friendly.

This matters because the preceding Playwright spike returned **NO-GO — organisational policy block**: `Target.attachToTarget`
is refused for the M2LB origin, pre-authentication, by Defender for Cloud Apps in-browser protection. The companion path is
not affected by that block, because it is not CDP. It is a content script the browser itself runs in the page.

---

## 1. Audit findings (done before any code was written)

**A. A reverse command path already exists and already ships.**
`popup.js` → `background.js:'popup:wcag-layout'` → `chrome.tabs.sendMessage(wcagTab.id, …)` → `content.js` listener.
The only hop that did not exist was *BirkNext backend → service worker*; popup → worker → page was already built, gated
and tested.

**B. The content script already writes to the DOM of the authenticated page.**
`lib/wcag-interaction.js` sets `font-size` with `!important` on live elements and restores the original values afterwards.
A `.click()` is a smaller intervention than what the companion already performs.

**C. The content script already runs inside authenticated M2LB.** Evidence collection from `https://m2lbdev.bufetat.no`
works today (that path was repaired earlier in this workstream). So *execution in the authenticated page* is not a
hypothesis — it is observed, in production use, on the real origin.

**D. No new permission is required.** Content scripts already hold DOM access on matched origins, and
`chrome.tabs.sendMessage` to a known tab id needs no `tabs` permission. `manifest.json` is **byte-identical** after this
spike — `git diff manifest.json` is empty.

**E. The environment gate already existed.** Layout probes are restricted to `Local | Development | QA | Test | RC`.
The probe runner reuses that list (now a single shared constant rather than a duplicated literal) and re-checks it *inside
the page*, so the worker's answer is not the only thing standing between a probe and a production page.

---

## 2. What was built (the minimum to answer the question)

| File | Role |
|---|---|
| `lib/automation.js` | Typed action layer: `click`, `assertVisible`, `assertText`, `assertRoute`; selectors by `role`, `testid`, `text`, `label`, `css`; bounded `waitFor` |
| `content.js` | `e2e:probe` runner — environment re-check, allow-list check, bounded timeout, structured result |
| `background.js` | `popup:e2e-probe` — extension-UI-only sender check, paired-session check, shared `PROBE_ENVIRONMENTS` gate |
| `popup.html` / `popup.js` | Spike control: name a link, name the expected route, run see → click → confirm |
| `build.mjs` | `eval(` and `new Function` added to the banned-token scan |

**Command shape.** BirkNext sends an *action name* and a *described element*:

```js
{ action: 'click', selector: { kind: 'text', value: 'Saker' }, timeoutMs: 8000 }
```

**Result shape.** `{ commandId, status, startedAt, completedAt, durationMs, observedRoute, summary, error }` —
`status` is `passed | failed | blocked`, which keeps "the assertion was false" separate from "the probe could not run".

---

## 3. Safety properties, and how each is enforced

| Property | Enforcement |
|---|---|
| No JavaScript crosses the boundary | Only an action *name* is transmitted; the build fails if any shipped script contains `eval(` or `new Function` (verified by canary — the gate really fails) |
| Production is never driven | Shared `PROBE_ENVIRONMENTS`, checked in the worker **and** re-checked in the page; covered by a test that pairs a `Production` session and asserts the page never navigates |
| No action outside the allow-list | `ACTIONS` allow-list checked in the page; unknown actions return `blocked`, tested via the real extension |
| A disabled control is never forced | `enabled()` refuses `disabled`, `aria-disabled="true"` and `[inert]` ancestors; proven red by deleting the guard |
| A hidden control is never revealed | `visible()` refuses `display:none`, `visibility:hidden`, `opacity:0`, `hidden`, `aria-hidden`, zero-size |
| The wrong control is never clicked | An ambiguous selector is an error, not a first-match guess; proven red by deleting the guard |
| No credentials, cookies or tokens | Unchanged — the existing credential-API build scan still passes; the probe reads element names and `location.pathname` only |
| No writes to the application | Action set has no type/submit/save; only `click` mutates anything, and the spike target is read-only navigation |
| No new attack surface | `manifest.json` unchanged; no `debugger`, no `chrome.proxy`, no CDP, no broad host permissions |
| Only the companion can ask | Sender must be an extension URL, so a web page that reached the worker is refused |

---

## 4. Evidence

**End-to-end, through the real extension** (`tests/e2e-probe.test.mjs`) — real service worker, real dynamic content-script
registration, real isolated world, fixture served at `https://m2lbdev.bufetat.no`. Playwright launches the browser and
serves the fixture; **it never drives the page under test**. Every click and assertion is performed by the companion:

- see the link → `passed`, `observedRoute: "/"`
- click it → `passed`; the application's own `pushState` handler ran
- confirm the route → `passed` at `/saker`
- confirm the render → `passed` (the fixture renders 300 ms late; the probe waits rather than sleeping)
- assert a wrong route → `failed` with `Expected route /arkiv, observed /saker` (not an error, not a silent pass)
- click a disabled control → `failed`; nothing happened to the page
- four non-allow-listed actions → `blocked`
- a `Production` session → `blocked`; the page stayed at `/`
- zero page errors raised

**Unit** (`tests/automation.test.mjs`) — 9 cases. Two guards were proven by deleting them and watching the suite go red
(2 failures), then restored (9/9 green).

**Regression** — full companion suite: **90 passed, 0 failed** (91 including the suite root). `node build.mjs` passes,
including the manifest security constraints and the credential-API scan.

---

## 5. Outstanding confirmation (manual — needs your hands)

I have no access to the authenticated M2LB UI, so I could not choose a real target element or observe a real click there.
To close this:

1. Sign into M2LB DEV normally (`https://m2lbdev.bufetat.no`), MFA as usual.
2. Confirm the companion popup shows **Connected** and the current page.
3. Open the popup, enter a **read-only** navigation link's visible name (e.g. a menu item that only lists things) and the
   route you expect it to land on.
4. **Run safe navigation probe.**

Expected: `See it: passed | Click it: passed | Route: passed`.

What each failure would mean:

- `blocked — Probes require a paired non-production reporting page` → the session or environment type is wrong, not a policy block.
- `failed — No element matched` or `matched N elements` → M2LB's DOM needs a better selector (add `data-testid`, or select by role+name). **This is a selector problem, not a feasibility problem.**
- `failed — not visible` / `disabled` → you picked a control the application is not currently offering.
- Nothing happens at all, or the content script is absent → *that* would be the policy signal, and would change the verdict.

---

## 6. What this does not yet include

- **Backend → worker transport.** The spike is popup-initiated. For the product, the narrowest safe design is to carry an
  optional pending command on the **existing heartbeat response**: it binds to the already-paired session and profile
  automatically, adds no endpoint, and adds no authentication surface. The worker would POST the result back the same way
  it posts evidence today.
- **Unattended runs.** The companion needs a real authenticated browser session, so this is an *attended* capability by
  construction. That is the correct split: attended companion probes for authenticated M2LB journeys, unattended
  integration tests for everything that does not need a browser identity.
- **Multi-step journeys.** The spike runs one command at a time. Sequencing belongs in BirkNext, not in the page.

---

## 7. Recommendation

Build Critical E2E regression on the companion path, not on CDP. The CDP route is closed by organisational policy and the
closure is not something we should be trying to work around. The companion route uses a mechanism the browser sanctions,
needs no new permission, and is already trusted with this page.

Before building journeys, ask the M2LB team for `data-testid` attributes on the handful of elements the critical journeys
touch. Text and role selectors work, but they break on copy changes and they break on Norwegian/English switches; stable
test ids are the difference between a regression suite that lasts and one that gets muted.
