# Release readiness — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. This is the test lead's release decision layer, built on the canonical store (Phases 1–3), Functional Quality Scope and impact (#20 / #23) and the governance gate (#24). Nothing was rebuilt.

## RELEASE READINESS ORCHESTRATION AUDIT MAP

| Source | Before | Used by release readiness |
|---|---|---|
| Delivery Overview / Reports | Story / feature progress, task checkboxes | **Not** used as readiness. Checked tasks are shown only as "a checked task is not implementation, test or release evidence". |
| Canonical trace (requirements, VERIFIES, task → symbol) | Counts per project / feature | Requirements, Test design and Implementation dimensions, module-scoped |
| Shared review queue | Decisions / suggestions | Open review decisions dimension (via governance findings) |
| Governance gate (#24) | `GateAction.ReleaseReadiness` | Governance dimension + blockers |
| Functional Quality Scope / scenario drafts | Scope, coverage, drafts | Linked as the next action for test design (drafts never count) |
| AQR / IQR / FQR / Performance / Critical E2E | Own readiness and runs | **Not assessed** (Optional) with a link to the owning page; no result is invented |
| Snapshot Registry | Interpretations, review runs, reports | Frozen release reports register as `GeneratedReport` (FrozenReportEvidence) generated from their exact interpretation |
| Sign-off / approval | None | **New**: append-only sign-off beside a frozen report |

## What was built

### Model (`shared/ReleaseReadiness.cs`, rule set `release/1`)

**Scope reference:** project + optional modules + the exact interpretation id. References only; entities are never copied.

**Dimensions** (each has Required / Optional / Not applicable applicability, a state, a summary, evidence, a next action and a link):

| Applicability | Dimensions |
|---|---|
| Required | Scope, Requirements, Traceability, Test design, Governance, Open review decisions |
| Optional | Implementation evidence, Functional test execution, API review, Integration review, Frontend review, Performance, Critical E2E |

**Decision** (`ReleaseReadinessRules.Decide`):

| Condition | Decision |
|---|---|
| A Required dimension is Blocked | **Blocked** |
| A Required dimension is Not assessed | **Not assessed** (never Ready) |
| Any applicable limitation, an accepted exception, or an Optional dimension not assessed | **Ready with limitations** |
| Otherwise | **Ready** |

Not applicable never counts, and there is no score.

**Blockers:** unwaived blocking governance findings with their resolution, plus blocked required dimensions.

**Next actions:** required dimensions that are not ready, blocked first.

**Available modules:** the declared Module names that have feature packs (feature identities carry only the normalized "skoleservice/…").

### Service and endpoints (`CanonicalInterpretationService.Release.cs`)

**Assessment:** `GET api/canonical/{scope}/release-readiness?module=…&name=…`
- Module-scoped: requirements and tasks by their `module`, features by attribute or identity prefix.
- Governance is evaluated for the same modules, so another module's findings never weigh.

**Freeze:** `POST …/release-readiness/reports {name, modules}`
- Writes an **immutable** report to `canonical_interpretation.release_readiness_report`.
- The report keeps the scope, interpretation id, rule and policy versions, and the whole assessment as JSON.
- Later interpretations, decisions or policy changes never change it. A new freeze is a new report.

**List and open:** `GET …/reports` and `GET …/reports/{id}`.
- An unknown id returns **404, never the latest instead**.
- Reports are project-scoped.

**Sign-off:** `POST …/reports/{id}/sign-off {Accepted | AcceptedWithRisk | Rejected, note}`
- Append-only, in `release_sign_off`.
- A note is required unless accepting a plain Ready report; a rejection needs a reason.
- **A sign-off never changes the report's evidence or computed decision.**

**Persistence:** migration `AddReleaseReadinessReports`, which creates only the two tables in the `canonical_interpretation` schema.

**Snapshot Registry:** `ReleaseReadinessSnapshotSource` registers each report as frozen report evidence, with lineage "generated report from" its interpretation.

### UI: Release Readiness (`/release-readiness`, Traceability & Impact)

**Release scope card:** module checkboxes (none checked means the whole project), a report name, and Freeze.

**Decision card:** icon + label + reason (never colour alone), "Must happen before release" and "Next actions".

**Dimension cards:** applicability badge, state, summary, evidence (expandable), next step, and "Open <page>".

**Frozen reports list:** decision and latest sign-off for each report.

**Report view** (`?report=id`):
- frozen time, scope, rule/policy versions and interpretation;
- the frozen assessment;
- sign-off history and form;
- a missing report shows "does not exist" (no fallback).

**Also:** a User Guide registry entry, and the sidebar and its pinned tests updated.

**Fixed during acceptance**
- The report name was bound on change, so Freeze stayed disabled until the field lost focus. It now binds on input.
- Dimension links read "Open Open review decisions"; they now name the page.

## Real data (throwaway `birknext_ci2`, scope `ci-live`; localhost only)

| Scope | Decision | Why |
|---|---|---|
| Whole project | **Blocked** | Identity conflict Utdanning ↔ SkoleAdapter |
| SkoleService | **Ready with limitations** | 1 of 65 requirements with a confirmed test, 33 suggested only, 31 none; 2 governance warnings; execution dimensions Not assessed |

**SkoleService details**
- Scope, Requirements, Traceability and Open decisions are Ready.
- The identity conflict belongs to SkoleAdapter and does not block.

**Frozen report check**
- The report "Skolenærvær acceptance" was frozen.
- Sign-off without a note was refused ("not plain Ready: record the accepted risk").
- "Accepted with risk" with a note was recorded.
- Reopening by URL in four browser contexts showed the same frozen decision.
- An unknown id showed "does not exist".

**Measurements:** assessment API 0.8 s, 5.8 KB; page ready in about 5 s with the WASM start.

**Browser** (1440/390): no horizontal overflow, **0 external requests**.
- The light and dark runs render the same, because the app follows its own theme setting rather than the OS preference.

## Tests

- **Backend** `ReleaseReadinessTests` (4):
  - decision rules: Not applicable ignored, optional not assessed is a limitation, required not assessed is Not assessed, required blocked blocks, an exception is a limitation;
  - module assessment: checked tasks are not implementation, test-design counts, governance block, execution Not assessed with a link, PersonService not blocked by SkoleService, available modules;
  - nothing interpreted or nothing in scope gives Not assessed;
  - freeze → later evidence changes the live decision but not the frozen one (byte-identical assessment);
  - sign-off rules and history, decision unchanged, unknown id or other project returns null, unnamed freeze rejected.
- **Frontend** `ReleaseReadinessPageTests` (3): decision and dimensions, modules sent; freeze → exact report URL → sign-off (decision unchanged); a missing report shows an error and the live assessment is never loaded instead.
- **Navigation / docs:** pinned sidebar tests and the documentation catalog updated for the new row.
- **Full suites:** see the commit message and the final report.

## Not done (follow-ups)

- **Execution dimensions:** read AQR / IQR / FQR / Performance / Critical E2E run results (exact runs, freshness) instead of Not assessed; test execution matrix, must/should test from impact, failed-test priority.
- **Change scopes:** baseline vs current interpretation (needs #23 baseline impact); scope types (change / regression / custom) and persisted editable scopes.
- **Risk register:** risk ≠ finding, test-lead risk override, accepted risk register.
- **Freshness:** evidence freshness and stale-evidence rule ("re-run recommended").
- **Report comparison** across frozen reports; release notes / export; CI readiness result.
- **Integrations:** Dashboard and Delivery Overview tiles; drill-downs from governance findings to the exact review item.
- **Dark-mode verification** with the app's own theme toggle.

## Confirmations

- Release readiness is evidence-based, scope-aware and explainable; it is never a task percentage or one score.
- Checked tasks are explicitly not implementation, test or release evidence.
- Dimensions without evidence say Not assessed and link to their owner; nothing is invented.
- Module scope: unrelated modules' findings do not block.
- Frozen reports are immutable, versioned (rules, policy, interpretation) and opened by exact id with no latest fallback.
- A sign-off is recorded beside the report and never changes it.
- No real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
