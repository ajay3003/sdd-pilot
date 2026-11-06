# Integration Quality Review — guided workflow (2026-11-06)

## 1. Confirmed UX defects (source verified)

- **Unexplained counts.** IQR said "19 integrations relevant · 15 resolved · 4 need your decision" (25/16/9 in the brief's screenshot). The Integration Catalog said "2–3 relevant proposals" and "63 technical relationships". The counts were never explained, and they count three different populations:
  - **IQR subjects** are every enabled catalog integration, plus integrations discovered from topology evidence (Strongly supported / Resolved / Observed flows) and enabled Connected Systems (`IntegrationReviewEvidence.Compose`).
  - **Catalog proposals** are Test Pack templates that are not configured and have recognized source evidence.
  - **Technical relationships** are unconfigured Source Analysis candidates.

  None of these should equal another.
- **Three readiness answers presented without structure.** Static review readiness (`CanRun`/`Headline`), configuration completeness (decisions, contracts) and active verification readiness (journeys + environment permission) were already computed separately, but nothing showed their order or their independence.
- **Duplicated actions.** "Confirm mapping" appears three times and "Run review" twice (Overview and Review view). No step order guided the user.
- **Not a defect:** no placeholder readiness builder feeds this page. `QualityReviewPageModelService` is not used by IQR.

## 2. Previous vs new workflow

**Before:** the run decision, the light summary, the readiness pair, the attention list and a Run bar were stacked without an order.

**After:** guided steps sit at the top of Overview: Select integration → Review evidence → Resolve integration setup → Configure contracts → Run static review → Test & verify. A "What these counts mean" disclosure explains the five populations. The existing sections are unchanged below; the steps link to them.

## 3. State model

The steps are derived only from values the page already computes: `IntegrationReviewReadiness`, `IntegrationReviewLightPresentation.Classify`, `IntegrationReviewPrerunPresentation.NeedsAttention`, the journey summary and the environment's execution permission.

| Step | Done | Current | Blocked / Waiting |
|---|---|---|---|
| Select | integrations in scope | none (link to Integration Catalog) | — |
| Evidence | integrations in scope (shows bound source snapshots and runtime available/total) | — | no scope |
| Resolve | no open decision ("resolved is not runtime verified") | N need your decision (link: the mappings attention action) | no scope |
| Contracts | contract evidence for every topic | "Contracts: Not configured / x of y" (link: Manage contracts) | no scope |
| Static review | — | `CanRun`: Reviewable / Partially reviewable, with the limitations stated | Blocked with the first backend reason when it cannot run |
| Test & verify | never Done here | — | Blocked when the environment does not allow execution or no journey is executable; Waiting when executable journeys still need confirmation |

Only the first current step is current. Configuration decisions come first; the static review still says it can run with limitations, so a limitation never blocks it.

## 4. Count definitions (shown on the page)

| Count | Definition |
|---|---|
| Integrations relevant here | Enabled catalog integrations + topology-discovered + enabled Connected Systems |
| Resolved / need your decision | The light classification of those (mapping, source evidence, contract) |
| Integration Catalog proposals | Test Pack templates not configured yet; not reviewed until configured |
| Technical relationships | Source Analysis relationships: evidence, not integrations |
| Journeys executable | Test Pack journeys environment-wide with execution prerequisites met; active verification only |

## 5. Shared components reused

`GuidedStep` / `GuidedStepList` were introduced for Connected Systems and the Integration Catalog, and are reused here unchanged.

## 6. Files changed

- `frontend/BirkNext.Web/Services/IntegrationReviewWorkflow.cs` (new)
- `frontend/BirkNext.Web/Pages/IntegrationQualityReview.razor`: steps + count definitions in Overview
- `frontend/BirkNext.Web/Pages/IntegrationQualityReview.razor.css`
- Tests:
  - `Services/IntegrationReviewWorkflowTests` (new, 6 tests): no scope, 9 of 25 open decisions, a genuine prerequisite blocking the static review, active verification gating, "resolved is not runtime verified", and limitations never reading as "0 limitations";
  - `Pages/IntegrationQualityReviewWorkflowTests` (+1 render test).

## 7. Evidence and decision invariants preserved

- No mapping, identity or proposal is confirmed by the steps; they only link to the owning place.
- Static evidence is never runtime verification.
- Active verification is never enabled by reviews or evidence.
- Saved run history, its disclosure and its wording ("does not change the current readiness") are unchanged.
- The identity conflict is still decided only in Relationship Review.

## 8. Automated test results (actual runs, 2026-11-06)

- **Integration / IQR / Authentication / Connected Systems set:** 778/778 passed. It includes the 6 new step tests and the new render test; the run log was checked to confirm they ran.
- **After the wording fix:** IQR-focused set (`IntegrationQualityReview*`, `Iqr*`, `IntegrationReview*`) 159/159 passed. It includes the new "never 0 limitations" test.
- **Browser re-run after the fix:** `iqr-e2e` 10/10 passed. The static step now reads "can run now with limitations (see what needs attention below)".

## 9. Browser verification

`iqr-e2e.cjs` ran against the published build on the local stack (`birknext_gaps`): 10/10 passed.
- **Steps:** six in order (select ✓, evidence ✓, resolve current, contracts waiting, static waiting, verify blocked).
- **Counts:** the resolve step uses the same count as the light summary (19 relevant · 15 resolved · 4 need a decision).
- **Readiness:** the static step shows the same label as the readiness card ("Partially reviewable").
- **Active verification:** blocked with "not enabled for this environment".
- **Layout:** count definitions present; no horizontal scroll at 390px.
- **Safety:** no state-changing request, no external host, no page errors.

This run exposed one wording bug. The static step said "can run now with 0 limitations" when the backend headline was "limited" but no reasons were listed. It was fixed and re-verified (§8).

## 10. Remaining limitations

- The duplicate "Confirm mapping" and "Run review" controls inside the existing sections are kept. Tests and deep links depend on their testids. The steps now give the order, but the deeper sections were not restructured.
- The post-run result tabs are unchanged; this task covers the pre-run workflow.
- Journey counts are environment-wide, not per selected integration. That is the existing semantics, and the page now states it.
