# BirkNext Difference Analysis — Gap Register — 2026-11-07

Source: real-data validation of M2LB.zip → M2LB-language.zip against an independent oracle (`birknext-real-data-difference-analysis-2026-11-07.md`).

Status vocabulary:
- **FIXED**: reproduced, regression test, fix and real-data recheck.
- **OPEN**: a defect or limitation that remains.
- **DECISION**: the semantics are intentional or ambiguous, so a product decision is needed and the code was not changed.
- **HUMAN REVIEW**: no authoritative label exists.

## Fixed defects

| Id | Severity | Area | Finding | Evidence | Fix | Status |
|---|---|---|---|---|---|---|
| DIFF-DEF-1 | Medium | Delivery Reporting — comparison preview | The preview announced 9 features "only in the baseline" and 14 "only in the current snapshot" for the same pair the generated report (correctly) shows as 0 removed / 5 added. The preview matched by exact key; the report also matches a unique folder whose module path changed | API preview + browser UI-DLR-2 (FAIL before, PASS after); oracle S1: 9 moved, 5 added, 0 removed | `Preview` uses `DeliverySnapshotComparison.Match`; test `The_preview_pairs_a_feature_whose_module_path_changed_exactly_like_the_report` | FIXED |
| DIFF-DEF-2 | High | Shared contract comparison (Contract evidence compare, Source Change Impact, Schema Studio compare, generated-documentation drift) | Added/removed GraphQL/OpenAPI enum members were never compared although parsed (`ContractTypeShape.Values`) and although `EnumValueAdded/Removed` classes exist. 3 real enum additions reported as "0 changes" | Oracle G2 on the 3 changed SkoleService SDL files; BirkNext's own snapshot holds the new member | `SourceEvidenceDiff.ContractChanges` compares enum members per type; test `Enum_values_added_or_removed_between_versions_are_contract_changes` | FIXED (86 → 89 contract changes, exactly +3; reverse −3) |
| DIFF-DEF-3 | High | Unified Impact Analysis — evidence binding | All evidence was bound to the current snapshot id, including files that exist only in the baseline (removed/renamed): 752 of 1,671 change findings pointed at a snapshot that does not contain the cited file | Persisted inventories of both snapshots vs evidence paths | Evidence whose file is only in the baseline inventory is bound to the baseline (complete inventories required); test `Evidence_of_a_removed_file_cites_the_baseline_snapshot_that_holds_it` | FIXED (0 mis-bound) |

## Open gaps and decisions

| Id | Severity | Area | Gap | Impact | Recommendation | Status |
|---|---|---|---|---|---|---|
| DIFF-GAP-1 | Medium | Source Analysis file inventory / Code Risk change scope | Only files Source Analysis reads are fingerprinted. 95 real modifications (62 Markdown, 32 Blazor `.razor`, 1 HTML) are "without a content fingerprint"; the count is stated, but they are excluded from "changed files" | Changed Blazor components are not in the Code Risk "Changed files" scope; Markdown spec changes are invisible at file level (delivery covers specs) | Fingerprint every non-ignored file (hash only; no content retained), or state the excluded extensions in the UI note | DECISION |
| DIFF-GAP-2 | Medium | Delivery comparison | Tasks are compared by id and checkbox only; 116 task texts whose file references changed with the module rename, and 4 changed plan.md files, leave their features "Unchanged" | "Changed task references" and plan changes are not reported; the word "Unchanged" over-claims | Compare task text (with an id-stable "text changed" kind) and plan fingerprints, or say "progress unchanged" | DECISION |
| DIFF-GAP-3 | High | Source Change Impact / Unified Impact | Repository identity comes from the archive file name (`m2lb` vs `m2lb-language`). Impact analysis requires equal identity and, unlike Code Risk Review and Test Coverage, ignores the remembered reviewer declaration (`source_repository_associations`) | The authentic version pair can only be impact-analysed through a DERIVED copy uploaded under the baseline's name | Let impact analysis use the shared `BaselineEligibility` with remembered declarations (the association service already documents exactly this M2LB pair) | DECISION (scope of the association was deliberately limited to two modules) |
| DIFF-GAP-4 | High | Unified Impact — Project Import context | With an imported project loaded, the page sends its import id and the backend requires both snapshots to carry that same import id. The import id derives from the archive hash, so every other version is a different import → HTTP 409 "do not match this Project Import" | Version-to-version impact from an imported workspace is structurally unreachable; the page only offers same-identity baselines | Bind runs to a project identity spanning imports (e.g. the declared repository association) rather than to a single archive's import id | DECISION |
| DIFF-GAP-5 | Low | Impact — Database domain | 12 table changes appear twice with identical text and files: BirkNext's model holds two table entries for the same logical name (different database ids) and the change shows only the logical name | Looks like duplicate rows; reviewer cannot tell them apart | Qualify the object with its database/context, or merge when the entries are the same table | OPEN / HUMAN REVIEW |
| DIFF-GAP-6 | Medium | Contract identity | Contract ids are path-based; 19 contracts under renamed modules appear as removed + added, never compared field by field | Contract evolution across a module rename is invisible | Offer a move candidate (same file name + same producer) for review, never automatic | DECISION |
| DIFF-GAP-7 | Low | GraphQL operation documents | Operations are compared by identity only; a same-name operation whose selection set changed (1 real case) is not reported; fragments are stated as "not expanded" | Client-consumption changes missed | Compare a normalized operation body hash as "operation changed (review)" | DECISION |
| DIFF-GAP-8 | Low | File moves | 139 exact-hash move candidates are shown as removed + added without a move hint | Noise in change review | Show exact-content moves as candidates (never confirmed) | DECISION |
| DIFF-HR-1 | — | Canonical relations | 7,681 added / 6,083 removed relations; arithmetic and direction verified, 2,459 removals touch renamed modules; the rest follow translated identifiers | Semantic correctness needs interpretation rules | Human review sample | HUMAN REVIEW |
| DIFF-HR-2 | — | Observability impact | 74 changes do not pair across directions, 28 cite no file, 29 cite only unchanged files | Possibly identity-derived (component names) rather than file changes | Human review | HUMAN REVIEW |
| DIFF-HR-3 | — | Impact wording | Unified rows such as "DirectRelation · Confirmed" (1,116) use the architecture evidence state "Confirmed", not a reviewed impact | May be read as confirmed impact | Label as "source dependency confirmed; impact not reviewed" | HUMAN REVIEW |

## Registered follow-up gaps (2026-11-07, after review) — all OPEN

None of these is implemented. They are not part of the Execution Evidence task.

| Id | Area | Gap | Relation | Status |
|---|---|---|---|---|
| GAP-UIA-1 | Unified Impact | Explicit baseline/current source snapshot selection regardless of archive file name (repository identity is derived from the name today) | DIFF-GAP-3 | OPEN |
| GAP-UIA-2 | Unified Impact | Selecting versions inside an imported project's context (each version is its own import id today → HTTP 409) | DIFF-GAP-4 | OPEN |
| GAP-DLV-1 | Delivery comparison | Compare changed plan.md content and task text/references (116 reference changes and 4 plan changes are reported as "Unchanged" today) | DIFF-GAP-2 | OPEN |
| GAP-MOVE-1 | Delivery / contracts / files | Explicit, evidence-based classification of module renames and moves (folder matching works, but it is not stated; contracts and files appear as removed + added) | DIFF-GAP-6, DIFF-GAP-8 | OPEN |
| GAP-FILE-1 | Source Analysis file inventory | Investigate the 95 changed Markdown/Blazor/HTML files without a content fingerprint and give them an accurate coverage status (currently "not read", counted outside "changed") | DIFF-GAP-1 | OPEN |
| GAP-TD-1 | Test Data consumers | Reusable data selection/binding inside API Quality Review, Performance Test Review and Skolenærvær (readiness and binding exist via Test Data and its API only) | Test Data implementation report | OPEN — planned inside the contract-driven API flow |
| GAP-TD-2 | Validation | Arbitrary XSD validation of authored XML (not re-verified in this pass; the XML fixture editor is Test Pack bound) and JSON Schema validation (missing; shown as Not assessed) | Test Data implementation report | OPEN — separate, unverified/missing |

## Not run (by scope)

These edge cases were not exercised on derived real data in this pass, because none occurs in the authentic pair according to the oracle:
- added-only and removed-only versions;
- rename-only, CRLF-only, whitespace-only and case-only renames;
- conflicting requirement ids;
- partial import;
- an interrupted comparison;
- a snapshot changed while a comparison is open.

Corrupt, empty, duplicate-path and traversal archives were covered by the previous acceptance (RD-NEG-1…5).

The SkoleAdapter identity conflict was preserved and not resolved.
