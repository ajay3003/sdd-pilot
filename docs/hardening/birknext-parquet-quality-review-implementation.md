# BirkNext — Parquet Quality Review: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `f55c7c49`. Audit and gap matrix: `docs/audits/birknext-parquet-quality-review-audit.md`.
Local commits: `52f3c395` (backend, contracts, migration, tests) · `13f1dc5b` (UI, client, bUnit) · reports commit.

## Architecture: one reader, one engine, existing stores

| Concern | Where | Notes |
|---|---|---|
| Reader | `ParquetFile` / `ThriftCompact` (unchanged) | no second Parquet engine |
| Dataset contracts | `Services/Parquet/Quality/DatasetContractParser.cs` | pure parser over Markdown retained with Source Analysis snapshots |
| Retention | `IqrSourceStore` (snapshot save) | dataset-contract documents (a Parquet column table) and documents stating renames of their versions are kept in `source_contract_documents` (bounded: 40 documents, 4 MB). Snapshots analysed earlier hold no contract text → discovery says so (re-analyse) |
| Discovery, association, rules, review, baseline, evidence, report | `Services/Parquet/Quality/ParquetQualityService.cs` | reads snapshots through `IReviewSourceEvidenceProvider` (explicit project + snapshot) |
| Rule generation | `ParquetRuleGenerator` | classified rules with provenance; `ParquetSchemaChecker` |
| Evaluation | `ParquetInspectionService.Quality.cs` | the existing validation engine, extended (no second engine) |
| Approved rule sets | new table `parquet_rule_sets` (migration `20261110113017_AddParquetRuleSets`) | insert-only versions per project + contract + contract version |
| Evidence | existing `parquet_inspections` (document version 2) | no new evidence store |
| Runs | existing Execution Evidence (read-only lookups) | |

## Contract discovery and association

- The parser recognises structure only: a heading naming `container/…parquet` followed by a `Column | type | Null | content` table, the
  document's `Contract version`, a Location table, a Manifest JSON block and a Determinism section. Content cells that are exactly an
  enumeration become allowed values; `YYYY-MM` becomes a format. Prose sentences of a dataset section are each a requirement; only these are
  machine-checkable: backticked identities `A + B = C` ("for each `g`"), "add up to `X`", "`A` and `B` appear only with `c = V`", "sorted by …",
  "Never present: …", "Only for `X`" (null column), one row group, codec. Everything else is listed as NOT SUPPORTED with a reason; Kode 6/7
  statements are NEEDS DOMAIN DECISION.
- Renames (`a` → `b`) are taken from the contract header and related documents and kept only when `b` is a contract column or value.
- Discovery per file: AUTHORITATIVE_CONTRACT only from grounded evidence (an attached manifest naming this file's SHA-256 and the contract
  version, or a person's earlier confirmation for this exact file); CANDIDATE_MAPPING from schema overlap / documented renames / the storage
  container name (a hint only); AMBIGUOUS when equally good candidates exist (e.g. the same document in two projects); UNRESOLVED otherwise
  (generic inspection, "No contract selected"). Every association keeps project, snapshot, document path + SHA-256, version, match,
  confidence, reasons and review status. The server re-resolves the association on every use (document text unchanged, version never
  switched silently) and validates declared mappings (file column exists, contract column exists, one to one, no hiding of a present column).
- Real case: the imported `bufdata-leveranse.md` is `1.0.0-draft` (English names). The sample uses the pre-translation names; only
  `periode`→`period` and the value renames are documented, so the sample is schema-incompatible as delivered and can be validated only through
  person-declared mappings (labelled; schema dimension "Partial").

## Rules

| Origin | From | Default state |
|---|---|---|
| CONTRACT_DERIVED (column facts) | column table: presence, type, not null, allowed values, format | Active |
| CONTRACT_DERIVED (technical) | determinism (row groups, codec), manifest, "Never present" identifier scan, Kode 6/7 canary scan | Active |
| CONTRACT_DERIVED (business) | sum identity, roll-up (excluding values allowed only with the total), allowed combinations, conditional null, sort order | NEEDS REVIEW — not run |
| APPROVED_BUSINESS_RULE | a business rule present with the same parameters in an approved rule-set version | Active |
| SCHEMA_DERIVED | the file's own schema when no contract is selected | Active ("readability, not correctness") |
| USER_AUTHORED | rule editor (required, required-when, unique (composite), allowed values, range, format, row count) | Active |

Every rule records provenance (document:line), semantics (incl. interpretations a reviewer must confirm), contract version, rule version,
evaluation scope (schema / metadata / full scan), exposure (counts and row numbers only). The server reconciles rule sets from the browser: a
generated rule is always taken as generated (a client cannot activate or "approve" it); other rules are USER_AUTHORED. Approval stores a new
immutable version (`parquet_rule_sets`); rejected rules make their requirement NOT APPLICABLE.

New rule kinds in the existing engine: Pattern, NullUnless, RequiredWhen, OnlyWith, SumIdentity, RollUp, SortedBy, RowGroupCount,
Compression, Manifest, NoIdentifierValues (11-digit detector shared with Execution Evidence redaction), NoProtectedCanary (approved
`protected-identity` Test Data references; a hit fails, absence is never proof). Results add rule name, origin, contract version, requirement
ids, mode (Exact / Metadata / NotAvailable), affected rows, severity, evidence reference; group details and drill-down filters are live only.
Mapped columns read through documented value renames (e.g. `TOTALT`→`TOTAL`).

## Schema, coverage, dimensions

- `ParquetSchemaChecker`: missing, unexpected (identifier-like = High), physical, logical, decimal precision/scale, nullability (file optional vs
  contract not-null = warning; the data rule decides), nested groups → Compatible / Compatible with warnings / Incompatible.
- Requirement coverage: COVERED_PASS (all linked active rules passed) · COVERED_FAIL · NOT_ASSESSED (not run, awaiting review, manifest missing,
  domain decision) · BLOCKED (a needed column is missing/unreadable) · NOT_SUPPORTED · NOT_APPLICABLE. A rule definition or a saved report is
  never counted as execution; the page states the denominator (requirements extracted from the document).
- Five dimensions, never one PASS: File readable · Schema compatible · Data valid · Business rules verified · E2E evidence verified (only a
  CONFIRMED run correlation passes it). Next action: Select contract → Confirm contract → Generate rules → Run validation → Review findings →
  Link evidence.

## Profile, data, comparison

- Statistics: exact full scan (rows, nulls, distinct, min/max for numeric/date non-identifier columns, category distributions up to 50 values,
  duplicate rows), plus the contract aggregate pivot (e.g. count by category × measure). Footer statistics stay labelled as metadata.
- Data grid: column search/visibility, multiple typed filters as chips (between = From/To, value lists, empty/not empty; `NotIn` added),
  explicit "current page" vs "whole file" scope, paging of full-scan matches (previously impossible), row detail, rule drill-down. Sorting:
  NOT SUPPORTED (stated on the page).
- Baseline: another local file (composite keys; row order never identity; duplicate keys ambiguous; identifier key columns masked) or saved
  evidence (summary level). Comparability checks (same file, dataset contract, contract version, approval). Classification REGRESSION (rule
  pass→fail), IMPROVEMENT, EXPECTED_CHANGE (different period), INCOMPATIBLE (schema no longer satisfies the contract), INCONCLUSIVE (changed
  counts/aggregates without an approved expectation), NOT_COMPARABLE (different dataset). Saved evidence can be approved as a baseline
  (annotation only).

## Evidence, correlation, report

- Saving re-runs the review on the server and stores: file identity (SHA-256, schema fingerprint, schema columns), source (temporary local
  session), association + snapshot, rule set ref, results (no live details/filters), coverage, dimensions, sanitized statistics (category values
  only when the contract enumerates them), manifest summary, baseline assessment, comparison counts (record keys never stored — and stripped
  from older documents on every read/export), correlation, limitations.
- Correlation: direct run lookup (no newest-500 limit); environment and project must match; CONFIRMED when the run's captured evidence
  contains the file SHA-256; CANDIDATE when it shares the source snapshot or references the contract; otherwise the tester must choose a
  MANUAL_UNVERIFIED association with a reason; nothing linked = NOT_CORRELATED.
- Report: Markdown, 14 sections (summary, dataset, contract, coverage, passed / failed / not assessed, statistics, schema, baseline,
  confidentiality, correlation, limitations, evidence references) from saved evidence only, through the existing `downloadBase64File`.

## Sessions and safety

64 MB enforced client + server (unchanged; no limit raised). Two-hour expiry (lazy + sweep on open). New: at most 20 live sessions on disk
(oldest closed), at most 4 decoded files in memory (eviction on open — previously unbounded), sessions survive page navigation (recent files
per browser, validated against the server), explicit "Close file". Manifest attachment ≤ 64 KB, only documented fields kept. No endpoint
takes a server path; no external call.

## UI (`/parquet-viewer`, "Parquet Quality Review")

Compact source bar (name, source, size, rows, columns, expiry, contract chip) and a guided empty state. Four areas:
**Quality Overview** (dimensions, KPIs, next action, contract with reasons/governance/mappings/confirmation, prerequisites, manifest),
**Validation** (rules / requirements / schema checks, approve-reject, type-specific editor with preview), **Explore & Compare** (data, schema
with technical tree, profile, compare), **Evidence & History** (run picker, save, history, detail, report, export, approve baseline).
