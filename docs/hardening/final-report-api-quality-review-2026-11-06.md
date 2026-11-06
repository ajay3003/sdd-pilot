# API Quality Review — hardening (2026-11-06)

## 1. Confirmed bugs

1. **A run where nothing was executed because every target was blocked was labelled "Automated review: Failed".**
   - In `ApiReviewPresentation.Result`, any run with 0 assessed targets was `FailedToRun` and `Outcome` turned that into "Failed", whatever the cause.
   - The engine (`ApiReviewEngine.ResolveAccess`) blocks a protected target *before any request* when the mode is None or Unattended, or there is no authenticated context. In that case no `rest-reachability` check and no finding are produced, which is exactly the screenshot ("0 operations executed · 0 source findings").
2. **Run eligibility ignored the selected authentication mode.**
   - `ApiReviewRunEligibility.Evaluate` looked only at `capabilities.AuthenticatedApi`.
   - With a verified proxy session and the effective mode None, Run was enabled and every protected target was then blocked.
3. **Auto resolved to None while an attended session was verified, with no explanation.**
   - The Auto rule (`RuntimeAuthenticationPolicy.Resolve`) chooses None when the *frontend* needs no sign-in and the operation is anonymous-capable. The API is a separate question.
   - The page only said "Authentication None selected" in the capability list.

**Not a defect:** the placeholder builder `ApiQualityReviewPageModelBuilder` is not used by the page.

## 2. Root cause of the failed review in the screenshot

The cause is **B + C: blocked before execution, because authentication was not used**. It is not an execution error.

1. Auto → None (the frontend needs no sign-in).
2. The REST target requires authentication.
3. The engine returns Unavailable → Blocked, sends no request, and marks every operation Blocked.
4. The presentation mapped 0 assessed to "Failed".

The other screenshot values follow directly from that:
- 0 executed;
- 0 of 1 reviewed;
- no contract and no baseline (independent limitations);
- authorization correctness not assessed;
- 3 manual areas / 5 obligations (static).

## 3. Authentication integration findings

**The connection chain:** Target Environment → Authentication (Local HTTPS proxy captures an authenticated API context per profile + context fingerprint) → `AuthenticatedReviewGateway.Resolve(identity)` gives `AuthenticatedApi`.
- The API Quality Review run sends `AuthenticationMode = effective`.
- The engine executes protected operations only for `LocalHttpsProxy` with an authenticated context, and when the mode is Auto/Attended.

**The questions the brief asked:**
- **Is the session eligible?** Yes, through the existing proxy transport, when the mode reaches it.
- **Must the user choose Attended?** When Auto resolves to None, yes. BirkNext never switches to it silently. The secret stays in `TransientAuthenticatedApiContextStore` and is never copied.
- **Target vs BirkNext authentication:** they are distinct. The local session is BirkNext's own; the target credential lives in the proxy.

**Changes:**
- **Explanation** (`ApiReviewPresentation.AuthenticationModeNote`, `aqr-auth-mode-note`): "Auto chose None (…): the frontend needs no sign-in, but the API is a separate question. … blocked before execution — reported as blocked, never passed. A verified attended session is available for this environment: select Attended to use it. Nothing is transferred automatically."
- **Eligibility** takes the effective mode:
  - None/Unattended never makes a protected target runnable;
  - all protected → Run unavailable, with "Select Attended to use the verified session";
  - mixed → runs the public ones and states that the others are blocked;
  - a protected target with a contract still gets the limited static review.

## 4. Previous vs new workflow

The pre-run now starts with six guided steps (`ApiReviewWorkflow`): Select API targets → Configure authentication → Review contracts and evidence → Check review readiness → Run automated review → Review findings and coverage.
- Steps are derived from targets, selection, capabilities, the resolved mode and eligibility. There is no second readiness model.
- One current step.
- The readiness card remains the single action surface for blockers. This is an existing tested rule, so the steps state reasons and add no duplicate links.

## 5. Readiness state matrix

| Situation | Targets | Authentication | Contracts | Readiness | Run |
|---|---|---|---|---|---|
| No API target | Current (link: Endpoint Discovery) | Not assessed | Not assessed | Cannot run | Waiting |
| Protected target, None/Unattended, session available | Done | **Current**: blocked, never passed; select Attended | x of y | **Blocked** | Waiting |
| Protected target, no context | Done | Current: no authenticated context | x of y | Blocked (card links to Authentication) | Waiting |
| Protected target with contract, None | Done | Current | Done | Can run (static contract review only) | — |
| Attended/Auto + session | Done | Done (value never shown or copied) | x of y (limitation, not blocker) | Can run | **Current** |

**Post-run execution states:**
- Completed / Completed with limitations / Partial coverage, as before.
- **Blocked before execution** (new): every target was blocked before any request. Assessment: **Not assessed**.
- **Failed**: tried and executed nothing, or no targets. Assessment: **Incomplete**.
- A mix of blocked and not-tested targets stays Failed.

## 6. Manual review

The obligations stay at 3 areas / 5 details from one source, and each area now states:
- its **purpose**, including "one rejected anonymous request proves nothing" for authorization between roles and tenants;
- the **evidence required**;
- status **Not reviewed**.

BirkNext has no decision model or storage for these. The audit found only report history in browser storage. The page says so, and that the automated run never marks an obligation done. No new storage was added.

## 7. Analysis semantics preserved

- The engine, its read-only execution policy and its access resolution are unchanged.
- No unsafe request or mutation was added.
- Zero execution is never success.
- Historical reports are unchanged. Old blocked-only reports now read "Blocked before execution" instead of "Failed", which is a relabel of the same stored facts.

## 8. Files changed

- `frontend/BirkNext.Web/Models/ApiReviewPresentationModels.cs` (`BlockedBeforeExecution`)
- `Services/ApiReviewPresentation.cs` (Result, AuthenticationModeNote, ManualObligationGuide)
- `Services/ApiReviewPostRunPresentation.cs`
- `Services/ApiReviewTargetResolver.cs` (eligibility mode)
- `Services/ApiReviewWorkflow.cs` (new)
- `Pages/ApiQualityReview.razor` (+ css)
- Tests:
  - `Services/ApiReviewAuthenticationOutcomeTests.cs` (new, 10 tests);
  - `Pages/ApiQualityReviewWorkflowTests.cs`: the assertion that pinned blocked = FailedToRun now expects BlockedBeforeExecution.

## 9. Test results (actual runs, 2026-11-06)

- **Filter:** ApiQuality*, ApiReview* and RuntimeAuthentication*. Result: **359/359 passed** after one fix. The first run had 1 failure: the steps had added a second "Open Authentication setup" link, breaking the tested one-action-surface rule. The step links were removed.
- **The new tests cover:**
  - blocked before execution → "Blocked before execution" / "Not assessed" / 0 executed;
  - tried and executed nothing → Failed / Incomplete;
  - mixed → Failed;
  - None never runnable with a verified session (Attended is runnable);
  - None + a public target runs and states the blocked targets;
  - a protected target with a contract gets the static review under None;
  - the Auto → None note and its absence cases;
  - steps with None and with Attended;
  - 3 areas / 5 obligations, each with purpose and evidence.

## 10. Browser verification

`aqr-e2e.cjs` ran against the published build on the local stack (`birknext_gaps`): **8/8 passed**.
- Six steps; the targets step is current.
- "No REST or GraphQL API target is available for review" is shown in the readiness step.
- With no target, the targets step links to Endpoint Discovery and no authentication choice is offered.
- No horizontal scroll at 390px.
- Run was never clicked: no state-changing request, no external host, no page errors.

**Not verified in the browser:** the None/Attended path. In this database no API target exists, because targets come from this browser's Endpoint Discovery observations, and none was fabricated. That path is covered by the unit tests above.

## 11. Remaining limitations

- **The Auto policy itself is unchanged.** It still chooses None for an anonymous-capable operation when the frontend needs no sign-in. Making Auto prefer an available attended session would change a shared policy used by other modules. The explicit choice plus explanation is the safe fix here.
- **The post-run tab structure is unchanged.** It already has a compact shared summary (Execution / Assessment / Manual / Coverage). Per-tab redesign was not done.
- **No persistence for manual decisions** (see §6).
- **Error-handling and performance tabs** keep their existing not-assessed wording.
