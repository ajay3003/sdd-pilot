# Local data reset

System Settings → Maintenance → **Reset Local Data** returns this BirkNext installation to "no project loaded". The rule: after a reset,
no page shows, and nothing can save again, a previous project or the workspace, review or evidence state derived from it (for example the
M2LB Source Analysis snapshot, the `m2lb.child-security-classification` domain extension, Security Classification or Pipeline Review
results). The reset keeps only installation-level configuration, provider capabilities, UI preferences and files outside BirkNext.

The button was renamed from "Reset Local Database" to **Reset Local Data**, because it clears more than the database: the reset epoch,
backend caches, browser storage and in-memory state.

## Availability and safety

The backend enforces the policy itself, whatever the UI shows (`AdminService.CheckResetPolicy`). All of these must hold:

- `AdminSettings:AllowLocalDatabaseReset=true`;
- `DatabaseSettings:Mode=Local`;
- a Development, Local or Test backend environment;
- a loopback PostgreSQL host (`localhost`, `127.0.0.1` or `::1`).

Production, an unknown environment, a remote host and a shared database mode are all refused, so an unknown environment fails safe. The
Maintenance diagnostic reports whether reset is available.

### Active operations

The reset does not run while something is writing to the stores it clears:

| Condition | Result |
| --- | --- |
| A CDC or performance run executes in this backend process (`ActiveCdcRunCoordinator.HasRunning`, `PerformanceTestExecutionService.HasActiveRuns`) | `Blocked` (HTTP 409); nothing is changed |
| A CDC or performance run row is `Running` in the database | `Refused`; nothing is changed |
| Another reset is in progress | `Blocked` (HTTP 409); the reset never runs twice |
| A pending or in-flight auto-save in this tab | Auto-save is paused and the in-flight save finishes before the request is sent; it resumes only after the frontend is cleared |
| Owned authenticated browser sessions, Companion pairings | Disposed or forgotten by the reset (they belong to Target Environments that the reset removes) |

## Order of operations

1. **Frontend:** pause auto-save, then send `POST /api/admin/reset-database` with the body `{"confirmation":"RESET"}`.
2. **Backend coordinator** (`LocalDataResetCoordinator`):
   1. Apply the gate and active-operation checks.
   2. Record the CDC key floors.
   3. Delete every application data table in **one transaction**. A failure rolls back and changes nothing else.
   4. Advance the **reset epoch**.
   5. Clear backend state that is not in the database (see the matrix).
3. **Structured result:**
   - `Status`: `Completed`, `CompletedWithWarnings`, `Blocked`, `Refused` or `Failed`.
   - Also returns `ResetId`, `ResetAt`, `ResetEpoch`, `DatabaseCleared`, `DeletedRows`, `BackendStateCleared`, `ClearedDomains`,
     `PreservedDomains` and `Warnings`.
   - A backend step that fails after the database commit becomes a warning. The database is clean either way; it is the authoritative store.
4. **Frontend coordinator** (`ApplicationRuntimeResetService.ClearFrontendRuntimeStateAsync`). This runs only after a successful result:
   1. Record the new epoch.
   2. Clear every frontend store and project localStorage key, and reset Target Environments to the generic seed.
   3. Rebuild ReviewContext and the navigation badges.
   4. Resume auto-save.
   5. Navigate to the Dashboard. It shows "Local data was reset", or the warnings.
5. A failed, refused or blocked reset leaves the frontend untouched.

The reset is idempotent. Running it on an empty installation completes and advances the epoch again.

## Resurrection protection: the reset epoch

The epoch is stored outside the database, in `App_Data/local-data-reset.json` (configurable with `LocalDataReset:StatePath`), so it
survives both the reset and a backend restart.

**Why it is needed:** auto-save creates a new workspace from its payload when no workspace is current. Without the epoch, any tab that
still holds M2LB would bring M2LB back on its next auto-save.

**How it works:**

- `GET /api/workspace-persistence/current-state` returns `resetEpoch`.
- `save-current` and `auto-save` must send it back.
- After the first reset, a write with an older epoch, or with no epoch, gets **409 `{code:"stale-reset-epoch", resetEpoch}`** and changes
  nothing.
- When a tab receives that 409 (`LocalDataResetEpoch.StaleDetected`, handled in `MainLayout`), it clears its own frontend state the same
  way and navigates to the Dashboard ("Local data was reset in another tab").
- **Multi-tab:** another open tab therefore cleans itself on its next write instead of saving the old project. A tab that never writes keeps
  showing old in-memory content until it is reloaded. That content cannot be saved, and every backend read already returns the empty state.
- A tab running a pre-epoch build sends no epoch, so all its workspace writes are refused after the first reset. Reloading the tab fixes
  it.
- **No empty workspace after a reset.** An auto-save that has no artifacts, a blank project and no current workspace returns 204. Before
  this change it created an empty `Auto_…` workspace on the next navigation, so the installation looked like it had a project again.
- **No target is not an outage.** Without an active Target Environment, the reset's normal end state, Technology Coverage and the
  navigation badges now say "Select a Target Environment". Before, they sent `environmentId=` (400) and reported "The BirkNext backend did
  not answer".

**CDC key floor:** the same file keeps the highest synthetic CDC PersonPK sent per environment. Deleting the run rows must never let a
later run reuse a key that already exists in the real Event Hub or downstream system (`ActiveCdcRunStore.MaxPersonPkAsync`).

## Matrix

### Database (PostgreSQL, one transaction, model-derived: new mapped tables are included by default)

| Domain | Entity sets | Cleared | Reason |
| --- | --- | --- | --- |
| Workspaces and SDD lifecycle | `SavedWorkspaces`, `SavedWorkspaceArtifacts`, `WorkspaceReviewProgress` | Yes | Project state. Includes revisions, baselines, questions, decisions, traceability, CodeLinks and TRX test evidence (in lifecycle JSON), plus the selected sample project slug |
| Review and traceability | `Scenarios`, `ReviewedCandidates`, `CandidateLinks`, `QaDeltaReviews`, `TraceLinks`, `TraceabilitySuggestions`, `CodeFiles`, `CodeLinks`, `ProjectDocuments` | Yes | Project state |
| Source Analysis and integration evidence | `IqrSourceSnapshots`, `IntegrationQualitySnapshots`, `IntegrationReviewRuns`, `ApplicationMessagingEvidence`, `ActiveCdcRuns` | Yes | Project evidence: snapshots and everything derived from them (architecture, database, observability, technology inventory, CI/CD, Pipeline Review, Technology Coverage) |
| Target-environment integration catalog | `IntegrationPlatforms`, `IntegrationDefinitions`, `IntegrationEnvironmentStates`, `IntegrationContractArtifacts`, `GraphQlSchemaArtifacts`, `IntegrationMessageFlows` | **Yes (changed)** | These are project state, not installation configuration. `IntegrationEnvironmentStates.seed_name = m2lb-dev-eventhub` is the source of the M2LB domain extension in Technology Coverage. Before this change they were preserved, which is why M2LB stayed visible |
| Analysis and security results | `DependencyReviewRuns`, `DependencyInventories`, `DependencyHealthRuns`, `ScimEvidence`, `SecurityClassificationEvidence`, `SecurityExpectationDiscoveries`, `AzureEnvironmentSnapshots` | Yes | Project evidence |
| Performance | `PerformanceTestDefinitions`, `PerformanceTestDataProfiles`, `PerformanceTestRuns`, `PerformanceBaselines` | **Yes (definitions and profiles changed)** | Definitions and data profiles target a project's endpoints |
| Schema | `__EFMigrationsHistory`, tables and indexes | No | The reset deletes rows; it does not drop the database |

### Backend outside the database

| Domain | Cleared | Reason |
| --- | --- | --- |
| Critical E2E flows and history (`App_Data/critical-e2e/flows.json`, `history.json`, in memory) | Yes | Project flows and evidence |
| BirkNext-owned authenticated browser sessions | Yes (disposed) | Bound to removed Target Environments |
| Browser Companion pairings and challenges | Yes | Bound to Target Environments. The installed extension pairs again on request |
| Captured authenticated API credentials (`TransientAuthenticatedApiContextStore`) | Yes | Belong to a project target |
| Security-classification test contexts | Yes | Project test data |
| Browser-automation diagnostic results | Yes | Name Target Environments |
| Performance target reachability checks | Yes | Per project target |
| `IMemoryCache` entries | Yes (compacted) | Review caches |
| Reset epoch, last reset time, CDC key floors (`App_Data/local-data-reset.json`) | Kept and advanced | Resurrection protection; no key reuse |
| Performance provider and runtime status (k6 image, Podman) | No | Host capability |
| Dependency advisory cache (nuget.org/OSV responses) | No | Global, not project data |
| Local HTTPS proxy server, managed Edge processes and profiles, proxy certificate, Azure sign-in | No | User-started tooling and machine identity. They no longer refer to any stored project |
| Logs | No | Diagnostic history |

### Frontend (memory and browser storage)

| Domain | Cleared | Reason |
| --- | --- | --- |
| Workspace artifacts, SDD lifecycle, current project and workspace id, restore metadata | Yes | Project state |
| Quality, dashboard, runtime review, task alignment and extraction sessions; integration mapping evidence | Yes | Project results |
| ReviewContext and navigation applicability badges | Rebuilt from the empty state | Derived |
| Target Environments (`birknext:frontend-analysis-settings`) and detection snapshots | Yes: replaced by the generic seed (Local / Development / QA / Production at `example*.local`) | The profiles hold project URLs, integrations, thresholds, authentication and expectations |
| Endpoint discovery, API review history, integration target hints (`birknext:endpoint-discovery`, `birknext:api-review`, `birknext:integration-target-registry`) | Yes (memory and key) | Per project target |
| Extraction session (`birknext:extraction:session`), standalone editors (`ce-standalone-constitution`, `pe-standalone-plan`, `te-standalone-tasks`, `dme-standalone-datamodel`) | Yes | Project documents |
| Diagram layouts (`architecture-layout:*`, `database-layout:*`) | Yes | Keyed by source snapshot |
| BirkNext-owned authenticated browser session, Browser Companion following | Yes | Project targets |
| Feature visibility, sidebar section state, theme | No | UI preferences |
| Sample Project catalog | No | Installation content. A sample can be loaded again |

### Never touched

- Source code, binaries, `appsettings*.json`, provider installation configuration and the sample project files.
- Any user or library file outside BirkNext. Source Analysis archives are processed in memory and never stored on disk, so there is nothing
  to delete.

## Known limitations

- **Per-page scanner caches.** WASM security and WASM performance scanner results live in a page component. They disappear on navigation,
  and the reset always navigates to the Dashboard.
- **Proxy and Edge sessions keep running.** A running local HTTPS proxy or managed Edge window is not stopped. Stop it in Authentication if
  it is no longer needed.
- **Restart required for the fix.** A backend started before this change still runs the old reset, which preserved the integration catalog.
  Restart it to get this behaviour.

## Verification

- **Backend:** `LocalDataResetTests` cover:
  - the epoch persisting across a restart;
  - backend state cleared;
  - idempotence;
  - a failed reset changing nothing;
  - refused and blocked results;
  - concurrent resets;
  - the CDC key floor;
  - stale auto-save and save-current refused without creating a workspace;
  - an auto-save with the current epoch accepted.
- **Frontend:** `LocalDataResetFrontendTests` and `ResetLocalDatabaseTests` cover:
  - the stores cleared;
  - the seed profiles;
  - the localStorage keys removed and the seed written back;
  - idempotence;
  - stale refusal raising the layout handler;
  - an unrelated 409 not treated as a reset.
- **Live smoke against an isolated database:**
  1. Populate it with a copy of an M2LB workspace.
  2. Reset through the UI.
  3. Check that every page (Source Analysis, Technology Coverage, Environment Analysis, Security Classification, Pipeline Review,
     Performance, IQR, Dependency Review, Critical E2E) shows the empty state and no M2LB name, archive or snapshot id.
  4. Check that a reload and a backend restart stay clean.
  5. Check that a stale-epoch auto-save gets 409.
