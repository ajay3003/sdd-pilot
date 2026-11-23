# BirkNext — Project Test Setup: redesign and final UX acceptance

**Date:** 2026-11-15
**Branch:** `008-traceability-first`
**Base:** HEAD `6f65d6d0` (follows `birknext-release-integrity-persistence-hardening.md`)
**Commits:** local only, listed at the end

No push and no deployment. No remote system was contacted. `appsettings.Local.json` and `AIAssisted/testdata/` were not touched. The preserved acceptance databases were not modified.

This report covers both requested tasks: the redesign ("Project Test Setup") and its completion and acceptance.

## Publish status carried over from the previous task

The final web Release publish after the user-guide change **succeeded** (exit 0, output `acc\web\`). The API publish had succeeded earlier.

## What was wrong with Systems & Scopes

The page opened on three equal technical tabs (Systems, Connections, Scopes) and a name/purpose-first scope wizard. It did not say:
- what BirkNext had found;
- whether the 61 source-discovered connections were real;
- whether reviewing the roughly 26 uncertain ones was required;
- what a scope is for;
- what to do next.

## What it is now

**Project Test Setup** (sidebar label; the route `/project-scopes` and all ids are unchanged; old `?tab=` links are mapped).

**Header:** project name, Target Environment, the purpose line ("Choose what you want to test and use the selected test area across BirkNext.") and a compact summary of systems, source connections and test areas. Ids, fingerprints and revisions sit under Technical details.

**Guided workflow:** reuses the shared `GuidedStepList` (the Integration Quality Review component). It gained optional parameters for headings, per-step content and actions, and state keys; Integration Quality Review renders as before.

| Step | Purpose | Actions |
|---|---|---|
| 1 Understand your project | Systems found; source and analysis state; limitations. Informational | Explore systems |
| 2 Explore connections | Found in source / confirmed / "N connections may need review (optional)" / runtime only with evidence. States "Source-discovered connections are not proof that integrations work in a running environment." Never required | View connections, Review uncertain relationships |
| 3 Choose what to test | Primary step | Create test area, or open an existing one |
| 4 Review selected coverage | Members by kind, each Explicitly selected / Human-confirmed / Suggested / Candidate / Unresolved | Review selection, Add or remove items |
| 5 Save test area | "Saved — revision X", approval state, stale draft | Save, Submit, Approve (existing endpoints; approved revisions immutable) |
| 6 Use your test area | Links only to modules that consume the scope | See below |

**States:** derived from data on each render; nothing is persisted. The states are Completed, Current, NeedsAttention, Available, NotStarted, Unavailable and Error.

**Setup progress and evidence confidence are shown separately.** A saved test area with unresolved connections is a valid state.

**Recommended next step:** one action, chosen from this ordered list:
1. Import project
2. Choose environment
3. Import source
4. Analyze source
5. Try again
6. Create test area
7. Open test area
8. Review unresolved items
9. Continue editing
10. Prepare test plan, with Review coverage as a secondary action

### Creating a test area

The flow is: **What do you want to test?** → select → details → review → save.

- **Suggestions** come only from real evidence:
  - specification groups (requirement, user story, scenario and feature identities that share a module/feature path);
  - journeys declared for the current project;
  - scenarios that verify the chosen requirements.

  There are no hardcoded names. Each suggestion shows where it came from and what it proposes; with no evidence, none are shown. **Create my own test area** is always available.
- **Selection:** suggested items must be **kept** explicitly; any other member can be added through the existing member picker. Only kept or selected items are saved, as confirmed members. Items not kept are not saved, not even as candidates.
- **Details:** the name is prefilled from the suggestion; the description is optional.
- **Review:** shows what will be saved, what was not kept and any unresolved items.
- Works with requirements only, mixed members, and no systems.

### Exploring

- **Explore systems:** grouping, search and a system/service filter. Each system shows its related services, connection count, review items and the test areas that include it. Evidence is on demand.
- **View connections:** the existing focused graph (one system, incoming and outgoing, zoom, pan, fit, clickable edges) plus a list. Filters cover status, system and the selected test area. Relationship Review is the contextual action and returns with "← Back to Project Test Setup".
- **My test areas:** the full existing scope management (revisions, compare, archive, approve with rationale).

### Where a test area can be used

| Module | Behaviour | Link |
|---|---|---|
| Test Plan Suggestions, Test Coverage, Test Data, Critical E2E, Implementation Review, Delivery (Functional Quality Scope) | **filters** (the shared ScopeFilter, approved revisions only) | `…?scope={id}` |
| Relationship Review (impact) | context | `correlation-review` |
| Integration Journeys | context, only when the area names a journey | `integration-journeys` |

For a draft, filter destinations are listed as "Available after approval" without links, because ScopeFilter only offers approved revisions.

### Navigation state

The URL carries `view`, `scope`, `system`, `step`, `kind`, `status` and `inarea`, so Back/Forward works. A test-area id that is not one of the current project's is **dropped from the URL** (on a project switch and on a fresh load), never looked up or shown. This was fixed during acceptance: a stale link previously kept the foreign id in the URL, though the page already showed only the current project's areas.

## Local Data Reset: shared test areas (explicit policy)

| Reset scope | Shared test areas (`scope_registry`) |
|---|---|
| Reset everything except protected data | **Kept**, and listed in the plan as preserved ("they hold reviewed test boundaries, so only Reset everything removes them") |
| Reset everything (typed `RESET EVERYTHING`) | **Removed**, in one transaction, dependents first, migration history untouched |
| Delivery Management only | Not touched |

**Caveat:** both full scopes remove the project imports. A re-imported project gets a new project key, so kept test areas are not shown for it. This is documented in `docs/local-data-reset.md`.

**Tests:** ordinary reset keeps scope rows and says why; Reset everything removes them and their revisions; plans state the policy per scope.

## Acceptance network isolation

`AIAssisted/frontend/BirkNext.Web.PlaywrightTests/acceptance-network-guard.cjs` provides:
- validation of the configured URLs (loopback only; ports 5000 and 5173 always refused);
- validation of the published frontend's `BackendUrl` (must equal the isolated API, never the `:5000` default);
- an allowlist route on the browser context: every request outside the allowed loopback origins, including preflights and redirects, is **aborted before it is sent** and recorded as a violation, which fails the run.

7 unit tests, with no network. The scratch harness points the published (scratch) frontend at `http://localhost:5097` before launch, so the app never targets `:5000`; the earlier request rewrite is no longer used. All acceptance runs in this task recorded **zero violations**.

## Browser acceptance (real M2LB import, disposable DB `birknext_pts_acc_20261115`, guarded)

PTS_RESULT

**Checks covered** (an objective first-time-tester checklist, not a study with real users):
- sidebar label;
- purpose and summary;
- six steps;
- state-aware next step;
- connections explained as source-only with optional review;
- no raw ids on the landing;
- Explore systems with search, detail and Back;
- focused graph with no runtime claim, edge evidence, and the Relationship Review round trip;
- creation from a real suggestion ("skolenaervaer — From the specification and source analysis, proposes 21 scenarios"), keep and dismiss, prefilled name, review, save (Saved — revision 1, draft);
- persistence after reload; members shown as human-confirmed;
- filter links pending before approval; approval;
- exact `?scope=` on all six filter links;
- Test Plan Suggestions applying the test area ("2 unique members from 1 scope"); Back/Forward;
- Test Coverage scoped comparison;
- Delivery opening its scope tab with the area;
- project switch with no leakage;
- API failure → Error step plus "Try again" → recovery;
- no overflow on six views at 1440, 1280, 1024 and 768 px;
- heading structure; primary action reachable by keyboard;
- no page, console or server errors; zero network violations.

Screenshots: session scratchpad `acc/shots-pts/`.

## Automated tests

- `ProjectScopesPageTests`: 27 tests. Existing data assertions are kept; new tests cover states, next action, creation, use links, URL state, project switch and a foreign-id link.
- `ProjectTestSetupPresentationTests`: unit tests for the rules.
- Focused frontend (Project Test Setup, guided steps, IQR, navigation, guide, documentation, relationship review, scope consumers): 1297/1297.
- Focused backend (reset and scope registry): 45/45.
- Full regression: see the consolidated results in `birknext-global-navigation-loading-hardening.md`, run once on the final code of all three queued tasks.

## Limitations

- Suggestions read at most 300 catalog items per kind.
- Grouping needs identities shaped like `module/feature/id`.
- Inline approval in step 5 records no rationale; the My test areas view has the rationale field.
- Integration Journeys does not filter by test area, so it is only linked as context.
- Editing an existing area still uses the earlier member wizard.
- bUnit cannot exercise real browser Back/Forward; this was verified in the browser.
