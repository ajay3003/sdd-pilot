# BirkNext Difference Ground-Truth Matrix — 2026-11-07

**Pair:** `M2LB.zip` (`22339207…`, baseline, 2026-10-28) → `M2LB-language.zip` (`a5c083c8…`, current, 2026-11-03). The entry timestamps confirm the direction.

**Oracle:**
- Python standard library only, using rules R1–R8, S1–S7, G1–G3 and D1–D3 (see the main report).
- The rules were fixed before any BirkNext output was read and were not changed afterwards.

**Content:** no real content is reproduced; only structural paths, keys, counts and hashes.

**Verdicts:** MATCH / MISMATCH / NOT COVERED / HUMAN REVIEW.

## 1. Snapshot identity and direction

| Check | Expected (oracle) | BirkNext | Verdict |
|---|---|---|---|
| Baseline snapshot identity | file SHA `22339207…` | snapshot `4d267525`, archive SHA `22339207…` | MATCH |
| Current snapshot identity | file SHA `a5c083c8…` | snapshot `17ee5906`, archive SHA `a5c083c8…` | MATCH |
| Direction | baseline 2026-10-28 → current 2026-11-03 | Code Risk provenance names both fingerprints; delivery direction "Chronological", the reverse preview "BaselineLater" | MATCH |
| Total files | 2,361 / 2,612 | TargetIndex 2,361 / 2,612 (complete) | MATCH |
| Path sets | — | 0 missing, 0 extra, 0 duplicate paths in either snapshot | MATCH |
| Fingerprints | decoded-text SHA-256 for the read scope | Equal for every file both sides fingerprinted. `renovate-pipeline.yml` (Windows-1252) is fingerprinted only by BirkNext; its value equals an independent cp1252 decode | MATCH (the oracle's encoding rule is stricter) |

## 2. File-level reconciliation (all 3,304 path records)

- **Raw contract:** the oracle classifies by raw bytes.
- **C2:** the same records under BirkNext's documented reader scope.
- **BirkNext's class per record** comes from its persisted inventories (path + fingerprint), which is exactly what its Code Risk change review compares.
- The Code Risk counts (943 / 692 / 134 / 1,009, plus 526 unknown) equal the per-record sums.

| Contract | Oracle → BirkNext | Records | Verdict |
|---|---|---|---|
| Raw (R4) | Added → Added | 943 | MATCH |
| Raw (R4) | Modified → Modified | 134 | MATCH |
| Raw (R4) | Modified → Unknown | 95 | NOT COVERED (stated) |
| Raw (R4) | Removed → Removed | 692 | MATCH |
| Raw (R4) | Unchanged → Unchanged | 1,009 | MATCH |
| Raw (R4) | Unchanged → Unknown | 431 | NOT COVERED (stated) |
| BirkNext documented scope (C2) | Added → Added | 943 | MATCH |
| BirkNext documented scope (C2) | Modified → Modified | 134 | MATCH |
| BirkNext documented scope (C2) | Removed → Removed | 692 | MATCH |
| BirkNext documented scope (C2) | Unchanged → Unchanged | 1,008 | MATCH |
| BirkNext documented scope (C2) | Unknown → Unchanged | 1 | MATCH (decoder difference, verified) |
| BirkNext documented scope (C2) | Unknown → Unknown | 526 | MATCH |

- **Raw modifications without a BirkNext fingerprint (95):** `.md` 62, `.razor` 32, `.html` 1 (DIFF-GAP-1).
- **Oracle detail for modified text:**
  - 229 text, 0 binary;
  - 0 line-ending-only, 0 whitespace-only, 0 encoding-only;
  - +3,718 / −3,461 lines (difflib, asymmetric by construction; the reverse direction gives +3,445 / −3,702).
- **Moves:** 139 exact candidates, 10 ambiguous, 258 possible. HUMAN REVIEW, because BirkNext lists them as removed + added.

## 3. Delivery (Spec-Kit) reconciliation

**Oracle findings, 28 → 33 features:**
- 19 same key, 9 moved (module renamed, same folder), 5 added, 0 removed.
- Tasks 1,415 → 1,742; checked 1,359 → 1,658.
- FR ids 458 → 571.
- 2 specs changed.
- 0 tasks added, removed, checked or unchecked.
- 116 task texts changed under the same id, all of them reference/path tokens only.
- 4 plan.md files changed.
- 0 acceptance, story or plan-heading changes.

**BirkNext:** stored report `5fc559b1`. The reports generated in the UI (`93386904`, `91ea1f41`) have the same metrics.

| # | Feature (current key) | Oracle | Spec changed (oracle / BirkNext) | Tasks +/−/✓ (oracle / BirkNext) | Not covered | Verdict |
|---|---|---|---|---|---|---|
| 1 | `autorisasjonservice::001-autorisasjon-initial` | moved | False / False | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 2 | `autorisasjonservice::001-translate-source-english` | moved | True / True | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 3 | `autorisasjonservice::002-wolverine-shouldly` | moved | False / False | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 4 | `autorisasjonservice::004-scim-user-sync` | moved | False / False | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 5 | `autorisasjonservice::005-translate-adr-029` | added | — | — | — | MATCH |
| 6 | `frontend::001-initial-dashboard` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 7 | `frontend::002-child-search-profile` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 8 | `frontend::003-designer-polish` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 9 | `frontend::004-settings-devmode` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 10 | `frontend::005-access-admin-panel` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 11 | `frontend::006-admin-module-alignment` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 12 | `frontend::007-translate-adr-029` | added | — | — | — | MATCH |
| 13 | `hendelseadapter::001-birk-hendelse-adapter` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 14 | `hendelseadapter::002-testcontainers-wolverine` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 15 | `hendelseadapter::003-multi-entity-debezium` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 16 | `hendelseservice::001-hendelsestjenesten` | moved | False / False | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 17 | `hendelseservice::002-fix-romning-refdata` | moved | False / False | 0/0/0 / 0/0/0 | module moved (not stated) | MATCH + HUMAN REVIEW |
| 18 | `hendelseservice::003-translate-adr-029` | added | — | — | — | MATCH |
| 19 | `meldingsutvekslerreceiver::001-meldingsutveksler-receiver` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 20 | `person::001-person-module` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 21 | `person::002-barn-parent-feilkode` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 22 | `personadapter::001-birk-person-adapter` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 23 | `personadapter::002-batch-cdc-self-heal` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 24 | `proxy::001-proxy-initial-setup` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 25 | `proxy::002-adr026-health-checks` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 26 | `revisjon::001-revisjonstjenesten-m01` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 27 | `skoleadapter::001-skoleadapter` | same-key | False / False | 0/0/0 / 0/0/0 | — | MATCH |
| 28 | `skoleservice::001-skoleservice` | same-key | True / True | 0/0/0 / 0/0/0 | 8 task refs, plan.md | MATCH |
| 29 | `tjenesteservice::001-tjenestemodul-m01` | moved | False / False | 0/0/0 / 0/0/0 | 58 task refs, plan.md, module moved (not stated) | MATCH + HUMAN REVIEW |
| 30 | `tjenesteservice::002-birk-cdc-alignment` | moved | False / False | 0/0/0 / 0/0/0 | 35 task refs, plan.md, module moved (not stated) | MATCH + HUMAN REVIEW |
| 31 | `tjenesteservice::003-domain-sync-self-heal` | moved | False / False | 0/0/0 / 0/0/0 | 15 task refs, plan.md, module moved (not stated) | MATCH + HUMAN REVIEW |
| 32 | `tjenesteservice::004-child-placements-period` | added | — | — | — | MATCH |
| 33 | `tjenesteservice::005-translate-source-english` | added | — | — | — | MATCH |

Delivery verdict counts: acceptance changed | MATCH: 28; class | MATCH: 33; module path changed | HUMAN REVIEW: 9; plan.md changed | NOT COVERED: 4; spec changed | MATCH: 28; task inventory | MATCH: 28; task text/references changed | NOT COVERED: 4; tasks added | MATCH: 28; tasks completed | MATCH: 28; tasks removed | MATCH: 28; tasks reopened | MATCH: 28

| Item | Oracle | BirkNext preview (before fix) | BirkNext preview (after fix) | BirkNext report | Verdict |
|---|---|---|---|---|---|
| Features only in baseline / only in current | 0 / 5 | 9 / 14 | 0 / 5 | 0 removed / 5 added | MISMATCH before fix (DIFF-DEF-1), MATCH after |
| Reverse direction | 5 / 0 | 14 / 9 | 5 / 0 | — | MATCH after fix |

## 4. Contract reconciliation (39 contracts present under the same id)

33 contracts whose file is byte-identical in both versions report 0 changes: MATCH.

Contracts whose file changed:

| Contract file | Type | Oracle | BirkNext before fix | BirkNext after fix | Verdict |
|---|---|---|---|---|---|
| `SkoleService/autodoc/schema.graphql` | GraphQlSchema | 1 enum value added | 0 changes | Added Enum value 1 | MISMATCH before fix (DIFF-DEF-2), MATCH after |
| `SkoleService/specs/001-skoleservice/contracts/graphql-skole.graphql` | GraphQlSchema | 1 enum value added | 0 changes | Added Enum value 1 | MISMATCH before fix (DIFF-DEF-2), MATCH after |
| `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/schema.snapshot.graphql` | GraphQlSchema | 1 enum value added | 0 changes | Added Enum value 1 | MISMATCH before fix (DIFF-DEF-2), MATCH after |
| `Frontend/src/M2LB.Frontend.Web/GraphQL/Authorization/Mutations.graphql` | GraphQlOperations | ops +24 / -24 (all renamed) | Added Operation 24, Removed Operation 24 | Added Operation 24, Removed Operation 24 | MATCH |
| `Frontend/src/M2LB.Frontend.Web/GraphQL/Authorization/Queries.graphql` | GraphQlOperations | queries +18 / -18 (renamed); 7 fragments | Added Operation 18, Removed Operation 18 | Added Operation 18, Removed Operation 18 | MATCH (fragments stated as not expanded) |
| `Frontend/src/M2LB.Frontend.Web/GraphQL/Person/Queries.graphql` | GraphQlOperations | ops +1 / -1; 1 same-name selection change | Added Operation 1, Removed Operation 1 | Added Operation 1, Removed Operation 1 | MATCH + NOT COVERED (same-name operation body) |

- **Reverse direction after the fix:** identical counts with the kinds inverted (3 `EnumValueRemoved`).
- **19 further contracts** changed id with the renamed modules, because contract ids are path-based.
  - Source Change Impact reports them as contract removed + added, and these id sets equal the oracle's path changes.
  - They are not compared field by field (DIFF-GAP-6).

## 5. Dependency reconciliation (Source Change Impact, Dependencies domain)

| Kind | Oracle | BirkNext | Matched | Missed | Extra | Verdict |
|---|---|---|---|---|---|---|
| Added (nuget 159, dockerfile 8, docker-compose 5) | 172 | 172 | 172 | 0 | 0 | MATCH |
| Removed (nuget 146, dockerfile 8, docker-compose 5) | 159 | 159 | 159 | 0 | 0 | MATCH |
| Version changed | 0 | 0 | — | 0 | 0 | MATCH |

## 6. Impact reconciliation (DERIVED same-identity copy; forward 1,674 changes after the fixes)

| Domain | Check | Result | Verdict |
|---|---|---|---|
| Contracts | 19 + 19 contract ids, 43 + 43 operations, 3 enum values | equal to the contract comparison and the oracle | MATCH (after DIFF-DEF-2) |
| Dependencies | section 5 | exact | MATCH |
| Database | the reverse direction inverts (after normalizing "Added/Removed table"); 12 names are shown twice | BirkNext's own model has two entries per name (11 of 12) | HUMAN REVIEW (DIFF-GAP-5) |
| Configuration / Architecture / CI/CD | the reverse direction inverts; changes cite changed files | 1,614 of 1,674 changes cite at least one changed file | not assessed by the oracle (traceable) |
| Observability | 74 changes unpaired across directions, 28 without a file, 29 citing only unchanged files | — | HUMAN REVIEW |
| Unified findings | 1,674 SelectedChange = number of changes; ids unique; uncertain links are Possible / NeedsReview / Unresolved | — | MATCH |
| Evidence → snapshot | change findings binding a baseline-only file to the current snapshot: 752 before → 0 after | 54,861 current→current, 48,290 baseline→baseline | MISMATCH before fix (DIFF-DEF-3), MATCH after |
| Canonical relations | 7,681 added / 6,083 removed / 2,481 persisting; the reverse inverts; comparing a snapshot with itself gives an empty result | semantic correctness needs the interpretation rules | HUMAN REVIEW |

## 7. Metrics (Section 8)

| Metric | Definition / scope | Expected | BirkNext | Result |
|---|---|---|---|---|
| Total files per snapshot | non-directory archive entries (none ignored here) | 2,361 / 2,612 | 2,361 / 2,612 | MATCH |
| Added files | path only in current | 943 | 943 | MATCH |
| Deleted files | path only in baseline | 692 | 692 | MATCH |
| Modified files | raw: bytes differ / C2: both fingerprinted and different | 229 / 134 | 134 (+95 stated as unread) | MATCH (C2) / NOT COVERED (raw) |
| Unchanged files | raw / C2 | 1,440 / 1,008 (+1 cp1252) | 1,009 (+526 unknown, not counted) | MATCH (C2) |
| Changed requirements | FR ids added, removed or with changed text in matched features | 0 | 0 | MATCH |
| Changed specifications | normalized spec.md differs | 2 | 2 | MATCH |
| Changed tasks | id added / removed / checkbox flipped | 0 | 0 | MATCH |
| Changed task text/references | same id, different text | 116 | not measured | NOT COVERED |
| Changed implementation artifacts | `.cs` files (raw modified) | inside the file metrics | inside Code Risk "changed" (C2) | MATCH (C2) |
| Changed test artifacts | modified test-project files | inside the file metrics | not reported separately | NOT COVERED |
| Impacted entities | Unified findings other than selected changes | not independently computable | 2,518 | HUMAN REVIEW |
| Unresolved mappings | Unified findings marked `Unresolved` | not independently computable | 203 | HUMAN REVIEW |

These metrics measure different things and were not compared with each other:
- "changed files" ≠ "changed requirements";
- "impacted" ≠ "changed";
- a test reference ≠ an executed test.
