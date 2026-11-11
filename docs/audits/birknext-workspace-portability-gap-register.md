# BirkNext — Workspace backup, export, import and recovery: gap register (2026-11-11)

Audit: `birknext-workspace-portability-audit.md`. Implementation: `../hardening/birknext-workspace-portability-implementation.md`.
Acceptance: `birknext-workspace-portability-acceptance.md`.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Audit gaps

| ID | Gap | Status | Evidence |
|---|---|---|---|
| WP-G1 | No full backup | PASS | `FullBackupService`; unit tests against real pg_dump; browser WT-11/12 |
| WP-G2 | No database ↔ file consistency boundary | PASS | Exported snapshot + file re-check with retry and drift label (unit drift test); limitation WP-R5 |
| WP-G3 | No encryption | PASS | AES-256-GCM STREAM + PBKDF2 (crypto tests); WT-13/14 |
| WP-G4 | No restore | PASS | Isolated restore + independent validation; WT-15–17; restored installation 4/4 |
| WP-G5 | No portable package | PASS | `.birknext` v1 (format document); WT-06/09 |
| WP-G6 | No integration transfer | PASS | Integration closure; WT-04, WT-20–34 |
| WP-G7 | No portable identity | PASS | Logical ids from natural keys; uuid kept only when free; identity map (round-trip tests) |
| WP-G8 | No conflict model | PASS | 9 classifications, allowed actions per item (unit); WT-42/43 |
| WP-G9 | No two-phase import | PASS | Preview writes nothing (WT-20); fingerprint + destination check (unit); rollback and retry (unit) |
| WP-G10 | No per-project authorization in BirkNext | PARTIAL | A new `WorkspaceTransfer` capability gates every operation; scope is the server-side closure only; a hand-edited cross-project object is refused (unit). True per-project authorization does not exist in the platform — see WP-R1. |
| WP-G11 | Application commit unknown at runtime | PASS | `SourceCommit` assembly metadata (git, or `unknown` — the scratch build reports `unknown`, honestly) |
| WP-G12 | `CriticalE2EStore.ClearAll` takes the wrong lock | NOT RUN | Out of scope; recorded |
| WP-G13 | Suggestion reviews and Parquet sessions not cleared by Local Data Reset | NOT RUN | Out of scope; recorded |

## Defects found during verification and fixed

| ID | Defect | Found by | Fix |
|---|---|---|---|
| WP-D1 | A clean file capture was labelled drift (`"FILES_UNCHANGED…".Contains("CHANGED")`) | unit test | exact token |
| WP-D2 | Environment-bound objects skipped for lack of a destination were not reported | unit test | reported as UNRESOLVED |
| WP-D3 | Diagnostic redaction ran on serialised JSON; `"` escapes hid identifier digits inside embedded JSON | unit test | redaction walks the JSON tree |
| WP-D4 | `CheckStatus` clashed with the frontend's own type | frontend build | renamed `TransferCheckStatus` |
| WP-D5 | An export acknowledgement survived a new review | bUnit | each review starts unacknowledged |
| WP-D6 | Integration and project evidence missed the environment's own contract-test runs | browser WT-28 | runs of the environment and of the package's contract tests (DefinitionRef) are included; regression test |
| WP-D7 | Import report readiness rows overflowed at 768 px | browser WT-L5 | wrapping row |
| WP-D8 | CI compile error CS1003: the .NET 8 compiler parses `x is A.B ? [..]` as a nullable array type | Azure DevOps build; repository guard `CompilerCompatibilityTests` | parenthesised pattern (`f641cbb3`) |
| WP-D9 | The secret scanner flagged random GUIDs (eleven digits + a letter in the last group) as identifier-shaped, an intermittent export blocker; redaction could rewrite part of a GUID | intermittent round-trip test failure | GUID spans masked for the identifier rule and preserved by redaction; `PackageSecretScannerTests` |

## Remaining gaps

| ID | Gap | Status | Owner | Note |
|---|---|---|---|---|
| WP-R1 | Per-project authorization | NOT SUPPORTED (platform) | Platform | BirkNext has no project-scoped permissions. Packages cannot add them; a WorkspaceTransfer holder can export any project. |
| WP-R2 | Package authenticity (signatures) | NOT SUPPORTED | Platform | Packages are integrity-checked only, and say so in the manifest and the UI. Signing needs a key-management decision. |
| WP-R3 | Not in v1 packages | NOT SUPPORTED | Platform | Performance test definitions and data profiles; canonical interpretation; delivery reporting; saved SDD workspaces (they have their own JSON export); manual-review and test-coverage decisions; suggestion reviews and their locator verifications (destinations regenerate suggestions and must re-verify DOM). Full backups contain all of these. |
| WP-R4 | Target Environment profiles are browser-held | PARTIAL | Platform | Carried only in integration packages, created as a new disabled draft through the page; a full backup cannot contain them. |
| WP-R5 | No application-wide write barrier | PARTIAL by design | — | Database and files are consistent when files are stable across the snapshot (verified with retries); otherwise the backup is labelled, never claimed point-in-time. |
| WP-R6 | Cross-machine restore of an encrypted backup | NOT RUN | Operator | Verified on one machine and server; DPAPI stores are deliberately excluded, so credentials must be re-entered anyway. |
| WP-R7 | Cutover to a restored database | NOT SUPPORTED by design | Operator | A documented manual procedure (operations guide §5). |
| WP-R8 | Module pages after import (Connected Systems, Integration Suites, Critical E2E UI) | PARTIAL | QA | Imported state was verified by database rows, `flows.json` and the API; those pages were not driven in this acceptance. |
| WP-R9 | Scratch databases from acceptance | OPEN (needs approval) | User | `birknext_wtx_src_20261111`, `birknext_wtx_dst_20261111`, `birknext_wtx_dst2_20261111`, `birknext_wtx_dst3_20261111`, `birknext_restore_1780c6057e64`, `birknext_restore_f8cbfc0e2cda`. Kept; deletion needs your approval. |
| WP-R10 | MU → MM compatibility | OPEN (domain, unchanged) | Domain owner | Still Incompatible and not accepted after transfer (WT-31); see `birknext-mu-mm-skolenaervaer-contract-gap.md` (MM-G1). |
