# Multi-branch source context — acceptance status (tasks 14 + 15)

**Status: PARTIAL.** Nothing below is claimed beyond what actually ran.

## Ran and passed (final working tree, scratch build)

- **Focused backend:** 271/271. Covers SourceBaseline, SourceContentFingerprint, SourceChangeImpact, ContractRegistry, Topology, SystemGuide/SystemUnderstanding, Composite, TechnologyCoverage, ProjectImport, archive reader and migrations.
- **Focused frontend:** 589/589. Covers ProjectEvidence (including the reproduction of the reported Target Environment state), ProjectImport (content duplicates), SourceManagement, SourceChangeImpact, Dashboard, RecommendedWorkflow, SystemUnderstanding, IntegrationTopology and TargetEnvironment.
- **Independent oracle** of the five real archives: `birknext-m2lb-source-inventory.md`.
- **Migration** `AddSourceDecisions` applied on an empty isolated database: the startup log shows it applied.

## Real five-package browser acceptance (isolated install; disposable PostgreSQL 16 cluster, database `birknext_mb`)

Imports through the Project Import UI, sequentially:

| Package | Commit duration (API) | Result |
|---|---|---|
| M2LB (3).zip | 230 s | snapshot created (context: 1 logical source, NotSelected) |
| M2LB (4).zip | 93 s | snapshot created |
| M2LB (5).zip | 166 s | snapshot created (context then 1 logical source, 3 snapshots, NotSelected) |
| M2LB (6).zip | ≈150 s | snapshot created (context: 4 snapshots) |
| M2LB (7).zip | — | **NOT RUN.** Claude Code stopped the background services (cluster, API, web server, harness) because the machine ran low on memory. |

The harness's per-check results were never written: the harness was killed before it finished. The import facts above come from the API log and `api/source-context`.

**NOT RUN:**
- duplicate checks;
- baseline selection and switch across modules;
- comparison;
- restart persistence;
- visual checks.

Harness: `scratchpad/mb/accept.cjs` (phases import / verify / baseline / restart / visual), ready to re-run.
