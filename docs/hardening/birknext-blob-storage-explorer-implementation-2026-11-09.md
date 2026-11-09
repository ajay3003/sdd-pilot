# BirkNext — Azure Blob Storage Explorer — implementation (2026-11-09)

Branch `008-traceability-first` (local only). Audit: `docs/audits/birknext-blob-storage-explorer-audit-2026-11-09.md`.

## Flow

Integration Topology → **Storage**: choose a store → container → browse virtual folders (paged, "Load more") → properties (metadata with
sensitive values masked) → bounded preview → **Check an expected artifact** (exact name, or a folder ending in `/` for the newest blob at any
depth below it) → **Save observation**. Saved observations are listed per environment and can be deleted (the blob is never touched).

## Code

| Area | Files |
|---|---|
| Contracts | `shared/BlobStorageContracts.cs` (stores, readiness, listing, properties, preview, expectation, check levels, correlation, observation) |
| Stores | `Services/BlobStorage/BlobReadStores.cs` — `BlobStorageOptions` (`BlobStorage:Stores`), `IBlobReadStore`, `AzureBlobReadStore` (read-only SDK calls through BirkNext's Azure identity), `SyntheticLocalBlobStore` (Development only, sidecar `.metadata.json`, path-safe) |
| Engine | `Services/BlobStorage/BlobExplorerService.cs` — readiness, listing, properties, previews (JSON; XML with DTD refused; CSV with delimiter detection and formula neutralisation; NDJSON with malformed-line report; Parquet through `Services/Parquet`), expectation checks by level (Exists → Metadata → Readable → Syntax → Content → Correlation), correlation by exact execution id, observations |
| API | `Controllers/BlobStorageController.cs` — `api/blob-storage` (GET stores/containers/blobs/properties/preview, POST validate; observations POST/DELETE need the integration-configuration write permission) |
| Persistence | `BlobObservationRecord`, table `blob_observations`, migration `20261109132134_AddBlobObservations` |
| UI | `Components/BlobStoragePanel.razor(+css)`, `Services/BlobStorageApiService.cs`, Storage tab in `IntegrationTopologyPage.razor` / `TopologyWorkflow.cs` |

## Defect found by acceptance and fixed

**BS-35 — a folder expectation never found blobs in sub-folders.** Expectation search reused the one-level browse listing (delimiter `/`),
so `2026/` saw only the virtual folder `2026/11/` and reported "No blob under 2026/". Regression test
`BlobExplorerTests.A_prefix_expectation_searches_every_level_below_the_prefix_while_browsing_stays_one_level` failed first; the store
listing gained a `flat` mode (Azure: flat `GetBlobsAsync`; synthetic: no folder collapsing) used only by expectation search. Browsing stays
one level.

## Tests

`BlobExplorerTests` (12, backend), `BlobStoragePanelTests` (frontend), `IntegrationTopologyPageTests` tab list.
