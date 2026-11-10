# BirkNext — Multi-source end-to-end integration testing: gap register (2026-11-10)

Branch `008-traceability-first`. Audit: `birknext-multisource-e2e-audit.md`. Implementation: `../hardening/birknext-multisource-e2e-implementation.md`.
Acceptance: `birknext-multisource-e2e-acceptance.md` (final run 62/62 + 6/6 PASS). Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Audit gaps

| ID | Gap | Status | Evidence |
|---|---|---|---|
| MS-G1 | One ZIP had to contain the whole integration | PASS | Composite Source Context over four separately imported synthetic projects (SS-06) and five real archives (RS-01…09); unit `CompositeSuiteTests` |
| MS-G2 | No cross-project association | PASS | per-snapshot association keeps project key, stages, approval by/at/reason; manual association requires a reason (unit; RS/SS checks) |
| MS-G3 | No stage → source discovery | PASS | CONFIRMED / CANDIDATE / AMBIGUOUS / MISSING per stage; real MU = only `buf.meldingsutveksler` (RS-01); M2LB vs M2LB-language AMBIGUOUS (RS-02); libraries and IaC supporting only (RS-05) |
| MS-G4 | No cross-source compatibility | PASS | real MU → MM XML INCOMPATIBLE (v2.0.0 vs v2.0.1.flat, RS-07) as the independent oracle predicts; synthetic compatible/incompatible pair (SS-03, SS-25) |
| MS-G5 | Readiness single level | PASS | three levels; real context Blocked, real draft PARTIAL (RS-09…12); synthetic Ready / Ready / Needs review before plan approval (SS-10) |
| MS-G6 | Stage tests by name substring | PASS | stage contracts come from the associated snapshot's matched modules (SS-06: each contract keeps project and stage) |
| MS-G7 | No composite execution | PASS (synthetic) / NOT SUPPORTED (real) | one approved chain = one Execution Evidence run; FULL_E2E_VERIFIED against an independent ledger (SS-13…19); subflow is SUBFLOW_VERIFIED, never full (SS-20). Real stages have no BirkNext executor/observer (RS-13) |
| MS-G8 | No asynchronous handling | PASS | bounded GET observation; failed handoff → TIMED_OUT, later stages BLOCKED, verdict INCOMPLETE (SS-21); unanswered submit → UNKNOWN_OUTCOME, sent once (SS-23) |
| MS-G9 | No boundary correlation | PASS | six boundaries CONFIRMED by echoed identifiers equal to the ledger's (SS-15…17); wrong echo → WRONG_CORRELATION although calls passed (SS-22) |
| MS-G10 | Composite evidence not reopenable | PASS | plan, kind and exact source versions stored with the run; reopen after API restart (PR-01, PR-02) |
| MS-G11 | Regeneration impact | PASS | newer snapshot proposed with affected stages, never applied (SS-27); context revision records the change (SS-28); suite revision 2 with drift (SS-29); no duplicate contracts (SS-30) |
| MS-G12 | Coverage | PASS | Generated → Configured → Executable → Executed → Verified with "why not verified"; complete E2E Verified only from the continuous run (SS-24) |
| MS-G13 | Blockers not actionable | PASS | owner, severity, action per blocker; real blockers listed (RS-14); missing authentication blocks Bufdata (SS-31) |
| MS-G14 | Giant source dropdown | PASS | stage strip, per-stage candidates, searchable snapshot list (MS-U1, MS-U2); layout at five widths (MS-L1…L4) |
| MS-G15 | Period lock | NOT SUPPORTED (real) / PASS (synthetic) | operator-only prerequisite with real sources; synthetic local lock in the approved plan (SS-14) |
| MS-G16 | Bufdata delivery | NOT SUPPORTED (real runtime) | SPECIFIED_BUT_NOT_RUNTIME_VERIFIED (RS-08, RS-15) |

## Defects found by acceptance and fixed

| ID | Defect | Root cause | Fix | Regression test |
|---|---|---|---|---|
| MS-D1 | Stage execution readiness Partial for a ready synthetic plan (SS-10) | a data-changing step's "Changes data" readiness warning counted as a blocking gap, although the preflight confirms data-changing calls per run | `BlockingGaps` ignores "Changes data" (an unbound authentication still blocks) | `CompositeSuiteTests` asserts Stage execution Ready before plan approval |
| MS-D2 | The run view did not show the correlated identifier (SS-17) | only the explanation text was rendered | the correlated value is rendered (`its-correlation-value`) | `IntegrationSuitesCompositePageTests` |
| MS-D3 | Terraform-only archive missing from supporting evidence (RS-05) | Source Analysis marks an archive without application code Failed; discovery dropped every Failed snapshot | a Failed snapshot with Infrastructure-as-Code evidence is listed as supporting, with its status; never a stage source, never associable | `CompositeSuiteTests` real-shaped rules test |
| MS-D5 | Secured v2 Bufdata stage reported "no positive contract test" instead of its real blockers (SS-31, run 2) | the suite keeps the positive test bound to v1 (contract evolution never rebinds silently); the planner matched only tests of the exact stage version | the planner uses the newest positive test of the same contract at or below the stage version; that test's own readiness names the update proposal to review and the missing authentication | `CompositeSuiteTests` (changed snapshot: plan step keeps a test, `VersionOf`) + SS-31 |
| MS-D4 | Saving after reopening a context link was refused (409) | each revision has its own id; the address bar kept the opened revision's id after a save, so a reload or shared link opened an older revision and the conflict guard refused the save | the URL follows the opened revision; an older revision says so and offers "Open revision N" | `IntegrationSuitesCompositePageTests` (older revision) + MS-U3/U4 |

Harness defects (not product): the acceptance `settle()` treated the intentionally disabled Run button as busy (10-minute waits, session expiry
→ cascading 401s); the keyboard check's Tab bound was smaller than the sidebar. The harness now waits on the page's `aria-busy` (added to the page)
and fails MS-K1 honestly when focus never reaches the area tab.

## Remaining gaps

| ID | Gap | Status | Owner | Note |
|---|---|---|---|---|
| MS-R1 | No real executor/observer for MU, MM, Skolenærvær, Person, Bufdata | NOT SUPPORTED | Platform | needs approved read contracts/observers in a non-production environment; nothing is invented |
| MS-R2 | Real MU → MM schema incompatibility | OPEN (domain) | Test lead / Domain owner | Tiltak 2 ships Skolenærvær XSD v2.0.0 (and v0.8.2); M2LB MM validates v2.0.1.flat. Accepting it needs a written reason (e.g. MU forwards opaquely) |
| MS-R3 | M2LB and M2LB-language claim the same stages | OPEN (human choice) | Tester | AMBIGUOUS by design; a person chooses one, nothing merged |
| MS-R4 | Bufdata consumer side | NOT SUPPORTED | Domain owner | only the producer's draft dataset contract; files verified separately in Parquet Quality Review |
| MS-R5 | Authentication and environment compatibility per transition | NOT ASSESSED | Operator | never inferred from source; assessed per run via the Target Environment |
| MS-R6 | Real period lock | NOT SUPPORTED | Operator | BirkNext never locks a real period |
| MS-R7 | No per-project ACL in BirkNext | NOT SUPPORTED | Platform | single local workspace; endpoints use the existing write/execute policies |
