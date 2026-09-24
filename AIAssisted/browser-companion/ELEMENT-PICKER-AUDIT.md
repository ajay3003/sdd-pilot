# Browser Companion DOM evidence and attended flow authoring (2026-09-24)

**Classification: SMALL EXTENSION.** Companion already had element-level DOM access on approved pages, a typed
command/result transport, typed selectors, and a resolver that rejects ambiguity. Browser Discovery had aggregate
evidence only. The missing piece was an on-demand, live element descriptor. That was added as the `PickElement` command.
No second DOM scanner was added.

## What exists and where

| Question | Finding (verified in code) |
|---|---|
| DOM access | Yes. `content.js` runs in the ISOLATED world, registered dynamically only for the paired environment's approved origins, top frame only (no `allFrames`). |
| Evidence traversal | `lib/dom.js` makes full-document `querySelectorAll` **counts** (nodes, depth, interactive, forms, iframes, images, headings, landmarks, duplicate ids, hidden focusable, dialogs, positive tabindex). No element list is built. |
| Accessibility evidence | `lib/a11y.js` gives rule id, count and ≤5 structural selectors per rule (`sanitize.selectorFor`: tag, safe id, role, ≤2 classes, `nth-of-type`, 4 levels). `lib/axe-evidence.js` keeps rule id, outcome, WCAG ids and node **count**; axe nodes never leave the page. |
| Sent to BirkNext | `BrowserPageEvidence`: the summaries above, sanitized title/URLs, perf/runtime/Blazor summaries. No HTML, no text content, no values, no role/name/label/test id per element. |
| Persisted | Backend: memory only (`BrowserCompanionService`). Frontend: `PageAnalysis.BrowserEvidence` in localStorage `birknext:endpoint-discovery`. |
| Reusable for selectors? | Not as primary selectors: a11y selectors are structural and `nth-of-type`-heavy and meant as evidence. `selectorFor` is reused only as the last-resort Css candidate. |
| Full DOM leaves the browser | No, neither before nor after this change. |

## Critical E2E action layer (as implemented)

- **Actions**: Navigate, Click, Fill, Select, WaitForVisible/Text/Route, AssertVisible/Hidden/Text/Value/Route, ReadValue. Mutating actions run once; observations are polled until the timeout.
- **Selectors**: one per step, with no fallback list. Supported kinds:
  - TestId: `data-testid` only.
  - Role + name: role from `ROLE_TAGS` (explicit role or native tag); name = aria-label → aria-labelledby → textContent. Not the full accessible-name algorithm: `<label>` and `title` are not used for the name.
  - Label: aria-label, wrapping `<label>`, `label[for]`. Not aria-labelledby.
  - Text: exact after trim, case-insensitive, on links/buttons/tabs/menuitems/headings/status.
  - Css: any selector, not code; invalid CSS is an error.
- **Ambiguity**: visible matches are preferred; zero matches or more than one is an error, never the first match.
- **Visibility and state**: Click/Fill/Select refuse hidden, disabled, inert and read-only controls.
- **Blazor input**: Fill/Select use the native prototype value setter, then bubbling `input` and `change` events. The value is then read back and verified.
- **Page identity**: each command carries `TargetOrigin`, `PageId` and `ContentScriptInstanceId`, and is bound to exactly one live page. Steps store no expected route; route checks are explicit AssertRoute/WaitForRoute steps, comparing pathname exactly.
- **Navigation**: SPA pushState changes are detected by a 500 ms route poll plus the navigation tracker. Steps wait for an approved visit.
- **Frames, shadow DOM, popups**:
  - iframes: counted only; no frame targeting.
  - Shadow DOM: not traversed. A pick inside an open shadow root identifies the host.
  - Auth hosts: `login.microsoftonline.com`, `mcas.ms` and similar are never approved origins.
  - Popups and new tabs: they get the content script only when on an approved origin. Two live pages block both runs and picking.
- **Manual steps and attended states**: there is no manual-action step type and no "waiting for tester" run state. Run states are NotRun, Running, Passed, Failed, Blocked, Cancelled; command states are Pending, Claimed, Running, Passed, Failed, Blocked, Expired, Cancelled.
- **Capabilities**: before this change the extension reported only its version string.

## Added

- `PickElement` is authoring only; `ConfigurationProblem` rejects it as a flow step. It rides the existing heartbeat transport and inherits all existing guards: profile, non-production (backend, worker and page), approved origin, live `PageId`, and one command in flight. The content script also requires the same content-script instance.
- `lib/picker.js`: overlay and banner, swallowing pointer and activation events. Esc cancels, Enter picks the focused element, Tab is not trapped, and everything is removed on exit.
- Candidates are TestId → Role+name → Label → Text → Css, each counted with `automation.matches`, the same pool the replay resolver uses. The recommendation is the first unique non-Css candidate, else a unique Css, else none.
- Names that the sanitizer would redact are never used as selectors. No field values are read.
- `CompanionElementDescriptor` lives in `shared/CriticalE2EContracts.cs` and is sanitized again on the backend.
- The heartbeat now reports `capabilities: ['element-pick']`. Older builds report none; they are refused at dispatch, and the editor button is disabled with the reason.
- Pick lifetime is the timeout plus the normal command lifetime (45 s + 45 s by default). Other commands keep 45 s.
- Worker fix: a command is delivered to the tab of its `PageId`, not to the last page that announced itself.
- Editor:
  - "Select from browser" on each step that targets an element.
  - A Role selector can now be authored as role + name. Previously it had only a Value box, so a Role step saved from the UI failed with "Unsupported role".
  - Draft edits survive parent re-renders.

## Open

- The real M2LB smoke test is done; see the section below, which supersedes this list where they differ.
- Not implemented:
  - selector fallback lists;
  - per-step expected route (the pick knows the route and shows it, but it is not stored);
  - manual-action steps and a waiting-for-tester state;
  - frame targeting;
  - shadow-DOM resolution;
  - the full accessible-name algorithm.

## Real M2LB DEV smoke test (2026-09-24, attended)

Run against `https://m2lbdev.bufetat.no`, on `/admin/general-roles` and `/`. One paired tab, driven through the real `pick-element` and `run` endpoints.

| Element | data-testid | Recommended | Notes |
|---|---|---|---|
| input "Søk etter roller" | none | Role textbox + name | Label also unique. |
| input "Nytt rollenavn" | none | Role textbox + name | CSS `input#hD-TQioy7k`: the generated id changes on every page load. |
| input "Beskrivelse av ny rolle" | none | Role textbox + name | Generated id, as above. |
| button "Opprett ny rolle" (picked with Enter) | none | Role button + name | Text matched 2 elements. |
| button "Start søk" | none | Role button + name | |
| role row `div[role=listitem]` "Velg rolle testrolle" | none | was a `nth-of-type` CSS path | That path **broke after the search re-render**. The row's aria-label survived it. |

**Results:**
- **data-testid:** 0 of 6 elements had one. 5 were identified by Role + name; the rows fell to CSS before the fix.
- **Fill and Blazor binding:** filling the search box made Blazor filter the list ("Admin - Generell" gone) and clearing it restored the list, so the value reached Blazor's model, not just the DOM.
- **Ambiguity:** `div[role=listitem]` was refused with "Selector matched 3 elements", and nothing was clicked.
- **Two tabs:** picking was Blocked, and the editor status showed the same reason.
- **SPA route change:** a user navigation from `/admin/general-roles` to `/` kept the same content-script instance, and the route was updated.
- **Wrong page:** 3 page-specific selectors resolved to nothing on `/`. The step then reports **Failed** ("No element matched"), not Blocked.

**Fixes, from evidence:**
1. `listitem` role in the resolver, and a click inside a row resolves to the row.
2. A CSS path cut off at the 160-character cap is no longer offered (it matched 0 on M2LB).
3. **Navigate-by-route rebinds to the same tab's reloaded page.** Before the fix, every step after a Navigate was Blocked: "The page this step was bound to is no longer open". Only the run's own Navigate rebinds, only in the same tab and origin; any other reload is still stale.

The final flow passed end to end: Navigate → WaitForVisible → Fill → AssertValue → AssertHidden, recorded in the run history.

**Decisions:**
- **Expected-route binding:** recommended as the next hardening step. It would make a wrong page a Blocked prerequisite, not an application Failed, and protect generic names such as "Lagre". It was not built, because no cross-page match was observed.
- **data-testid:** recommended for dynamic list rows and repeated generic actions ("Lagre", "Slett") in critical journeys. Not needed for labelled inputs or uniquely named buttons.
- **Fallback selectors:** not needed.
- **iframes and shadow DOM:** none met on the pages tested.
- **Still untested:** a navigation caused by clicking a link in the flow (no safe link was available), and native `<select>` / custom combobox (none found on these pages).
