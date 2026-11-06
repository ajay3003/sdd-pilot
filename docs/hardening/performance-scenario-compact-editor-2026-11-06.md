# NEXT55 — Performance Test Review compact scenario editor (2026-11-06)

Branch `008-traceability-first`. Builds on NEXT51 (101296b2): no authentication verdict is shown without endpoint evidence. Frontend only. The readiness, authentication and runtime-value rules are unchanged: one evaluator, `PerformanceScenarioRuntime`, is shared by the step, the summary, Readiness, Run and the backend start. **No load test, readiness check, validation request or run was started; no traffic reached m2lbdev or any external host.** Nothing pushed.

**Status: partially completed.**

- Done:
  - The compact, step-oriented editor (brief §2).
  - Operation discovery, step configuration, authentication, runtime values and execution readiness are presented separately.
  - Unresolved Bearer authentication and missing required path values are visible as blocking on the row and in the editor.
  - Unloaded discovery evidence is never read as "no authentication needed".
- Not done: the remaining tab simplifications (§6–§8 beyond the Scenario tab) and the explicit draft → v1 save sequence (§10). Details under "Not done".

## Audit — the Scenario editor after NEXT51

| # | Finding | Before |
|---|---|---|
| E1 | Oversized steps (§2) | A scenario of up to three steps showed every step fully expanded. Each expanded step held a status summary list, a reasons list, six action links and two side-by-side cards. The **Request** card held the request view, runtime values, authentication and the request editor; the **Response** card held the expected status, the validation result and outputs. On M2LB's "Baseline check" the one step filled more than two screens. |
| E2 | No focus (§2) | No step was "the one to work on". Longer scenarios started all collapsed, with only a first reason visible. |
| E3 | Mixed concerns (§2, §3, §4) | Authentication, runtime values and request detail were stacked in one card. A tester could not see what the evidence requires next to what is configured. |
| E4 | Status vocabulary (§6) | Rows carried one status ("Ready", "Needs configuration", "Configured · execution blocked"). The Definition / Runtime values / Execution dimensions existed but appeared only inside the expanded step. |
| E5 | Mobile | The scenario overflowed a 390 px screen by 154 px. Causes: grid items at min-content width, long authentication option labels, the runtime-values table, and the authentication facts list. |

The authentication and parameter rules themselves were already correct, from NEXT37b and NEXT51:

- an observed Bearer with nothing configured is `Missing` and blocks execution;
- a missing `{brukerId}` is "Runtime value missing" and blocks;
- the backend re-checks both before a run.

The defect was presentation and focus, not the rule.

## Changes (`Components/PerformanceScenarioSteps.razor`)

### Compact operation rows

Every step is now one row: order and name, **method + path**, source (observed / declared / manual / discovered draft), the delivery status, and three dimension chips: **Defined · Configured · Executable**, each ✓ / ! / ✕ / ○ with the dimension's text as tooltip. The row also shows the **issue count** and a **Configure / Close** button (`aria-expanded`).

- A discovered draft keeps its Accept / Dismiss review on the row. It never runs until accepted.
- A closed row shows its first reason.
- **One step is open by default**: the first enabled step that needs attention, otherwise the first step.
  - That choice is made once and kept while the step exists, so moving or editing never closes the editor in use. This bug was found and fixed during the work.
  - A person's own open/close choice per step always wins.

### Focused editor — five parts as keyboard tabs

Parts: **Authentication · Parameters & Test Data · Request · Response & extraction · Advanced**.

- Keyboard: ←/→/Home/End; `role=tablist/tab/tabpanel`, roving tabindex.
- A part with an open issue carries a "!" flag.
- The editor opens on the first part with an issue: authentication, then parameters, then request. Otherwise it opens on Parameters & Test Data when the operation has runtime values, else Request.
- **Authentication**:
  - **Required by the evidence** (observed by Endpoint Discovery or declared) beside **Effective for this step** (step override, scenario default, or None).
  - The problem, when missing or outdated: "… — execution stays blocked until it is configured".
  - The inherit / none / override selector.
  - The credential facts: source, attended or not, compatible with the load generator, execution credential.
  - When the endpoint evidence has not loaded, the requirement reads **"Not fully known — endpoint evidence not loaded"**, never "No authentication observed or declared".
- **Parameters & Test Data**: a one-line summary per parameter ("brukerId → not configured"), then the existing runtime-value table with every source: approved Test Data, scenario variable, fixed non-sensitive value, previous step output, generated value. Shared scenario variables are unchanged and still reusable across steps.
- **Request**: the request view, with Edit for method, path, content type, query, headers, body and expected status. An Authorization header is still refused.
- **Response & extraction**: expected status, the latest validation-request result (only after one is explicitly sent) and outputs.
- **Advanced**: the Definition / Runtime values / Execution texts, endpoint evidence (current / newer available / not in history) and the observation tracking choice (follow latest / pinned).
- Duplicate, Enable/Disable, Move up/down and Delete stay contextual inside the open step. Drag reordering and dependency-conflict protection are unchanged.
- **No edit is lost when switching parts or steps**: every change is applied to the definition when it is made, and nothing waits in the editor. A test locks this.

### Mobile

- The scenario grid column may shrink.
- Selects and inputs are capped at their container.
- The authentication facts list becomes one column.
- The runtime-values table stacks into one block per parameter, with no nested horizontal scroll.

## Tests

- New in `PerformanceScenarioStepsTests` (4):
  1. **Primary scenario**: two Autorisasjon GETs with an observed Bearer and a missing `{brukerId}`.
     - The first step is open, both rows show their issues, and Executable is never Ready.
     - The editor opens on Authentication (Missing, "execution stays blocked"); the Parameters part is flagged.
     - Scenario readiness is not Ready, and the request and response panels are not shown.
  2. Unloaded endpoint evidence reads "Not fully known — endpoint evidence not loaded", never "not required".
  3. The editor parts are keyboard tabs. An edit made in Request survives switching parts (→, Home) and closing and reopening steps.
  4. With attended Target Environment authentication the step is Defined but not Executable, and the Authentication part stays flagged.
- Updated (layout only; no rule changed):
  - The steps, request/response UI, discovery drafts and page tests now open the part they assert.
  - Multi-step tests open every editor (`Render` opens all; `RenderAsIs` keeps the default).
  - The header test now asserts "one step open: the first that needs attention".
  - The request-card test asserts that authentication is no longer mixed into the Request part.
- Performance subset: **298 passed, 0 failed**; scenario steps 20/20. Full frontend suite: **8255 passed, 0 failed**.

## Browser verification (M2LB copy, environment d1c77ffc…, "Baseline check") — view only, 13/13

Script `next55-e2e.cjs`; screenshots `next55-scenario.png` and `next55-mobile.png`.

1. Compact rows, with one step open by default (`getchildrenforuser`).
2. The row shows **Defined ✓ · Configured ! · Executable ✕** and **1 issue**.
3. No step with unresolved authentication or values reads as executable.
4. The editor has five parts and opens on **Authentication** (flags: Authentication !, Parameters & Test Data !).
5. No request or response panel is shown until asked for.
6. Authentication: **Required by the evidence: "Authenticated requests observed by Endpoint Discovery"** · **Effective: "Override · Target Environment authentication"** · Execution credential ✕ Missing · "Execution blocked — unattended authentication required".
7. The requirement is never "not required".
8. ArrowRight moves to Parameters & Test Data.
9. No overflow at 1440 px.
10. No overflow at 390 px, checked on every part after the mobile fixes.

Also:

- **No state-changing request.** The only POST was the stateless `runtime-authentication/resolve`, which computes and stores nothing.
- No external host contacted; no page errors.

## Not done (remaining acceptance criteria)

- **§5 discovery/acceptance redesign**: the Observed / Declared / Candidate / Already added / Pending review / Accepted / Excluded states are not a new view. Existing behaviour is kept: candidates are added explicitly, drafts need Accept, and dismissed discoveries are remembered.
- **§6/§8 status chips on the other tabs**: Overview, Readiness and Run keep their own (consistent) summaries. The Defined / Configured / Executable chips are on Scenario rows only.
- **§8 tab simplification** (Overview, Workload, Thresholds, Resources, Readiness, Run, Results): not redesigned in this pass.
- **§10 explicit draft → v1 save sequence before readiness**: unchanged. The unsaved-changes indicator and version-bound readiness from NEXT37 apply.
- **§2 explicit "unsaved edits per step" guard**: there is no per-step buffer to lose, because edits are applied immediately. The page-level unsaved-changes state is unchanged.
