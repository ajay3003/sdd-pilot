# Project Test Setup removal (2026-11-19)

Branch `008-traceability-first`, starting HEAD `f915815f`. Local commits only; nothing pushed. No database was modified; no external system was
contacted. `appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register and the unrelated audit drafts were not modified.

## 1. Decision

Project Test Setup (`/project-scopes`, a six-step wizard for *test areas* — named, versioned, approved selections of systems, scenarios,
requirements, journeys, contracts and Test Data) is removed. Testers select or import a project and work directly in Manual Testing or Technical
Testing; no test area has to be created, saved or approved first. No replacement page, wizard or scope registry was introduced.

## 2. Dependency audit (as found)

No frontend feature **required** a test area: every consumer used the shared `ScopeFilter`, which rendered nothing unless an approved,
non-archived test area existed, and every consumer behaved "exactly as before" without a selection.

| Consumer | Use | Action |
|---|---|---|
| Plan & prepare (`TestPlanSuggestions`) | optional filter; "Test boundary" context item; plan drafts recorded `ScopeIds` | filter, boundary item and `InScope` rule removed; new drafts record no scope ids; stored ids kept |
| Test Coverage & Overlap Review | optional filter; "selected scope vs whole project" comparison; scope marks on journeys, behaviours, overlaps, assets | removed; journey association decisions and Test Data links kept (`TestCoverageScopePresentation` reduced to them) |
| Test Data | optional filter by `TestDataAsset` members | removed |
| Critical E2E Regression | optional filter and scope count tables | removed (`CriticalE2EScopePresentation` deleted); release gate untouched |
| Implementation Review gap explorer | optional in / out / unconfirmed classification | removed; canonical requirement evidence (`api/scopes/catalog`, `verifying-scenarios`) kept |
| Integration journeys panel (IQR) | optional filter | removed |
| Delivery Reporting › Functional Quality Scope | Context / Filter-by-scope mode when freezing; provenance sent with generated reports | creating new shared-scope provenance removed; **stored provenance of frozen snapshots and reports is still shown** (read-only) |
| Canonical impact summary | live list of test areas naming impacted items | removed |
| Generated Documentation Explorer, Integration Testing, Technical Testing stage, Relationship Review | links to `/project-scopes` (systems / connections views, back link) | point to Integration Topology or removed |

Backend (unchanged): `ScopeRegistryController` (`api/scopes`) — its `systems`, `catalog`, `verifying-scenarios` and `check-members` endpoints
serve other features; `ScopeRegistryService`, the `scope_registry` schema (`scope`, `scope_revision`), its migration, startup migrate,
version-downgrade check, workspace-transfer migration list and the "Reset everything" ownership stay. `DeliveryScopeFilterService` (Filter mode)
remains for API compatibility; the UI no longer sends a filter request.

## 3. Removed

* Page `Pages/ProjectScopes.razor` (1 283 lines) → a compatibility redirect; `ProjectScopes.razor.css`.
* `Components/Scopes/` — `ScopeFilter`, `TestAreaCreateFlow`, `ScopeMemberPicker`, `SystemConnectionGraph` (+ CSS).
* `Services/ProjectTestSetupPresentation.cs`, `Services/CriticalE2EScopePresentation.cs`, the test-area rules of
  `TestCoverageScopePresentation`, `TestPlanSuggestionsPresentation.InScope`, `DeliveryReportingSession` scope provenance.
* Navigation: the *Project Test Setup* item (and its aliases) in Workspace, Test Lead *Environments & setup*, Technical Tester *Project &
  environment* and Manual Testing *Project setup*; the Documentation Catalog row and feature description; in-page hints "add … in Project Test
  Setup".
* User Guide: the five Project Test Setup paragraphs → one paragraph explaining that no test area is needed, where systems and connections are,
  and what happens to old links and stored test areas; three other mentions corrected.

## 4. Compatibility

| Old link | Opens |
|---|---|
| `/project-scopes`, `?view=setup|areas`, `?tab=scopes`, `?scope=…` | `project-import` (choose or import a project) |
| `?view=systems|connections`, `?tab=systems|connections`, `?system=…` | `integration-topology` (no `system` parameter: Integration Topology does not read one) |
| `?scope=…` on Plan & prepare, Test Coverage, Test Data, Critical E2E, Implementation Review, IQR | ignored — the page shows the whole project |
| `delivery-reporting?…&scope=` | unchanged: a Functional Quality Scope key there (GUIDs were already ignored) |

The redirect uses `replace`, so Back does not return to the retired route (no loop).

## 5. Historical information

Nothing is deleted. Stored test areas and their revisions stay in `scope_registry` (only "Reset everything" removes them, as before). Frozen
delivery snapshots and weekly reports keep their `DeliveryScopeProvenance` JSON and still show it ("Shared scope …, revision n", filtered
counts, comparison mismatch); a report filtered by a test area is never re-read as covering the whole project. Plan drafts keep their recorded
`ScopeIds` (exported as before). New records no longer get a test-area reference.

## 6. Tests

* Removed with the feature: `ProjectScopesPageTests` (27, the wizard), `ProjectTestSetupPresentationTests` (6), `TestCoverageScopeFilterTests`
  (10), `CriticalE2EScopeFilterTests` (4), and single tests that exercised a test-area filter or the creation of shared-scope provenance in Plan &
  prepare (2), Test Data (1), Critical E2E test-type counts of the scope table (3), the impact summary (1), Delivery Functional Quality (2) and
  report generation (2).
* Adapted (business assertions kept): Test Coverage journey-association and Test Data link tests now render without a test area (an old
  `?scope=` link is ignored) — association decisions, rejected associations, asset revisions and unresolved links are still asserted; the
  Implementation Review canonical-requirement test now asserts that an old scope link narrows nothing; the Delivery URL test asserts there is no
  shared-scope mode; stored provenance display tests (filtered reports, comparison mismatch) are unchanged and pass.
* Navigation and inventory: Project Test Setup removed from the expected sidebar, workspace and getting-started lists; module counts 60→59 shown
  of 66→65 entries; the retired route is classified as a redirect in `FeatureVisibilityTaxonomy`.
* New: `ProjectTestSetupRetirementTests` (redirect targets for every old view/tab/system/scope link, the page replaces its URL, no navigation
  group or catalog lists the page or a replacement) and Plan & prepare's "without any test area every candidate is listed and an old scope link
  narrows nothing".

Results on this code: full frontend suite 9 256 passed, 4 failed — the four (two module counts, the redirect not classified, one Critical E2E
test that relied on the scope view to list a non-critical flow) were fixed and their classes rerun: **316 passed, 0 failed**. No backend code
changed.

## 7. Remaining

* Systems and connections are now only in Integration Topology, which has no per-system filter: a system chip in Generated Documentation opens
  the topology without preselecting that system.
* The `api/scopes` test-area write endpoints and the Delivery *Filter by scope* service remain on the backend for compatibility; no UI calls them.
* Browser acceptance and the backend/full regression are run with the next sequential regression round (see below).

## 7a. Regression and acceptance

Browser acceptance, backend partitions and the Release publish of this code run in the next sequential regression round together with Contract Testing; their results are recorded in a follow-up commit.

## 8. Local commits

One local commit (starting HEAD `f915815f`). Nothing pushed.
