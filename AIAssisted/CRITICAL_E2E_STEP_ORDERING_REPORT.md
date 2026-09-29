# Critical E2E step ordering — implementation and verification

Verified 2026-09-29. Starting HEAD: `603fa1639f39353723e7b50930d34ced8ad1387a`.

## Audit and behavior

| Requested item | Result |
| --- | --- |
| A — starting HEAD | `603fa1639f39353723e7b50930d34ced8ad1387a`; repository initially clean. |
| B–C — persistence and original order | `CriticalE2EFlowDefinition.Steps` is a JSON array in `App_Data/critical-e2e/flows.json`. Its position is the persisted execution sequence. `StepId` identifies a step; it does not determine order. |
| D — migration | None. No duplicate sequence concept or database change. |
| E — legacy preservation | Legacy JSON load/save test passes. All 16 real definitions, including 15 smoke/diagnostic flows, compare equal before and after browser smoke. |
| F — identity | Existing valid IDs retained; newly inserted steps receive full GUID-based IDs immediately. Missing/duplicate legacy IDs receive unique draft IDs without changing array order or the source definition. The store's existing ID normalization is retained. |
| G — Blazor keys | Cards previously had no `@key`; now keyed by `StepId`. Disclosure state and picker metadata follow identity. Picker metadata/reference dictionaries previously used indexes; now use IDs. |
| H–I — moves | One shared draft helper removes and reinserts the same immutable step record at its destination. Up/down move exactly one position; boundary controls disabled; no wrapping. |
| J–L — inserts and append | Before/after insert at the selected array position. Add step still appends through the same factory. Browser default remains Click with an empty selector; integration default remains HTTP GET with an empty expectation. |
| M–O — drag UX | Only the visible grip is draggable. Native drag/drop uses the same move helper. A line above the destination card indicates insertion before it; a bottom target supports moving to the end. No order change until drop. Escape/aborted drag leaves order unchanged. Native dragover cancellation requires no Blazor hover callback or server request. |
| P–R — save, cancel, dirty state | Existing explicit Save flow/Cancel pattern retained. A detached draft serializes the ordered array on Save; Cancel discards it. No existing dirty indicator or optimistic concurrency mechanism was found; no separate system introduced. |
| S–V — field preservation | Selectors, actions, Fill values, expected text, final-assertion flags and other metadata move with the complete step record. Verified by record identity/deep equality tests and real API round-trip comparison. |
| W–X — final assertion | Semantics A: business-success assertion regardless of position. `HasFinalAssertion` uses `Any`; the runner checks flagged results after sequential execution. Moving it into the middle or first is allowed. Existing missing-final-assertion validation remains in effect. |
| Y — execution and order validation | Runner already iterates the saved array directly. C/A/B persistence/reload/runner test verifies that exact trace. Array positions are inherently contiguous and unique: missing, sparse or duplicate order integers cannot exist. Existing save-time normalization handles missing/duplicate IDs without sorting; no new order-field validation is applicable. |
| Z — historical evidence | History renders recorded `StepResults`, independent of current definitions. Active and archived A/B/C snapshots remain A/B/C after the definition becomes C/A/B. Editing does not call archive/clear/run APIs or rewrite history. Existing sanitized evidence format is retained. |
| AA — flow types | Critical and Smoke / diagnostic share this editor and ordering helper. Classification is unchanged; parameterized frontend/backend tests cover both. |
| AB–AC — accessibility and focus | Descriptive button names, disabled boundaries, polite live status. Grip is excluded from tab order because keyboard users use move buttons. Focus follows the moved control; at a boundary it moves to the enabled opposite control on the same card. Insert focuses Action; remove focuses the adjacent Action or Add step. Structural editing and mode changes are disabled during a pending browser pick. Reorder otherwise works offline. |
| AD — responsive behavior | Toolbar wraps; narrow-screen controls have 44px minimum height. Fields remain within cards at all four requested widths. Native touch dragging depends on browser support; touch move controls are the verified fallback. |

## Tests and builds

| Requested item | Result |
| --- | --- |
| AE–AH — move up/down, insert before/after | Passed component tests and real-browser editor checks. First/last insertion and append also covered. |
| AI — drag | Passed component drag/drop and abort tests; real Chromium native pointer drag, scrolled viewport, visible target line and Escape checks passed. |
| AJ — preservation | Passed selector/value/expected/flag tests, keyed disclosure-state test, pending-picker/metadata test, unsaved-ID test and remove-after-reorder test. |
| AK–AL — save/reload and cancel | Passed component and live-backend browser checks. |
| AM–AN — execution/history | Passed saved C/A/B execution trace and active/archived historical immutability tests. History file remained byte-identical during real editor smoke. |
| AO — focused backend | 131 passed, 0 failed (baseline 128 passed). |
| AP — focused frontend | 79 passed, 0 failed (baseline 63 passed). |
| AQ — Critical E2E regression | Combined focused suites: 210 passed, 0 failed. Includes existing runner, picker, readiness, flow-kind, coverage and history tests. |
| AR — full backend | 2,910 passed, 6 skipped, 0 failed; total 2,916. Initial sandbox run failed process/network-sensitive tests; normal-permission rerun passed. |
| AS — full frontend | Component/unit project: 4,347 passed, 0 failed. Full solution additionally ran 17 general Playwright tests: 2 passed, 15 failed. Thirteen failures report occupied backend port 5000; two FQR layout checks require a configured target. Full solution is therefore **not green**. |
| AT–AU — Debug/Release | Both backend and frontend solutions built successfully in Debug and Release. Existing warnings remain. |
| AV — responsive | 1440, 1100, 768, 390: document scroll width equals viewport width. Screenshots inspected at desktop/mobile. |
| AW — accessibility | Editor axe WCAG 2 A/AA, 2.1 AA and 2.2 AA: zero violations. Keyboard Enter move and logical focus checks passed; 390px touch move fallback passed. No claim of a manual screen-reader audit. |
| AX — real browser smoke | Used Gradert tilgang / Uautorisert bruker kan ikke åpne gradert barn through the live local backend. Moved SØK down/up and assertion up/down; inserted/removed temporary steps; verified drag, Cancel, Save/reload. Restored exact original order and fields: Barn → Fill lars → Start søk → Lars Ola Testbarn – Åpne profil → assertion. No attended automation was started. |

## Delivery and limitations

| Requested item | Result |
| --- | --- |
| AY — files | `frontend/BirkNext.Web/Models/CriticalE2EFlowDraft.cs`; `frontend/BirkNext.Web/Pages/CriticalE2EFlowEditor.razor`; its `.razor.css`; `shared/CriticalE2EContracts.cs` (ordering documentation only); frontend and backend `CriticalE2EStepOrderingTests.cs`; this report. All paths are relative to `AIAssisted`. |
| AZ — commit | Feature-only commit hash is provided in the delivery response and Git history. |
| BA — whitespace | `git diff --check` passes for the feature. |
| BB — final status | Feature changes committed separately. Concurrent unrelated dependency-review changes remain in the shared checkout; they were not staged or committed by this task. |
| BC — limitations | General Playwright solution failures above remain. Native touch drag is browser-dependent; move controls are available and tested. No new optimistic concurrency or dirty-state mechanism. No new raw selector/value history snapshot format. |

Validation used an isolated checkout at the starting HEAD plus these feature files after concurrent unrelated edits appeared in the shared checkout. A stale physical scoped-CSS bundle was preserved outside the project to unblock baseline builds. The isolated checkout also required regenerating the existing axe catalogue because its byte-level build check is sensitive to Windows line endings; no catalogue content change is part of this feature.

Local evidence is in the parent workspace: `step-order-baseline-*.log`, `step-order-focused-*.log`, `step-order-full-backend-unrestricted.log`, `step-order-full-frontend.log`, `step-order-full-frontend-solution.log`, `step-order-build-*.log`, `step-order-browser.cjs`, `step-order-browser.log`, `step-order-browser-result.json`, and `step-order-{1440,1100,768,390}.png`.
