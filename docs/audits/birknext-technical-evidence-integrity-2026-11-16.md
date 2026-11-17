# Technical evidence integrity and testing completeness (2026-11-16)

Base: `4116df33` on `008-traceability-first` (local commits only, nothing pushed). Ports 5097/5197, disposable PostgreSQL databases, synthetic
fixtures only. No external system (TT02, m2lbdev, Altinn, MU, MM, Azure DevOps, scanners) was contacted. The user's services on 5000/5173 and
their databases were not touched. No new module, engine or registry: every change extends the owner the audit found.

## 1. Original gaps and what the code showed

| # | Reported gap | Evidence in the code (before) | Classification | Result |
|---|---|---|---|---|
| 1 | Requirement links trust the browser's reading of the specification | Specification text passes through Project Import and is discarded; the server holds no requirement list (only `ImplementationGapEvaluations` ids without titles or files, and only when tasks.md exists). Journey links and manual case links were checked against the list the browser sends. | **Confirmed** | Server catalog (sections 3–4) |
| 2 | Composite runs do not pin test revisions | The flat suite run passes `PinnedRevisions`, but `RunChainAsync` loaded each test's **current** revision and the chain's current revision; the run record stored the plan but not what ran, environment or engine. | **Confirmed** (composite only; flat suite runs already pinned) | Pinned definition (sections 5–6) |
| 3 | Technical history browser-only | API Quality Review: already server-side (task 5). Frontend Quality Review, safe fuzzing and authorization checks: browser `localStorage` only (10/5/5 per environment). Performance Test Review, IQR, Contract Testing, Integration Suites: server-side. Critical E2E: server files. | **Partially resolved** before; **confirmed** for FQR, fuzzing, authorization | Server storage (section 7) |
| 4 | A single stage cannot be retested | `Preflight` accepts only a prefix of stages; `RunChainAsync` refuses a subset that misses a dependency. No retest link for suite runs. | **Confirmed** | Stage retest (section 8) |
| 5 | Finding resolution not comparable enough | `VerifiedResolved` needed a later run of the same environment with the target reviewed; a *partially* reviewed target counted as reviewed, a changed contract was not detected, and there were no findings for integration stages. | **Partially resolved** before | Comparability rules (section 9) |
| 6 | Attachments: no scanning or retention | Allow-list + image/PDF signatures existed; text types only checked for NUL bytes (any JSON/XML/HTML-in-.txt passed); filename kept control and bidirectional characters and leading formula characters; no scan state; no deletion path; download headers: attachment + nosniff only. | **Confirmed** | Sections 10–12 |
| 7 | Playwright suite needs ports 5000/5173 | Self-starting fixture hard-codes 5000/5173 and the shared `birknext` database and may run `compose up`; the pre-started fixture polls 5000/5173; three env-guarded acceptance tests **returned as passed** when not configured. | **Confirmed** | Section 13 |

## 2. Evidence ownership (after this task)

| Record | Owner (storage) | Project identity | Revision / version | Who / when | Immutability | Downstream |
|---|---|---|---|---|---|---|
| Specification requirement catalog | `SpecificationCatalogService` (`specification_catalogs`) | `import:{importId}` | Revision per content fingerprint | Captured at import commit | Append-only (DB trigger) | Journey links, manual case links, check endpoint |
| Journey ↔ requirement link | `JourneyRequirementLinkStore` (`integration_journeys.journey_requirement_link`) | ProjectKey | Journey revision, catalog revision, wording hash | LinkedBy/At + history | Append-only history, concurrency token | Coverage, Traceability |
| Manual case requirement link | Test plan draft (`TestCaseDraft.RequirementLinks`) | Plan's project | Catalog revision, wording hash (server-checked links) | LinkedBy/At | Plan revisions | Coverage, Traceability |
| Suite revision | `integration_suites` | WorkspaceProjects | Suite revision + fingerprint | GeneratedBy/At | Insert-only revisions | Runs |
| Composite run | `integration_suite_runs` + Execution Evidence | Definition.ProjectKey | Pinned definition (suite r, plan r, test revisions) + SHA-256 | Initiator, StartedAt | DB trigger refuses any change of a pinned row; unique per execution | Run view, findings, explorers |
| Stage retest | `integration_suite_runs` (`RetestOfRunId`, `RetestStageId`) | as above | Original's pinned revisions | Initiator, StartedAt | as above | Run view, finding resolution |
| API Quality Review run | `api_review_runs` | ProjectKey (nullable for older) | Report as produced | GeneratedAt | Append-only | Verify, findings |
| FQR / fuzzing / authorization run | `technical_review_runs` | ProjectKey | Report SHA-256 | GeneratedAt | Append-only (DB trigger) | Verify, FQR `?storedRun=` |
| Technical finding | `technical_findings` | ProjectKey | Version token; comparison basis | History events | Append-only history | Verify |
| Evidence file | `manual_run_attachments` | ProjectKey | SHA-256 | AddedBy/At; WithdrawnBy/At/Reason | Only withdrawal allowed (DB trigger) | Run view |

Still browser-only (named, not hidden): browser discovery snapshots, API review drift baselines; the browser copies of all reviews remain as a
fallback. Critical E2E history stays in server files (not a database) — unchanged.

## 3. Server-authoritative requirements (design)

* **Capture.** At Project Import commit, while the archive's documents are in memory, `SpecificationCatalogService.CaptureAsync` classifies the
  Markdown documents with the shared `ArtifactDocumentDiscovery` classifier and parses every *Specification* with the shared
  `SpecExplorerService.Parse/BuildSemanticModel` (the same code the browser runs; it is compiled into the API). Each requirement is stored with its
  identifier, archive path, wording and the wording's SHA-256; documents with their SHA-256. One row per content revision (same content = same
  revision, idempotent under concurrent commits); rows are append-only (trigger). The document text itself is not kept.
* **Identity.** Identifiers use the shared grammar (`FR-1` = `FR-001`, case-insensitive). The same identifier in two specification files is two
  entries; resolving it needs the file. A browser path may omit the archive root folder (`specs/…` vs `LendoPlus/specs/…`); a bare file name never
  matches across folders. Prose tokens such as `UTF-8` are not requirements unless the parser reads them as requirement lines.
* **Validation.** New journey links and new manual case links are resolved against the current server catalog when the project has one; the
  browser's list is then not consulted (a requirement the browser invents is refused). Projects without a server catalog keep the previous check
  and are labelled.

## 4. Legacy requirement links

Nothing is relabelled or deleted. Each link records `CheckedAgainst` (`server-specification` / `browser-specification` / absent = before checks
were kept), and server-checked links record the catalog revision and wording hash. A read-only check
(`POST api/specification-catalog/{project}/check`) reports, per link: *In the current specification*, *Wording changed since it was linked*
(also for any link without a recorded wording hash — it is not called current), *Not in the current specification*, or *No server copy of the
specification* (import again to verify). Human confirmation (Proposed/Confirmed/…) is a separate axis and is never treated as source proof.
Integration Testing → Prepare shows both per link.

## 5. Composite run snapshot model

`CompositeExecutionDefinition`, written once when a composite run starts (`integration_suite_runs.DefinitionJson`): owning project, suite id/key,
suite revision and fingerprint, journeys the suite was prepared for (with revisions), plan (chain) id and revision, the stages, every step with its
stored test id **and test revision**, source snapshots (id, archive SHA-256, approval), environment id, target URL, configuration fingerprint,
engine (`BirkNext contract-test chain runner {version}`), start time, initiator, and a SHA-256 over the definition content. No credential or secret.
The chain runner is now held to it: `ExpectedChainRevision` (a plan changed since is not run) and `PinnedRevisions` (each step runs the stored
revision, never newer edits; a retired or missing pinned revision is not run).

## 6. Historical immutability

* A pinned run row cannot be updated by anyone (PostgreSQL trigger `integration_suite_runs_no_rewrite`); one row per execution (unique `RunId`).
* The run view computes **Changed since this run** (newer suite revision, revised plan, revised step tests) without touching the run.
* Runs recorded before pinning are shown as legacy: no definition is fabricated; they cannot be retested comparably or followed up as stage findings.
* Cancelled, failed and partial runs keep their own execution evidence states (unchanged owner: Execution Evidence).

## 7. Technical review persistence

`technical_review_runs` stores finished Frontend Quality Reviews, safe fuzzing runs and authorization checks (kind, project, environment, target,
time, outcome, counts, summary, report SHA-256, report JSON as produced), idempotent per kind + environment + time, append-only. The pages store a
run when it finishes (best effort; the browser copy stays the fallback). A report containing a credential-named property with an unredacted value
is refused — and the value is not echoed. Technical Testing → Verify reads FQR, fuzzing and authorization runs from the server for the selected
project (runs of another project are excluded; runs stored without a project — older API review runs — are shown). A stored FQR run opens in its own
page (`frontend-quality-review?storedRun=`); fuzzing and authorization reports are reachable through the API but their panels still reopen only the
browser copy (residual).

## 8. Stage retesting

`GET …/composite-runs/{run}/retest-plan?stageId=` explains what a retest takes; `POST …/composite-runs/retest` runs it. Rules:
* only a pinned run; only a stage that was part of it;
* the run's own environment (another environment is refused: start a new run instead);
* a stage that takes values from earlier stages (`FromStep`) is retested together with its dependency closure — values captured in the original
  run are never reused; without `IncludePrerequisites` the retest is refused with the reason;
* the original definition's revisions are enforced by the chain runner; explicit authorisation and data-changing confirmation as for any run;
* the retest is a new row linked to the original (`RetestOfRunId`, `RetestStageId`); the original lists its retests and is never changed.

## 9. Finding-resolution evidence rules

Outcomes recorded with each retest event (never re-evaluated): *Absent*, *Still present*, *Not reviewed*, *Not comparable*, *Inconclusive*. Only
*Absent* can make a follow-up *Verified resolved*; *Not comparable* and *Inconclusive* say "review needed" and can be recorded as an observation
without changing the status.
* API Quality Review: same project and environment, later run, finding absent, the target **completed** (partially completed = inconclusive) and the
  target's contract unchanged (`ComparisonBasis` = contract kind/version/title; changed = not comparable).
* Integration suite stage (new): follow up a failed/timed-out stage of a pinned run; verification needs a later pinned run (retest or new run) of the
  same suite, project and environment whose stage ran **the same test revisions** (`ComparisonBasis`) and succeeded; failed again = still present;
  not reached = not reviewed; unknown outcome = inconclusive; legacy run = not comparable.

## 10. Attachment security (enforced)

Allow-list unchanged (png, jpg/jpeg, gif, webp, pdf, txt, log, csv, json, xml, har; content type ours, never the browser's); 10 MB per file, 20
files per run; project ownership on upload, list, download and withdrawal (another project: 404). New content checks: PDF without active content
markers (`/JavaScript`, `/JS`, `/Launch`, `/EmbeddedFile`, `/RichMedia`, `/XFA`); text strictly UTF-8 and not an HTML page; JSON/HAR parse (HAR
needs `log`); XML parses with DTDs prohibited. Safe names: last segment, control and bidirectional/format characters removed, `"<>:|?*;` replaced,
leading `= + - @` prefixed; the uploaded name is kept as metadata only. Downloads: `Content-Disposition: attachment`, `nosniff`,
`Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: no-store`, `Cross-Origin-Resource-Policy: same-origin`. Uploaded files are
never executed, rendered inline or parsed beyond these checks.

## 11. Malware scanning limitations

No scanner exists in this deployment and none was contacted. `IEvidenceFileScanner` is the extension point: when a host registers an available
scanner, a file is stored only as `clean` or refused as `rejected`; otherwise it is `not-scanned` and the UI says **not scanned for malware**. The
database refuses changing a file's scan state afterwards. PDF marker checks do not see inside compressed object streams; the type checks reduce,
but do not replace, scanning.

## 12. Retention design

* Metadata (name, original name, type, size, SHA-256, who/when) is kept with the run's history.
* Binary content is kept until (a) a person **withdraws** the file with a reason — content removed, record kept with who/when/why, the run's
  evidence entry unchanged, download answers 410; or (b) an explicit Local Data Reset (all project data).
* No automatic deletion and no time-based retention job: there is no documented policy for one; adding it needs that policy.
* Backup & Transfer packages do not include attachment content (unchanged; documented).

## 13. Playwright isolation

`BrowserTestServers` decides where browser tests run: an external isolated instance (`BIRKNEXT_PLAYWRIGHT_FRONTEND_URL`/`_BACKEND_URL`; developer
ports 5000/5173 are refused unless `BIRKNEXT_PLAYWRIGHT_ALLOW_DEV_PORTS=1`), or — only with `BIRKNEXT_PLAYWRIGHT_START_SERVERS=1` — self-started dev
servers against `BIRKNEXT_PLAYWRIGHT_DATABASE` (default `birknext_playwright`, never the shared `birknext`); compose is run only with
`BIRKNEXT_PLAYWRIGHT_START_POSTGRES=1`; only owned processes are stopped. `[BrowserFact]`/`[BrowserTheory]` report unconfigured tests as
**skipped with the prerequisite** (the three env-guarded acceptance tests no longer pass silently). Self-starting classes share a non-parallel
collection. The real M2LB tests stay blocked on `BIRKNEXT_REAL_PROJECT_M2LB` / `BIRKNEXT_M2LB_ARCHIVE` (no fabricated M2LB evidence).
Every fixture browser context is connected to the API's local session through `BrowserTestServers.ConnectAsync` (launch code from
`BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE`, `BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE`, or the self-started API's `App_Data/local-session/launch-code.txt`);
without it every page stopped at *Connect this browser to BirkNext*.

## 14. Tests

All on the final task-6 code; backend built to `bin/Verify/net8.0` (the user's API on :5000 holds `bin/Debug`).

| Suite | Result |
|---|---|
| Frontend (bUnit, `BirkNext.Web.Tests`) | **9 212 passed**, 0 failed, 0 skipped |
| Backend partition 1 | 1 052 passed, 2 skipped |
| Backend partition 2 | 1 101 passed, 5 skipped |
| Backend partition 3 | 894 passed |
| Backend partition 4 | 682 passed, 1 skipped |
| Backend partition 5 | 541 passed, 3 skipped |
| Backend partition 6 | 2 655 passed, 8 skipped |
| Backend total | **6 925 passed**, 0 failed, 19 skipped (pre-existing environment skips) |
| Release publish (API + Web) | 0 errors; all 87 boot-manifest asset hashes verified |

New or extended tests (backend, PostgreSQL where storage rules matter — the triggers only exist there):

* `SpecificationCatalogTests` (6, PostgreSQL): classification through the shared discovery, idempotent capture and revisions, append-only trigger,
  concurrent capture, resolution (id, path suffix, ambiguity), check states, journey links server vs. legacy.
* `CompositeSuiteTests` (+4, run in memory **and** on PostgreSQL via `CompositeSuitePostgresTests`; 26/26): pinned definition → stage failure →
  tracked finding → retest with prerequisites → *Verified resolved*; *Still present* and *Not comparable* outcomes; *Changed since this run* and
  legacy runs; concurrent retests of one stage.
* `EvidenceIntegrityDatabaseTests` (PostgreSQL): pinned suite-run trigger and unique run id; stored review runs (idempotent, project isolation,
  credential refusal without echo, append-only); attachment withdraw-only trigger. `EvidenceFilePolicyTests`: signature, PDF active content,
  strict UTF-8, HTML in text, JSON/HAR/XML parsing (DTD prohibited), safe and original names.
* Frontend: composite run definition/retest panel (4), journey requirement source/provenance chips (1), stored reviews in Technical Testing (1),
  attachment scan chip and withdrawal (1), `TechnicalReviewArchiveTests` (builders refuse credential paths, project scoping).

Known flake (not task 6, not weakened): `ExecutionEvidenceRedaction` credential masking in a composite wrong-correlation test failed once during
development when a random GUID happened to contain the masked fragment; it passed on three reruns and in the full run above.

## 15. Browser acceptance

Isolated published Release build: API on **:5097**, static web on **:5197**, each phase on a fresh PostgreSQL database copied from the synthetic
template (`birknext_t6_20261116`, `birknext_t6link_20261116`, `birknext_t6ado_20261116`, `birknext_t6pw_20261117`, `birknext_t6final_20261117`).
Network guard on every script: any request outside 5097/5197 fails the run. Edge (Chromium) headless.

| Script | Checks |
|---|---|
| **t6 (this task)** — server catalog, check endpoint, forged-link refusal, provenance chips; stored FQR/fuzzing/authorization runs, credential refusal, project isolation, Verify page, `?storedRun=`; spoofed/HTML files refused, safe filename, *not scanned* chip, download headers, cross-project 404, withdraw → 410; widths 1440/1280/1024/768/390 | **34/34** (full run and again on the final build) |
| t5 | 27/27 |
| utps (project import) | 106/106 |
| mt a / b / c (manual testing, API restart between) | 23/23 · 48/48 · 7/7 (full run and again on the final build) |
| lc l1 / l2 (lifecycle, API restart) | 38/38 · 13/13 |
| p2 | 10/10 |
| cx | 6/6 |
| ui · rw · open-now | 30/30 · 22/22 · 16/16 |
| itx · tt · ttnav | 46/46 · 76/76 · 48/48 |
| nav · nav2 · brand | 41/41 · 32/32 · 17/17 (final build) |
| link / link verify (fresh DB, API restart) | 12/12 · 26/26 |
| Azure DevOps export (fresh DB, no ADO write) | 43/43 |

0 page errors, 0 HTTP 5xx, 0 guard violations in all scripts. One browser console line in `rw` (*Failed to fetch* from a request cancelled by
navigation; checks unaffected). `brand` first failed 2 checks on 240 s sidebar waits at the end of an overnight run; on the final build it passed
17/17 together with nav/nav2.

**Repository Playwright suite** (now isolated; run against 5097/5197 with `BIRKNEXT_PLAYWRIGHT_FRONTEND_URL`/`_BACKEND_URL` and the new
`BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE`):

| Variant | First run | After local-session connect + contrast fix |
|---|---|---|
| Self-starting set (`Verify`) | 5 passed · 19 failed · 4 skipped | **11 passed · 13 failed · 4 skipped** |
| Pre-started set (`VerifyPre`) | 12 passed · 48 failed · 4 skipped | **18 passed · 42 failed · 4 skipped** |

Classification of the failures (from the TRX messages):

* **Environment (fixed):** every page showed *Connect this browser to BirkNext* — the fixtures never exchanged the local-session launch code,
  so nothing loaded. `BrowserTestServers.ConnectAsync` now connects every fixture context (code from `BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE`, the file in
  `BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE`, or the self-started API's own file; never logged).
* **Real regression (fixed):** `FqrLandingHasNoAxeContrastViolations` — the sidebar *All modules* hint link (added 2026-11-15, `94d795d0`) had
  2.87:1 contrast on the dark sidebar. Link colour now `#93c5fd` (≈ 6.9:1); the test passes in both variants.
* **Stale tests (not fixed, not weakened; pre-date this task):** selectors for UI that earlier tasks removed or moved — the global *Check target
  application* button and *Target Environments* link/tab container (Target Environment detect settings were removed), the *Select Project* button
  on sample projects, `.dev-diag-section` settings layout, `fqr-dimension-card`; and FQR layout tests expecting *Run* enabled with a browser-only
  target that the current readiness rules do not accept. None touch code changed in task 6 (the FQR page change only adds stored-run opening
  and server archiving after a run). They need rewriting against the current UI — recorded as debt in section 16.
* **Skipped with the exact prerequisite (4):** real M2LB acceptance (`BIRKNEXT_REAL_PROJECT_M2LB`, `BIRKNEXT_M2LB_ARCHIVE` — no fabricated M2LB
  evidence), Integration Journey, Schema Studio and Active Event acceptance (their own isolated-instance variables).

## 16. Remaining risks and gaps

*Unsupported:* malware scanning (no scanner); time-based retention; single-stage retest with tester-supplied values instead of rerunning
prerequisites; server copies of specifications for projects imported before this change (import again); fuzzing/authorization stored reports
reopen only through the API.
*External verification:* composite execution against a real target (synthetic local integration only); live Azure DevOps import; the real M2LB
archive acceptance.
*Architecture debt:* Critical E2E history in server files; manual case requirement links show their source state only through the check endpoint
(the journey panel shows it); download authorization is the local client boundary plus project key (no per-user ACL in this single-user tool).
*Test debt:* 13 (`Verify`) / 42 (`VerifyPre`) repository Playwright tests target UI that earlier tasks removed or moved (section 15); they now run
isolated and fail honestly, and need rewriting against the current pages.

## 17. Local commits

On `008-traceability-first`, from `4116df33`; nothing pushed.

| Commit | Content |
|---|---|
| `af0ae194` | Shared contracts, backend services/controllers, migration `20261116213526_TechnicalEvidenceIntegrity` (with triggers), backend tests |
| `1105b1ec` | Frontend (requirement source chips, pinned run + stage retest UI, stored reviews, attachment withdrawal), frontend tests, user guide |
| `52e93a32` | Playwright isolation and local-session connect; sidebar hint contrast fix |
| (next) | This document and the session report addendum |

Not committed (not task-owned, left exactly as found): `appsettings.Local.json`, the Kode67 gap register, `AIAssisted/testdata/`, and the
untracked audit drafts (`birknext-automatic-integration*`, `birknext-final-gap-register-2026-11-07`, `birknext-final-independent-audit*`,
`birknext-project-test-setup-final*`, `birknext-release-assurance*`, `birknext-scope-*`, `birknext-unified-*`).
