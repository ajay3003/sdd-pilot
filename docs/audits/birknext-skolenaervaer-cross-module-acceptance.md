# BirkNext — Skolenærvær cross-module acceptance (2026-11-11)

All six real packages were imported into one isolated installation (see `birknext-real-m2lb-source-acceptance.md`). The same snapshots must read consistently in every module that consumes them.

| Module | What it shows for the real snapshots | Consistent with the oracle | Result |
|---|---|---|---|
| Project Import | Six commits, six separate snapshots, provenance with SHA-256 | Yes | PASS |
| Snapshot Registry (`/snapshots`) | Six snapshots, all "Analysis: Partial" (tool limitations), none "requiring attention" | Yes | PASS |
| Source Management (`/sources`) | "No sources yet" for API-driven imports | No: it lists workspace import records only | **GAP BN-13** |
| Source Analysis (API) | Components, projects, messaging channels with producers and consumers (`skole.perioder`, `person.barn`, …) and file:line evidence | Yes | PASS |
| Contract Evidence (API) | XSD v2.0.1 (`M2LB (7)`, `(3)`), v2.0.0 (`M2LB (5)`, MU), MU v0.8.x; intake OpenAPI in every M2LB package | Yes. Usage label for specs-only: BN-9 | PASS (with gap) |
| Skolenærvær Testing: source roles | MU: `Tiltak 2 (1)`, available, not bound. M2LB: four candidates, review required, none chosen. Altinn: Test Pack reference. IaC: optional, not provided | Yes. Per-candidate capability coverage: BN-8 | PASS |
| Skolenærvær Testing: Periodeavslutning | Before: channel `skole.reports`, never found. After: `skole.perioder` found in `M2LB (3)`, "not bound"; event contract checked under both names | Yes (after fix) | PASS (fixed BN-1, BN-2) |
| Contract chain (contract profile) | Supported versions not guessed; generation dependency stated | Yes (after fix) | PASS (fixed BN-3) |
| Integration Topology profile | Person recognised in both generations; Bufdata described per generation. Tjeneste and the Person HTTP lookup are not expected relationships | Partly | PASS (with gap BN-7) |
| Message Verification | Bufdata: no provider and no receipt surface in any package | Yes (after fix) | PASS |
| System Understanding | "Sources changed — may need review (5)"; content per generation; nothing runtime-verified | Yes (after fix) | PASS (fixed BN-4, BN-5) |
| Setup Profiles (template) | Period-closed channel `skole.perioder` | Yes (after fix) | PASS |

## Automated tests

| Suite | Result |
|---|---|
| Focused backend: SystemGuide, SkoleNaervaer*, IntegrationIdentity, SetupProfileService, MessageVerification, ScenarioCapability, TestPackSource, ContractEvolution, IntegrationJourney*, Topology* | **410/410 PASS** (final build). An earlier run was 409/410: the new seed-basis test's fixture had no pack roles (a test error, fixed) |
| Focused frontend: SystemUnderstanding, SkoleNaervaer, IntegrationJourney, IntegrationQualityReview | **226/226 PASS** |
| Full frontend at `a900c22a` | **8630/8630 PASS** |
| Full backend at `a900c22a`, six sequential memory-guarded partitions | **6511 passed, 1 failed, 19 skipped (6531)**. Partitions 1–5 = 1049/1098/840/652/520 passed; partition 6 = 2352 passed, 1 failed, 8 skipped. The failure is `CompositeSuiteTests.One_continuous_synthetic_transaction_is_verified_end_to_end_against_an_independent_ledger` (a loopback mock reported a 200 call as failed; peak memory 5.6 GB in that partition). Re-run alone on the same build: **`CompositeSuiteTests` 9/9 PASS**. It passed in the previous full run (`02bac857`). Classified as load-dependent flakiness, not a regression from this change (the test does not use the Skolenærvær pack, guide or channel) |

## Test changes

The following tests asserted facts that the real source disproves. They were corrected to the source, not relaxed:
- `skole.reports` as the period-closed channel;
- "Klargjort / no delivery";
- "Ikke implementert" for Bufdata.

New assertions:
- the report topics `skole.rapporter` and `skole.reports` are **not** the Period-closed integration;
- both event names identify the contract;
- a curated seed never claims the snapshots that existed at seeding time as its basis;
- an unedited seed draft is refreshed, while an edited one never is.
