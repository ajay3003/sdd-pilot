# Target Environments → Authentication: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

The aim was to replace the long, configuration-heavy Authentication tab with a guided flow:

> **Detect → Validate → Populate safe missing settings → Start authentication → Verify session → Continue to Endpoint Discovery**

All technical functionality stays available, collapsed under **Advanced Authentication Diagnostics**.

## A. Audit findings (root causes)

1. **Routine "differences" needed a decision for every field.**
   - `AuthenticationSettingsChecks.Evaluate` marked every row `Undecided`, including `SafeChange` rows where nothing was saved.
   - So the four Entra values (provider, authority, tenant, client ID) each needed "Use detected value", then "Apply selected changes" (into the draft), then "Save changes".
   - No trust policy, provenance, or record of a rejected proposal existed.
2. **The provider was a special case.**
   - `EffectiveAuthentication` already uses an observed provider directly, without saving.
   - Copying it into configuration turns evidence into a "Configured override" when the profile predates override tracking (`ConfiguredOverrides == null`).
3. **No single next action.**
   - Configuration, verification and testing context were shown as several equal-weight cards.
   - The prerequisite cards (certificate, proxy, browser) and the large comparison table sat above anything a person could act on.
4. **Found in the browser run** (fixed):
   - The "Dedicated browser" step showed Done while the proxy was not running (the live prerequisite was `NotRequired`).
   - "Don't use" on a redirect-URL suggestion did not stick.
   - A saved but never-checked configuration offered no next action.

## B. Configuration: detection, validation, autofill, conflicts, persistence

`Services/AuthenticationAdoption.cs` is a pure service; every rule is unit-tested.

**Trust policy.** A detected value is adopted without a person only when all of these hold:

- **Same target:** the detection was made for this environment's own Frontend URL (same origin).
- **Confidence:** at least **High**.
- **Authority:**
  - https on a Microsoft identity platform host (`login.microsoftonline.com` and its national clouds, `*.ciamlogin.com`, `*.b2clogin.com`);
  - its tenant segment agrees with the detected tenant.
- **Tenant:** a concrete tenant (GUID or `*.onmicrosoft.com`). `common`, `organizations` and `consumers` are multi-tenant sign-in, so they are a suggestion only.
- **Client ID:** a GUID.
- **Redirect URLs:** only URLs on the target's own origin are added; URLs on other origins need review.

**Outcomes per setting:**

| Outcome | When | What happens |
|---|---|---|
| `AutoAdopt` | Saved value empty, detected value passes the trust policy | Populated and saved, with provenance |
| `Equal` | Saved and detected values match | Not shown |
| `Conflict` | Saved value differs from the detected one | Saved value kept; flagged for review |
| `Suggestion` | Detected value is untrusted or low confidence | Shown with its reason |
| `Rejected` | A person already declined this exact detected value | Not proposed again until detection finds a different one |
| `NotDetected` | Nothing detected | Saved value kept |

- **Provider:** never copied (it is "Observed and used directly").
- **Legacy override tracking:** before the first adoption, legacy override tracking is made explicit. What a person saved stays an override; adopted values do not become one.

**Persistence:**

- Adoption runs after a successful detection and only while no draft is open. It saves through the existing `IFrontendAnalysisSettingsService.SaveAsync`.
- Provenance is stored in `FrontendAnalysisProfile.AuthenticationAdoption`:
  - per value: setting, value, source (Detected / Reviewed / Manual), detection time, validation outcome, previous value;
  - rejected proposals.
- This metadata sits **outside** `Authentication`, so recording a decision never changes the fingerprints that proxy sessions and sign-in checks are bound to.
- Adoption is idempotent: a second run adopts nothing and adds no provenance.
- The adopted values come from that same detection, so the detection is re-bound as current. It does not turn stale, and a sign-in check can be recorded without detecting again.

**Conflict review.** Only conflicts and suggestions are shown, as **Configuration needs review**. Each offers:

- **Keep existing** (or **Don't use**), recorded as rejected;
- **Use detected value**;
- **Enter custom value**;
- **Restore previous value**.

Each decision is saved immediately: the explicit choice is the approval.

## C. UI

`Components/AuthenticationWorkflowPanel.razor` sits at the top of the tab:

- **Header:** environment, Frontend URL, provider, method, session state.
- **Mode:** **Attended login** or **Unattended authentication**, kept separate.
- **One primary status** with one next-action button. Examples:
  - "Configuration needs review"
  - "Configuration validated — Local HTTPS proxy: Not running"
  - "Waiting for sign-in"
  - "Signed in — Verify the session"
  - "Signed in — Session verified"
  - "Session expired — Sign in again"
- **Three dimensions** as shared `SemanticStatusBadge`s: Configuration, Session, Testing context.
- **Numbered steps.** Each has a state, one sentence and at most one action.
  - **Attended with proxy:** Configuration → HTTPS inspection certificate → Local HTTPS proxy → Dedicated browser → Sign in with MFA → Verify session → Continue to Endpoint Discovery.
  - A step that depends on an unfinished earlier step waits and says why ("Start the proxy first").
  - The managed-Edge and manual-only methods show their own, shorter step lists.
- **Detected configuration summary:** "Authentication configuration detected · Microsoft Entra ID · Tenant: Configured · Application: Configured · Status: Ready for authentication setup".
  - **Configuration details** (collapsed): value, source ("Detected and validated", "Detected, accepted by a person", "Manual override"), validation result and last-detected time.
  - The provider is always shown as people read it ("Microsoft Entra ID"), never as the enum name.
- **Loading:** a busy action disables the next-action buttons (no duplicate starts) and shows the shared `ReviewStageList` with a contextual message.

The previous surface remains complete inside **Advanced Authentication Diagnostics**, collapsed by default:

- readiness panel and prerequisite cards;
- the configuration form, including edit mode;
- runtime verification and the manual sign-in check;
- authenticated testing context;
- the proxy and Edge runtime panels;
- headless diagnostics.

The four-row "Authentication differences" table was removed.

## D. Authentication flows

- **Attended.** Steps are derived only from the existing pane state and prerequisite cards (`AuthenticationWorkflow`); there is no second readiness model.
  - A configured provider is not a session.
  - A launched browser is "Waiting for sign-in", not signed in.
  - A detected session must be verified ("a redirect or a cookie alone is not proof of authorization").
  - BirkNext never asks for a password or MFA code.
- **Unattended.** Shows only Configuration → Unattended identity → Verify access → Endpoint Discovery.
  - Points to System Settings → Authentication.
  - States that the browser application's client ID is not a service identity.
  - Never shows secrets.

## E. Readiness integration

Endpoint Discovery is the last step:

- "Source-only available" while there is no session;
- "Authenticated discovery available" once a session exists.

The AQR, IQR and Performance readiness rules were **not changed in this pass**. Rule 9 of the brief (Bearer required and no context → Blocked) is part of the queued Performance Test Review work; see G.

## F. Testing

**Automated:**

| Suite | Result |
|---|---|
| New `AuthenticationAdoptionTests` | Trust policy, conflicts, rejection memory (including redirects), Use / Custom / Restore, idempotence, provider never copied, explicit override tracking |
| New `AuthenticationWorkflowTests` | Step order, next action, proxy before browser, waiting vs signed in, verify, expired, conflict first, unattended isolation, saved-but-unchecked |
| 4 new page tests in `AuthenticationApplyDraftTests` | Detection fills validated identifiers with provenance; conflict kept and remembered; Use detected keeps the previous value; guided panel leads with diagnostics collapsed and modes separated |
| Removed (obsolete) | 17 tests that exercised only the removed "Use detected → Apply selected → Save changes" table and draft-apply mechanism |
| Authentication / settings / proxy / Edge / detection groups | 1058/1058 passed |
| Full frontend suite (`dotnet test frontend/BirkNext.Web.Tests`) | **8085 passed, 0 failed** |

The 17 removed tests are listed here because they are gone from the suite:

- `AuthenticationApplyDraftTests`: 9 tests, including PopulatesDraft, PartialDetection, UnsavedIndicator, Cancel/Save of the applied draft, AuthChange, TargetCheck and ManualEditDiverging.
- `AuthenticationCardStateTests`: 2.
- `AuthenticationSettingsCheckTests`: 1.
- `AuthenticationTabHierarchyTests`: 1.
- `TargetApplicationAndAuthenticationSeparationTests`: 4.

`ManagedEdgePreflightUiTests` was updated: its non-GUID tenant and client ID are now correctly kept for review.

**Real browser** (`scratchpad/auth-e2e.cjs`): **25/25 passed**.

- **Setup:** Edge via Playwright against a published Release web build (:5199) and an isolated API (:5099, throwaway database).
- **Isolation:** the target is a `.invalid` placeholder, and the detection endpoint is answered by the script with a controlled Entra response. Nothing reached a real target, Microsoft or Azure, and no external host was contacted.
- **Scenario A — nothing saved:**
  - detect;
  - authority, tenant, client and same-origin redirect filled in, with provenance;
  - provider not copied;
  - only the other-origin redirect needs review;
  - "Don't use" is remembered;
  - values persist across reload;
  - diagnostics collapsed but available;
  - unattended steps isolated.
- **Scenario B — different client ID saved:** "Configuration needs review"; the manual value is preserved while empty values are filled in.
- **Scenario C:** 390 px width with no page-level horizontal scroll.

## G. Remaining gaps

| Item | Status |
|---|---|
| Live Microsoft sign-in, proxy start, Edge launch, session verification | **Not verified.** Never executed in tests (no real target or Microsoft calls). The step transitions are verified with controlled state. |
| Rule 9: AQR / IQR / Performance blocking on unresolved required auth | **Not changed here.** Covered by the queued Performance Test Review brief (scenario-level auth; Bearer + None → Blocked). |
| Unattended identity status in the panel | System Settings → Authentication's unattended identity is not yet read into `UnattendedIdentityStatus`. The panel shows "Not configured" with a link; it never claims verification. |
| Endpoint Discovery evidence-type separation (source / browser / authenticated / unauthenticated) | Not changed in this pass. |
| Copyable sanitized diagnostics | The existing "Copy Diagnostics" (System Settings) is unchanged; no new export. |
| Cancellation of a running detection | Not added; duplicate starts are prevented. |
