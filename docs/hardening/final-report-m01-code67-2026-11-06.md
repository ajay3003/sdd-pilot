# M01 – Gradert tilgang (Kode 6/7): static review vs runtime verification and the security matrix (2026-11-06)

This covers both queued briefs: "M01 graded access UX" and "M01 Code 6/7 security verification matrix".

Status legend: **DONE** · **PARTIAL** · **NOT IMPLEMENTED** · **RUNTIME UNVERIFIED**.

## 1. Current architecture and confirmed issues (source verified)

- **Page and API.** Page `/security-classification-review` (`SecurityClassificationReview.razor`) → `api/security-classification` → `ClassificationReviewService.RunAsync`. The run executes:
  1. source scope resolve;
  2. `ClassificationLiveProbe.ProbeAsync` (always called; its `Gate()` refuses without a network call unless there is an approved DEV/QA context, an endpoint, test children, a token, and the environment is not Production);
  3. `ClassificationEvaluator.Evaluate`;
  4. store the result.
- **One mixed "Run safe security checks" action.** Static and runtime are mixed in one run. No runtime prerequisite disables Run, so a static review *already* runs without runtime identities, but the page did not say so.
- **Mixed prerequisite count.** "What's needed to complete this review" counted source items and runtime items as one number. Its lead said only "Runtime authorization checks cannot run yet".
- **No per-control view.** There was no control registry and no static vs runtime matrix. Statuses are per area (`ClassificationArea`) and per check (`ClassificationCheck`, with `Provenance` SourceCode = static, NetworkProbe = runtime).
- **What is already correct and kept:**
  - Pass is set only by the probe; static evidence alone reaches at most "Source verified".
  - Overall is Verified only when every live observation passed.
  - Tokens are never stored or logged; the context is in memory only and validated (no `@`, at most 60 characters).

## 2. Static vs runtime responsibility matrix

| | Before | After |
|---|---|---|
| Static review | Implicit in "Run safe security checks" | Explicit: "Run static security review" when runtime is not ready (`data-mode=static`), with "runtime checks stay unverified (never passed)" |
| Static readiness | Not shown | Ready for static review / Ready with evidence limitations (names missing source roles) / Blocked — insufficient applicable source evidence |
| Runtime readiness | "Live checks: Not run — reason" | Separate step: Approved DEV/QA context, or Blocked — missing prerequisites (reason + count); "never blocks the static review" |
| Runtime execution | Inside the same run | Same backend run (unchanged), only when the live gate allows; otherwise **RUNTIME UNVERIFIED** |
| Prerequisite count | One mixed number | Same total plus a split: "N for the static review (source evidence) · M for runtime verification" |

## 3. Implemented security control matrix

`SecurityControlMatrix` is a documented review rule over the **existing** check ids and areas. It adds no new control record and no new analyzer. It is rendered as `sc-control-matrix` after a stored review, with columns Control · Static evidence · Runtime verification · Result · Residual risk · Next action.

| Family | Existing evidence it groups |
|---|---|
| SEC-01 Code 6/7 classification | Model area (`model-*`) |
| SEC-02 Classification propagation | Pipeline and Guard areas (`cdc-*`, `guard-logic`), `level-change` (NotTested by design) |
| SEC-03 Authorization enforcement | DirectAccess, Search, GraphQL, ChildAccess, Grants and EmergencyAccess areas; live `live-*` probe checks; `parallel-authorization` |
| SEC-04 Protected data disclosure | `access-profile-antidisclosure`, `search-count`, `browser-storage`; Privacy and Caching areas |
| SEC-05 Downstream protection | Consistency area only. No shared control exists; consuming reviews assess their own requirements (stated) |
| SEC-06 Audit and revision evidence | AuditAccess, ReadLogging and Observability areas |
| SEC-07 Failure and recovery | `access-authz-fail-closed`, `access-policy-handler`, `readlog-fail-closed`, `cdc-unknown-level` (no safe runtime check yet) |

**Static states** (from SourceCode checks):
- Evidence found;
- Potential issue (IssueDetected/Fail; not a confirmed defect);
- Partial evidence;
- No supporting evidence found;
- Not assessed (no source in the run).

**Runtime states** (from NetworkProbe checks): Passed (all Pass) / Failed / Partial / Not assessed.

**Result rules:**
- **Passed** only with runtime all passed and no open potential static issue.
- **Failed** on a runtime failure.
- **Not executed** is neither passed nor failed.

**Residual risk** names what is unknown, e.g. "Mechanism found in source; unknown: whether the guard makes the correct decision for each identity (a guard in source is a mechanism, not a verified decision)". **The next action** names the evidence that would reduce the uncertainty, or the owning step.

**Provenance line:** run time · source snapshot id (short) · runtime evidence capture time, or "no runtime evidence (runtime unverified)".

## 4. Before / after workflow

Guided steps (`SecurityReviewWorkflow`) were added: Select scope and evidence → Review security requirements (7 families) → Run static security review → Prepare runtime test context → Run runtime verification → Review security matrix and risks.
- The static step is current when it can run, even while runtime is blocked.
- A completed static review never unblocks runtime.
- It is guided navigation, not a wizard.

## 5. Persistence impact

**None.** The matrix is computed from the stored immutable `ClassificationReviewResult` (its checks and live evidence), so historical runs show their own matrix. No table or migration was added.

## 6. Tests and actual results

- `Services/SecurityControlMatrixTests` (9, synthetic fixtures; no identifier, token or endpoint value). They cover:
  - 7 families traceable to existing checks (a check may support two families);
  - a guard found but runtime not assessed is never Passed (and a CDC mapping is not propagation);
  - no static finding ≠ Passed, and missing evidence is not a defect;
  - a potential issue ≠ a confirmed defect;
  - all-Pass runtime → Passed, a runtime Fail → Failed, NotTested → Partial;
  - static ready despite missing runtime identities, with runtime blocked and RUNTIME UNVERIFIED;
  - a completed static review never unblocks runtime;
  - partial source → "Ready with evidence limitations"; no source → Blocked.
- **Focused run** (SecurityClassification*, SecurityControl*, M01*, SecurityConsumer*, SkoleNaervaer*, TestPack*): **169/169 passed**.
  - The first run had one failure: the split line also appeared when nothing was missing, which broke the existing compact "prerequisites complete" state. It is now shown only when items are missing.
  - An earlier build failed on `@{ }` at code-block level and was fixed.
- **Browser** (`m01-snt-e2e.cjs`, published build, `birknext_gaps`): **11/11 passed** for M01 and Skolenærvær together. On real data:
  - six steps: static review current, runtime context Blocked, runtime **RUNTIME UNVERIFIED**;
  - Run reads "Run static security review" (`data-mode=static`);
  - "0 for the static review (source evidence) · 11 for runtime verification (test context and runtime evidence). Same 11 items";
  - no horizontal scroll at 390px;
  - no state-changing request (Run never clicked), no external host, no page errors.
- **Not verified in the browser:** the matrix itself. No stored M01 review exists in this database, and no run was started, because that would call the probe gate. The matrix is covered by the unit tests above.

## 7. Status per acceptance criterion

| Criterion | Status |
|---|---|
| Static review runs independently of missing runtime prerequisites | **DONE** (it always did in the backend; now explicit, labelled and gated only on source evidence + Target Environment) |
| Runtime verification explicitly protected | **DONE** (unchanged gate; explicit Run; never on page open or readiness) |
| Static never masquerades as runtime | **DONE** (separate columns; Passed only from runtime) |
| Per-control matrix | **DONE** (7 families over existing checks) |
| Separate static and runtime *actions* | **PARTIAL**. One backend run does both when the gate allows. The button says which it will do. A runtime-only run would need a backend change. |
| Static review without any Target Environment | **NOT IMPLEMENTED**. Runs are stored per environment; the API requires `environmentId`. |
| Runtime behaviour of any control | **RUNTIME UNVERIFIED**. No probe was run against a real system in this task. |

## 8. Remaining risks

- **SEC-05 and SEC-07** have no runtime evidence path, and SEC-05 has no shared static control. They stay Not assessed or "Mechanism found", by design.
- **Manual security assessment decisions** are not stored by M01. The next action points to recording them.
- **Analyzer version.** The analyzer version is on the source evidence but not copied into each stored result.
