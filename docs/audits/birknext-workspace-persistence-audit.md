# Workspace persistence — audit (task 19C)

Baseline: `6b682770` on `008-traceability-first`.

## Observed failure

In the real-archive acceptance (seven large M2LB imports), every workspace auto-save failed with:

> `Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException: Request body too large. The max request body size is 30000000 bytes.`

Loading also failed ("Error loading workspace … Failed to fetch"). Browser-side state such as import records and their history was then no longer saved.

## Measured composition

The last successfully saved lifecycle (`saved_workspaces.sdd_lifecycle_json` in the acceptance database) was 23 MB. By top-level key:

| Key | Size | Items |
|---|---|---|
| `Revisions` | **19 MB** | 1 368 |
| `GeneratedDocumentContents` | 1.0 MB | — |
| `GeneratedDocumentationSets` | 83 kB | 4 |
| `ProjectImports` | 3.6 kB | 4 |

Revisions per import scope: about 340 documents and **4.8–5.2 MB per M2LB import**. Each revision carries its full document text (`Content`).

## Root cause (not assumed — traced in code)

| Question | Finding |
|---|---|
| Where is state serialized? | Frontend `WorkspacePersistenceApiService` (`AutoSaveAsync`, `SaveCurrentAsync`, `SaveAsAsync`) posts `JsonSerializer.Serialize(SddLifecycle)`: the **whole** lifecycle, every body, on **every** save |
| Storage | One `text` column per workspace (`saved_workspaces.sdd_lifecycle_json`); the backend treats it as opaque JSON |
| Application-level limit | None |
| Transport limit | Kestrel's default `MaxRequestBodySize`, 30 000 000 bytes. The auto-save endpoint has no `RequestSizeLimit`. |
| Frontend limit | None. The whole state is held in memory. |
| Duplication | The same document is stored once per import scope. A re-zip (`M2LB.zip` = `M2LB (3).zip`) or the identical documents of alternative M2LB branches are stored again. |
| Base64 / ZIP / snapshots embedded? | No. The ZIP is never serialized and Source Analysis snapshots are not embedded. Bodies are plain JSON strings. JSON escaping adds a little. |
| Existing partial mechanism | A **manifest** path already existed. Bootstrap sends the lifecycle without bodies; a write without a body is completed from the stored lifecycle (`WorkspaceLifecycleDocuments.FillMissing`). But a client with loaded bodies (every client right after an import) always sent them all. |
| Partial write / retry | A refused request writes nothing (no corruption). The debounced auto-save retries on the next change; the 413-class failure repeats forever. |
| **Silent-loss path** (found during the audit) | `FillMissing` removed `ContentLength` even when it found no stored body, so a revision whose body was neither sent nor stored was saved with an **empty** body |
| Honest UI? | No. Dashboard and Recommended Workflow show the last persisted status ("Auto-saved") after failures, and nothing tells the person that their work is not saved. |

**Root cause:** the persisted unit was the whole mutable lifecycle with every immutable document body inline. Its size grows linearly with imports
(about 5 MB per M2LB import), and every save re-sent all of it until the transport limit refused the request.

## Decision

Document bodies are immutable once captured (a change is a new revision), so they are stored **once, content-addressed**, apart from the mutable
lifecycle. This reuses the existing manifest mechanism and adds no second source repository: source archives and Source Analysis snapshots are
unaffected. Raising the request limit was rejected, because it only moves the cliff and keeps re-sending tens of MB per save.
Details: `docs/hardening/birknext-workspace-persistence-implementation.md`.
