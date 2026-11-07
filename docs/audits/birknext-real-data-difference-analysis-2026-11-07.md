# BirkNext Real-Data Difference Analysis Validation — 2026-11-07

Ground-truth matrix: `birknext-difference-ground-truth-matrix-2026-11-07.md` · Gap register: `birknext-difference-gap-register-2026-11-07.md`.

**Question answered:** does BirkNext find the same real differences as an independent source-of-truth comparison of the same archives, and does it explain their downstream impact correctly? A successful run was never taken as a correct result. Every BirkNext output below was reconciled against an oracle that does not use BirkNext code.

## Environment and safety

| Item | Value |
|---|---|
| Source under test | `3e7b0a13` + Git Sources `32d17ed0` (backend/shared trees `b8b19072`/`a2f94ea7`, identical to `60671884`); fixes in this task's fix commit |
| Isolated stack | API `localhost:5099`, web `:5199`, new content root `api-diff` (fresh App_Data, own launch code), new database `birknext_diffval_20261107` (created empty for this run; the API's only connection was verified to be this database before any write; 88 tables migrated) |
| Outbound blocking | Browser: every non-localhost request aborted (0 attempted). API: `HTTP(S)_PROXY`/`ALL_PROXY` pointed at a dead local port, loopback exempt |
| Untouched | User stack on :5000/:5173 (PIDs 27508/31956), all other databases, `appsettings.Local.json`, `testdata/` (read only; SHA-256 re-checked) |
| Real content | Never printed, logged into reports or captured in screenshots. Reports use relative paths of structural items, counts, ids and hashes only; screenshots are element captures of counts/metrics |

## A. Dataset pairing

| Archive | SHA-256 | Files | Entry dates |
|---|---|---|---|
| `M2LB.zip` | `22339207cd2ef2f75ea418f66037c2ccacd8df7aad3cd89ddf73bd2a439c28f0` | 2,361 | 2026-10-28 |
| `M2LB-language.zip` | `a5c083c81e63c1a2f3d6268893aa5b63acf99e5a2bf2821548da1c07905e83a8` | 2,612 | 2026-11-03 |

- **Only authentic version pair: M2LB.zip → M2LB-language.zip.** Evidence: same 27 top-level roots, 1,669 identical paths (Jaccard 0.505), the same Spec-Kit solution. **Direction CONFIRMED** by the entry timestamps inside both archives (every entry dated 2026-10-28 vs 2026-11-03) and the file dates; it is not inferred from file names. Both orientations were still executed.
- **Not version pairs:** the Terraform archives overlap only through a shared template (`Terraform-iac-m2lbm01` / `BasicOpsInfra_*` share 10 of 14–18 files) but are different projects/environments; `M2LB.Common`, `Tiltak 2` and `skolenaervaer.json` have no counterpart.
- **What changed (summary):** three modules were renamed (`Autorisasjon→AutorisasjonService`, `Hendelse→HendelseService`, `Tjeneste→TjenesteService`), identifiers were translated (Norwegian → English operation/type names), five Spec-Kit features were added, task file references were updated to the new module paths, and one enum value was added to the SkoleService GraphQL schema.

A **derived** copy was used once: `M2LB-language.zip` re-packed with byte-identical entries (2,612/2,612 entry hashes equal, archive SHA `48cddd0e…`) and uploaded under the name `M2LB.zip`. This gives the same repository identity as the baseline so that Source Change Impact can run (see DIFF-GAP-3). It is labelled DERIVED everywhere.

## B. Independent oracle

`oracle.py`, `semantic_oracle.py`, `graphql_oracle.py` and `deps_oracle.py` are test-only. They use only the Python standard library and no BirkNext code, DTO, rule or result. Their rules were fixed before any BirkNext output was read:

- **File level (R1–R8):**
  - Paths: `\`→`/`, `.` segments dropped, unsafe paths rejected. Path identity is case-sensitive, with case collisions reported.
  - **Raw contract**: classification by raw SHA-256.
  - Modified text files additionally get line-ending-, whitespace- and encoding-only flags plus line counts.
  - Moves are classified as exact-hash unique, ambiguous, or same-name possible. A move is never confirmed; it stays removed + added.
  - **C2 projection** applies BirkNext's *documented* reader scope (written down from the reader's rules, not by running it): ignored folders, read extensions, the 2 MB limit, and a decoded-text fingerprint.
- **Spec-Kit (S1–S7):**
  - Features are `<module>/specs/<folder>`; a unique folder whose module changed = "moved".
  - Specs are compared raw and normalized.
  - Tasks are checkbox lines, keyed by T-id.
  - FR ids, acceptance (`**Given**`/`SC-`), user stories and plan headings are compared.
- **GraphQL (G1–G3):** comments and descriptions are stripped; SDL types, fields and enum members are compared; operations are keyed by kind and name.
- **Dependencies (D1–D3):** NuGet `PackageReference`/`PackageVersion`, Dockerfile `FROM`, docker-compose `image:`, all keyed `ecosystem|package|manifest`.

## C. BirkNext results (real processing, persistence and UI)

| Step | Result |
|---|---|
| Import both archives (Project Import preview → commit) | Snapshots `4d267525` (M2LB) and `17ee5906` (M2LB-language); snapshot SHA = file SHA (2/2); history kept; preview 13.0 / 7.4 s, commit 216 / 247 s |
| Persisted file inventory (TargetIndex) | 2,361 / 2,612 paths, both complete |
| Code Risk Review, snapshot-change mode | Refused first (identities `m2lb` ≠ `m2lb-language`), then run with the explicit reviewer declaration this product provides (remembered). Files: **943 introduced / 692 removed / 134 changed / 1,009 unchanged**, with 526 without fingerprint stated. Reverse direction inverts exactly; rerun identical |
| Delivery snapshots + comparison | 28 / 33 features; preview, report (stored, 3 API + 3 UI), reverse preview, reruns |
| Contract comparison | 39 contracts with the same id in both versions; forward / reverse / rerun |
| Canonical interpretation | 5,792 / 8,564 → 6,773 / 10,162 entities/relations; compare forward/reverse/self/rerun |
| Source Change Impact + Unified Impact | Only with the DERIVED same-identity copy: 1,671 → **1,674** structured changes after fixes; Unified run 4,186 → 4,192 findings |
| Browser (17 checks) | Delivery preview/report/reload/stories/filter, Code Risk result, Impact page, Snapshots; before fixes 15 PASS / 1 FAIL / 1 REVIEW, after fixes **17/17 PASS**; 0 external requests, 0 console/page errors, 0 API 5xx |

## D. Reconciliation (detail in the ground-truth matrix)

| Area | Result |
|---|---|
| File inventory (path identity, both snapshots) | **Exact set equality** 2,361/2,361 and 2,612/2,612; 0 missing, 0 extra, 0 duplicate records |
| File fingerprints | All files fingerprinted by both agree (4,971 per-snapshot records); 1 file (`renovate-pipeline.yml`, Windows-1252 text) fingerprinted by BirkNext and not by the oracle's strict UTF-8 rule. BirkNext's value was verified equal to an independent cp1252 decode, so it is correct; the oracle rule was **not** changed |
| File classification, BirkNext contract (C2) | **Exact per-record agreement**: Added 943/943, Removed 692/692, Modified 134/134, Unchanged 1,009 (1,008 + the cp1252 file), Unknown 526. Counts and identities both match |
| File classification, raw contract | 95 raw modifications are reported as "not read" (62 `.md`, 32 `.razor`, 1 `.html`): NOT COVERED by design but stated with its count (DIFF-GAP-1). 431 raw-unchanged unread files are likewise Unknown |
| Moves | Oracle: 139 exact-move candidates, 10 ambiguous, 258 possible. BirkNext reports moves as removed + added (no move hint): HUMAN REVIEW, not a mismatch |
| Delivery (Spec-Kit) | Report: **0 mismatches** in 33 class checks and 28 each for spec, acceptance, tasks added/removed/completed/reopened and inventory. 9 module moves are matched by unique folder exactly as the oracle's S1, but the rows do not say the module changed (HUMAN REVIEW). Task reference changes (116 tasks in 4 features) and plan.md changes (4) are NOT COVERED (DIFF-GAP-2). **Preview: MISMATCH (9 missing / 14 new vs 0 / 5) → DIFF-DEF-1, fixed** |
| Contracts | 36 contracts with unchanged files: 0 changes (MATCH). Operations: Mutations 24/24, Queries 18/18 and Person Queries 1/1 renamed, all MATCH; 7 fragments stated by BirkNext as "not expanded". **3 enum values added: BirkNext reported 0 → DIFF-DEF-2, fixed (now 3/3; reverse 3 removed)**. 1 same-name operation selection change: NOT COVERED (identity-level comparison) |
| Dependencies (impact) | **Exact set equality**: Added 172/172, Removed 159/159, Changed 0/0 (NuGet, Dockerfile, docker-compose) |
| Contracts (impact) | 19 contract ids moved with the renamed modules (Contract removed + added: MATCH with id sets) + 43/43 operations; enum values now included |
| Database (impact) | Inverts correctly; 12 table names appear twice with identical text because BirkNext's own model holds two table entries per name (11 of 12): presentation ambiguity, HUMAN REVIEW (DIFF-GAP-5) |
| Observability (impact) | 74 changes do not pair up between directions, 28 have no file, and 29 cite only unchanged files: HUMAN REVIEW (no oracle) |
| Canonical relations | Arithmetic and direction consistent (7,681 added / 6,083 removed / 2,481 persist; reverse inverts; self = empty; rerun identical). 2,459 of the removed relations touch a renamed module; the semantic correctness of the remainder needs interpretation rules → HUMAN REVIEW |
| Impact evidence binding | **DIFF-DEF-3, fixed**: before the fix, 752 of 1,671 change findings (and ~58k related-impact evidence entries) cited baseline-only files under the current snapshot id. After the fix, 54,861 references point to files in current → current and 48,290 to baseline-only → baseline; 0 wrong |

## E. Fixes (separate local commit)

| Id | Root cause | Fix | Regression test (red → green) | Before → after on real data |
|---|---|---|---|---|
| DIFF-DEF-1 | `WeeklyReportBuilder.Preview` matched features by exact key only; the report (`DeliverySnapshotComparison.Match`) also matches a unique folder whose module path changed | Preview uses the same `Match` | `The_preview_pairs_a_feature_whose_module_path_changed_exactly_like_the_report` | Preview 9 missing / 14 new → 0 / 5 (= report = oracle); reverse 5 / 0 |
| DIFF-DEF-2 | `SourceEvidenceDiff.ContractChanges` compared operations, types and fields but never `ContractTypeShape.Values`, although `EnumValueAdded/Removed` exist and are classified | Enum members compared per type (added → Non-breaking candidate, removed → Potential breaking change; order ignored) | `Enum_values_added_or_removed_between_versions_are_contract_changes` | Contract changes 86 → 89 and impact changes 1,671 → 1,674 (exactly the 3 oracle enum additions; 0 other changes lost or added) |
| DIFF-DEF-3 | `ImpactAnalysisRunService.AddSourceFindings` bound every evidence item to `TargetSnapshotId` | After findings are built, evidence whose file exists only in the baseline inventory is bound to the baseline snapshot (both inventories must be complete, otherwise nothing changes) | `Evidence_of_a_removed_file_cites_the_baseline_snapshot_that_holds_it` | 752 mis-bound change findings → 0 |

The three tests were run red on the unfixed source (3/3 failing for the stated reason), then green. The affected areas were 351 passed / 0 failed. The oracle was not changed to agree with BirkNext.

## F. Acceptance decision

| Dimension | Verdict | Basis |
|---|---|---|
| File-level correctness | **PASS** (BirkNext's documented contract) / **PARTIAL** (raw contract) | Exact per-record set equality under the documented reader scope; 95 raw modifications in unread file types are stated, not hidden |
| Semantic correctness | **PARTIAL** | Delivery report and contract operations exact; enum false negatives fixed; task-reference, plan.md and same-name operation changes not compared; moves not labelled |
| Impact-analysis correctness | **PARTIAL** | Dependency and contract domains exact; evidence binding fixed; versions with different repository identity or a different Project Import cannot be impact-analysed from the UI (DIFF-GAP-3/4); observability/canonical need human review |
| Persistence correctness | **PASS** | Snapshots, reports, runs and history re-read by id; reverse and rerun deterministic; no duplicate records except intentional new report/run per user action |
| UI correctness | **PASS** after DIFF-DEF-1 | 17/17; UI values equal the persisted results, which equal the oracle |
| Remaining gaps | see gap register | DIFF-GAP-1 … 8 |

### Final acceptance questions

1. **Compared:** M2LB.zip (2026-10-28) → M2LB-language.zip (2026-11-03).
2. **What changed:** 943 files added, 692 removed, 229 modified and 1,440 unchanged. In detail: three modules renamed, translated identifiers, five new features, 116 task references updated, plan changes in four features, two spec changes, three enum values, 172/159 dependency declarations, and GraphQL operations renamed.
3. **Oracle:** as above, in the ground-truth matrix.
4. **BirkNext:** the same file sets under its documented scope, the same delivery results, operations and dependencies, and (after the fix) the same enum changes.
5. **Exact matches:** all comparable file, delivery, contract and dependency records.
6. **Missed or misclassified:**
   - fixed: the preview count, enum values and evidence binding;
   - remaining as documented gaps: unread file types, task references, plan.md, same-name operation bodies and move labels.
7. **Counts and source identities:** correct, including the snapshot fingerprints and direction.
8. **Impact propagation:**
   - dependency and contract changes are propagated correctly;
   - the version pair is only reachable via the API with the derived copy (gap);
   - "Confirmed" in impact rows is an architecture evidence state, not a reviewed impact (HUMAN REVIEW);
   - the SkoleAdapter identity conflict was not touched.
9. **History:** yes. Old snapshots, reports and runs remain readable by id, and the evidence now names the right snapshot.
10. **Browser = backend:** yes (17/17).
11. **Reproducible:** yes. Reverse directions invert, reruns are identical, and comparing a snapshot with itself gives an empty result.
12. **Human review:** module moves, file moves, canonical relation changes, observability changes, duplicate-looking table rows, and whether "Confirmed" architecture relations should be labelled as such in impact.

### Performance

| Operation | Time |
|---|---|
| Project Import preview / commit, M2LB | 13.0 s / 216 s |
| Project Import preview / commit, M2LB-language | 7.4 s / 247 s (3.5× slower than in the earlier acceptance: the run shared memory with build and browser processes) |
| Delivery import | ≤ 1.7 s each |
| Code Risk change review | 0.35–0.93 s |
| Delivery preview / report | 0.09–0.9 s |
| Interpretation | 3.6 / 4.7 s |
| Canonical compare | ≤ 0.19 s |
| Contract compare | 39 contracts × 3 in 26–52 s |
| Source Change Impact | 6.3–7.4 s |
| Unified Impact | ~6 s; result 36 MB JSON |

Database after the run: 128 MB. API peak memory: **not measured** (the probe returned no value). The 1.5 GB memory floor was enforced; one build was aborted by the guard and repeated after the isolated stack was stopped.

### Edge cases (Section 10)

| Case | Data | Result |
|---|---|---|
| Same archive against itself | authentic | canonical compare empty; delivery/impact refuse identical ids with a reason |
| Same archive re-imported | authentic + DERIVED | API re-import of the current snapshot is reused; a UI re-import after another import creates a new snapshot by the documented rule, and a comparison of the two shows no changes (correct) |
| Reversed direction | authentic | all engines invert exactly (impact database rows after normalizing "Added/Removed table") |
| Repeated comparison | authentic | identical results; each run/report is a new record by user action |
| Re-packed archive, identical entries | DERIVED | identical file-level results; new archive identity |
| Corrupt / empty / duplicate path / traversal | DERIVED (previous acceptance RD-NEG-1…5) | rejected with codes; not re-run here |
| Added-only, removed-only, rename-only, CRLF-only, whitespace-only, case-only rename, conflicting requirement ids, partial import, interrupted comparison, snapshot changed while open | — | **NOT RUN** in this pass (the authentic pair contains none of them: 0 line-ending-, whitespace-, encoding- or case-only changes per the oracle) |

## Commits

- Fix commit (DIFF-DEF-1/2/3 with tests).
- Report commit (these three documents).
- Nothing pushed. Real archive content, raw API output and the derived copy stay in the local scratch folder.
