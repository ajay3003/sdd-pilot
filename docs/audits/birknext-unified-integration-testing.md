# BirkNext — Unified Integration Testing (2026-11-15)

Branch `008-traceability-first`. Builds on the Technical Testing workspace (`a909061c`) and its stage-navigation fix.

## 1. Summary

Technical Testing now has one entry for integration testing — **Technical Testing → Integration Testing** (`/technical-testing/integrations`) — in four stages: **Discover & understand**, **Define & assess**, **Prepare test suite**, **Verify & investigate**. It reuses every existing engine: Integration Topology, the Integrations catalog, declared Integration Journeys, Integration Test Suites and Integration Quality Review (IQR). No engine, store or table was added. What was missing was the connection between them; that is what changed: one overview with counts that name their entity and one next step, a journey editor that builds the chain from discovered systems and connections, and handoffs that carry the owning module's exact identity (journey id, catalog integration id, suite id) — never a match by display name.

## 2. Module audit — what each module actually is

| Module | Route | What it is | Identity | Persistence / owner | Scope |
|---|---|---|---|---|---|
| Integration Topology | `/integration-topology` (`?tab=`) | Components, interfaces and connections derived from sources and metadata, with relationship states (expected, candidate, strongly supported, resolved, observed) | Derived keys: component by resolved name; connection `from->to|type|address` | None — computed on read (`IntegrationTopologyController`, `TopologyBuilder`) | Target Environment |
| Integrations catalog | IQR `setup-evidence`, Integrations pane | Integrations configured for an environment (Event Hub, Service Bus, …) — what IQR checks | `IntegrationDefinition.Id` (e.g. `dev:eventhub:…`) | `integration_definitions` (+ bindings) | Target Environment |
| Integration Quality Review | `/integration-quality-review` (`?view=`, `?integration=`) | Static quality review per integration; registered test-pack journeys and authorised runs (Test & verify) | catalog integration id; pack journey id | `integration_review_runs`, `integration_journey_runs` | Target Environment |
| Integration Journeys (declared) | `/integration-journeys`, `/integration-journeys/declare` (`?journey=`) | A person's declaration of an integration process: ordered component chain, append-only revisions | `project.{key}:{journeyId}` | `integration_journeys.journey` / `journey_revision` (`DeclaredIntegrationJourneyStore`) | Project |
| Integration Test Suites | `/integration-suites` (`?area= snapshot= scope= context= suite=`) | Test design from source snapshots: composite source contexts, generated drafts, readiness, execution | Suite `Id` (Guid), `SuiteKey` | `integration_suites`, `integration_suite_runs`, `composite_source_contexts` | Source snapshot (scopes are built in) |
| Test Coverage (journey associations) | `/test-coverage-review` | Proposes source journeys that may match a declared journey (normalised chain names); a reviewer confirms or rejects | `IntegrationJourneyId` + source journey id | Manual review decisions (`JourneyAssociation`) | Project |
| Project Test Setup — systems & connections | `/project-scopes?view=systems|connections` | Canonical systems and reviewed connections for shared test areas | canonical system key | `scope_registry` | Environment catalog scope |

Classification of the overlap the user saw: the modules are **intentionally separate** in data (each owns a different entity and scope); the problem was **missing integration** — no shared entry, no handoffs with identity, free-text journey chains, and a suites page that could not receive a journey. No duplicate engine was found.

## 3. Domain entities kept distinct

Discovered component/connection (Topology, derived, not reviewed) · configured integration (catalog, environment) · declared journey (human, project, revisioned) · source journey (Test Coverage, from source) · test-pack journey (registered, IQR) · prepared suite (test design) · static review result (IQR run) · runtime run (authorised execution record) · coverage association (reviewed decision). None is merged with another; none is shown as a passed test.

## 4. What changed

1. **Integration Testing page** (`TechnicalTestingIntegrations.razor`, `IntegrationTestingPresentation`, `IntegrationTestingWorkspace`): overview (counts per entity, one next action, four linked stages) and four stage views. Reads: topology `GET` (never discovery), catalog, the project's journeys, suites of the project's own source snapshot (others counted, not shown), IQR run history. Read-only.
2. **Journey editor — Systems and steps** (`DeclaredIntegrationJourneys.razor`): add discovered systems (search) or both ends of a discovered connection (each labelled with its evidence state), move steps up/down, remove; typing stays possible and the text box remains the saved value. Adding a discovered connection does not confirm it. Leaving with unsaved journey edits asks first.
3. **Handoffs with exact identity**: journey → Integration Test Suites (`?journey={project.key:journeyId}`, shown as context only, accepted only for a journey of the current project); topology connection → IQR on its catalog integration (from Integrations-catalog provenance only); Integrations pane row → IQR `&integration={id}` (was the bare page); Test Coverage association candidate → the journey editor (declared project journeys only); journey overview → *Prepare test suite*; Technical Testing integrations area → Integration Testing stages.
4. **Navigation and guide**: sidebar row *Integration Testing* (Technical Testing section and workspace), documentation entry, guide topic *Integration testing* (Integration & E2E group).

## 5. Suite handoff — what it is and is not

Integration Test Suites builds suites from source snapshots and two built-in scopes (`IntegrationSuiteComposer.Scopes`); a suite has no journey field. The handoff therefore shows the journey (name, revision, steps) as context for choosing sources and scope and states that it is not stored on the suite. Binding a suite to a declared journey needs a backend scope source built from `DeclaredIntegrationJourney` — recorded as backlog (§10), not faked.

## 6. Evidence semantics

| Shown as | Comes from | Never shown as |
|---|---|---|
| Connection state (expected / candidate / strongly supported / resolved / observed) | Topology sources and metadata; *observed* = runtime observation | a passed test |
| Journey ("Declared by a person") | human declaration | a confirmed connection |
| Possible match (Test Coverage) | normalised chain comparison | coverage until a reviewer confirms |
| Suite status (draft … ready to execute) | Integration Test Suites readiness | an executed test |
| Quality review outcome | IQR static analysis | a passed run |
| Runtime run | IQR Test & verify / suite Execute & Evidence, each behind its own authorisation | — |

## 7. Isolation and compatibility

Journeys are read for the current project only; a journey id from another project is refused as context on both Integration Testing and Integration Test Suites. Suites are listed only for the project's own source snapshot. Topology, catalog and IQR are environment-scoped and labelled with the Target Environment. No migration, no change to stored journeys, suites, decisions or runs; journey revisions stay append-only (the editor saves through the existing store).

## 8–9. Tests and browser acceptance

All on one source tree (the committed files), Release publish from a clean copy, fresh disposable database `birknext_itx2_20261116` from template `birknext_pts_acc_20261115`, isolated ports 5097/5197.

| Check | Result |
|---|---|
| Frontend build (non-incremental) | 0 errors |
| Frontend suite | **9,125 / 9,125 passed** |
| Backend build (separate output `bin/Verify/net8.0` — the user's API on :5000 locks `bin/Debug`) | 0 errors |
| Backend suite, 6 partitions | 1,052 + 1,099 + 888 + 676 + 539 + 2,593 = **6,847 passed**, 19 skipped, **1 failed** — `ManualRunPersistencePostgresTests.Traceability_read_cost_follows_cases_not_history(10000)`: it measures `GC.GetTotalAllocatedBytes` (process-wide), so parallel tests in the same host add their allocations. Rerun alone: class **11 / 11 passed**. Not related to this change (frontend only); isolated from parallel execution in the next commit, assertion unchanged |
| Release publish (API + Web) | exit 0, 0 errors |
| Browser: Unified Test Plan / project setup regression (`utps-accept`) | **106 / 106** |
| Browser: Integration Testing (`itx-accept`) | **42 / 42** — M2LB template data: 39 systems and components, 41 connections, 0 configured integrations (honest empty catalog), journey created through Systems and steps, suite handoff with journey context, unknown-journey refusal |
| Browser: Technical Testing (`tt-accept`) | **76 / 76** |
| Browser: Technical Testing stage navigation (`ttnav-accept`) | **48 / 48** |
| Browser errors / console errors / 5xx / guard (non-allow-listed requests) | none |

An earlier run (before the topology resolver fix) found the real bug that topology was never read (the client is not a DI registration): itx 41/42. Fixed through `IntegrationTopologyApiService.Resolve`, with a regression test; the results above are after the fix.

## 10. Known limitations and backlog

- Suites cannot be bound to a declared journey (no journey field; built-in scopes). Backlog: a suite scope source from declared journeys, keyed by `project.{key}:{journeyId}` and revision.
- Declared journey components are names; nothing links them by id to topology components, canonical systems or catalog integrations. The editor removes the need to type them, but matching a chain to topology remains Test Coverage's reviewed association.
- Integration Topology does not read a focus parameter (`?connection=` / `?integration=`); links open the page, not a selected item.
- Test Coverage reads only `?scope=` (a Project Test Setup test area, through its scope filter), not a journey id, so links cannot open it on one journey (also listed in the feature-portfolio audit).
- Topology, catalog and IQR are per Target Environment, journeys per project: a project's integration view shows the active environment's integrations.

## 11. Git

One local commit on `008-traceability-first` (this report included); not pushed. Unrelated working-tree changes (`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register, other audit drafts and the feature-portfolio documents) are left untouched and uncommitted.
