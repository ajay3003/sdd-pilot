# Workspace persistence — implementation (task 19C)

Root cause: `docs/audits/birknext-workspace-persistence-audit.md`. Acceptance: `docs/audits/birknext-workspace-persistence-acceptance.md`.

## Design

| Concern | Decision |
|---|---|
| Separation | The lifecycle (workspace metadata, revisions with identity, role, scope, fingerprint, import records and their history, decisions) stays in `saved_workspaces.sdd_lifecycle_json`, with **content addresses** instead of bodies. Document bodies live in `workspace_document_bodies`, keyed by the lowercase hex SHA-256 of their UTF-8 bytes. No second source repository: source archives and Source Analysis snapshots are untouched. |
| Upload | The client hashes each body once (cached per revision) and asks `POST api/workspace-persistence/bodies/missing` which addresses are missing. It uploads only those through `POST …/bodies`, at most 7.5 M characters per request (the server limit is 8 M characters, request cap 24 MB). The server **verifies each address**, and a wrong one refuses the whole batch. A retried upload is idempotent. |
| Write integrity | `PrepareForStorageAsync` resolves each revision's body from the request, then from the stored lifecycle (legacy inline rows), then from the store. A revision that claims a body that cannot be resolved makes the save fail with **409 `document-bodies-missing`**: nothing is written. This closes the former silent loss, where `FillMissing` dropped the claim and stored an empty body. The row is written in one `SaveChanges`; bodies are immutable, so an orphan left by an interrupted save is harmless. |
| Read | Load, bootstrap and documents put the bodies back (`WithDocumentBodiesAsync`), so every reader sees the same complete lifecycle as before. Bootstrap still defers bodies through the existing manifest. |
| Immutability | Migration `20261112103830_AddWorkspaceDocumentBodies`: table plus a `BEFORE UPDATE` trigger that refuses changes. DELETE stays possible (Local Data Reset deletes every model table). |
| Migration of existing data | Lazy and loss-free: a legacy row keeps its inline bodies until its next save, which moves them into the store. Workspace ids, import records, history, baselines and decisions are untouched. There is no bulk rewrite. |
| Honest status | `IWorkspaceAutoSaveService.SaveState` (Idle, Pending, Saving, Saved, Failed) with `LastSaveError` from the persistence layer. The new `WorkspaceSaveIndicator` beside Activity shows "Saving workspace…" or "Not saved. <reason>. Your changes are kept in this browser." with **Retry**. It never says "Saved" after a failure, never clears local state, and never retries in a loop. Saves still happen only on changes or an explicit retry. |
| Rejected alternative | Raising the request limit. It keeps posting tens of MB per save and only moves the cliff. |

## Files

| Area | Files |
|---|---|
| Backend | `Models/WorkspaceDocumentBodyRecord.cs`, `Data/AppDbContext.cs`, `Data/Migrations/20261112103830_AddWorkspaceDocumentBodies*.cs`, `AppDbContextModelSnapshot.cs`, `Services/WorkspaceDocumentBodies.cs` (store, prepare, restore, missing-body exception), `Services/WorkspaceLifecycleDocuments.cs` (`FillMissing` keeps unresolved claims and carries addresses), `Services/WorkspacePersistenceService.cs` and `IWorkspacePersistenceService.cs` (writes prepared; three body methods), `Controllers/WorkspacePersistenceController.cs` (bodies endpoints, 409 mapping, reads restore bodies) |
| Frontend | `Models/SddLifecycleModels.cs` (`ContentSha256`, `WithoutBody`, `WithRevisions`), `Services/WorkspaceDocumentBodyUploader.cs`, `Services/WorkspacePersistenceApiService.cs` (all three save paths; `LastSaveError`), `Services/WorkspaceAutoSaveService.cs` (save state), `Layout/WorkspaceSaveIndicator.razor(.css)`, `Layout/MainLayout.razor` |

## Tests

- **Backend `WorkspaceDocumentBodiesTests`:**
  - stored once and an exact round trip;
  - a manifest resolves, and an unknown address writes nothing;
  - the silent-loss path is refused;
  - a legacy row stays readable and moves into the store;
  - a wrong address refuses the whole batch, and uploads are idempotent;
  - 5, 35 and 75 MB workspaces in bounded batches, round-tripped byte for byte.
- **Backend `WorkspacePackageRoundTripTests.A_stored_workspace_document_body_is_immutable_in_the_database`:** real PostgreSQL; the trigger's own message is asserted.
- **Frontend `WorkspaceDocumentBodyUploaderTests`:**
  - only missing bodies are uploaded, once each;
  - batches are bounded;
  - the live state is unchanged;
  - a failed upload is a failed save;
  - the auto-save state is Failed with its reason, never Saved.
- **Frontend `AutoSaveHttpFlowTest`:** the test now selects the auto-save request explicitly, because the save first asks which bodies are missing. Its assertions about the artifacts sent are unchanged.
