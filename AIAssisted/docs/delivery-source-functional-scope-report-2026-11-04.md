# Delivery Source + Functional scope filter — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed.

## SOURCE + FUNCTIONAL SCOPE FILTER AUDIT MAP

**Column key**

| Short name | Meaning |
|---|---|
| Source sel. | Current project/source selector |
| Functional sel. | Current functional selector |
| Pkg ids | Canonical Package/Repository ids available |
| Cap ids | Canonical Capability/Feature ids available |
| Server / client | Where filtering happens today |
| Counts / summary scoped | Whether counts and summary follow the filter |
| Name repeats | Whether the same source name can repeat |
| Cross-source capability | Whether one capability can span several sources |

| Consumer | Source sel. | Functional sel. | Pkg ids | Cap ids | Server / client | Counts / summary scoped | Name repeats | Cross-source capability | Decision |
|---|---|---|---|---|---|---|---|---|---|
| **Delivery Overview** | Delivery project only | none (Module/State/Attention/Search filtered the list only) | snapshot source (file / ADO repo) → now `package:` / `repo:` id | canonical Feature identity `module/featureKey` + Capability | client (list only) | **no** → **yes now** | yes (re-downloads `(2).zip`) | yes | **migrated** (this task) |
| Delivery Reports | project + current/baseline snapshot ids | none | same | same | server build | whole comparison | yes | yes | **extended**: optional Source / Functional scope at generation, ids frozen in `WeeklyDeliveryReport.Scope` |
| Delivery Stories (report tab) | report | none | same | same | client | follows the open report | — | — | reuses the frozen (scoped) report — no separate matcher |
| Traceability (`artifact-traceability`, CanonicalTracePanel) | canonical scope | none | Package entities | Feature/Capability | server (`trace/*`) | per feature | — | yes | reuse later: `CanonicalScopeFilter` + `FunctionalScopesAsync` |
| Impact Analysis | workspace | none | partly | Feature | client | no | — | yes | reuse in #23 (smart views) via the same filter |
| Test Coverage (FQS coverage) | Delivery project | FQS scope | — | VERIFIES relations | server | yes (FQS) | — | yes | reuse: FQS scopes are offered in the same dropdown |
| Project Evidence | project | none | package/source roles | — | server | n/a | yes | — | candidate for Source filter; Functional scope = "which sources support a capability" (later) |
| Source Analysis | snapshot | none | Package | — | server | per snapshot | yes | — | keep the snapshot selector; Functional scope would give misleading partial analysis — **not applied** |
| Dashboard | workspace | none | — | — | client/server mix | global | — | — | **not** scoped: a global functional scope would make the generic dashboard confusing |
| Governance (#24) | — | scope-aware per brief | — | — | — | — | — | — | must use `capability:` / `profile:` / `custom:` ids from this model |
| Release Readiness (#25) | — | release scope | — | — | — | — | — | — | reuse the same functional-scope ids; no Delivery-specific copy |
| AQR | target | none | — | ApiOperation | server | per run | — | — | audit only: never hide review-critical evidence through a source filter |
| IQR | integration | none | — | System/Channel | server | per run | — | — | audit only |
| FQR | target/scope | FQS scope (pre-run) | — | — | server | per run | — | — | audit only |
| Performance | target | none | — | — | server | per target | — | — | leave feature-specific |
| Critical E2E | module registration | none | — | — | server | per run | — | — | leave |
| Technology Coverage | project | none | Package | — | server | project | yes | — | candidate for Source filter later |
| Code Risk | snapshot | none | Package | SourceSymbol | server | snapshot | yes | — | leave |
| Documentation Drift | project | none | — | — | client | project | — | — | leave |

## What was built

1. **Source identity.**
   - `DeliverySourceIdentity` lives in `shared/DeliveryScope.cs`.
   - A ZIP gets `package:{archive stem}`, using the same rule as the canonical Package identity: the copy suffix is dropped, so `M2LB-language (2).zip` and `M2LB-language.zip` resolve to the same id.
   - Azure DevOps gets `repo:{org/project/repo}@{branch}`.
   - The file name stays provenance only ("Imported from M2LB-language.zip").
2. **Functional scope identity.** No capability engine was added to Delivery. The functional scopes are:
   - **Canonical capabilities and change intents:** Feature —Implements→ Capability. Only established relations count:
     - Explicit or Derived relations;
     - relations a person confirmed (ReviewState = Confirmed).
     Candidates are listed for review and never counted. Rejected relations are dropped.
   - **Domain flows and saved scopes:** from the existing Functional Quality Scope. `FunctionalQualityScopeService.FeatureMembershipAsync` reuses its definition and module rules.
3. **New canonical rule CR-G-DECISION** (rule set `correlation/4`, in `shared/CorrelationRules.cs`):
   - **Members (Derived):** two or more features that declare the same decision record (ADR-nnn) in their title or folder form one change intent. Its id is `decision:adr-029`.
   - **Name:** built from the terms that more than half of the declaring titles share, giving "ADR-029: Translate English". The id is independent of the name.
   - **Candidate (Medium):** a feature whose spec.md only *cites* the decision.
   - **Candidate (Low):** a feature whose title carries every shared intent term without any reference.
   - Features stay separate entities.
   - ADR-029 has no built-in meaning. The rule is "same declared decision record".
4. **Citations recorded at import.**
   - The parser records the ADR ids each spec.md cites (`ParsedDeliveryFeature.DecisionReferences`).
   - They are stored as `feature_snapshot.decision_references` (jsonb, nullable; migration `AddDeliveryDecisionReferences`).
   - Snapshots imported earlier read as null ("not recorded"). Title/folder declarations still work for them.
5. **Shared scope model.**
   - `shared/CanonicalScope.cs`: `CanonicalScopeFilter(SourceId, FunctionalScopeId, Module)`, `CanonicalFunctionalScope`, `CanonicalScopeMember`, help texts and `DecisionReferences`.
   - `CanonicalInterpretationService.ScopeOfDeliveryAsync` and `FunctionalScopesAsync` hold the one bounded query (Implements only; dependencies never add members).
6. **Server-side scoping.**
   - `DeliveryScopeService` with `GET api/delivery-reporting/projects/{id}/scope?report=|snapshot=&source=&scope=` returns:
     - options with dependent counts;
     - invalid-filter messages;
     - notices;
     - "why included" per feature;
     - review candidates;
     - the scoped projection.
   - `DeliveryReportScoping.Project` (shared) filters rows, attention and scope changes, then **recomputes every metric** with the report builder's rule.
   - A test (`Projection_OverEveryFeature_ReproducesEveryFrozenMetric`) proves that an all-features projection reproduces every frozen metric exactly.
7. **Delivery Overview.**
   - **Filter bar:** Source | Functional scope | Module | State | Attention | Search.
     - Source, Functional scope and Module recalculate the whole overview: KPIs, the checklist donut, feature states, change this period, quality and readiness, and needs attention.
     - State, Attention and Search narrow the list.
   - **Header:** shows Source ("All sources · N evidence sources"), Functional scope and Module.
   - **Chips:** filter chips plus a "Clear filters" action.
   - **Notices:** "Source not present in baseline", "Functional scope introduced in the current snapshot" and invalid ids ("Selected functional scope is not available in this snapshot.").
   - **Empty scope:** "No Delivery features match this Source + Functional scope." with Clear functional scope / Clear source.
   - **Change this period:** shows "Not comparable" when the source is absent from the baseline.
   - **Attention:** comparison warnings are labelled "Comparison-level warnings". Feature-less items are shown as "Snapshot-level items (not scoped)".
   - **Review candidates:** listed ("awaiting review, not counted"), with a link to Correlation Review.
   - **State:** all filters live in the URL as ids (`source`, `scope`, `module`, `state`, `attention`, `q`).
   - **Base report:** the overview starts from the newest *whole-comparison* report.
8. **Delivery Reports.**
   - Generation offers an optional Source and Functional scope (from the chosen current snapshot).
   - The server refuses unknown ids.
   - The frozen report stores `Scope`: ids, then-labels, canonical scope, rule set, feature keys and the in-baseline flags.
   - The report header and history show the scope.

**Semantics (documented and tested)**

- **Source:** the imported source a Delivery feature was read from, i.e. its snapshot's source. A related contract in another source never pulls a feature in.
- **Functional scope:** established Feature → Capability relations, plus FQS domain flow and saved scope membership.
- Current and baseline sides are scoped with the same selection.

## Real-data results (throwaway DB `birknext_ci2`, real `M2LB-language.zip` from Downloads, localhost only)

**Import**
- 38 features, 2038 / 2142 tasks (matches the observed screenshot).
- Interpreted into canonical scope `ci-live` with `correlation/4`.

**ADR-029 change intent**
- Derived members (8):
  - Translate Autorisasjon / CdcReplay / Frontend / HendelseAdapter / Hendelse / Person and PersonAdapter / RevisjonService to English (ADR-029);
  - Rename the platform-owned contract names (ADR-029, platform level).
- Candidates:
  - "Translate Source Code to English" (its spec.md cites ADR-029);
  - "Translate the Tjeneste module to English" (no ADR reference; same intent wording).
- Both candidates were confirmed through the shared review decision API on the throwaway DB, standing in for a tester's decision. They then joined the scope.
- Excluded: Children in Placements per Period, Designer Feedback Polish, Admin Module Alignment Spec and every other unrelated feature.

**Scoped report** (Source M2LB-language + ADR-029): 10 features, 720 tasks, 674 checked. Lineage is frozen: `ci-live`, `correlation/4`, "source not in baseline".

**Browser** (1440/390 × light/dark, Edge, `:5199`/`:5099`, non-localhost aborted: **0 external requests**):

| View | Features | Checklist |
|---|---|---|
| Global | 38 | 95% (2038/2142) |
| Source = M2LB-language | 38 | change "Not comparable: M2LB-language is not present in the baseline" |
| + Functional scope ADR-029 | **10** | **93% (674/720)**, 46 open; states InProgress 7 / Complete 3 |

- Refresh keeps the scope.
- Module = AutorisasjonService narrows the scoped view to its 2 translation features.
- An invalid scope id gives the explicit notice and the empty-scope card.
- Clear filters returns exactly the global 38.
- No horizontal overflow and no console errors in any mode.
- Screenshots are in the session scratchpad, `scope-shots/`.

## Tests

**Backend** (`DeliveryScopeTests`, 9):
- parser citations;
- decision-rule inclusion and exclusion: wording-only and unrelated features excluded; a single declaration is no intent; identities stay separate;
- scoped metrics: established members only, candidates listed apart;
- confirmed candidate joins, rejected one never does;
- source by stable id across `(2).zip`; source absent from the baseline;
- source ∩ scope;
- unknown ids reported and never substituted;
- capability rename keeps the id while the frozen label stays;
- no interpretation → no invented scope;
- whole projection reproduces every frozen metric.

**Frontend** (`DeliveryOverviewScopeTests`, 7, plus 1 in `DeliveryReportingPageTests`):
- default global view;
- whole-overview recalculation;
- why-included;
- comparison- and snapshot-level attention labels;
- source absent from the baseline;
- module within scope;
- invalid scope with clear actions;
- URL ids and Clear filters;
- endpoint missing → the requested scope is reported as not applied;
- scoped report generation sends exact ids and the frozen scope is shown.

**Suites:** frontend 8000/8000 passed; backend 5805 passed, 0 failed, 13 skipped (full suites, run sequentially with the acceptance stack stopped).

## Not done / follow-ups

- **Traceability, Impact, Test Coverage, Project Evidence:** not migrated. The audit map names the reuse path: the same `CanonicalScopeFilter` plus `FunctionalScopesAsync`.
- **Module filter:** stays a page-level projection of the server-scoped report. It uses the same projection, not a separate rule.
- **Existing real data:** for Delivery snapshots imported before this change, citations are "not recorded". Re-import, then **Correlation Review → Interpret** with the Delivery snapshot, to get `correlation/4` change intents in the real `birknext` DB. The real DB was not touched.
- **Same logical repository across ZIPs:** "M2LB -main" vs "M2LB-language" are distinct sources (no evidence that they are one repository). ADO imports carry the repository and branch.
