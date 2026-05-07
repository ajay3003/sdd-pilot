# Tasks: Scenario Management

**Input**: Design documents from `/specs/001-create-scenario/`  
**Prerequisites**: plan.md ✅ | spec.md ✅ | research.md ✅ | data-model.md ✅ | contracts/schema.graphql ✅

**Stack**: ASP.NET Core + HotChocolate 14 (backend) | Blazor WebAssembly + Strawberry Shake 14 (frontend) | EF Core 8 + PostgreSQL 16

**Tests**: Test tasks are **mandatory** — the constitution (§I Test-First Development, NON-NEGOTIABLE) requires every test to be written and confirmed **failing** before implementation begins.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

---

## Format: `[ID] [P?] [Story?] Description with file path`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[US#]**: Which user story this task belongs to

---

## Phase 1: Setup

**Purpose**: Initialize project structure and install all dependencies before any feature code is written.

- [ ] T001 Create folder structure `backend/` and `frontend/` at repo root per plan.md
- [ ] T002 Initialize `backend/BirkNext.Api` (ASP.NET Core Web API, .NET 8) and `backend/BirkNext.Api.Tests` (xUnit) with project references; add solution file `backend/BirkNext.sln`
- [ ] T003 Initialize `frontend/BirkNext.Web` (Blazor WebAssembly, .NET 8) and `frontend/BirkNext.Web.Tests` (bUnit) with project references; add solution file `frontend/BirkNext.sln`
- [ ] T004 [P] Add NuGet packages to `backend/BirkNext.Api/BirkNext.Api.csproj`: `HotChocolate.AspNetCore`, `HotChocolate.Data.EntityFramework`, `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Serilog.AspNetCore`, `Serilog.Formatting.Compact`
- [ ] T005 [P] Add NuGet packages to `backend/BirkNext.Api.Tests/BirkNext.Api.Tests.csproj`: `FluentAssertions`, `HotChocolate.Testing`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `Moq`
- [ ] T006 [P] Add NuGet packages to `frontend/BirkNext.Web/BirkNext.Web.csproj`: `StrawberryShake.Blazor`; add to `frontend/BirkNext.Web.Tests/BirkNext.Web.Tests.csproj`: `bunit`, `Moq`
- [ ] T007 [P] Create `docker-compose.yml` at repo root with a `postgres` service (image `postgres:16`, port `5432`, database `birknext`, credentials via `.env`)

**Checkpoint**: All projects initialize and build. `dotnet build` passes on both solutions.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before any user story work begins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T008 Create `Scenario` entity in `backend/BirkNext.Api/Models/Scenario.cs` and `ScenarioKind` enum in `backend/BirkNext.Api/Models/ScenarioKind.cs` per data-model.md (Id, Title, Description, Kind, ProjectId, CreatedAt)
- [ ] T009 Create `AppDbContext` with `Scenarios` DbSet, `ScenarioKind` varchar value converter, and column/index configuration in `backend/BirkNext.Api/Data/AppDbContext.cs`
- [ ] T010 Add initial EF Core migration and verify generated SQL matches the schema in data-model.md (table `scenarios`, index on `project_id, created_at DESC`) in `backend/BirkNext.Api/Data/Migrations/`
- [ ] T011 [P] Implement `CorrelationIdMiddleware` (reads or generates `X-Correlation-Id`, pushes to Serilog `LogContext`) in `backend/BirkNext.Api/Middleware/CorrelationIdMiddleware.cs`; configure Serilog `CompactJsonFormatter` console sink in `backend/BirkNext.Api/Program.cs`
- [ ] T012 [P] Configure CORS (allow `FRONTEND_ORIGIN` env var, default `http://localhost:5173`) and bind `ConnectionStrings__Default` from environment in `backend/BirkNext.Api/Program.cs`; populate `backend/BirkNext.Api/appsettings.json` with dev defaults
- [ ] T013 Register HotChocolate GraphQL server on `/graphql` (empty schema, Banana Cake Pop enabled in Development) in `backend/BirkNext.Api/Program.cs`
- [ ] T014 [P] Create `strawberry-shake.json` (or `.editorconfig` codegen config) targeting `http://localhost:5000/graphql` in `frontend/BirkNext.Web/`; register generated Strawberry Shake client in `frontend/BirkNext.Web/Program.cs`
- [ ] T015 [P] Create `frontend/BirkNext.Web/Pages/Scenarios.razor` with route `@page "/scenarios"` (empty shell); add nav link in `frontend/BirkNext.Web/Shared/NavMenu.razor`

**Checkpoint**: Backend starts, `/graphql` responds to introspection. Frontend starts and navigates to `/scenarios`.

---

## Phase 3: User Story 1 — Create a New Scenario (Priority: P1) 🎯 MVP

**Goal**: A user fills in the form (title, optional description, type), submits it, and the new scenario is saved and confirmed.

**Independent Test**: Submit the `ScenarioForm` with valid data → `createScenario` mutation returns a scenario with an `id` → scenario appears in the list.

### ⚠️ Tests FIRST — confirm each test FAILS before writing any implementation

- [ ] T016 [P] [US1] Write failing unit tests for `ScenarioService.CreateAsync`: title required, kind required, valid input inserts row and returns scenario in `backend/BirkNext.Api.Tests/Unit/ScenarioServiceTests.cs`
- [ ] T017 [P] [US1] Write failing integration test: `createScenario` mutation with valid input returns `scenario.id`; with missing title returns `errors[0].code == "TITLE_REQUIRED"` in `backend/BirkNext.Api.Tests/Integration/ScenariosMutationTests.cs`
- [ ] T018 [P] [US1] Write failing bUnit tests for `ScenarioForm.razor`: title input, description input, kind dropdown, and submit button render; submit button is disabled while mutation is in flight in `frontend/BirkNext.Web.Tests/Components/ScenarioFormTests.cs`
- [ ] T019 [P] [US1] Write failing contract/integration test: `createScenario` mutation returns non-empty `correlationId` in `CreateScenarioPayload` for both success and validation-error responses in `backend/BirkNext.Api.Tests/Integration/ScenariosMutationTests.cs`

### Implementation

- [ ] T020 [US1] Implement `ScenarioService.CreateAsync` (validate title/kind, EF Core insert, log `ScenarioCreated` and `ScenarioCreationFailed` events with correlationId and projectId) in `backend/BirkNext.Api/Services/ScenarioService.cs` — **starts only after T016 is RED**
- [ ] T021 [P] [US1] Define `ScenarioObjectType` (HotChocolate object type mapping all Scenario fields) in `backend/BirkNext.Api/GraphQL/ScenarioObjectType.cs`; define `CreateScenarioInput` and `CreateScenarioPayload` (with `UserError` type and `correlationId`) in `backend/BirkNext.Api/GraphQL/CreateScenarioInput.cs` — **starts only after T017 and T019 are RED**
- [ ] T022 [US1] Implement `Mutation.CreateScenario` resolver (calls `ScenarioService`, maps result to `CreateScenarioPayload`, populates `correlationId` from request correlation context) in `backend/BirkNext.Api/GraphQL/Mutation.cs`; register `MutationType` in schema in `backend/BirkNext.Api/Program.cs` — **starts only after T017 and T019 are RED**
- [ ] T023 [P] [US1] Write `CreateScenario.graphql` operation document (mutation with `CreateScenarioInput`, returns `scenario { id title kind createdAt }`, `errors { code message field }`, and `correlationId`) in `frontend/BirkNext.Web/GraphQL/CreateScenario.graphql`
- [ ] T024 [US1] Implement `ScenarioForm.razor` (Blazor `EditForm`, title input, description textarea, kind `InputSelect`, submit handler via Strawberry Shake `ICreateScenarioMutation`, `_isSubmitting` guard, error display from `payload.Errors`, optional technical support display using `correlationId`) in `frontend/BirkNext.Web/Components/ScenarioForm.razor` — **starts only after T018 is RED**
- [ ] T025 [US1] Wire `ScenarioForm` into `Scenarios.razor` page; invoke `OnScenarioCreated` callback to trigger list refresh in `frontend/BirkNext.Web/Pages/Scenarios.razor` — **starts only after T018 is RED**
- [ ] T026 [US1] Write HotChocolate schema snapshot test asserting `createScenario` mutation shape, `CreateScenarioPayload.scenario`, `CreateScenarioPayload.errors`, and `CreateScenarioPayload.correlationId` do not regress in `backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs`

**Checkpoint**: `createScenario` mutation works end-to-end. T016, T017, T018, and T019 tests all pass. US1 acceptance scenarios 1, 2, and 5 verified.

---

## Phase 4: User Story 2 — View Scenario List (Priority: P2)

**Goal**: A user navigates to the scenario list and sees all scenarios for the project, or an empty-state message if none exist.

**Independent Test**: Execute `scenarios(projectId: "proj-001")` query → returns created scenarios ordered by `createdAt DESC`; an empty project returns `[]` and the Blazor component shows the empty-state message.

### ⚠️ Tests FIRST — confirm each test FAILS before writing any implementation

- [ ] T027 [P] [US2] Write failing unit tests for `ScenarioService.GetAllAsync`: returns list ordered by `createdAt DESC` for given `projectId`; returns empty list when none exist in `backend/BirkNext.Api.Tests/Unit/ScenarioServiceTests.cs`
- [ ] T028 [P] [US2] Write failing integration test: `scenarios` query returns all scenarios for `projectId`; query on empty project returns `[]`; query with unknown `projectId` returns `[]` and does not leak scenarios from other projects in `backend/BirkNext.Api.Tests/Integration/ScenariosQueryTests.cs`
- [ ] T029 [P] [US2] Write failing integration test: `scenarios` query without required `projectId` is rejected by GraphQL validation before resolver execution in `backend/BirkNext.Api.Tests/Integration/ScenariosQueryTests.cs`
- [ ] T030 [P] [US2] Write failing bUnit tests for `ScenarioList.razor`: renders one row per scenario showing title, kind, description; renders empty-state message when list is empty in `frontend/BirkNext.Web.Tests/Components/ScenarioListTests.cs`

### Implementation

- [ ] T031 [US2] Implement `ScenarioService.GetAllAsync` (query by `projectId`, order by `CreatedAt DESC`, prevent cross-project leakage) in `backend/BirkNext.Api/Services/ScenarioService.cs` — **starts only after T027, T028, and T029 are RED**
- [ ] T032 [US2] Implement `Query.Scenarios` resolver (calls `ScenarioService.GetAllAsync`) in `backend/BirkNext.Api/GraphQL/Query.cs`; register `QueryType` in schema in `backend/BirkNext.Api/Program.cs` — **starts only after T027, T028, and T029 are RED**
- [ ] T033 [P] [US2] Write `GetScenarios.graphql` operation document (query returning `id title description kind createdAt`) in `frontend/BirkNext.Web/GraphQL/GetScenarios.graphql`
- [ ] T034 [US2] Implement `ScenarioList.razor` (iterate scenarios into table/list rows; show empty-state `<p>` when list is empty) in `frontend/BirkNext.Web/Components/ScenarioList.razor` — **starts only after T030 is RED**
- [ ] T035 [US2] Wire `ScenarioList` into `Scenarios.razor` (execute `GetScenarios` query on load; re-execute after `ScenarioForm.OnScenarioCreated` fires, without full page refresh) in `frontend/BirkNext.Web/Pages/Scenarios.razor` — **starts only after T030 is RED**
- [ ] T036 [US2] Add schema snapshot assertion for `scenarios` query to contract test in `backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs`

**Checkpoint**: `scenarios` query returns correct data. T027, T028, T029, and T030 tests all pass. US2 acceptance scenarios 1, 2, and 3 verified (including no-refresh update).

---

## Phase 5: User Story 3 — Inline Validation Feedback (Priority: P3)

**Goal**: A user who submits an incomplete form sees clear inline error messages next to the offending fields, and can correct and resubmit successfully.

**Independent Test**: Submit `ScenarioForm` with empty title → error message appears next to the title field. Submit with no kind → error appears next to kind field. Correct both → resubmit succeeds and scenario appears in list.

### ⚠️ Tests FIRST — confirm each test FAILS before writing any implementation

- [ ] T037 [P] [US3] Write failing bUnit tests for `ScenarioForm.razor` client-side validation: empty title submit shows "Title is required" near title field; no kind submit shows "A valid type must be selected" near kind field in `frontend/BirkNext.Web.Tests/Components/ScenarioFormTests.cs`
- [ ] T038 [P] [US3] Write failing bUnit test: correcting all validation errors and resubmitting calls the mutation and resets the form in `frontend/BirkNext.Web.Tests/Components/ScenarioFormTests.cs`
- [ ] T039 [P] [US3] Write failing integration test: `createScenario` with empty title returns `errors[0] = { code: "TITLE_REQUIRED", field: "title", message: "Title is required" }` in `backend/BirkNext.Api.Tests/Integration/ScenariosMutationTests.cs`
- [ ] T040 [P] [US3] Write failing integration test: `createScenario` with title longer than 500 characters returns `errors[0].code == "TITLE_TOO_LONG"` and does not insert a row in `backend/BirkNext.Api.Tests/Integration/ScenariosMutationTests.cs`

### Implementation

- [ ] T041 [US3] Add `DataAnnotations` (`[Required]`, `[MaxLength(500)]`) to `ScenarioForm` model and enable `<DataAnnotationsValidator>` and `<ValidationSummary>` inside `EditForm` in `frontend/BirkNext.Web/Components/ScenarioForm.razor` — **starts only after T037 and T038 are RED**
- [ ] T042 [US3] Add `<ValidationMessage For="...">` components next to title input and kind select to display per-field inline errors in `frontend/BirkNext.Web/Components/ScenarioForm.razor` — **starts only after T037 and T038 are RED**
- [ ] T043 [US3] Implement server-side input validation in `ScenarioService.CreateAsync` returning `UserError` list with codes `TITLE_REQUIRED`, `TITLE_TOO_LONG`, and `INVALID_KIND` and correct `field` paths; log `ScenarioValidationFailed` event in `backend/BirkNext.Api/Services/ScenarioService.cs` — **starts only after T039 and T040 are RED**
- [ ] T044 [US3] Map server-returned `payload.Errors` to per-field error messages in `ScenarioForm.razor` (display inline under the relevant field, not just in a summary banner) in `frontend/BirkNext.Web/Components/ScenarioForm.razor` — **starts only after T037, T038, T039, and T040 are RED**

**Checkpoint**: T037, T038, T039, and T040 tests all pass. US3 acceptance scenarios 1, 2, and 3 verified. All US1 acceptance scenarios still pass (no regression).

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Observability verification, formatting, performance sanity checks, and end-to-end validation across all stories.

- [ ] T045 [P] Verify all three Serilog events (`ScenarioCreated`, `ScenarioValidationFailed`, `ScenarioCreationFailed`) include `correlationId`, `projectId`, `level`, and `timestamp` fields by reviewing log output in `backend/BirkNext.Api/Services/ScenarioService.cs`
- [ ] T046 [P] Write integration test or structured log assertion verifying `correlationId` appears both in `createScenario` GraphQL payload and backend logs for the same request in `backend/BirkNext.Api.Tests/Integration/ScenariosMutationTests.cs`
- [ ] T047 [P] Add GraphQL operation duration logging for `createScenario` and `scenarios` operations (operation name, durationMs, correlationId, projectId, result status) via HotChocolate instrumentation or middleware in `backend/BirkNext.Api/Program.cs` or `backend/BirkNext.Api/GraphQL/`
- [ ] T048 [P] Verify `scenarios` query performance with multiple records (at least 100 scenarios in one project) and confirm ordering by `CreatedAt DESC` remains correct in `backend/BirkNext.Api.Tests/Integration/ScenariosQueryTests.cs`
- [ ] T049 [P] Run `dotnet format` on `backend/BirkNext.sln` and `frontend/BirkNext.sln`; fix all formatting errors to reach zero warnings
- [ ] T050 [P] Add XML doc comments to all public HotChocolate types in `backend/BirkNext.Api/GraphQL/` to enable schema field descriptions (matches `schema.graphql` doc strings)
- [ ] T051 [P] Write bUnit integration test covering US1 acceptance scenario 5: `ScenarioForm` shows a user-friendly error message when the Strawberry Shake client throws a network exception in `frontend/BirkNext.Web.Tests/Pages/ScenariosPageTests.cs`
- [ ] T052 Validate `quickstart.md` end-to-end: `docker compose up -d postgres`, `dotnet ef database update`, `dotnet run` both tiers, `dotnet test` both solutions — all pass with zero failures

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 completion — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Phase 2
- **US2 (Phase 4)**: Depends on Phase 2; may start in parallel with US1 if staffed
- **US3 (Phase 5)**: Depends on Phase 3 (extends `ScenarioForm` and `ScenarioService`)
- **Polish (Phase 6)**: Depends on all desired stories being complete

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependency on US2 or US3
- **US2 (P2)**: Can start after Phase 2 — no dependency on US1 (separate resolver, separate component)
- **US3 (P3)**: Depends on US1 (extends the same form and service)

### Within Each Phase

- Test tasks MUST be written and confirmed **RED** before the matching implementation task starts
- Each implementation task MUST explicitly depend on its corresponding RED test task where applicable
- Models/entities before services
- Services before resolvers
- Resolvers before frontend integration
- Commit after each completed task or logical group

---

## Parallel Opportunities

### Phase 3 (US1)
```
# Run in parallel (different files):
T016 — ScenarioServiceTests.cs (unit)
T017 — ScenariosMutationTests.cs (integration)
T018 — ScenarioFormTests.cs (bUnit)
T019 — ScenariosMutationTests.cs (correlationId payload)

# Then in parallel once tests are RED:
T020 — ScenarioService.cs (after T016 RED)
T021 — GraphQL types and payload (after T017/T019 RED)
T022 — Mutation resolver (after T017/T019 RED)
T023 — CreateScenario.graphql operation document
T024 — ScenarioForm.razor (after T018 RED)
```

### Phase 4 (US2)
```
# Run in parallel (different files):
T027 — ScenarioServiceTests.cs (unit, GetAllAsync)
T028 — ScenariosQueryTests.cs (valid/empty/unknown project)
T029 — ScenariosQueryTests.cs (missing projectId validation)
T030 — ScenarioListTests.cs (bUnit)

# Then in parallel:
T031 — ScenarioService.cs (GetAllAsync)
T033 — GetScenarios.graphql operation document
```

### Phase 5 (US3)
```
# Run in parallel:
T037 — ScenarioFormTests.cs (client-side validation)
T038 — ScenarioFormTests.cs (fix-and-resubmit)
T039 — ScenariosMutationTests.cs (server UserError TITLE_REQUIRED)
T040 — ScenariosMutationTests.cs (server UserError TITLE_TOO_LONG)
```

---

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (**CRITICAL — blocks all stories**)
3. Complete Phase 3: US1 — Create a New Scenario
4. **STOP and VALIDATE**: `createScenario` mutation works, form submits, scenario confirmed, `correlationId` returned
5. Demo / deploy

### Incremental Delivery

1. Setup + Foundational → foundation ready
2. US1 complete → create scenario end-to-end (**MVP**)
3. US2 complete → scenario list visible
4. US3 complete → refined inline validation
5. Polish → observability verified, performance sanity checked, all tests green

### Parallel Team Strategy

With two developers after Phase 2 completes:
- **Dev A**: US1 (Phase 3) — mutation, `ScenarioForm`, service
- **Dev B**: US2 (Phase 4) — query, `ScenarioList`, service
- Merge when both are independently tested; US3 follows on top of US1

---

## Notes

- `[P]` tasks operate on different files with no cross-task dependencies within the same phase
- Each user story phase is independently completable and testable
- Test tasks must be confirmed **RED** before the matching implementation task starts (constitution §I)
- Each implementation task should clearly follow from a RED test task
- Commit after each task or logical group; branch stays shippable at every checkpoint
- Total tasks: **52** | Setup: 7 | Foundational: 8 | US1: 11 | US2: 10 | US3: 8 | Polish: 8
