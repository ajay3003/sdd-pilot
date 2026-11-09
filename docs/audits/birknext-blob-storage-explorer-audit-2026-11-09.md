# BirkNext — Azure Blob Storage Explorer — audit (2026-11-09)

Branch `008-traceability-first` (local only). Baseline: `5e30f803` (Parquet Data Explorer).

## What existed before this change

| Capability | Before | Evidence |
|---|---|---|
| Browse containers / blobs | **Missing** | `Azure.Storage.Blobs` was referenced only to list Event Hub checkpoint containers (`Services/Integrations`) |
| Read blob properties / metadata | **Missing** | — |
| Preview JSON / XML / CSV / NDJSON / Parquet from storage | **Missing** | the Parquet engine (`Services/Parquet`) opened uploaded local files only |
| Check an expected artifact produced by an integration | **Missing** | Execution Evidence recorded HTTP calls, never their storage side effects |
| Correlate a blob with a test execution | **Missing** | the execution id header `X-BirkNext-Execution-Id` existed (messaging work) but nothing read it back from storage |
| Saved storage observations | **Missing** | — |
| Azure identity for runtime evidence | Present (`IntegrationAzureCredential`, disabled unless `IntegrationReview:Azure:Enabled`) | reused, not duplicated |

## Decisions

- **Read-only by construction.** The store interface (`IBlobReadStore`) has exactly four operations: containers, list, properties, read.
  No upload, delete, lease, snapshot, tier, metadata or SAS operation exists anywhere in the API or the stores; a test asserts this.
- **Two store kinds.** An Azure storage account (identity-based, disabled when BirkNext has no Azure identity) and a **synthetic local**
  store (a folder, Development only) that is labelled "not Azure" in the API and in the UI. Acceptance used only the synthetic kind.
- **Stores are configured per environment** (`BlobStorage:Stores`), with an optional container allow-list. Nothing is configured by default.
- **Correlation needs proof.** A blob is linked to a test execution only when that execution's id appears in its metadata or name.
  A matching time is reported as "not proof", never as a correlation.
- **Observations store no content**: identity, properties, redacted metadata, check results, correlation and a SHA-256 of the bytes read.
- **One Parquet engine.** Parquet previews and checks call the Parquet Data Explorer's reader; there is no second implementation.
- **UI placement:** a *Storage* tab on Integration Topology (beside *Messages*), not a new module.
