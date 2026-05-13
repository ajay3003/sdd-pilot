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

---
---

# Tasks: Feature US2 — Deterministic Scenario Extraction

**Input**: Design documents from `/specs/001-create-scenario/`
**Prerequisites**: plan.md §US2 ✅ | spec.md §US2 ✅ | research.md §R-US2 ✅ | data-model.md §US2 ✅ | contracts/schema.graphql ✅
**Depends on**: Phases 1–2 complete (project infrastructure, ASP.NET Core + Blazor WASM scaffolding, EF Core + PostgreSQL, Serilog, HotChocolate)

**Stack**: same as US1 — ASP.NET Core + HotChocolate 14 (backend) | Blazor WebAssembly + Strawberry Shake 14 (frontend)

**Architecture boundary**: extraction pipeline runs client-side (Blazor WASM) only. The server is contacted exclusively at the batch save step. Raw pasted specification text never crosses the wire.

**Organization**: Tasks are grouped by architectural layer to enable the backend GraphQL extension and the frontend extraction pipeline to proceed in parallel after Phase 7.

---

## Format: `[ID] [P?] Description with file path`

- **[P]**: Can run in parallel with other `[P]` tasks in the same phase (different files, no mutual dependencies)

---

## Phase 7: Domain and Service Foundation

**Purpose**: Define all client-side model types and service interfaces before any pipeline or component work begins. All tasks in this phase target different files and can run in parallel.

**Prerequisite**: Phases 1–2 complete.

- [ ] T053 [P] Create `BlockType` enum (13 values: `Heading`, `UnorderedListItem`, `OrderedListItem`, `FencedCodeBlock`, `Blockquote`, `TableBodyRow`, `TableHeaderRow`, `TableSeparatorRow`, `HorizontalRule`, `ParagraphLine`, `YamlFrontMatter`, `HtmlComment`, `Empty`) per data-model.md §BlockType in `frontend/BirkNext.Web/Models/BlockType.cs`
- [ ] T054 [P] Create `ClassificationSignal` enum (7 values: `BddPattern`, `Rfc2119Uppercase`, `Rfc2119Lowercase`, `FrPrefix`, `QuestionTerminator`, `DeferralMarker`, `Default`) per data-model.md §ClassificationSignal in `frontend/BirkNext.Web/Models/ClassificationSignal.cs`
- [ ] T055 [P] Create `PipelineStatus` enum (4 values: `Success`, `EmptyInput`, `InputTooLarge`, `NoResults`) per data-model.md §PipelineStatus in `frontend/BirkNext.Web/Models/PipelineStatus.cs`
- [ ] T056 [P] Create `CandidateSaveState` enum (5 values: `Pending`, `Saving`, `Saved`, `Failed`, `Retrying`) per data-model.md §CandidateSaveState in `frontend/BirkNext.Web/Models/CandidateSaveState.cs`
- [ ] T057 [P] Create `ReviewSavePhase` enum (5 values: `Idle`, `Saving`, `PartialSuccess`, `Complete`, `Failed`) per data-model.md §ReviewSavePhase in `frontend/BirkNext.Web/Models/ReviewSavePhase.cs`
- [ ] T058 [P] Create `TextBlock` record (fields: `RawText string`, `BlockType BlockType`, `IndentationLevel int`, `PrecedingHeading string?`) per data-model.md §TextBlock in `frontend/BirkNext.Web/Models/TextBlock.cs`; pipeline-internal only — no public access from components
- [ ] T059 [P] Create `ExtractionCandidate` record (10 fields: `CandidateId Guid`, `Title string`, `Classification ScenarioKind`, `ClassificationSignal ClassificationSignal`, `ContextHeading string?`, `SourceBlockType BlockType`, `IsSelected bool` default `false`, `SaveState CandidateSaveState` default `Pending`, `SaveError string?` default `null`, `SavedScenarioId Guid?` default `null`; reserve `Confidence float?` as null — AI extensibility seam per data-model.md §Extensibility) in `frontend/BirkNext.Web/Models/ExtractionCandidate.cs`
- [ ] T060 [P] Create `ExtractionPipelineResult` record (fields: `Status PipelineStatus`, `Candidates IReadOnlyList<ExtractionCandidate>`, `InputLengthChars int`, `InputLineCount int`, `DurationMs long`, `RequirementCount int`, `TestCount int`, `NeedsClarificationCount int`) per data-model.md §ExtractionPipelineResult; enforce via constructor or factory that `RequirementCount + TestCount + NeedsClarificationCount == Candidates.Count` and that `Candidates` is never null (use `Array.Empty<ExtractionCandidate>()` for non-Success status) in `frontend/BirkNext.Web/Models/ExtractionPipelineResult.cs`
- [ ] T061 [P] Create `IExtractionConfiguration` interface (properties: `MaxInputLengthChars int`, `MinCandidateLengthChars int`, `MaxLineLengthForPatternMatching int`) and `ExtractionConfiguration` default implementation (`MaxInputLengthChars = 50_000`, `MinCandidateLengthChars = 3`, `MaxLineLengthForPatternMatching = 2_000`); register as singleton in `frontend/BirkNext.Web/Program.cs` in `frontend/BirkNext.Web/Services/ExtractionConfiguration.cs`
- [ ] T062 [P] Create `IScenarioExtractionService` interface (single method `Extract(string rawInput): ExtractionPipelineResult`) in `frontend/BirkNext.Web/Services/IScenarioExtractionService.cs`; create empty `ScenarioExtractionService : IScenarioExtractionService` stub that throws `NotImplementedException`; register as scoped in `frontend/BirkNext.Web/Program.cs`

**Checkpoint**: `dotnet build frontend/BirkNext.sln` passes with all new types. No runtime dependency on the backend.

---

## Phase 8: Extraction Pipeline

**Purpose**: Implement all 8 pipeline stages inside `ScenarioExtractionService`. Each stage task extends the same file incrementally; each must leave the project in a buildable state. Tests are written after all stages are complete and can be written in parallel with the later stages.

**Prerequisite**: Phase 7 complete (model types and interface must exist).

- [ ] T063 Implement Stage 1 (Input Validation Gate) and Stage 2 (Normalization) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — Stage 1: return `PipelineStatus.EmptyInput` when `rawInput` is null, empty, or whitespace only; return `PipelineStatus.InputTooLarge` when `rawInput.Length > IExtractionConfiguration.MaxInputLengthChars`; Stage 2: replace `\r\n` with `\n`, strip UTF-8 BOM (`﻿`) if present; record `InputLengthChars` and `InputLineCount` from the raw input before normalization
- [ ] T064 Implement Stage 3 (Block Partitioning) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — iterate normalized lines sequentially with look-ahead; detect and group fenced code block ranges (` ``` ` open/close pairs) so their interior lines are tagged `FencedCodeBlock`; detect YAML front matter (leading `---` block before any non-empty line); classify each line as one of the 13 `BlockType` values; set `IndentationLevel` for list items (count leading spaces / 2); track `PrecedingHeading` as the text of the most recent `Heading` line seen; output ordered `IReadOnlyList<TextBlock>`
- [ ] T065 Implement Stage 4 (Structure Filter) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — discard `TextBlock` instances with `BlockType` in: `Heading`, `FencedCodeBlock`, `Blockquote`, `HorizontalRule`, `HtmlComment`, `YamlFrontMatter`, `Empty`, `TableHeaderRow`, `TableSeparatorRow`; retain: `UnorderedListItem`, `OrderedListItem`, `TableBodyRow`, `ParagraphLine`; output filtered sequence preserving order
- [ ] T066 Implement Stage 5 (Content Extraction) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — for each retained block: strip list markers (`-`/`*`/`+`/`N.` prefix), strip inline code backticks preserving inner text, strip link syntax `[text](url)` retaining display text, strip image syntax `![alt](url)` entirely, strip leading table pipe characters for `TableBodyRow`; trim result; discard if result length < `IExtractionConfiguration.MinCandidateLengthChars`; carry `TextBlock.PrecedingHeading` forward as `ContextHeading`; output ordered sequence of `(PlainText: string, ContextHeading: string?, SourceBlockType: BlockType)`
- [ ] T067 Implement Stage 6 (Classification) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — for each content item: if `PlainText.Length > IExtractionConfiguration.MaxLineLengthForPatternMatching` assign `ClassificationSignal.Default` (NeedsClarification) without pattern matching; otherwise apply heuristics in priority order: (1) `BddPattern` — line contains Given/When/Then triple or starts with "Given "/"When "/"Then "; (2) `Rfc2119Uppercase` — line contains MUST/SHALL/SHOULD/MAY/MUST NOT/SHALL NOT (case-sensitive uppercase); (3) `Rfc2119Lowercase` — line contains must/shall/required/is required to (case-insensitive, word-boundary matched); (4) `FrPrefix` — line matches `FR-\d+` pattern; (5) `QuestionTerminator` — trimmed line ends with `?`; (6) `DeferralMarker` — line contains TBD/TODO/TBC/open question/to be defined (case-insensitive); (7) `Default` — NeedsClarification fallback; first matching signal wins; record `ClassificationSignal` and derive `Classification` (`ScenarioKind`) from it
- [ ] T068 Implement Stage 7 (Deduplication) and Stage 8 (Result Assembly) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — Stage 7: deduplicate by case-folded, trimmed `PlainText`; keep first occurrence, discard subsequent exact matches; Stage 8: assemble `ExtractionCandidate` records (new `CandidateId = Guid.NewGuid()`, `IsSelected = false`, `SaveState = CandidateSaveState.Pending`); compute `RequirementCount`, `TestCount`, `NeedsClarificationCount`; if deduplicated list is empty return `PipelineStatus.NoResults`; otherwise return `PipelineStatus.Success` with fully populated `ExtractionPipelineResult`; record `DurationMs` from a `Stopwatch` started at the top of `Extract()`
- [ ] T069 [P] Write unit tests for `ScenarioExtractionService` covering all pipeline stages in `frontend/BirkNext.Web.Tests/Services/ScenarioExtractionServiceTests.cs` — required coverage: empty string → `EmptyInput`; whitespace-only → `EmptyInput`; input at exactly `MaxInputLengthChars` → `Success`; input at `MaxInputLengthChars + 1` → `InputTooLarge`; input with no extractable bullets → `NoResults`; unordered bullet extraction; ordered bullet extraction; BDD triple classification → `Test`; MUST keyword classification → `Requirement`; question mark classification → `NeedsClarification`; TBD marker classification → `NeedsClarification`; default fallback → `NeedsClarification`; duplicate bullets → single candidate after deduplication; blank bullet (`- ` with no text) → discarded; Windows line endings normalized; heading text propagated as `ContextHeading`; fenced code block content not extracted; `RequirementCount + TestCount + NeedsClarificationCount == Candidates.Count` invariant holds; `DurationMs > 0` on Success

**Checkpoint**: `dotnet test frontend/BirkNext.Web.Tests --filter "ScenarioExtractionService"` passes. `ScenarioExtractionService.Extract()` has no Blazor, network, or DI dependency beyond `IExtractionConfiguration`.

---

## Phase 9: Backend GraphQL Extension

**Purpose**: Extend the HotChocolate schema with the `createScenarios` batch mutation and all supporting types. Can proceed fully in parallel with Phases 8 and 10 once Phase 7 is complete, as this work is in a different project.

**Prerequisite**: Phase 2 complete (HotChocolate server running). No dependency on client-side extraction pipeline.

- [ ] T070 [P] Add `CreateScenariosInput` and `ExtractionMetadataInput` HotChocolate input types in `backend/BirkNext.Api/GraphQL/CreateScenariosInput.cs` per data-model.md §Wire Models and schema.graphql — `CreateScenariosInput`: `items: [CreateScenarioInput!]!` (reuses existing US1 input type), `extractionMetadata: ExtractionMetadataInput?`; `ExtractionMetadataInput`: `totalExtracted int`, `selectedCount int`, `extractionDurationMs int`, `sessionId string` — all non-nullable; add HotChocolate `[GraphQLDescription]` attributes matching schema.graphql doc strings
- [ ] T071 [P] Add `CreateScenariosPayload`, `CreateScenarioSuccess`, `CreateScenarioError`, and `CreateScenarioResult` union HotChocolate types in `backend/BirkNext.Api/GraphQL/CreateScenariosPayload.cs` per data-model.md §Wire Models — `CreateScenariosPayload`: `results IReadOnlyList<CreateScenarioResult>`, `successCount int`, `failureCount int`, `correlationId string`; `CreateScenarioSuccess`: `scenario Scenario`; `CreateScenarioError`: `code string`, `message string`, `field string?`; register the `CreateScenarioResult` union type with HotChocolate
- [ ] T072 Add `CreateBatchAsync` method to `ScenarioService` in `backend/BirkNext.Api/Services/ScenarioService.cs` — accepts `IEnumerable<CreateScenarioInput>` and `string correlationId`; processes each item independently using the same validation rules as `CreateAsync` (title non-empty, max 500 chars, kind valid enum value, projectId non-empty); successful items are inserted; failed items produce a `CreateScenarioError` with the appropriate error code (`TITLE_REQUIRED`, `TITLE_TOO_LONG`, `KIND_INVALID`, `PROJECT_ID_REQUIRED`); returns ordered `IReadOnlyList<CreateScenarioResult>` preserving input order; does not throw on per-item validation failure — failures are captured as `CreateScenarioError` results
- [ ] T073 Implement `Mutation.CreateScenarios` resolver in `backend/BirkNext.Api/GraphQL/Mutation.cs` — call `ScenarioService.CreateBatchAsync`; compute `successCount` and `failureCount` from results; emit `CandidateReviewSaved` Serilog structured event with fields: `selectedCount` (from `input.items.Count`), `totalExtracted` (from `input.extractionMetadata.TotalExtracted` when present, else -1 to indicate not provided), `scenariosCreated` (`successCount`), `failedCount` (`failureCount`), `durationMs` (Stopwatch from resolver entry), `projectId` (from first item's `projectId`), `correlationId`; no field in the log event may carry text content from the pasted specification — only the counts and identifiers listed above; register the mutation field in the HotChocolate schema
- [ ] T074 Write integration tests for `createScenarios` mutation in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` — test cases: (a) all items valid → `successCount == items.Count`, each result is `CreateScenarioSuccess` with a non-null `scenario.id`, all scenarios visible in `scenarios` query; (b) one item has empty title → that result is `CreateScenarioError` with `code == "TITLE_REQUIRED"` and `field == "title"`, all other items succeed, `successCount + failureCount == items.Count`; (c) title exceeding 500 characters → `TITLE_TOO_LONG`; (d) empty `items` array → mutation rejected before resolver; (e) `extractionMetadata` omitted → mutation succeeds (field is optional); (f) `extractionMetadata` present → `CandidateReviewSaved` log event contains `totalExtracted` from metadata
- [ ] T075 [P] Extend schema snapshot test in `backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs` — add assertions verifying `createScenarios` mutation, `CreateScenariosInput`, `ExtractionMetadataInput`, `CreateScenariosPayload`, `CreateScenarioResult` union, `CreateScenarioSuccess`, and `CreateScenarioError` are all present in the HotChocolate-generated schema; verify snapshot matches `contracts/schema.graphql`; verify no existing US1 types (`Scenario`, `ScenarioKind`, `createScenario`, `scenarios`, `CreateScenarioPayload`, `UserError`) have changed shape

**Checkpoint**: `createScenarios` mutation works end-to-end against Testcontainers PostgreSQL. Partial success verified. Schema snapshot test passes. `contracts/schema.graphql` matches the generated schema.

---

## Phase 10: Frontend Strawberry Shake Integration

**Purpose**: Generate a typed batch mutation client from the operation document. Requires the backend schema to be available for code generation.

**Prerequisite**: Phase 9 complete (backend schema must include `createScenarios`).

- [ ] T076 Write `CreateScenarios.graphql` operation document in `frontend/BirkNext.Web/GraphQL/CreateScenarios.graphql` — mutation accepting `$input: CreateScenariosInput!`; return `results { ... on CreateScenarioSuccess { scenario { id title kind createdAt } } ... on CreateScenarioError { code message field } }`, `successCount`, `failureCount`, `correlationId`; run Strawberry Shake code generation (`dotnet build`) to confirm typed client `ICreateScenariosMutation` is generated without errors
- [ ] T077 Register the generated `ICreateScenariosMutation` Strawberry Shake client in `frontend/BirkNext.Web/Program.cs` DI container; confirm `dotnet build frontend/BirkNext.sln` produces zero errors and the generated client is injectable into Blazor components

**Checkpoint**: `ICreateScenariosMutation` is available for injection. `dotnet build` passes.

---

## Phase 11: Frontend Component Tree

**Purpose**: Implement the four components that form the extraction view UI. `ExtractionCandidateRow` and `ExtractionInput` are independent. `ExtractionReviewList` depends on `ExtractionCandidateRow`. `ScenarioExtraction` page depends on both `ExtractionInput` and `ExtractionReviewList`. Tests for each component can run in parallel with each other.

**Prerequisite**: Phase 7 (models), Phase 8 (extraction service), Phase 10 (Strawberry Shake batch client).

- [ ] T078 Implement `ExtractionCandidateRow.razor` in `frontend/BirkNext.Web/Components/ExtractionCandidateRow.razor` — parameters: `[Parameter] ExtractionCandidate Candidate` and `[Parameter] EventCallback<Guid> OnSelectionToggled`; render: classification badge using `Candidate.Classification` display name; `Candidate.ContextHeading` in muted text when non-null; `Candidate.Title` as plain text using `@Candidate.Title` within an element (never `@((MarkupString)...)` — XSS constraint from plan.md §Security and schema.graphql boundary rule); checkbox bound to `Candidate.IsSelected` that invokes `OnSelectionToggled` with `Candidate.CandidateId` on change; `SaveState` indicator: show "Saved" badge when `Candidate.SaveState == Saved`; show `Candidate.SaveError` error text when `Candidate.SaveState == Failed`; show spinner when `Candidate.SaveState == Saving`
- [ ] T079 Implement `ExtractionReviewList.razor` in `frontend/BirkNext.Web/Components/ExtractionReviewList.razor` — parameter: `[Parameter] ExtractionPipelineResult? PipelineResult`; inject `ICreateScenariosMutation` and active project context; render nothing when `PipelineResult` is null; render empty-state message when `PipelineResult.Status == NoResults`; render count summary header: "N candidates extracted — X REQUIREMENT, Y TEST, Z NEEDS_CLARIFICATION"; render three candidate groups (Requirement / Test / NeedsClarification) each via `ExtractionCandidateRow`; maintain `HashSet<Guid> _selectedIds` — default empty (opt-in selection, FR-US2-006); confirm-save button disabled when `_selectedIds` is empty; on save confirm: set `ReviewSavePhase = Saving`, set `SaveState = Saving` on all selected candidates, call `ICreateScenariosMutation.ExecuteAsync(input)` with selected candidates mapped to `CreateScenariosInput` per data-model.md §Persistence Boundary field mapping; on response: update per-candidate `SaveState` to `Saved` (with `SavedScenarioId`) or `Failed` (with `SaveError`) using `results[i]` → `items[i]` positional mapping; update `ReviewSavePhase` to `Complete`, `PartialSuccess`, or `Failed`; implement `IDisposable` to emit `CandidateReviewAbandoned` log event when disposed with a non-null `PipelineResult` that has candidates not all in `Saved` state; include `ExtractionMetadataInput` in the mutation input when `PipelineResult` metadata is available
- [ ] T080 Implement `ExtractionInput.razor` in `frontend/BirkNext.Web/Components/ExtractionInput.razor` — inject `IScenarioExtractionService`; render: multi-line text area bound to `_rawInput string`; extract trigger button; on trigger: validate input before calling `Extract()` — show inline message "Paste some text to extract candidates from" when text area empty (FR-US2-009); show "Input is too large (max 50,000 characters)" when input exceeds cap; call `IScenarioExtractionService.Extract(_rawInput)` otherwise; emit `ExtractionTriggered` log event before calling `Extract()` (inputLengthChars, inputLineCount, generated sessionId stored in component state); emit `ExtractionCompleted` log event after `Extract()` returns with `Status == Success` (candidateCount, requirementCount, testCount, needsClarificationCount, durationMs); emit `ExtractionEmpty` log event after `Extract()` returns with `Status != Success` (inputLengthChars, reason derived from PipelineStatus); raise `EventCallback<ExtractionPipelineResult> OnExtractionCompleted` with the result; disable extract button while extraction is running
- [ ] T081 Implement `ScenarioExtraction.razor` page in `frontend/BirkNext.Web/Pages/ScenarioExtraction.razor` — route `@page "/extract"`; host `ExtractionInput` and `ExtractionReviewList`; declare `ExtractionPipelineResult? _pipelineResult` field; wire `ExtractionInput.OnExtractionCompleted` to set `_pipelineResult` and pass it to `ExtractionReviewList.PipelineResult`; no business logic in the page — orchestration only; add nav link entry for "Extract" pointing to `/extract` in `frontend/BirkNext.Web/Shared/NavMenu.razor`
- [ ] T082 [P] Write bUnit tests for `ExtractionCandidateRow.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionCandidateRowTests.cs` — test cases: classification badge text matches `ScenarioKind` display name; `ContextHeading` appears when non-null and is absent when null; candidate title is rendered as text content not as HTML markup (assert `InnerHtml` does not contain unescaped `<` or `>` when title contains HTML characters); checkbox is unchecked by default; toggling checkbox raises `OnSelectionToggled` with correct `CandidateId`; `SaveState.Saved` shows saved indicator; `SaveState.Failed` shows `SaveError` text; `SaveState.Saving` shows spinner
- [ ] T083 [P] Write bUnit tests for `ExtractionReviewList.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — test cases: null `PipelineResult` renders nothing; `PipelineStatus.NoResults` shows empty-state message; count summary header shows correct totals; candidates are rendered in three groups by classification; no candidate checkbox is checked by default; confirm-save button is disabled when no candidates selected; confirm-save button enabled when at least one candidate selected; on successful save response, candidate row shows Saved indicator; on error response, candidate row shows error message; after complete save `ReviewSavePhase.Complete` state is reached
- [ ] T084 [P] Write bUnit tests for `ExtractionInput.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionInputTests.cs` — test cases: empty text area submission shows validation message and does not call `IScenarioExtractionService.Extract()`; input above `MaxInputLengthChars` shows length error and does not call `Extract()`; valid input calls `Extract()` with the raw string; successful extraction raises `OnExtractionCompleted` with the pipeline result; extract button is disabled during extraction and re-enabled after

**Checkpoint**: Navigate to `/extract`. `dotnet build` passes. Paste a spec.md fragment containing bullet points. Candidates appear grouped by classification. Selecting candidates and clicking confirm-save calls `createScenarios`. Saved candidates appear in the US1 `/scenarios` list on navigation.

---

## Phase 12: Observability

**Purpose**: Verify that all five structured log events from plan.md §Observability Integration are emitted with correct fields and without text content from pasted input. Client-side events use console logging in v1 (plan.md §Observability Option B) pending a telemetry endpoint decision.

**Prerequisite**: Phase 11 complete (components must be implemented to instrument them).

- [ ] T085 Verify `ExtractionTriggered`, `ExtractionCompleted`, and `ExtractionEmpty` log events in `frontend/BirkNext.Web/Components/ExtractionInput.razor` — confirm each event contains only the fields specified in data-model.md §Observability Model Fields; confirm no field carries text from `_rawInput` (only `inputLengthChars` and `inputLineCount` numeric values); confirm `sessionId` is a consistent identifier across the three events for the same extraction session; use `ILogger<ExtractionInput>` injected via DI; add an inline comment at each log call noting the "no raw text" constraint for code review awareness
- [ ] T086 Verify `CandidateReviewAbandoned` log event in `frontend/BirkNext.Web/Components/ExtractionReviewList.razor` — confirm the event is emitted in `Dispose()` when `PipelineResult` is non-null and at least one candidate is not in `Saved` state; confirm it logs only `totalExtracted` and `selectedCount` (counts from `_selectedIds`) — no candidate title text; verify event is not emitted when all selected candidates have been successfully saved
- [ ] T087 Verify `CandidateReviewSaved` Serilog event in `backend/BirkNext.Api/GraphQL/Mutation.cs` — manually inspect the structured log output for a `createScenarios` call with `extractionMetadata` present: confirm `selectedCount`, `totalExtracted`, `scenariosCreated`, `failedCount`, `durationMs`, `projectId`, and `correlationId` all appear; confirm no field contains text from any candidate title; add an integration test assertion in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` verifying `CandidateReviewSaved` is emitted with a non-zero `durationMs`

**Checkpoint**: All five log events are emitted. `CandidateReviewSaved` appears in backend structured JSON output. No pasted text appears in any log field.

---

## Phase 13: Validation and Security

**Purpose**: Verify input sanitization, XSS rendering constraints, and server-side batch validation rules — each as an independent verification step.

**Prerequisite**: Phases 11 and 9 complete.

- [ ] T088 Verify XSS rendering constraint across the component tree — inspect `ExtractionCandidateRow.razor` and confirm candidate `Title` is bound with `@Candidate.Title` inside element text content (not `@((MarkupString)Candidate.Title)` or `innerHTML`); verify `ContextHeading` is similarly plain-text bound; add a bUnit test in `frontend/BirkNext.Web.Tests/Components/ExtractionCandidateRowTests.cs` that passes a title containing `<script>alert(1)</script>` and asserts the rendered output contains the literal string `&lt;script&gt;` (escaped) not an executable script element
- [ ] T089 Extend batch mutation integration tests in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` — add test cases for server-side batch validation: (a) item with title exceeding 500 chars → `CreateScenarioError.code == "TITLE_TOO_LONG"`, `field == "title"`; (b) item with empty `projectId` → `CreateScenarioError.code == "PROJECT_ID_REQUIRED"`; (c) a batch where all items fail validation → `successCount == 0`, `failureCount == items.Count`, no rows inserted; (d) mixed batch (some valid, some invalid) → correct partial success counts, valid items committed to DB, invalid items rejected without rolling back committed items
- [ ] T090 Verify `ExtractionMetadataInput` carries no text content — add a schema-level assertion in the contract test (`backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs`) confirming `ExtractionMetadataInput` has exactly 4 fields (`totalExtracted`, `selectedCount`, `extractionDurationMs`, `sessionId`) with types `Int!`, `Int!`, `Int!`, `String!` and no additional fields; verify the resolver does not log the `sessionId` value if it resembles user content (document the constraint that `sessionId` must be an opaque client-generated identifier, not derived from pasted text)

**Checkpoint**: Pasting `<script>alert(1)</script>` as a bullet renders as literal escaped text in the review list. All batch validation error codes match those documented in schema.graphql. Schema structure of `ExtractionMetadataInput` is locked by snapshot test.

---

## Phase 14: Integration and Verification

**Purpose**: End-to-end acceptance scenario verification, US1 regression, performance measurement, and final build health. All `[P]` tasks are independent and can run concurrently.

**Prerequisite**: Phases 7–13 complete.

- [ ] T091 Verify all 6 US2 acceptance scenarios from spec.md §US2 against the running application — AC1: paste spec text with bullet points → all bullets extracted and displayed as candidates; AC2: each candidate shows classification label (REQUIREMENT/TEST/NEEDS_CLARIFICATION); AC3: no candidates auto-persisted before user confirm action (inspect `scenarios` query before and after extraction but before save); AC4: paste text with no extractable candidates → empty-state message displayed; AC5: select subset of candidates → only selected candidates appear in `scenarios` query after save, unselected do not; AC6: click extract with empty text area → validation message shown, no extraction attempted
- [ ] T092 [P] Run full regression to verify US1 is unaffected — execute `dotnet test backend/BirkNext.Api.Tests` and `dotnet test frontend/BirkNext.Web.Tests`; confirm all T016–T051 tests pass; verify `createScenario` (single) mutation still returns correct payload shape; verify `scenarios` query still returns results in `createdAt DESC` order; verify scenarios created via batch save appear in the `scenarios` query identically to manually created scenarios
- [ ] T093 [P] Measure extraction performance — paste a representative 10,000-character spec document (a copy of `spec.md` is suitable); confirm `ExtractionCompleted.durationMs < 200` in the log output; confirm time from extraction trigger to first candidate visible on screen is under 2 seconds; if extracted candidate count exceeds 100, confirm the large-extraction count notice is displayed; document measured durationMs and candidate count in a comment on this task
- [ ] T094 [P] Verify schema compatibility — run schema snapshot test (T075); confirm `contracts/schema.graphql` matches the HotChocolate-generated schema byte-for-byte (or diff is only whitespace/comment); confirm `createScenarios`, `CreateScenariosPayload`, the `CreateScenarioResult` union, `CreateScenarioSuccess`, `CreateScenarioError`, and `ExtractionMetadataInput` are all present in the generated output; confirm no US1 type has changed
- [ ] T095 [P] Run `dotnet format` on `backend/BirkNext.sln` and `frontend/BirkNext.sln`; fix all formatting violations; confirm `dotnet build` on both solutions produces zero errors and zero warnings; commit all changes

**Checkpoint**: All 6 US2 acceptance scenarios pass. All US1 tests continue to pass. Extraction completes in under 200 ms for a 10,000-character input. Schema snapshot is clean. Both solutions build with zero errors.

---

## Dependencies & Execution Order (US2)

### Phase Dependencies

- **Phase 7 (Domain Models)**: Requires Phases 1–2 complete — start immediately after foundation is ready
- **Phase 8 (Pipeline)**: Requires Phase 7
- **Phase 9 (Backend GraphQL)**: Requires Phase 2 only — **can run fully in parallel with Phases 7 and 8**
- **Phase 10 (Strawberry Shake)**: Requires Phase 9 (schema must exist for codegen)
- **Phase 11 (Components)**: Requires Phases 7, 8, and 10
- **Phase 12 (Observability)**: Requires Phase 11
- **Phase 13 (Validation/Security)**: Requires Phases 9 and 11
- **Phase 14 (Verification)**: Requires all prior phases

### Key Parallel Opportunity

Phases 8 and 9 can proceed concurrently after Phase 7:
- **Stream A**: Phase 8 (T063–T069) — client-side extraction pipeline in `frontend/`
- **Stream B**: Phase 9 (T070–T075) — backend batch mutation in `backend/`

They converge at Phase 10 (Strawberry Shake codegen requires the backend schema) and Phase 11 (components require both the pipeline service and the generated client).

### Within Each Phase

- All `[P]` tasks within a phase operate on different files with no mutual dependencies
- Non-`[P]` pipeline tasks (T063–T068) must complete in order: each stage extends the same file
- Component tasks within Phase 11 have a dependency chain: T078 (Row) → T079 (List, uses Row) → T081 (Page, uses both T079 and T080); T080 (Input) is independent

---

## Parallel Opportunities (US2)

### Phase 7 (all parallel — different files)
```
T053 BlockType enum
T054 ClassificationSignal enum
T055 PipelineStatus enum
T056 CandidateSaveState enum
T057 ReviewSavePhase enum
T058 TextBlock record
T059 ExtractionCandidate record
T060 ExtractionPipelineResult record
T061 IExtractionConfiguration + default implementation
T062 IScenarioExtractionService interface + stub
```

### Phase 9 (partial parallel)
```
T070 CreateScenariosInput + ExtractionMetadataInput types  ─┐
T071 CreateScenariosPayload + union types                   ─┤ parallel
T075 Schema snapshot test extension                         ─┘
T072 ScenarioService.CreateBatchAsync  (needs T070)
T073 Mutation.CreateScenarios resolver (needs T071 + T072)
T074 Integration tests                 (needs T073)
```

### Phase 11 (partial parallel)
```
T078 ExtractionCandidateRow.razor  ─┐
T080 ExtractionInput.razor          ─┤ parallel (different files)
T082 ExtractionCandidateRow tests   ─┤
T083 ExtractionReviewList tests     ─┤
T084 ExtractionInput tests          ─┘
T079 ExtractionReviewList.razor    (needs T078)
T081 ScenarioExtraction page       (needs T079 + T080)
```

### Phase 14 (all parallel)
```
T092 US1 regression
T093 Performance measurement
T094 Schema compatibility
T095 Format + build
```

---

## Implementation Strategy (US2)

### Recommended Sequence with Two Developers

After Phases 1–2 are complete:
- **Dev A**: Phase 7 → Phase 8 (client models + extraction pipeline)
- **Dev B**: Phase 9 (backend batch mutation + integration tests)

After Phase 8 and Phase 9 are both complete:
- **Dev A**: Phase 10 → Phase 11 (Strawberry Shake + component tree)
- **Dev B**: Phase 12 + Phase 13 (observability + validation/security)

Both converge on Phase 14 (verification).

### US2 Standalone Delivery

US2 can be delivered independently of US3 (inline validation polish). The extraction feature depends only on the backend infrastructure from Phases 1–2 and the batch mutation from Phase 9. US1 (manual scenario creation) and US2 (extraction) are complementary paths to the same scenario list.

---

## Notes (US2)

- `[P]` tasks operate on different files with no cross-task dependencies within the same phase
- Pipeline tasks T063–T068 are sequential additions to a single file; each must leave `ScenarioExtractionService` in a buildable state
- The XSS rendering constraint (T088) must be checked at code review for every candidate display component — it cannot be enforced by the type system
- `ExtractionMetadataInput` fields are numeric and identifier only — any PR adding a text field to this type must be rejected
- Raw pasted specification text must never appear in any log field, any GraphQL input, or any server-side payload
- Commit after each task or logical group; branch stays shippable at every checkpoint
- Total US2 tasks: **43** (T053–T095) | Phase 7: 10 | Phase 8: 7 | Phase 9: 6 | Phase 10: 2 | Phase 11: 7 | Phase 12: 3 | Phase 13: 3 | Phase 14: 5
- **Combined total**: **95 tasks** (T001–T095)
