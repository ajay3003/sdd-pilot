# Frontend Quality Review — guided workflow (2026-11-06)

## 1. Confirmed defects and usability problems (source verified)

1. **"Auto resolves to None while the scope is Public + authenticated", unexplained.** The two values come from different sources:
   - The **runtime authentication policy** (`RuntimeAuthenticationPolicy.Resolve`) reads `SignInRequired` from the *effective* authentication (saved override, else the observed requirement). It chooses None for this anonymous-capable operation when sign-in is not required.
   - The **review scope** reads the *saved* `RequiresAuthentication`, or the saved/recommended `FrontendReviewScope`.
   - The page narrowed the scope only when None was *requested*, not when Auto *resolved* to None. So with Auto → None the run reviews public pages only while the scope still says "Public + authenticated".
   - "Attended: Configured" is always reported for the interactive browser method, which made the contradiction look worse.
2. **No orientation.** The pre-run had target, profile, scope, authentication, readiness, evidence, run and details sections, but no step order or single next step.
3. **Not a defect:** the logical-issue grouping, the source/derived/informational split, the WCAG "no pass count" rule and the API ≠ DOM distinction (`AuthenticatedApiAvailable` vs `AuthenticatedBrowserDomAvailable`, post-run `fqr-auth-api/browser/ui`) were already correct and are unchanged.

## 2. Before / after workflow

The pre-run now starts with six guided steps (`FrontendQualityWorkflow`, using the shared `GuidedStepList`):
1. **Select target and scope:** URL · scope · WCAG profile (48 criteria for the Norwegian profile). "The scope is never widened for you."
2. **Configure authentication.**
   - Public only: not needed.
   - Scope includes signed-in pages and the effective mode is None: **Current**, with "Auto chose None (…). … only public pages are reviewed in this run; signed-in pages are reported as not assessed, never passed. Select Attended to review signed-in pages. Authenticated API traffic captured by the proxy is not signed-in page content."
   - Attended: done, with the same API ≠ page-content rule.
3. **Select quality checks.** A required check that cannot run is the current step and is never passed. Optional ones are limitations.
4. **Check readiness.** Ready / Ready with limitations / Blocked, using the existing `FrontendQualityLandingPresentation.Readiness`.
5. **Run frontend review.** Current only when Run is enabled; otherwise it shows the existing `RunDisabledReason`. "Execution completed is not review complete, coverage complete or manual review complete."
6. **Review findings and actions.** "No findings is not a pass, and automated scans do not establish WCAG conformance."

The same explanation also appears under the authentication selector (`fqr-auth-scope-note`).

**The saved scope is never changed automatically.** The earlier behaviour (an explicit None request narrows to Public only) is kept.

## 3. Authentication and review-scope findings

| Fact | Source |
|---|---|
| Attended configuration available | Runtime authentication resolution (always "Configured" for the interactive browser) |
| Authenticated session observed | Browser session / Companion state (`AuthenticatedBrowserSessionAvailable`) |
| Authenticated API traffic available | Local HTTPS proxy context (`AuthenticatedApiAvailable`) |
| Authenticated DOM actually available | `AuthenticatedBrowserDomAvailable` / capability `AuthenticatedBrowserDom` |
| Mode used by the run | `_authentication.Effective` |

API traffic never implies DOM. No cookie or token moves between engines.

## 4. Readiness semantics

These are unchanged and existing; the steps only order them:
- Readiness: Loading / Checking / Ready / Limited / Blocked.
- `CanRun` for Ready and Limited.
- Run gating: target, active engines, browser evidence, live/current-page blockers.

An earlier rule keeps "Required"/"Optional" policy words out of the setup area. The steps use lowercase "required check" and "optional unavailable", with no "Required: x/y" line, and point to Review details.

## 5. Coverage, findings, evidence and accessibility semantics (kept)

- **Logical issues vs source findings:** `FrontendQualityLogicalIssueGrouper` (a cross-engine equivalence registry plus a derived rule key); actionable issues only; source vs derived vs informational kept apart.
- **WCAG:** the Norwegian profile has 48 criteria. Manual-assessment criteria are listed. There is no pass count ("Automated evidence available (not a pass count)").
- **Existing filters:** the Issues tab has search and severity/domain/impact filters; the Evidence tab has search, selects and sort. The coverage table is an engine × policy × surface × prerequisite × result table.

## 6. Files changed

- `frontend/BirkNext.Web/Services/FrontendQualityWorkflow.cs` (new)
- `frontend/BirkNext.Web/Pages/FrontendQualityReview.razor`: steps, and the auth/scope note
- `frontend/BirkNext.Web.Tests/Services/FrontendQualityWorkflowTests.cs` (new, 6 tests)

## 7. Automated test results (actual runs, 2026-11-06)

**Filter:** FrontendQuality*, Wcag* and RuntimeAuthentication*. Result: **871/871 passed**.

**First run:** 2 failures.
- One was my own test text.
- The other was the existing rule "Required/Optional policy is not surfaced in the primary area". The step wording was changed to respect it.

**The new tests cover:**
- Auto → None under Public + authenticated (explained, current step);
- Public only and Attended (no explanation);
- the 48-criterion profile in the target step;
- a missing required engine (current, never passed);
- a missing optional engine (limitation, run current);
- blocked readiness with the run reason.

## 8. Rendered UI verification

`fqr-trace-e2e.cjs` ran against the published build on the local stack (`birknext_gaps`): **12/12 passed** (Frontend Quality Review and Requirements Traceability).
- **Frontend Quality Review:**
  - six steps (target ✓, authentication ✓, checks ✓, readiness ✓, run current, findings waiting);
  - here the effective mode is Attended and the scope Public only, so no mismatch note appears, which is correct;
  - no "Required"/"Optional" in the setup area;
  - no horizontal scroll at 390px.
- **Requirements Traceability:**
  - six steps, with Plan → Tasks Not applicable;
  - the breakdown "16 Constitution rules not cited … · 18 requirements without a plan reference · 32 tasks without a requirement/SC reference" (= 66);
  - requirement → code → test counts "Not assessed" (nothing interpreted);
  - the disabled feature-pack action explains why.
- **Safety:** no state-changing request, no external host, no page errors.

**Not verified in the browser:** the None + authenticated-scope note, because the local data has Attended and Public only. It is covered by unit tests.

## 9. Remaining limitations

- The post-run tabs, coverage matrix columns, the Evidence tab's table layout (testids for its sort/filter controls), history and the Summary's action grouping were **not** redesigned in this pass. They already meet the core semantics (§5), but the brief's fuller workspace redesign remains.
- There is no URL state for the result tab.
- The Auto policy is unchanged (a shared policy). The explanation plus an explicit Attended choice is the fix.
