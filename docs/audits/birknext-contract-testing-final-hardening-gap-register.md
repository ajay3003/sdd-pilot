# BirkNext — Contract Testing final integration & identity hardening: gap register (2026-11-10)

Branch `008-traceability-first`. **Baseline `ae1db8e5`**. Local commits: `086e0caf`, `64952f64`, `721b629d`, `66130b61`, `770c8c99` + the reports commit
(final SHA in the session report). No push, no deployment.

## Verification summary

| Item | Status | Result |
|---|---|---|
| Contract identity / rename / legacy tests (`ContractIdentityTests`) | PASS | 7/7 |
| Bulk binding tests (`ContractBulkBindingTests`) | PASS | 2/2 |
| Project coverage + chain suggestion tests (`ContractProjectCoverageTests`) | PASS | 2/2 |
| Focused backend (Contract Testing, Integration Suites, Execution Evidence) | PASS | all green in the focused runs (119 + 45 + 26) |
| Focused frontend (Contract Testing, Integration Suites pages) | PASS | 40/40 |
| Migration acceptance on a disposable synthetic legacy DB | PASS | MG-01…MG-07 |
| Real M2LB + synthetic projects + browser + restart | PASS | **67/67** (round 4) |
| Backend full regression (six memory-guarded partitions, sequential) | PASS | 1,031 + 1,098 + 830 + 652 + 520 + 2,222 = **6,353 passed, 19 skipped, 0 failed** |
| Frontend full regression | PASS | **8,548/8,548** |
| Release frontend build | PASS | published and used by the browser acceptance |

Test settings and timeouts unchanged. The pre-existing intermittent Altinn failure (CTD-G10) did not recur.

## Gap disposition

| Id | Gap | Final status | Notes |
|---|---|---|---|
| CTH-F01 | Project-safe contract identity | PASS | repository identity + declared associations; scoped continuation, discovery, listings, explicit targets; forward migration + legacy resolution |
| CTH-F02 | Rename/move detection | PASS | 7 classifications; exact move recorded; changed/ambiguous need a recorded human decision |
| CTH-F03 | Bulk environment & authentication | PASS | preview → approve → confirm → apply, audit, concurrency; secrets never copied |
| CTH-F04 | Browser execution | PASS (positive + 3 negative cases) / NOT RUN (other negatives) | see below |
| CTH-F05 | Project-wide coverage | PASS | exact sums, current vs history, NOT ASSESSED |
| CTH-F06 | Chain suggestions | PASS | typed-field evidence only, drafts never run |
| CTH-G1 | Per-user project authorization ("unauthorized project users") | NOT SUPPORTED | BirkNext has no project membership model (role policies, local session). Isolation is by identity and query scope; a project access model is a separate product decision |
| CTH-G2 | Token audience / scope validation | NOT SUPPORTED | the credential resolver does not validate audience; shown as a limitation in every bulk preview |
| CTH-G3 | Endpoint sources beyond saved bindings and declared OpenAPI servers (application configuration, Terraform, topology, connected systems) | NOT SUPPORTED | stated in the preview limitations; no URL is ever taken from them |
| CTH-G4 | Browser negative cases: wrong permission, expired attended session, target timeout, unknown outcome, partial and cancelled runs | NOT RUN | run-service behaviour covered by existing Execution Evidence tests; not re-verified through the browser here |
| CTH-G5 | Kode 6/7 relevance filter in project coverage | NOT SUPPORTED | no evidence-backed per-contract classification yet (stated in the coverage limitations) |
| CTH-G6 | Ambiguous identical GraphQL schemas in M2LB (`autodoc/schema.graphql` and `tests/.../schema.snapshot.graphql` with the same text) | BY DESIGN (needs review) | 7 joined-project rows left for a person; never merged automatically |
| CTH-G7 | Definition coverage of a new version counts only tests on that version (older tests shown as "on older versions" until the reviewed update proposal moves them) | BY DESIGN | exact-version semantics required by the brief |
| CTH-G8 | Chains over data-driven tests run once per chain, not once per dataset row | NOT SUPPORTED | each suggestion names the limitation |
| CTH-G9 | Real M2LB runtime | BLOCKED (by instruction) | no m2lbdev/TT02/Altinn/MU/MM/Azure/Bufdata request was made |
| CTD-G10 | `AltinnTestingTests.ASubmission_EvidencesTheSixAltinnSteps…` intermittent failure (earlier run) | OPEN (pre-existing, root cause not verified) | did not recur |

## Security findings

No cross-project contract continuation, listing or explicit import (FS-02/03, FA-05/06, unit tests). No unauthorized execution: suggestions,
configuration and imports sent nothing (ledger); a data-changing call is sent only with the explicit confirmation and never resent (BE-08/09);
drafts never run. Credentials are referenced by the environment's credential, never copied into bindings, audit rows or previews (tests assert the
secret is absent). Bulk configuration uses the same environment gate as runs (production refused) and path-only validation (no host injection).
Migration and backfill are additive and never merge or guess (MG-02/05/07, FR-02/05). No external request from the browser (BE-Z1/BR-Z1).
