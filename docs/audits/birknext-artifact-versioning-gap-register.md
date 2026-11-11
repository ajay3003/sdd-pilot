# BirkNext — Artifact versioning & provenance: gap register (2026-11-11)

- **Audit:** `birknext-artifact-versioning-audit.md`
- **Implementation:** `../hardening/birknext-artifact-versioning-implementation.md`
- **Acceptance:** `birknext-artifact-versioning-acceptance.md`

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Closed by this work

| ID | Gap | Status | Evidence |
|---|---|---|---|
| AV-C1 | Executions did not know their dependencies as an immutable unit | PASS | `execution_manifests` written in the run's transaction; DB trigger refuses UPDATE. Unit tests and acceptance AV-04, AV-17, AV-19, AV-31. |
| AV-C2 | Pinned references carried no content identity | PASS | Contract SHA-256, test revision document SHA-256 and Test Data fingerprint, each checked against psql or the file on disk (AV-05–AV-07). |
| AV-C3 | Any caller could declare definition references | PASS | Owner-resolved vs caller-declared provenance. Owner-only fields are `[JsonIgnore]`. Caller-declared refs never make a result current (unit test). |
| AV-C4 | Suite runs executed current test revisions, not the suite's pinned ones | PASS | `PinnedRevisions`. Unit test: the target never received the newer revision's header. |
| AV-C5 | No historical vs currently-applicable distinction | PASS | Applicability verdict per run. A historical PASS stays Passed (AV-16, AV-18, AV-20, AV-30). |
| AV-C6 | Artificial revisions: Test Data re-pin, identical regenerate, identical Setup Library version | PASS | No new revision or version (unit tests; AV-15). Contract identical re-import was already correct (AV-14). |
| AV-C7 | Critical E2E kept only the current flow definition | PASS | Append-only `flow-versions.json`, backfill, fingerprint on runs, stale-by-fingerprint (unit tests; AV-23, AV-32). |
| AV-C8 | The BirkNext build was not recorded per execution | PASS | `Build` in every manifest (AV-09). The scratch tree reports `unknown` because it has no `.git`; a real build is stamped from git. |

## Remaining gaps

| ID | Gap | Status | Owner | Note |
|---|---|---|---|---|
| AV-G1 | Manifests cover Execution Evidence runs only | PARTIAL | Platform | Critical E2E, performance, journeys, Parquet and Active Event/CDC keep their own pinning (audit §3); a unified manifest for those owners is not built. Critical E2E now pins its definition content. |
| AV-G2 | Critical E2E runs do not pin Test Data versions | NOT SUPPORTED (yet) | Critical E2E | Aliases and asset ids only, as before. |
| AV-G3 | Chain and composite steps run current test revisions | PARTIAL by design | Contract Testing | Recorded exactly in the manifest; a composite run is not forced to the suite's pinned test revisions. |
| AV-G4 | Snapshot Registry does not list contracts, tests, suites, flows or manifests | NOT RUN | Platform | The registry model fits (generic type/id, fingerprints, edges); new types and adapters are future work. |
| AV-G5 | Release readiness, Impact Analysis and Execution Explorer do not read applicability | NOT RUN | Interpretation / Explorer | Release readiness still reports execution dimensions as NotAssessed (unchanged). Nothing claims a historical result is current, but consumers do not yet show the verdict. |
| AV-G6 | Contract Testing "last result" and the suite run list do not show applicability | NOT RUN | Contract Testing UI | Visible on the run page (Execution Results) only. |
| AV-G7 | No shared version-history / compare component | NOT RUN (deprioritised) | UI | Deliberate: manifests and pinning first. Flow versions are API-only. |
| AV-G8 | Workspace packages carry runs as history but not their manifests | NOT SUPPORTED (v1 format) | Backup & Transfer | Imported runs are history records; a full backup does contain manifests. |
| AV-G9 | Connected Systems, integration catalog, Target Environments, performance definitions have no revision history | NOT SUPPORTED | Owners | Unchanged. Endpoint bindings are pinned by fingerprint in the manifest, so a binding change is still detected as Changed. |
| AV-G10 | Contract-version allocation under concurrent imports | NOT RUN | Contract registry | The unique index protects the data; a concurrent import would surface a database error rather than a friendly conflict. Not reproduced. |
| AV-G11 | Contract test approval has no who/when | NOT SUPPORTED | Contract Testing | `ReviewState` only, as before. |
| AV-G12 | Test Data `Remove(alias)` deletes every version | OPEN (detected) | Test Data | A pinned run's dependency becomes **Removed** and the run Historical; the deletion itself is unchanged. |
| AV-G13 | Scratch databases `birknext_av_20261111`, `birknext_av2_20261111` | CLOSED | User | Dropped on 2026-11-11 after your approval (no open connections; psql confirms none remain). |
