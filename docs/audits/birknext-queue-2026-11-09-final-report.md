# BirkNext — queue of 2026-11-09 — final report

Branch `008-traceability-first`, local only (no push, no deployment, no external system). Queue start: `1c279a0c`. Final: the commit
carrying this file.

## Commits

| Commit | Item | Acceptance | Regression on that commit's tree |
|---|---|---|---|
| `190ce1b0` | Partition-6 backend test stalls — root cause (xUnit sync-context workers blocked by synchronous host start starved the loopback test servers) | evidence in `birknext-backend-test-stall-root-cause-2026-11-09.md` | six partitions green |
| `d440e37d` | Performance Test Review — approved baselines, comparability gate, regression verdicts, trends, export | `birknext-performance-baseline-acceptance-2026-11-09.md` | see its gap register |
| `514478d2` | Messaging observability — imported, sanitized message evidence, processing state, test-run correlation | 31/31 | BE 6239/0/19, FE 8503/0 |
| `5e30f803` | Parquet Data Explorer | 44/44 | BE 6255 passed / 1 failed (`ScenariosQueryTests` p95 timing; 5/5 isolated), FE 8508/0 |
| `ad1ede3f` | Azure Blob Storage Explorer (synthetic store only) | 44/44 | BE 6267/0, FE 8514/0 (after a scratch-tree setup correction, see its gap register) |

## Session interruption and recovery

The implementing session ended at 2026-11-09 11:45:02Z, one second after a harness file write completed; its background regression was
killed by the session teardown. No crash or resource-exhaustion event was logged by Windows; memory guards recorded ≥ 5.7 GB free.
**The cause of the termination is not established.** Recovery found the Parquet work complete but uncommitted (regression cut off after
partitions 1–2) and the Blob work partially written and not compiling. Parquet was regressed in full and committed from a byte-verified
Parquet-only tree; Blob was completed (compile fixes, migration, acceptance, one product defect fixed with a regression test) and committed.
Nothing was reset, stashed or discarded.

## Open across the queue (not closed by unrelated tests)

- Real Edge attended login with normal certificate trust; expected-audience validation; AQR stored-contract selection in the browser;
  YAML/Swagger 2 structural parsing; parallel API execution; dataset-driven chained execution; scheduled retention; unsupported
  authentication modes.
- Messaging and Blob: no real Service Bus, Wolverine database or Azure storage account was contacted — synthetic only.
- Parquet: files from third-party writers (Spark, pyarrow, Parquet.Net) not verified; nested/list columns and ZSTD/LZ4/Brotli not read.
- `ScenariosQueryTests` warmed-p95 assertion is load-sensitive (failed once at 2.7 s under partition-6 load, passes in isolation).
- Intermittent `launch-code.txt.tmp` file-lock race between two test hosts in a mixed focused run (`ActiveEventAuthorizationTests`), not
  reproduced in full partitions.

## Scratch resources awaiting approval before deletion

Databases `birknext_pb_20261109`, `birknext_mo_20261109`, `birknext_pq_20261109`, `birknext_bs_20261109`; containers `bn-pb-target`,
`bn-mo-target`; scratch folders `pbval/`, `movalidation/`, `pqvalidation/`, `bvalidation/`, `webpub-pb/`, `webpub-bs/`, `wtb/`.
