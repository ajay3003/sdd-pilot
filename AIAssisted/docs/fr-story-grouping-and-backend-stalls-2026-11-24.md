# FR-based user story grouping, relationship management and backend test stalls — 2026-11-24

Branch `008-traceability-first`. One commit with all work from this session (not pushed; both stashes preserved).
Covers three queued tasks:

1. Manual Testing UX redesign. Details in `manual-testing-workspace-ux-2026-11-24.md`.
2. The backend test-suite stall diagnosis and fix.
3. "Intelligent FR-based user story grouping + complete relationship management + modern manual testing UX".

The report is honest about scope. The grouping task specification (sections 0–42, AC01–AC42, backend tests 01–76, frontend tests 01–58) is larger than what one session delivered. Section E lists every gap.

---

## A. Summary

| Area | Result |
|---|---|
| FR sections (source functional areas) | **Done.** Read from the specification's own structure (`shared/RequirementSections.cs`); nothing is invented. |
| US→FR relations with provenance | **Done.** Five bases (source, human-confirmed, scenario, tasks/plan structure, suggestion), each with evidence. |
| Relationship editing | **Done.** Add, confirm, reject, remove, replace, restore, dispute, undo and history. Every change is previewed first and saved atomically. |
| Source links | **Immutable.** They can only be disputed (with a reason); the source evidence stays. |
| Grouping suggestions | **Done.** Recomputed after every edit and explained in plain Norwegian. A tie gives "Trenger gruppering", never a guess. |
| Confirmed placement | **Never moved silently.** A changed suggestion shows "Grupperingsforslaget er endret" with Godta / Behold. |
| Business groups | **Done.** Create, rename, reorder, areas, place story, merge, split, delete-empty, restore source navigation and history. |
| "Grupper etter" | **Done.** Funksjonelle områder / Forretningsgrupper / Brukerhistorier / Ingen. |
| Persistence | **Done.** New `story_groupings` table: one document per project + specification, with optimistic concurrency (409 on a stale revision). |
| Planlegg integration | **Partial.** See E. |
| Backend stalls | **Root cause verified and fixed:** workstation GC pauses in the test host. Fixed with Server GC plus isolation of the test hosts' App_Data. |
| Tests | Targeted tests are green (backend 119/119, frontend 150/150). Full-suite results are in section H. |

## B. Source-derived functional areas (`RequirementSections.Read`)

- **Region.** A requirements heading ("Requirements", "Functional Requirements", "Krav", "Funksjonelle krav") opens the region.
  - A requirements-named sub-heading directly inside it, such as `### Functional Requirements` under `## Requirements`, narrows the region. It is **not** an area.
  - A sibling heading at the same level, such as `### Key Entities`, closes the region. This was fixed during verification: S01 and S05 caught both headings leaking in as areas.
- **Sections.** Inside the region, a bold-only line (`**Intake and identity**`) or a deeper heading starts a section. Requirement definitions belong to the innermost open section.
- **Never sections:** bold lines that carry an id or a label with text, `SR-FR-…` source references, and lines inside fenced code.
- **Identifiers.** The canonical id is used for identity (FR-027A); the display id stays as written (FR-027a).
- **No section.** An FR outside any section gets `Section = null`, shown as "Ukjent". It is never assigned an invented area.

## C. Grouping model (`shared/StoryGrouping.cs`)

### Evidence precedence

Lower number wins:

1. SourceExplicit — the story text names the FR.
2. HumanConfirmed.
3. ScenarioExplicit — an acceptance scenario names the FR.
4. Structural — a `tasks.md` line tagged `[USn]`, or a plan link.
5. Suggested — similar wording only. Never counted as a link.

### Primary area

- The suggested area is strictly the one holding the most **distinct established** (non-suggested), non-cross-cutting FRs in a known section.
- A tie gives **NeedsGrouping**. Only suggestions gives **OnlySuggestions**. No links gives **NoRequirements**.
- There is no transitive clustering.

### Cross-cutting FRs

An FR is cross-cutting when a person declares it so, or when all of these hold:

- it is linked to at least 3 stories,
- those stories are at least half of all linked stories,
- it is never the only link of any story.

### Human decisions

- `StoryGroupingDocument` holds groups, placements, link decisions and history. Every operation is validated by `StoryGroupingEngine.Apply`.
- An unknown FR is refused ("Kan ikke lagres …"), as is removing a source link or deleting a non-empty group. All operations in a request apply, or none do.
- A confirmed placement remembers the suggestion it was confirmed against. When the suggestion later differs, the placement is kept and the story is flagged (`SuggestionChanged`).
- Grouping operations never change requirement links, and link edits never move a confirmed group placement. Both are tested.

### Storage and API

- **Table.** `story_groupings` holds one row per (project, specification path), with `Revision` as the concurrency token. Migration: `20261124213606_StoryGroupings`, applied.
- **Endpoints.** `GET/POST api/test-plan-suggestions/story-grouping`.
  - Writes require `IntegrationConfigurationWritePolicy`. See limitation E6.
  - The FR catalog used for validation is scoped to the specification path.
- **Local Data Reset.** It treats `story_groupings` as test-planning data: the table is protected and backed up together with test plans.

## D. UI (Finn og forstå)

- **Rail.**
  - A "Grupper etter" selector and a collapsible tree. Each group shows its story count and stats.
  - Every story appears exactly once. Stories that can't be grouped stay visible under "Uten krav" or "Uten gruppe".
  - The default mode is Business when groups exist, otherwise Functional when sections exist, otherwise Stories.
- **Story card.** Shows the grouping state badge, the area and group, «Se begrunnelse» (the explanation, plus secondary areas), the changed-suggestion callout, and «Endre plassering» with a preview.
- **Story details.** «Tilknyttede krav» (`StoryRequirementLinks.razor`) shows links grouped by source section with provenance labels ("Nevnt i brukerhistorien", "Bekreftet av en person", "Foreslått" …).
  - «Administrer kravkoblinger» offers search (this specification's FRs only), confirm/reject, remove/replace (manual links only), dispute (source links only, reason required), restore, undo and history.
- **«Administrer grupper».** `GroupOrganizer.razor` covers create, rename, reorder, assigning areas, placing stories, merge, split, delete-empty and restore source navigation. Each change is staged with a preview of what changes and what stays unchanged (links and test cases).
- **Errors.** A refused or stale save shows the reason and keeps the staged change.
- **Inputs.** The page merges the browser's own `tasks.md` links, because stored discovery evidence predates the `tasks.md` fix.

## E. Limitations, gaps and failures

1. **Test count vs. the specification.** The spec lists backend tests 01–76 and frontend tests 01–58. Delivered:
   - Backend: S01–S08, G01–G35 (22 facts/theories), V01–V07, plus 2 isolation tests.
   - Frontend: P01–P05, C01–C06, C10.
   - They cover the core rules, but not one test per numbered item. Missing in particular: organizer component tests for merge, split, rename and reorder (engine-tested only) and accessibility/keyboard tests.
2. **Planlegg integration is partial.**
   - Planlegg links into Finn og forstå (breadcrumb, «Vis i Finn og forstå»).
   - Plan-level story links are **not** fed into grouping as Structural evidence. Only `tasks.md` links are.
   - Planlegg does not group its own list by business group.
3. **Measurement scope.** Performance was measured with the browser walkthrough only (section G). There is no load test with large specifications.
4. **FR display casing.** The source casing (FR-015a) is shown only where the section map is available. Elsewhere the canonical id is shown.
5. **Stored discovery evidence predates the `tasks.md` fix.** The UI compensates by merging the browser's evidence. The server-side `SpecificationEvidenceBuilder` now reads `tasks.md` for new discoveries.
6. **Permissions.** Grouping writes reuse `IntegrationConfigurationWritePolicy`. There is no dedicated "test design write" permission yet.
7. **No AI and no internet.** Suggestions are deterministic: section membership plus wording overlap. Wording suggestions can be weak; D-level suggestions are hidden behind a toggle.
8. **History is durable.** The walkthrough's disposable changes (a link added then removed, a group created then deleted) leave history entries in the SkoleService grouping document. The history is the audit trail by design.
9. **Real-project findings (SkoleService).**
   - **US2** has no linked FRs in the source: no FR is named in the story, and no `tasks.md` line is tagged `[US2]`. It shows as "Ingen krav", which is correct, not a defect.
   - **US5** is a 1–1 tie and needs a person to group it.
10. **Backend stalls.**
    - Server GC removed the long GC pauses. Before: 229 s total pause, 6.1 s maximum. After: 32 s total, 0.44 s maximum.
    - Two tests still time out only under the full parallel suite and pass alone: `CI_DEF_3_the_connect_callback_lets_an_allowed_host_through` and `GraphQL_variables_use_runtime_values…` (both make loopback HTTP calls). Not explained; not masked.
    - Run C (sequential) was dropped at the user's request.
11. **Full-app test hosts still use the real `birknext` database** (appsettings `Database=birknext`). App_Data is now isolated (the content-root redirect fixed launch-code rotation), but the database is not.

## F. Backend stall diagnosis (task 2)

- **Evidence.**
  - Probes: PG locks and sessions, process waits, and a runtime probe (`BIRKNEXT_TEST_STALL_PROBE`: GC pause, timer lateness, thread-pool queue wait).
  - Logs are in the session scratchpad `diag/`.
- **Ruled out.**
  - PG connection exhaustion (peak 30 of 100).
  - Lock waits. Only the deliberate migration-lock test waits.
  - Thread starvation. The pool queue wait was 0–4 ms.
  - Proxy changes.
  - PG sessions that were "idle in transaction / ClientRead" were waiting on the frozen client.
- **Root cause.** Workstation GC "stop the world" pauses of several seconds in a test host running many parallel full-app hosts.
- **Fix.**
  - `<ServerGarbageCollection>true</ServerGarbageCollection>` plus concurrent GC in `BirkNext.Api.Tests.csproj`.
  - Run A (workstation GC): 9 failed, 11.7 min. Run B (Server GC): 3 failed, 10.3 min.
- **Also fixed** (found while auditing waits):
  - Test hosts wrote into the real `App_Data` and rotated the running app's launch code. Fixed with `AppDataLocation.ContentRootRedirect` and `TestAppDataIsolation`.
  - `LocalSessionService.Write`: unique temp name plus bounded retry.
  - Unbounded process waits are now bounded: performance tests, the native runtime, Lighthouse, `pg_dump` / `pg_restore`, and concurrent stdout/stderr drain in the test gates.
  - `LocalDeploymentBoundaryTests` reads `/api/system/build-info` instead of an endpoint that probed podman.

## G. Browser walkthrough (real SkoleService, disposable records) — NOT RUN

- **Prepared but not executed.** The script `grp_walk.py` is ready in the session scratchpad. It would:
  - add FR-024 to US5 manually and remove it again;
  - create an empty "TEMP" group and delete it;
  - time every save, mode switch and reload;
  - take screenshots at 1280, 1600 and 1920 px.
- **Why it didn't run.** Claude Code stopped the background BirkNext backend, frontend and full backend-suite processes because the machine was critically low on memory. The memory note says not to restart them automatically.
- **Consequence:**
  - BirkNext is **not running** at the end of this session.
  - No disposable records were written.
  - The user's plans and data were not touched by any walkthrough.
- **To finish:** start BirkNext with `scripts/start-local.ps1 -SkipContainers` and run `python grp_walk.py`.

## H. Verification results

| Check | Result |
|---|---|
| Backend + frontend build | Succeeded. |
| Targeted backend tests: grouping, sections, service, isolation, LocalSession, LocalDataReset | **119/119 passed.** |
| Targeted frontend tests: grouping, workspace, discover, manual testing | **150/150 passed.** |
| Full frontend suite | **9,615 passed / 0 failed** (1 m 47 s). |
| Full backend suite (final run) | **NOT COMPLETED.** Stopped after about 1 minute because of system memory pressure; no failures before it stopped. The last complete run was Run B (Server GC): 3 failed. See E10. |
| Real SkoleService grouping report | Generated by G04: `backend/BirkNext.Api.Tests/Services/TestPlanning/Fixtures/story-grouping-report.md`. |

### Failures found and fixed during verification

1. A Razor build break: a lost backslash escape in `Replace('\\', '/')`.
2. A CS8122 in a component test: an `is` pattern inside an expression tree.
3. S01 and S05: `### Functional Requirements` and `### Key Entities` leaked in as functional areas. This was a parser fix, not a test change.

### Memory risk of the stall fix (important)

- The orphaned `testhost.exe` of the stopped backend run held about **7.7 GB**. Server GC trades memory for shorter pauses: one heap per core and larger gen0 budgets.
- On this machine (31.6 GB), running the full backend suite next to BirkNext, VS and the browser now risks memory pressure.
- **Recommended follow-up (not applied, unverified):**
  - add `<GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>` (.NET 8 DATAS) to the test csproj, or set `DOTNET_GCHeapCount`;
  - then rerun the full suite to confirm the pauses stay short while memory stays bounded.

## Acceptance criteria (AC01–AC42) — condensed

**PASS:**
- Source areas, provenance, precedence and no invented areas.
- Tie → NeedsGrouping; cross-cutting rule.
- Add, confirm, reject, remove, replace, restore, dispute and undo, with history.
- Source links immutable.
- Preview before save; atomic save; stale-revision 409.
- Confirmed placement never moved silently (Godta / Behold).
- Business groups: create, rename, reorder, areas, place, merge, split, delete-empty, restore.
- "Grupper etter" modes; every story shown once; ungrouped stories visible.
- Grouping never changes links or test cases.
- Search limited to the specification's FRs.
- Local Data Reset protection.
- Real SkoleService report.

**PARTIAL:**
- Planlegg integration (E2).
- Performance measurement (E3, G).
- Test-count parity with the specification (E1).
- Accessibility and keyboard tests.

**NOT DONE:**
- Browser walkthrough with disposable records (G).
- Final full backend suite (H).
