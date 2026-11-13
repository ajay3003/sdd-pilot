# Systems & Scopes — audit, hardening and redesign report

**Date:** 2026-11-13 · branch `008-traceability-first` · local commits only (no push, no deployment, no remote calls).
**Builds on:** `17581966` (shared Scope Registry and the first Systems & Scopes page) and `docs/audits/birknext-unified-scope-implementation-report.md`.

## 1. Audit: what existed and where

| Area | Owner (code) | Identity / key | State before this task |
|---|---|---|---|
| Systems and services | Canonical interpretation (`CanonicalInterpretationService`, `CorrelationRules.Systems`, `CanonicalEvidenceAdapter.DeclaredSystems`) | `CanonicalKeys.EntityKey` = `System\|altinn`, `Service\|m2lb.person.api`, scoped by the **active Target Environment profile id** | Systems come from domain-profile and test-pack declarations; services come from source (operation owners, messaging endpoints). The page read them with two `entities?take=200` calls. |
| Connections | Canonical relations; Integration Topology (`IntegrationTopologyService.BuildAsync`) | Relation key `src->Type->tgt`; topology connection id | The page showed only the direct relations of one picked entity. Integration Topology connections and messaging producer/consumer pairs were never read. |
| Relationship review | `CanonicalInterpretationService.DecideAsync` (shared review decisions), Relationship Review page | Relation key / alias target key | Authoritative; unchanged and reused. |
| Scopes | `ScopeRegistryService` (`scope_registry` schema) | Scope Guid, keyed by workspace `ProjectSlug` | Create, save, approve, archive and compare in the backend. The UI had no archive, no submit and no compare choice, and members were added by a free-text id with no validation. |
| Member catalogs | Canonical interpretation (Requirement, Scenario, UserStory, Feature, Contract); Test Data catalog (`TestDataCatalog.View`, immutable `AssetId`); Integration Journey pack registry | Canonical entity key; `AssetId`; `packId:journeyId` | Not connected to scopes. |
| Consumers | Test Data, Test Plan Suggestions | — | Neither referenced scopes. |

## 2. Root causes

1. **"No systems in the canonical interpretation yet" while systems exist.**
   - The page rendered its empty-state text during the first render, before the systems request had returned. There was no loading state.
   - It also showed the same text when there was no active Target Environment and when the interpretation had never run. Errors were folded into that message too.
   - Systems are keyed by the **active Target Environment**, but scopes are keyed by the **project**. Switching the active environment silently changes the systems list while the project stays the same.
2. **"No relationship recorded for Altinn."** This is correct for the evidence the page read, but incomplete.
   - In the M2LB interpretation, Altinn is a declared system and no canonical relation touches it.
   - Connections between systems exist only in Integration Topology and as two services that produce and consume the same message channel. The page read neither.
   - The test pack's expected path `Altinn → MU` is an *expectation*, not evidence. It must not be shown as a connection, and it is not shown now.
3. **Entities left behind by older rule sets.** The page listed every entity in the scope. The interpretation summary counts only the latest snapshot's membership, so the two numbers could disagree.
4. **Fabricated members.** A free-text id was stored without checking it, so any syntactically valid id was accepted.

## 3. What changed

### Backend

- **Systems overview** (`GET api/scopes/systems?catalogScope=`):
  - `CanonicalInterpretationService.SystemsOverviewAsync` and `SystemsProjection.Build` read the latest snapshot's System, Service and MessagingChannel members, their aliases, and the relations among them in a few set-based queries. There is no query per system.
  - **Grouping:** a service is placed under a system only when that system's explicit or confirmed alias *is* the service's name (for example, Person declares `M2LB.Person.Api`). Two systems claiming one service is a conflict, so the service stays on its own. Similar names never group.
  - **Connections:**
    - direct relations between systems and services;
    - *derived messaging connections*: a producer and a consumer of the same channel. The weaker end decides the state, and the number of such connections is capped at 2000.
  - **Evidence states:** `FromSourceEvidence`, `ConfirmedByPerson`, `Candidate`, `NeedsReview` and `Rejected`. `ObservedAtRuntime` is set only from Integration Topology's `Observed` state.
  - **Identity questions:** same-as candidates and proposed aliases are listed for decision.
  - `SystemsOverviewService.Merge` adds Integration Topology connections, read offline from stored facts (no discovery and no network call).
    - Expected-but-unresolved relationships are dropped.
    - A topology component maps to a canonical system only by exact name or an explicit/confirmed alias.
    - Configured locations annotate a system as "configuration only".
- **Member catalogs** (`GET api/scopes/catalog`, `POST api/scopes/check-members`): `ScopeMemberCatalogService` is a thin adapter over the owning stores.
  - It searches the latest interpretation for systems/services, requirements, scenarios, user stories, features and contracts.
  - Test Data assets are listed by immutable `AssetId`; values are never shown.
  - Journeys come from the pack registry.
  - Kinds with no catalog (relationship, acceptance criterion, test suite, test case) report `Available=false`.
- **Validation on save** (`ScopeRegistryService.CheckNewMembersAsync`):
  - Every *new* member must be `Current` in its catalog: it must exist, be the right kind, and come from this Target Environment's interpretation. Otherwise nothing is saved.
  - Members carried over from an earlier revision are kept even when they have since disappeared. They are shown as *missing* and are never dropped.
  - New members record their `CatalogScope`. The field is not part of the fingerprint, so revisions created before this change keep the same fingerprint.
- **Lifecycle:**
  - `POST {id}/submit` marks the newest draft revision *In review* (not an approval).
  - An empty draft can be saved but cannot be approved. This is the smallest rule that keeps "create a scope with no systems" valid without approving an empty scope.
  - `GET {id}/check` reports each member's current catalog state.

### Frontend

- **`/project-scopes` redesign** (`ProjectScopes.razor`, `SystemsScopesPresentation`):
  - The header shows the project name and the Target Environment. Ids and fingerprints are under *Technical details*.
  - Explicit states: Loading, No project, No Target Environment, No source imported, Not analysed, Analysis found no systems, Error with retry, Ready. "No systems found" appears only for a finished analysis.
  - The page never starts an analysis; it links to Relationship Review.
  - A generation counter discards answers for a previous project or environment. The page reloads on a workspace change.
- **Systems:**
  - search across names and other names;
  - filters for kind, decision needed and scope membership;
  - grouped rows (system → declared services);
  - expandable detail: basis, other names, services, connections, configured location, scopes;
  - identity decisions through the shared review decisions;
  - "Start a scope with …".
- **Connections:**
  - a project-wide list by default, with an evidence summary;
  - a searchable system combobox (not a native dropdown);
  - direction, evidence and origin filters, with rejected connections hidden unless asked for;
  - a focused incoming/outgoing map as an accessible list;
  - expandable evidence and "Not proven" text;
  - Relationship Review deep links with a safe return link (`?from=project-scopes…`, local paths only).
- **Scopes:**
  - a searchable, filterable table;
  - a three-step wizard (details → catalog members → review) built on the reusable `ScopeMemberPicker`, with kind tabs, server-side search, paging and a selected tray. *Advanced* by-id entry is checked against the catalog before it is added.
  - a scope detail view with members grouped by kind and stale members flagged;
  - edit (a new revision after approval), submit for review, approve with a recorded rationale, archive with confirmation, revision history, and comparison of any two revisions.
- **Consumers:** a shared `ScopeFilter` component lists approved scopes only, keeps the choice in `?scope=…`, and is optional wherever it is embedded.
  - **Test Data** filters assets by the scope's `AssetId`s and reports scope members missing from the catalog and the scenarios linked in the same scope.
  - **Test Plan Suggestions** filters guide scenarios by their canonical scenario identity (`CorrelationRules.GuideScenarioIdentity`). It states that requirement, story and feature members do not filter scenarios yet.
  - Without a selection, both pages behave exactly as before.
- **Docs:** User Guide (`GuideProject.razor`: overview, systems, connections, scopes, consumers) and this report.

## 4. Verification

- **Backend focused** (`ScopeRegistry` + `SystemsAndScopes`): 12/12.
  - The 7 new tests use an M2LB-shaped fixture. They cover grouping by declaration only, no Altinn invention, the messaging connection, the candidate's weaker end, rejected relations, a stale entity excluded, the no-interpretation notice, topology merge (an expectation dropped, an unmatched component keeps its own name, a location annotated, nothing observed), catalogs, wrong-kind / fabricated / other-environment / no-catalog ids refused with nothing saved, a requirement- and scenario-only scope, an empty draft that cannot be approved, a missing member kept and flagged, a new revision leaving revision 1 approved, and submit for review.
- **Frontend focused:** 380/380.
  - `ProjectScopesPageTests` has 7 tests: loading is never an empty state, the not-analysed state never runs an analysis, readable header and grouping and search and the review filter, project-wide connections with Altinn's empty state and the return link, a scenario + requirement scope from catalogs without systems, the approved → new revision → compare flow with a stale flag, and presentation states.
  - New consumer tests in Test Data and Test Plan Suggestions.
  - The set also includes the Relationship Review, navigation, guide, documentation and feature-visibility suites.
- **Full frontend regression:** 8815/8815 passed.
- **Backend regression** (sequential memory-guarded partitions):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 852 | 0 | 0 |
  | 4 | 652 | 1 | 0 |
  | 5 | 521 | 3 | 0 |
  | 6 | **not run** | | |

  Partition 6 is the rest: root Services, Unit, Controllers and DeliveryReporting. Claude Code stopped it because the machine was low on memory, so its result is unknown. The new Scope Registry and systems tests ran in the focused run (12/12).
- **Release publish** of the API and web app (Release configuration): succeeded.
- **Browser acceptance (Edge, isolated):**
  - API on :5097 with the disposable database `birknext_scopes_acc_20261113`, published web on :5197. Ports 5000 and 5173 were not used. Requests to other hosts were blocked and recorded: none were attempted.
  - **Real M2LB data:** the latest canonical interpretation of the M2LB-language Target Environment (4,476 entities, 4,852 relations, one snapshot) was copied read-only from the local database into the disposable one.
  - The workspace project is a synthetic ZIP imported through Import Project.
  - **Result: 36/36 checks** (first run); **35/35** on the rebuilt UI (import skipped):
    - 23 systems and services, with the readable project name;
    - search "Person" finds Person, M2LB.Person.Api and M2LB.PersonBiRKAdapter.Worker; Person's detail lists M2LB.Person.Api by declaration, and the Worker stays separate;
    - project-wide connections (1, from source evidence; 0 observed at runtime);
    - Altinn: "No relationship evidence for Altinn" (no invented MU link);
    - Relationship Review → back keeps Altinn focused;
    - wizard with 2 systems + 1 scenario + 1 requirement + 1 Test Data asset; draft; reload by deep link; approve (recorded `local-session:loopback` with rationale); edit → "Creates draft revision 2"; compare shows the removed member; revision 1 stays approved;
    - Test Data and Test Plan Suggestions apply the scope;
    - no horizontal overflow at 1440, 1280, 1024 and 768 px on all three tabs;
    - no page errors, no 5xx responses, no external calls.
  - Screenshots are in the session scratchpad (`acc/shots`).
- **Performance (measured, acceptance API log, real M2LB data):**
  - `GET api/scopes/systems` median 55 ms, max 584 ms (cold);
  - catalog search median 38 ms, max 149 ms;
  - member check median 47 ms;
  - scope detail ~33 ms;
  - first render of the Systems tab after navigation 3.2–5.5 s (includes the WASM app start);
  - in-page system search ~0.36 s.

## 5. Remaining gaps (honest)

- **"Interpretation running"** is not observable from this page. The analysis runs inside a Relationship Review request, and there is no shared job state, so another tab's running analysis shows as the last finished state.
- **Project ↔ Target Environment:** scopes are per project and systems are per Target Environment. There is no binding between the two. Each member records the environment it was chosen in, and a member from another environment shows as "From another environment". A project with several environments must pick the right active one.
- **Integration Topology on the real M2LB copy:** in the acceptance database the environment was not a trusted Target Environment, so topology contributed no connections there. The merge is covered by unit tests only.
- **Real-data connections:** the real M2LB interpretation holds only 1 system-to-system connection (a message channel). The page reports what the evidence states and adds nothing.
- **Not consuming scopes yet:** Integration Journeys, Critical E2E, Test Coverage & Overlap, Requirements Traceability, Impact Analysis, Unified Impact Analysis, Delivery Overview / Reports and Recommended Workflow. Each still uses its own context. Scope selection is never an execution authorization.
- **Requirement → scenario filtering** in Test Plan Suggestions needs reviewed canonical Verifies relations. It is stated in the UI, not inferred.
- **Visual graph:** there is no graph canvas. The focused incoming/outgoing map is an accessible list, and no new graph library was added.
- **Membership per scope** on the Systems tab is loaded with one detail request per scope. This is fine for tens of scopes; a summary endpoint would be needed for hundreds.
- **Task-30 audit documents:** the five scope audit documents in `docs/audits` are untracked files from another session and were not edited. This report and the committed implementation report record the actual state.
