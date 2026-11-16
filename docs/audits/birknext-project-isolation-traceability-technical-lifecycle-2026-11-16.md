# Project isolation, end-to-end traceability and the technical testing lifecycle (2026-11-16)

**Base:** `fe9fc555`. The earlier commits are on `008-traceability-first`, not pushed: `9b02825a`, `983ca27a`, `420916bb`, `d4611e52`, `ed6cd9af`, `fe9fc555`.

**Rules followed:**
- No new test-management, traceability, execution, defect or source registry.
- Every change extends the existing owner.
- No external system was contacted.
- Disposable databases and ports 5097/5197 only.

## 1. Reported gaps reconciled with the code

| # | Reported gap | Code evidence | Classification | Result |
|---|---|---|---|---|
| 1 | Suite generation not project-scoped on the server | `IntegrationSuiteGenerateRequest` had no project. `GenerateAsync` checked the journey with `FindByIdAsync` only, so any project's journey passed. Suites had no owner. | **Confirmed** | Fixed (section 2) |
| 2 | Journeys and suites cannot be linked to requirements | `DeclaredIntegrationJourney` and `IntegrationSuite` have no requirement fields. The trace views ignored suites. | **Confirmed** | Fixed (section 3) |
| 3 | Technical history only in browser state | API Quality Review history is in `birknext:api-review` localStorage: 10 summaries, the latest full report only, and write errors swallowed. Contract Testing, Performance, Integration Test Suites and Critical E2E already persist server-side. | **Confirmed for API Quality Review**; other modules already resolved | Fixed (section 4) |
| 4 | Technical findings have no follow-up or retest | No defect or retest concept outside manual runs | **Confirmed** | Fixed for API Quality Review findings (section 5) |
| 5 | A new session briefly shows a default project | Probe, 3 fresh sessions: for about 0.6 s the page said "No project selected" while busy, then showed the saved project. Every project-scoped request was sent only after the saved state was read, with the restored project's key. | **Not reproduced as a wrong-project race**; the misleading message is confirmed | Message fixed (section 6) |
| 6 | Acceptance depends on the project selected earlier | p2's report step never selected a project | **Confirmed** | Fixed (section 7) |
| 7 | Azure DevOps export not validated by a real import | No live import is authorized | **Blocked (external)** | Independently validated files only (section 8) |

## 2. Server-side project isolation (Integration Test Suites)

**Owning project.** Every generate request must name the workspace project (`WorkspaceProjectKey`); without one it is refused.

**Single snapshot.** A snapshot imported by another project is refused. Identities are compared as `import:{id}` against the snapshot's `ProjectImport.ImportId`, exactly, never by name. A source-only upload belongs to no project and stays usable.

**Multi-source (composite context).** This is the supported way to combine several projects' sources: each source association is approved in the context. It is not blocked by the single-snapshot rule.

**Journey.** It must be a current, non-retired journey of the same project.

**Suite record.**
- A suite records `WorkspaceProjects`: every project that generated or reused it. This is metadata outside the fingerprint, so identity and revisions are unchanged.
- Single and composite runs from another project are refused ("generated for another project; nothing was sent").
- Runs without a project are refused when the suite has owners.
- Suites from before this change have no owners and stay usable.

**Page.** The page sends the selected project for generate, run and composite run.

**Tests.**
- A missing or blank project, another project's snapshot (forged id), an unowned upload, a second project reusing the same suite, and a foreign or missing project on run are covered. The identity rule is exact.
- All existing suite and composite tests updated to name a project.
- The browser test sends direct API requests that bypass the page: a request with no project, a forged snapshot and another project's journey are all refused.

## 3. Requirement → journey → suite → result

**Model.** One association record per project + journey + requirement (+ specification path), in the journeys schema (`journey_requirement_link`):
- the status: Proposed, Confirmed, Rejected or Removed;
- the journey revision at link time;
- who and when;
- an append-only history;
- optimistic concurrency.

Nothing is deleted: a removed link can be linked again under the same identity.

**Derived links.** A suite prepared for a journey inherits its requirements. That is a derived link, shown as "prepared", never as "tested".

**Provenance and trust.** The requirement must be in the project's own specification catalog, the same catalog and rule that manual case links use. That catalog is read in the browser. Each link therefore states its trust level: "Selected from the project's specification as read in the browser; not yet verified against a server-side reading."

**Tracked blocker.** A server-side specification reading would be required to verify requirement identity independently; one does not exist yet.

**UX.** Integration Testing → Prepare (journey):
- Requirements are listed by their text, with their status, who linked them and the journey revision.
- Search the project's requirements and link them.
- Confirm or reject proposals; remove or re-link.
- See the history.
- See which suites are prepared for the journey, with their status.

**Coverage and traceability.** The shared prepared-case trace view, used by both Test Coverage and Requirements Traceability, gains a separate **Integration testing** list:
- requirement → journey (and whether it was revised after linking) → suites prepared for the journey → each suite's outcome from its recorded runs ("Not executed" until a run records one);
- shown apart from the manual counts and never added to them, so nothing is counted twice;
- only confirmed links of current journeys count.

## 4. Persistent technical history

**Stored runs.** `api_review_runs` stores each finished API Quality Review run:
- it is append-only, and the report JSON is never changed;
- it is idempotent per environment + generation time;
- it records the project, environment, counts and full report.

**When runs are stored.**
- The page stores a run when it finishes.
- When the page opens it re-stores the report it holds, which moves existing browser history to the server.

**Technical Testing → Verify** reads the server history: every run opens **its own** report (`api-quality-review?run={id}`). The browser copy is the fallback only.

**Survival.** Runs survive a reload, a browser restart and an API restart (server records). They are covered by the local data reset of the main schema.

**Not changed.** Fuzzing and authorization runs remain browser-only (residual).

## 5. Technical finding follow-up and retest

`technical_findings` holds one follow-up per project + source + finding identity (rule, target and endpoint), so the same issue in a later run is the same follow-up.

**Lifecycle:**
- New → Under review / Confirmed / Deferred
- Confirmed → Resolved pending retest
- Resolved pending retest → Verified resolved (needs a retest) / Reopened
- Verified resolved → Reopened

Each step has a note, a work item, the actor and the time, in an append-only history.

**Retest.** A retest is a separate later stored run. *Verified resolved* is accepted only when the run is:
- of the same environment and project;
- later than the original;
- a run that actually reviewed the finding's target;
- a run that no longer shows the finding.

The outcome ("Absent", "Still present" or "Not reviewed") is recorded with the event and never re-evaluated. The original run is never changed. Another project's runs and follow-ups are not found.

## 6. Project restoration

There is no wrong-project race (see item 5 in the table). The shared workspace header now says "Loading the selected project…" while the saved selection is being read, instead of "No project selected". It detects this from the workspace projection having no snapshot yet. No delay was added.

## 7. Browser test isolation

p2's report step now selects its own project (LendoPlus, prepared by utps and mt) and checks it before reading. The other scripts already select their project first. The new task 5 acceptance declares its own journey, posts its own fixtures and checks isolation with a key that does not exist.

## 8. Azure DevOps export

The generated XLSX and CSV are validated by an independent standard-library reader (`check_export.py`, which shares no code with BirkNext). It checks:
- headers, including **Description**;
- IDs, including **remembered work-item IDs** in update files;
- step numbering, encoding and multi-line text;
- formula neutralisation.

ADO acceptance: 43/43. **No live Azure DevOps import was made** (not authorized). Whether Description and remembered IDs import as intended is unverified in a real project.

## 9. Verification

All on the final code, sequentially, on ports 5097/5197 with disposable databases:
- `birknext_t5_20261116`: main;
- `birknext_t5link_20261116`: link journey;
- `birknext_t5ado_20261116`: Azure DevOps.

**Unit and component tests**

| Suite | Result |
|---|---|
| Frontend (bUnit) | **9204 / 9204** passed |
| Backend partitions 1–6 (final code, after the `CriticalE2ESuggestionStore` retry fix) | **6885 passed, 0 failed**, 19 skipped (6904): partitions 1052 / 1101 / 894 / 682 / 541 / 2615 |

**Release publish:** API and web, 0 errors.

**Browser acceptance** (Edge, Release build, guarded network: any request outside 5097/5197 fails the run). Every script reported 0 page errors, 0 server errors and 0 guard violations.

| Script | Result |
|---|---|
| t5 (this task) | 27/27 |
| ui | 30/30 |
| rw | 22/22 |
| open-now | 16/16 |
| lc l1 / l2 (after API restart) | 38/38, 13/13 |
| p2 | 10/10 * |
| cx | 6/6 |
| utps | 106/106 |
| mt a / b / c | 23/23, 48/48, 7/7 |
| link / link verify (own database) | 12/12, 26/26 |
| ado (own database) | 43/43 |
| itx | 46/46 |
| tt | 76/76 |
| ttnav | 48/48 |
| nav / nav2 | 41/41, 32/32 |
| brand | 17/17 |

\* **p2.** It scored 5/8 in the first sequential run. The two-tab settings step's *Reload settings* correctly reads the environment's stored target application (ScopesAcceptance in the template database) into that browser session. The report step then read ScopesAcceptance's empty history. Its own project check passed only because "LendoPlus" appeared elsewhere in the panel.
- **Fix (test isolation, no product change):** the report step runs in its own browser session and checks the workspace header.
- **Rerun:** 10/10 on the same database and build.

**nav console message.** nav recorded one console message from Frontend Quality Review engine status. It is a request cut off by the script's navigation (known, roadmap R13), not a failure.

**Repository Playwright suite**
- **Real-project category:** run against 5097/5197, built into its own output folder because the default build copies into the `bin/Debug` that the user's API locks. Result: **skipped** (1 test), because `BIRKNEXT_REAL_PROJECT_M2LB` is not configured.
- **Fixture-based categories:** not run; they start servers on 5000/5173.

## 10. Remaining limitations

- Requirement identity for links (manual and integration) is checked against the browser-read specification catalog. A server-side specification reading is needed for independent verification.
- Executing a suite against a target was not exercised in the browser, because no external target may be contacted. Backend tests cover runs against a local loopback fixture. The browser acceptance therefore shows "Not executed" for the prepared suite. Finding retests use synthetic local runs, posted to the local API and labelled as fixtures.
- Fuzzing and authorization runs, and Frontend Quality Review history, are still browser-only.
- Composite runs do not pin test revisions; a single-stage retest is not possible (journey audit items G5/G6).
- The repository Playwright suite: its fixture-based tests start servers on 5000/5173 (the user's ports), so they were not run. The real-project category needs an external M2LB archive (`BIRKNEXT_REAL_PROJECT_M2LB`) that is not configured.
- No live Azure DevOps import.
