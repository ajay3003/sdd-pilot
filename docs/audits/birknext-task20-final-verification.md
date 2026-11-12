# Task 20 — final verification

**HEAD at start:**
- `85d4a48c` on `008-traceability-first`;
- preceded by `1d01dd6d` (Test Plan Suggestions) and `581e6314` (Help Center);
- git status: only the excluded files (`appsettings.Local.json`, `AIAssisted/testdata/`, other sessions' audit documents).

## The previously failing test

`WorkspaceBootstrapPayloadTests.Auto_save_and_save_from_a_manifest_keep_the_stored_bodies`, an outdated test from 19C (`437c455f`).
- **Original failure:** the test read the raw stored row and expected inline document bodies. Since 19C the bodies live in the content-addressed store.
- **Fix (already in `85d4a48c`):** the test reads through `WorkspacePersistenceService.WithDocumentBodiesAsync`, the same call every controller reader uses (`WorkspacePersistenceController` lines 94, 114, 135). It also asserts that the raw row holds no inline body.
- It checks the application's real behaviour (bodies restored for every reader), not a storage detail, so it is stricter, not weaker.
- **Retest:** `be-focused.ps1 -Filter 'FullyQualifiedName~WorkspaceBootstrapPayloadTests'` on HEAD `85d4a48c` (scratch tree, built from HEAD): **PASS 8/8**, 0.8 s test run (19 s including host start).

## Baseline full regression on HEAD `85d4a48c`

Six sequential partitions, partition 6 split into three; memory-guarded; no concurrent heavy suite.

| Partition | Passed | Skipped | Failed | Total |
|---|---|---|---|---|
| 1 / 2 / 3 / 4 / 5 | 1 049 / 1 099 / 840 / 652 / 521 | 2 / 5 / 0 / 1 / 3 | 0 | 1 051 / 1 104 / 840 / 653 / 524 |
| 6a / 6b / 6c | 139 / 983 / 1 311 | 0 / 0 / 8 | 0 | 139 / 983 / 1 319 |
| **Backend** | **6 594** | **19** | **0** | **6 613** |
| **Frontend** | **8 710** | 0 | 0 | 8 710 |

The Release build of the API and web was published for acceptance (`dotnet publish -c Release`, exit 0).

## Performance

See `birknext-test-plan-suggestions-performance.md`.

## Final regression (MT-7 + performance tree)

See the "Final" section of `birknext-mt7-acceptance.md` and the commit message of the MT-7 commit.
