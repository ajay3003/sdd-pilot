# Performance Test Review: authentication, Test Data, readiness integrity and scenario automation (2026-11-05)

Branch `008-traceability-first`, not pushed.

This covers NEXT37 and the NEXT37b "Shared-Foundation-First" addendum, implemented together.

## A. Audit

The flow was traced end to end: discovery → endpoint auth requirement → step creation → scenario default → step override → effective auth → Test Data → readiness → execution.

**What already exists (kept):**

| Concern | Existing model |
|---|---|
| Scenario-level auth default | `PerformanceTestDefinition.AuthenticationReference` |
| Per-step auth | `StepAuthenticationMode { Inherit, None, Override }` |
| Effective auth | `PerformanceScenarioRuntime.EffectiveAuthentication` (inheritance scenario default → step) |
| Requirement states | `AuthenticationRequirements` (NotRequired / Configured / Missing / IntentionallyUnauthenticated / OverrideOutdated). "None" was already never a confirmation. |
| Executable credential | The proxy-captured Target Environment API credential (`target-environment-api`). Attended sign-in (`target-environment`) is never executable by k6. |
| Readiness | The backend `PerformanceTestReadinessService` (PTR's own engine; not replaced). |
| Persistence | Definitions persist in the backend database. |
| Test Data | `ScenarioDataMapping` with stable `AssetIds`, central Test Data validation. |

**Defects confirmed:**

1. **False Ready.** A scenario using the API credential read Ready while no credential had been captured. Only the run start checked the credential, and the authentication item said "None — no enabled step was observed or declared with authentication".
2. **Unknown passed as fine.** An unreadable Endpoint Discovery history silently skipped the observed authentication requirements; it produced an Optional, non-blocking item.
3. **Observed value used as approved Test Data.** "Use in scenario" stored an observed runtime value (e.g. `{brukerId}`) as auto-filled, and it counted as resolved.
4. **Blockers dropped from the setup steps.** The central-test-data, scenario-contract and endpoint-evidence blockers had no setup step, so "Definition & scenario" could read ready while they blocked.
5. **Candidate card read the wrong auth.** It used only the scenario default: it ignored a step's None or Override, and showed "configured" for non-executable attended sign-in.
6. **Misleading page fact.** "Authentication: None / Referenced" ignored step overrides.
7. **"Already in scenario" was a dead end.** The button was disabled and offered no way to reach the existing step.
8. **Duplicate possible (found in the real browser).** A step added from the OpenAPI contract (`…/{brukerId}/barn`) and the same endpoint seen by Live Discovery (`…/{id}/barn`) had different operation keys. The card read "not added", so the same operation could be added twice.
9. **Stale catalog text.** The runtime-authentication catalog still said BirkNext issues no non-interactive credential for k6.

## B. Authentication architecture

There is still one model; nothing new was added.

- **Inheritance:** Target Environment (the API credential captured by the proxy) → scenario default → step override → effective auth per step. A step override is never overwritten by a scenario action.
- **Readiness credential check:** readiness receives the Target Environment proxy identity (as run start does) and adds an **Execution credential** item using exactly `PerformanceApiCredential.Problem`. "Authentication unresolved — execution blocked" appears in readiness, not only at run start. Without the identity, the item is not assessed and blocks.
- **Authentication item text:**
  - "Configured for N step(s)…" when configured;
  - "Not assessed — the authentication requirements could not be read" when the history is unreadable;
  - "No enabled step…" only when that is true.
- **Unreadable history** blocks as "Authentication requirements not assessed" (Category Definition, status Not assessed).
- **Secrets:** no token is shown, logged or copied. Only references are stored.

## C. UI

- **Scenario authentication** (top of the Scenario tab):
  - **Summary:** "Authentication unresolved — N steps blocked", "N step(s) use attended sign-in — not executable", or all configured.
  - **Counts:** "Scenario default · N require authentication · resolved (configured *and* executable) · unresolved · not executable · overrides".
  - **Actions:**
    - "Use Target Environment API credential as scenario default" (explicit).
    - "Apply the scenario default to N step(s) set to None…", with a preview of the affected steps and the overrides that are kept, then Confirm/Cancel. Only None-but-required steps change.
    - "Configure Target Environment authentication" (deep link).
  - **Unresolved steps:** each listed with Configure, which focuses the step.
- **Review unresolved items:** grouped by cause.
  - "Authentication — N steps affected" opens Scenario authentication.
  - "Runtime values and Test Data — N parameter bindings missing or not approved ({…})" opens the first step.
- **Endpoint cards:** the authentication the operation actually gets:
  - its step override, an explicit None ("authentication unresolved"), or the scenario default;
  - executable or "not executable by the load generator".
- **Configure scenario step:** an operation already in the scenario (matched by operation key or, for REST, the shared observation identity of method plus templated path) offers this instead of a disabled button. It focuses the existing step; no duplicate is created.
- **Readiness Fix:** when an authentication or credential blocker exists, the Fix opens the Scenario tab with Scenario authentication focused (focus, not scroll).

## D. Test Data

- An observed Live Discovery value is a **candidate**. `PerformanceScenarioRuntime.Problem` returns "Observed value is a candidate, not approved Test Data — map approved Test Data to {x} or enter it as a fixed value if it is approved for this environment".
- The step status and readiness both block on it.
- An explicit fixed value remains the person's decision. A step blocked on authentication and on Test Data keeps both blockers; fixing one leaves the other.

## E. Readiness before and after

| Situation | Before | After |
|---|---|---|
| API credential configured, none captured | Ready (blocked only at run start) | **Blocked** (Execution credential) |
| Endpoint Discovery history unreadable | Optional note, Ready | **Blocked**: requirements not assessed |
| `{brukerId}` auto-filled from Live Discovery | Resolved | **Blocked**: candidate, not approved Test Data |
| Central Test Data or contract-drift blocker | "Definition & scenario" could read ready | Counted in the Scenario setup step |
| Authentication item, configured | "None — no enabled step…" | "Configured for N step(s)…" |

## F. Shared foundations (37b)

- **Reused:** Target Environment authentication (proxy credential), the shared credential check, `PerformanceScenarioRuntime` (one rule for step status and readiness), central Test Data, the observation identity used by evidence history, `OperationProgress`/StatusVocabulary.
- **Add selected:** ready, read-only REST operations can be multi-selected and added in one action. Steps share the scenario's configuration: authentication inherits the default, with no per-step approval. Duplicates and the 20-step limit are skipped and reported. State-changing (non-GET), review-required and conflicting operations are never selectable.
- **Approval minimisation:** deterministic step composition stays automatic, as before. Security-sensitive choices remain explicit: the credential default, the bulk apply (behind a preview) and Test Data approval.

## G. Testing

| Suite | Test | What it covers |
|---|---|---|
| Backend `PerformanceTestReviewTests` | API credential without capture | Readiness not Ready; the credential item reads "Authentication unresolved — execution blocked"; run start refuses the same way; not assessed without identity |
| Backend `PerformanceTestReviewTests` | Observed value | Blocks as a candidate; an explicit fixed value does not |
| Backend `PerformanceTestReviewTests` | Setup steps | central-test-data, scenario-contract and endpoint-evidence map to the Scenario step |
| Frontend candidate UX | Effective auth | Inherit / None / API default / attended override |
| Frontend candidate UX | Configure scenario step | Opens the existing step; no duplicate |
| Frontend candidate UX | Contract step vs Live Discovery | The same operation is never added twice |
| Frontend builder | Add selected | GET only; shared configuration; selection cleared; added operations not selectable again |
| Frontend page | Scenario authentication | Counts; None never passes; preview and confirm; overrides kept; nothing saved |
| Frontend page | Readiness Fix | Credential blocker Fix opens Scenario authentication, focused |

Two existing assertions were updated to the corrected behaviour: the evidence panel wording, and the disabled "Added to scenario" button replaced by Configure.

| Run | Result |
|---|---|
| Full frontend suite | **8145 passed, 0 failed** |
| Full backend suite | **5906 passed, 13 skipped, 0 failed** |

**Real browser** (`scratchpad/ptr-e2e.cjs`):

- **Data:** Edge on the M2LB copy (DB `birknext_gaps`, environment `d1c77ff…`, the stored "Baseline check" v13).
- **Scenario under test:** `GET /api/autorisasjon/v1/brukere/{brukerId}/barn`, with an attended sign-in override and `{brukerId}` auto-filled from Live Discovery, plus the recorded history (Bearer observed, HTTP 200).
- **Safety:** localhost only. Writes to runs, trial, network and definitions were blocked, and none were attempted. Nothing was run, trialled, network-checked or saved, and m2lbdev was never contacted.
- **Result: 12/12 checks passed.**

| # | Check | Result |
|---|---|---|
| 1 | Scenario authentication | "1 step uses attended sign-in — not executable by the load generator"; counts "1 step requires authentication · 0 resolved · 0 unresolved · 1 not executable · 1 step override" |
| 2 | The step is named, with Configure | Passed |
| 3 | Review unresolved items | Names the `{brukerId}` binding |
| 4 | Discovery card | "Bearer observed · step override: Target Environment — not executable by the load generator" |
| 5 | Configure scenario step | Focuses `pt-step-h-1`; exactly one barn step. Before the fix (defect 8) the card read "configuration missing" and offered Add |
| 6–8 | Readiness of the saved v13 (backend GET) | Not Ready; names the observed `{brukerId}` as a candidate and the attended sign-in as non-executable |
| 9 | Unsaved discovered drafts | The page asks to save a version first, never Ready. The page opens with an unsaved "Discovered · review required" draft (existing behaviour) |
| 10 | No writes | Passed |
| — | External hosts, page errors | None |

## H. Limitations

1. **No positive path with a real credential.** No proxy credential was captured, and a real one cannot be fabricated. Ready with a valid credential is covered by the existing credential unit tests, not exercised in the browser.
2. **Readiness Fix → focus in the browser.** The UI evaluates readiness only for saved versions, and this check does not save, so this is covered by the component test only.
3. **37b items not implemented:**
   - the full Scenario Composer with per-setting provenance states (Resolved automatically / Inherited / Previously approved / Suggested / Conflict);
   - reuse of Relationship Review confirmations;
   - the tab restructuring into Build / Configure / Readiness / Run & Results;
   - smart recomposition on evidence change beyond the existing discovery drafts.

   The existing tab groups (Define / Execute / Analyze) were kept.
4. **No unattended automation identity.** There is still no automation identity for k6 beyond the attended-session API credential, which acts as the person's own account.
5. **Bearer + None in API Quality Review, Integration Quality Review and Critical E2E.** This was noted in NEXT31; NEXT37 fixed it in Performance Test Review only.
