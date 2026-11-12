# MT-7 — reviewed requirement coverage: identity audit

**Baseline:** `85d4a48c` (branch `008-traceability-first`).

**Question:** Can BirkNext say that a tester has reviewed a specification requirement, on the basis that the tester reviewed a Test Plan Suggestions draft?

**Answer:** Not without an association between the two identities. That association did not exist, so MT-7 adds it to the shared canonical registry (decision **C**, below).

## Identity chain (actual code)

| Identity | Identifier | Owner | Source / baseline | Revision | Review | Persistence |
|---|---|---|---|---|---|---|
| Guide business rule | `r-duplikat`, `r-ukjent-barn` … (`GuideRule.Id`) | System Understanding (`SystemGuideService`) | curated guide; evidence names files, tests and documents per source role | guide revision + per-scenario content fingerprint | guide lifecycle (Draft → InReview → Approved → Published) | `system_guide_revisions` |
| Guide scenario ("What to test") | `duplikat-melding` … (`GuideTestScenario.Id`) | System Understanding | same | `TestPlanSuggestionEngine.ScenarioFingerprint` | guide lifecycle | same |
| Test Case draft | guide key + scenario id (`ScenarioReference`) | Test Plan Suggestions | guide revision + source basis | draft `Revision` + scenario fingerprint | Accepted / NeedsClarification / Excluded … (review events) | `test_plan_drafts` / immutable `test_plan_snapshots` |
| Specification requirement | `module/feature/FR-nnn`, e.g. `skoleservice/skoleservice::001-skoleservice/FR-018` (`CorrelationRules.Scoped`) | Canonical interpretation (Relationship Review) | Delivery snapshot (spec.md per feature pack) | requirement text (attribute `text`) | — | `canonical_interpretation.entity` |
| Requirements Traceability rows | `FR-nnn` of the workspace SDD Specification document | `SddEvidenceGraphService` (workspace) | the selected workspace artifacts | — | — | workspace documents |
| Test → requirement | `Verifies` relation | Canonical interpretation | test inventory of a Source Analysis snapshot | evidence fingerprint | Relationship Review decisions | `canonical_interpretation.relation` / `review_decision` |

## Existing associations found

- **Guide rule → requirement:** none. `GuideEvidence.RequirementId` exists in the contract, but the curated Skolenærvær guide uses it nowhere (0 of 13 rules, 0 of 21 scenarios).
- **Scenario → requirement:** none.
- **Test Case draft → requirement:** none. `TestCaseRequirement` holds guide **rule** ids, not specification ids.
- **Test → requirement:** yes, in the canonical registry. On the real M2LB (7) workspace there are 22 explicit links, 1,331 candidate links (informal: the id is written in the test file) and 390 ambiguous ones.
- **Review decision → relation:** yes. `ReviewDecisionRecord` (sequence, superseded chain, evidence fingerprint) is applied by `CorrelationPrecedence`.

The workspace Traceability matrix and the canonical requirements are two different id spaces. The workspace id `FR-001` names no feature pack, while the canonical `module/feature/FR-001` does. MT-7 therefore never matches one to the other by id text. It shows its own panel, keyed by the canonical identity.

## Decision: C — a new reviewed association type in the shared relationship infrastructure

The shared registry already provides:
- stable entity identities;
- typed relations;
- Candidate, Explicit and Confirmed resolutions;
- human decisions with history and supersession;
- revalidation when the evidence changes;
- one review queue.

A guide scenario becomes a canonical `Scenario` entity (`system-understanding/<guide>/<scenario>`). Its link to a requirement is a `Verifies` relation that Relationship Review decides. No second traceability store, no new table, no migration.

| Rule | Evidence | Resolution | Trust |
|---|---|---|---|
| `CR-S-EXPLICIT` | the guide states `RequirementId` (+ spec file → feature pack) | Explicit if the guide revision is **Published**, else Candidate | EXPLICIT_SOURCE_REFERENCE / SUGGESTED |
| `CR-S-SCOPE` | the stated id is defined by several feature packs and no spec file is cited | Ambiguous (one item per pack) | SUGGESTED |
| `CR-S-VIA-TEST` | a test the guide names for the scenario references the requirement: explicitly (Medium) or as an informal id in the test source (Low) | always Candidate | SUGGESTED |
| — | title or text similarity | **never** | UNVERIFIED_SIMILARITY is not produced |

Only requirements a specification feature pack defines (module + feature) are associated or counted. Ids known only because a test names them, such as `SC-003`, have no verified identity and are left out.

## The coverage rule (`RequirementCoverageService`)

A requirement shows **Reviewed scenario coverage** only when all of these hold:
1. the requirement identity is canonical and pack-scoped;
2. the scenario identity is canonical;
3. the association is EXPLICIT (published guide) or CONFIRMED in Relationship Review on the current evidence;
4. a tester accepted that scenario in Test Plan Suggestions;
5. that acceptance was made on the scenario fingerprint the guide shows now;
6. the interpretation describes that same scenario revision;
7. the association is active in the latest interpretation (same baseline).

Otherwise the requirement shows one of:
- **Association verified · scenario not reviewed**;
- **Needs relationship review** (suggested, ambiguous, or a decision made on older evidence);
- **No documented scenario coverage**.

When there is no interpretation, no requirement or no guide scenario, the result is **Unknown**, never zero.

The evidence fingerprint covers the scenario content fingerprint and the requirement text fingerprint, so a changed scenario or a changed requirement turns a confirmation into **RevalidationRequired**. The source snapshot id is deliberately not part of it: identical requirement text in another baseline is the same requirement. A baseline without the requirement simply has no active association.
