# BirkNext — real-project implementation gap detection — baseline audit (2026-11-09)

Branch `008-traceability-first` (local only). Baseline SHA: `a541e20e`. Acceptance archive: **`M2LB Skole.zip`**
(`C:\Users\ajaan\Downloads\M2LB Skole.zip`, 4,446,106 bytes, SHA-256 `7516b48d6bf77426feef67bca89d68070486896a0430b30c41085f103149dcd8`).
The brief named `M2LB Skole(1).zip`; no file with that name exists on this machine. The user chose this archive explicitly
(2026-11-09); every report records its exact name and hash.

## Question

Can BirkNext tell apart **specified, planned, implemented in source, tested, runtime verified, blocked, not implemented, not assessed** —
and say why — by comparing tasks, plans, decisions and contracts with an imported source snapshot?

## Baseline: what existed before this change

| Capability | Current implementation (before) | Gap | Root cause | Required fix | Verification |
|---|---|---|---|---|---|
| Task parsing | `TaskExplorerService.Parse` (frontend, linked into backend): id, checkbox, [P], [USn], FR/SC refs | No file paths outside `src/…`/`tests/…` (case-sensitive regex), no declared types, no packages, no continuation bullets, no exact line | Parser built for the tree view, not for evidence | Gap extractor reusing the parser for identity/state; adds line, continuation, expected paths, declared types, packages | `ImplementationGapTests` (extraction theories), real archive T014/T105/T119–T122 lines = oracle |
| Gates / decision dependencies | Only plan.md "Gates" tables (`PlanAnalysisService.ParseGatesTable`) | "External gates" tables and `**Gate: …**` markers in tasks.md not read; no link from a decision gate to blocked tasks | Not modelled | Gate tables (Gate + Blocks columns, ranges expanded) and inline markers | GA-12/13, extraction test |
| Decision (ADR) status | ADR ids recognised in text (`DecisionReferences.InText`); ADR documents classified as supporting documents | No recorded status; nothing distinguished drafted / pending / approved | Status never parsed | Register sections (`**Status:**`) and index rows; only an explicit accepted/current status approves | status theory, GA-12 |
| Implementation evidence | Per requirement only (`SddEvidenceGraphService`, `TaskEvidenceIndex`); automatic evidence only from explicit CodeLinks; `CR-L-TASK` matched task text to symbols by name | A task's own expected artifacts (paths, types, packages) were never checked against the snapshot | Evidence model is requirement-centric | Per-task artifact checks against `TargetIndex`, Roslyn symbols, dependency evidence and test inventory, with explicit relationship classes | 40 backend tests, GA reconciliation |
| Task completion vs code | Task Explorer shows the checkbox; implementation shown only via FR refs | A checked task with no code, or code with an unchecked task, was invisible | Dimensions not separated per task | Three separate dimensions per task (task state, implementation evidence, verification evidence) | GB-09, control tests |
| Tests | Test inventory (xUnit discovery) and imported TRX (frontend lifecycle) | Not associated with tasks; "test defined" and "executed" not separated per task | No task ↔ test association | Test tasks associated by explicit task-id references; defined vs executed vs passed from inventory + recorded executions | positive/failed-execution tests, GA-C-positive-verified |
| Delivery readiness | `DeliveryReadinessService` (Quality Review): documentation only ("source implementation … not assessed") | No source-backed feature readiness | By design document-only | Separate feature readiness from source evidence, kept apart from the documentation gate | GB-13 |
| Snapshot / provenance | Snapshot identity, archive SHA-256, Project Import provenance | — (reused) | — | Evaluations keyed by import scope + snapshot; findings carry snapshot + archive hash | GA-03, GA-24 |
| Human review | `ReviewDecisionRecord` (canonical), `ManualReviewDecisionStore` (append-only) | Not available for gap findings | No module | Reuse `ManualReviewDecisionStore` with module `implementation-gaps`, subject = stable finding id | GA-23, GA-30, GB-15 |

Pages inspected (read-only audit): Source Import / Project Import, Source Analysis, Specification Review/Explorer, Plan Explorer, Task Explorer,
Task Deltas, Implementation Review (`/task-alignment`) and Implementation Evidence Review (`/implementation-review`), Requirements and Artifact
Traceability, Relationship Review, Delivery Readiness, Delivery Overview, Release Readiness, Unified Impact Analysis, Canonical Interpretation,
Snapshot Registry. None compared a task's expected artifacts with source. No competing interpretation engine was added: the new engine reuses
the task parser, `CorrelationRules.NamedSymbols`, `DecisionReferences`, `RequirementReferenceParser`, snapshot evidence and the manual review store.

**Constraint found:** source text is not kept after analysis (only paths, fingerprints, bounded Roslyn symbols, dependency and test
inventories). Gap detection therefore runs at Project Import (documents in memory) and persists only the extracted SDD inputs.

## Independent oracle (no BirkNext code)

`gap/oracle.py` (standard-library Python) read the ZIP in memory:

| Fact | Oracle |
|---|---|
| Files | 1,839; Skole: 18 files — specifications only (`Skole/specs/001-skoletjenesten/*`, `.specify/memory`); **no `Skole/src`, no `Skole/tests`** |
| Skole tasks | 159, **0 checked** |
| T014 (line 66) | `- [ ]` Draft ADR-028 "Parquet.Net for the Bufdata delivery" |
| T105 (527), T119 (600), T120 (601), T121 (606), T122 (607) | all `- [ ]`; T119 carries `**Gate: ADR-028 signed off.**` |
| Gate | `tasks.md:38` External gates: `ADR-028 Parquet.Net sign-off (T014 drafts it) | T105, T119–T122 | Tech lead` |
| ADR register | `docs/M2LB-ADR-(Architecture-Decisions-Record).md` — last entry **ADR-027**; no ADR-028 → **PENDING** |
| Packages | no `Parquet.Net` / `ParquetSharp` PackageReference anywhere |
| Types | `DatasettParquetSkriver`, `DekningParquetSkriver`, `ManifestSkriver`, `LeveransefilUtboksJobb`: 0 code files |
| Unrelated Blob usage | 15 code files (HendelseAdapter / PersonAdapter Event Hub checkpoints; Revisjon audit-log upload) — not the Bufdata exporter |
| Contract | `contracts/bufdata-leveranse.md`: 8 clauses (period path, manifest last, write-once, dataset, prohibited identifiers, coverage, manifest fields, determinism) — none evidenced in code |
| Conclusion | **Implementation not evidenced in supplied archive** (not: "does not exist") |

## Defects found in the new engine on real data (fixed, with regression tests)

| Id | Defect | Effect on real data | Regression test |
|---|---|---|---|
| GAP-DEF-1 | Paths in another module ("as `HendelseAdapter/…`", "from `…`") treated as expected artifacts | SkoleAdapter T002 counted HendelseAdapter's csproj as its implementation | `Referenced_files_of_other_modules_…` |
| GAP-DEF-2 | Globs and `../` paths treated as files | "`*.cs` not found" findings | same |
| GAP-DEF-3 | A package declared by another module's project made the task Candidate | T002/T003 "candidate" with no project | same |
| GAP-DEF-4 | Non-src scopes (`.pipeline/`) not recognised as absent | per-task missing-file findings | same |
| GAP-DEF-5 | Same-file-name search crossed modules | most of 271 "moved/renamed" findings | same |
| GAP-DEF-6 | A module holding only `specs/` counted as implementation present | Skole `global.json`, type-only tasks → "missing" | `A_spec_only_module_…` (red first) |
| GAP-DEF-7 | Field names after "with" taken as types | T078 fields → "type not declared" | same (red first) |
| GAP-DEF-8 | Contract yaml/graphql references treated as expected code | "found elsewhere" findings | same (red first) |
| GAP-DEF-9 | Framework types/methods taken as declared types (`IHostedService`, `HttpClient`, `…Async`) | false "type missing" | declaration theories |
| GAP-DEF-10 | Package paths are repository-relative with the repository named after the archive | positive control Partially implemented via the API | `A_package_is_found_in_its_project_whatever_the_archive_is_called` (red first) |
| GAP-DEF-11 | Decision-drafting task verified "Test not found" | T014 ≠ oracle "Not applicable" | assertion in `A_decision_absent_…` |
| GAP-DEF-12 | Keyword "testing task" flag made implementation tasks test tasks | T151 counted as a test | covered by T151 in real-archive probe; strict rule in extractor |

GAP-DEF-1–5 were observed on the real archive before the fix (probe output kept in scratch `gap/probe-before-defect-fixes.txt`); their
synthetic regression test was written together with the fix and not run red separately. GAP-DEF-6–8 and GAP-DEF-10 were run red first.

Detail of the change: `docs/hardening/birknext-implementation-gap-engine-remediation.md`. Acceptance: `docs/audits/birknext-skole-parquet-gap-acceptance.md`.
Gap register: `docs/audits/birknext-implementation-gap-register.md`.
