# Real Project Acceptance

Real Project Acceptance runs **every BirkNext page against one real, external project** — imported once through the production
Project Import flow — and reports, per feature, what was actually verified. It is test infrastructure only: no production code knows
about it, about any dataset, or about the archives it uses.

## What kind of test is this?

| Kind | Input | Proves | Where |
|---|---|---|---|
| Unit / component tests | Small synthetic fixtures | One class or component behaves | `BirkNext.Web.Tests`, `BirkNext.Api.Tests` |
| Integration tests | Synthetic fixtures through real services | Services work together | `BirkNext.Api.Tests` |
| **Real source acceptance** | An external real project archive | Every page handles a real project's documents and source truthfully | this framework |
| Real runtime acceptance | A paired runtime Target Environment | Runtime reviews against a running system | **not exercised** — runtime features report `NotVerified` |

Source evidence is not runtime evidence. A feature that needs a running target reports `NotVerified` when no runtime profile is paired —
that is the correct, successful outcome, not a failure and never a pass.

## Running it

The archive always stays outside the repository. Never copy it in, embed it, base64 it or unpack it into `SampleData`.

```powershell
# Standard run (default) against an external archive:
./AIAssisted/scripts/run-real-project-acceptance.ps1 -Dataset M2LB -Archive "<path outside the repo>\project.zip"

# Smoke (import, workflow, dashboard, first explorer, Source Analysis, consistency) or Full (more widths):
./AIAssisted/scripts/run-real-project-acceptance.ps1 -Archive "<path>" -Mode Smoke
```

The script creates a throwaway Postgres database, starts the backend (fresh database, `ProjectCompatibility__AcceptanceArchivePath`
pointing at the same archive so the System Settings diagnostics measure it too) and the frontend on their own ports, runs
`dotnet test --filter Category=RealProjectAcceptance`, then stops both servers and drops the database.

Environment variables (the script sets them; set them yourself to run against servers you started):

| Variable | Meaning |
|---|---|
| `BIRKNEXT_REAL_ACCEPTANCE_DATASET` | Dataset id (default: first registered, `M2LB`) |
| `BIRKNEXT_REAL_ACCEPTANCE_ARCHIVE` | Explicit archive path for the selected dataset |
| `BIRKNEXT_REAL_PROJECT_<ID>` | A dataset's own archive variable (e.g. `BIRKNEXT_REAL_PROJECT_M2LB`) |
| `BIRKNEXT_REAL_PROJECT_M2LB_SHA256` | Pin another approved M2LB archive version |
| `BIRKNEXT_REAL_ACCEPTANCE_MODE` | `Smoke`, `Standard` (default), `Full` |
| `BIRKNEXT_ACCEPTANCE_FRONTEND_URL` / `BIRKNEXT_ACCEPTANCE_BACKEND_URL` | Pre-started servers |
| `BIRKNEXT_ACCEPTANCE_FRONTEND_BACKEND_URL` | Backend URL the frontend build is configured with (requests are rewritten to the acceptance backend) |
| `BIRKNEXT_ACCEPTANCE_ARTIFACTS` | Report root (default `AIAssisted/artifacts/real-project-acceptance`, gitignored) |

Without a configured archive the test is **skipped** with `External real-project dataset not configured.` — ordinary CI never fails
because an external archive is absent, and never reports a silent pass. A configured archive whose SHA-256 differs from the descriptor is
refused (`HashMismatch`): a changed input is never used silently.

## What a run does

1. Fingerprints the archive (SHA-256, size) before anything is imported.
2. Imports it **once** through `/project-import` (preview fingerprint → import). Documents go to the current workspace, source to one
   Source Analysis snapshot. Every later feature reuses that workspace and snapshot; nothing is re-uploaded.
3. Visits every page in sidebar order (`BrowserAcceptanceFeatureRegistry`), each as one feature with its own result.
4. Runs cross-feature checks: all features used the same snapshot/project; explorer render identity matches what Explorer Text Coverage
   measured; navigation and a full reload keep the workspace; representative pages at desktop and phone width (overflow, axe, keyboard).
5. Writes `acceptance.json` and `acceptance.html` (plus screenshots of failures and captured exports) under the artifacts root.

### Safety

Acceptance never sends Event Hub or Service Bus messages, never performs mutations or destructive API calls, never contacts a runtime
target, and never clicks a run button of a target-gated review. A dataset's runtime profile defaults to everything `false`; there is none
for M2LB. Reports and exports are checked for and redacted of bearer tokens, JWTs, connection strings, secret assignments and national ids.

## Reading a result

Each feature reports six independent states — never one blended score, and there is no overall percentage:

| State | Values |
|---|---|
| Execution | Completed, Partial, Blocked, Failed, NotApplicable, NotAvailable |
| Evidence | Verified, PartiallyVerified, NotVerified, NotApplicable |
| Data | RealDataObserved, NoApplicableData, NotAssessed, Unsupported |
| Provenance | Traced, PartiallyTraced, Missing, NotApplicable |
| Export | Exported, Failed, NotAttempted, NotSupported |
| Browser | Rendered, Failed, NotUsed |

Rules applied to every feature: `NotAssessed` is never `Verified`; zero items is `NoApplicableData`/`NotAssessed`, never a pass;
source-derived data needs provenance; runtime evidence without a runtime profile is a defect. The summary buckets features into
Completed, Partial, NotApplicable, NotVerified, Blocked and Failed. A run succeeds when no feature failed and no run-level defect exists.

## Adding a dataset

1. Implement `IRealProjectDatasetProvider` in `frontend/BirkNext.RealProjectAcceptance/Datasets/` — id, archive environment variable,
   expected SHA-256, and **semantic** expectations per feature id (`RealData` with optional `MustMention` facts, `NotApplicable`,
   `RuntimeNotVerified`). Establish expectations from the archive's actual contents, not guesses; avoid exact counts except baselines bound
   to the archive hash (`HashBoundBaselines`).
2. Register it in `KnownRealProjectDatasets.Registry()`.
3. Run with `-Dataset <ID> -Archive <path>`.

No feature, runner or production change is needed. If a page misbehaves for the new project, fix the product generically — never add
`if (project == …)`.

## Adding a feature

Add a class deriving from `AcceptanceFeature` (browser) under `BirkNext.Web.PlaywrightTests/RealProjectAcceptance/Features/` and register
it in `BrowserAcceptanceFeatureRegistry`, or list the page in `ClassifiedElsewhere` with the reason it is not exercised. Use production
routes and controls only; read the run's snapshot through `SnapshotAsync`, record the snapshot/project the page shows with
`RecordIdentity`, and set states honestly.
