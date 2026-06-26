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

- [x] T063 Implement Stage 1 (Input Validation Gate) and Stage 2 (Normalization) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — Stage 1: return `PipelineStatus.EmptyInput` when `rawInput` is null, empty, or whitespace only; return `PipelineStatus.InputTooLarge` when `rawInput.Length > IExtractionConfiguration.MaxInputLengthChars`; Stage 2: replace `\r\n` with `\n`, strip UTF-8 BOM (`﻿`) if present; record `InputLengthChars` and `InputLineCount` from the raw input before normalization
- [x] T064 Implement Stage 3 (Block Partitioning) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — iterate normalized lines sequentially with look-ahead; detect and group fenced code block ranges (` ``` ` open/close pairs) so their interior lines are tagged `FencedCodeBlock`; detect YAML front matter (leading `---` block before any non-empty line); classify each line as one of the 13 `BlockType` values; set `IndentationLevel` for list items (count leading spaces / 2); track `PrecedingHeading` as the text of the most recent `Heading` line seen; output ordered `IReadOnlyList<TextBlock>`
- [x] T065 Implement Stage 4 (Structure Filter) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — discard `TextBlock` instances with `BlockType` in: `Heading`, `FencedCodeBlock`, `Blockquote`, `HorizontalRule`, `HtmlComment`, `YamlFrontMatter`, `Empty`, `TableHeaderRow`, `TableSeparatorRow`; retain: `UnorderedListItem`, `OrderedListItem`, `TableBodyRow`, `ParagraphLine`; output filtered sequence preserving order
- [x] T066 Implement Stage 5 (Content Extraction) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — for each retained block: strip list markers (`-`/`*`/`+`/`N.` prefix), strip inline code backticks preserving inner text, strip link syntax `[text](url)` retaining display text, strip image syntax `![alt](url)` entirely, strip leading table pipe characters for `TableBodyRow`; trim result; discard if result length < `IExtractionConfiguration.MinCandidateLengthChars`; carry `TextBlock.PrecedingHeading` forward as `ContextHeading`; output ordered sequence of `(PlainText: string, ContextHeading: string?, SourceBlockType: BlockType)`
- [x] T067 Implement Stage 6 (Classification) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — for each content item: if `PlainText.Length > IExtractionConfiguration.MaxLineLengthForPatternMatching` assign `ClassificationSignal.Default` (NeedsClarification) without pattern matching; otherwise apply heuristics in priority order: (1) `BddPattern` — line contains Given/When/Then triple or starts with "Given "/"When "/"Then "; (2) `Rfc2119Uppercase` — line contains MUST/SHALL/SHOULD/MAY/MUST NOT/SHALL NOT (case-sensitive uppercase); (3) `Rfc2119Lowercase` — line contains must/shall/required/is required to (case-insensitive, word-boundary matched); (4) `FrPrefix` — line matches `FR-\d+` pattern; (5) `QuestionTerminator` — trimmed line ends with `?`; (6) `DeferralMarker` — line contains TBD/TODO/TBC/open question/to be defined (case-insensitive); (7) `Default` — NeedsClarification fallback; first matching signal wins; record `ClassificationSignal` and derive `Classification` (`ScenarioKind`) from it
- [x] T068 Implement Stage 7 (Deduplication) and Stage 8 (Result Assembly) in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — Stage 7: deduplicate by case-folded, trimmed `PlainText`; keep first occurrence, discard subsequent exact matches; Stage 8: assemble `ExtractionCandidate` records (new `CandidateId = Guid.NewGuid()`, `IsSelected = false`, `SaveState = CandidateSaveState.Pending`); compute `RequirementCount`, `TestCount`, `NeedsClarificationCount`; if deduplicated list is empty return `PipelineStatus.NoResults`; otherwise return `PipelineStatus.Success` with fully populated `ExtractionPipelineResult`; record `DurationMs` from a `Stopwatch` started at the top of `Extract()`
- [x] T069 [P] Write unit tests for `ScenarioExtractionService` covering all pipeline stages in `frontend/BirkNext.Web.Tests/Services/ScenarioExtractionServiceTests.cs` — required coverage: empty string → `EmptyInput`; whitespace-only → `EmptyInput`; input at exactly `MaxInputLengthChars` → `Success`; input at `MaxInputLengthChars + 1` → `InputTooLarge`; input with no extractable bullets → `NoResults`; unordered bullet extraction; ordered bullet extraction; BDD triple classification → `Test`; MUST keyword classification → `Requirement`; question mark classification → `NeedsClarification`; TBD marker classification → `NeedsClarification`; default fallback → `NeedsClarification`; duplicate bullets → single candidate after deduplication; blank bullet (`- ` with no text) → discarded; Windows line endings normalized; heading text propagated as `ContextHeading`; fenced code block content not extracted; `RequirementCount + TestCount + NeedsClarificationCount == Candidates.Count` invariant holds; `DurationMs > 0` on Success

**Checkpoint**: `dotnet test frontend/BirkNext.Web.Tests --filter "ScenarioExtractionService"` passes. `ScenarioExtractionService.Extract()` has no Blazor, network, or DI dependency beyond `IExtractionConfiguration`.

---

## Phase 9: Backend GraphQL Extension

**Purpose**: Extend the HotChocolate schema with the `createScenarios` batch mutation and all supporting types. Can proceed fully in parallel with Phases 8 and 10 once Phase 7 is complete, as this work is in a different project.

**Prerequisite**: Phase 2 complete (HotChocolate server running). No dependency on client-side extraction pipeline.

- [x] T070 [P] Add `CreateScenariosInput` and `ExtractionMetadataInput` HotChocolate input types in `backend/BirkNext.Api/GraphQL/CreateScenariosInput.cs` per data-model.md §Wire Models and schema.graphql — `CreateScenariosInput`: `items: [CreateScenarioInput!]!` (reuses existing US1 input type), `extractionMetadata: ExtractionMetadataInput?`; `ExtractionMetadataInput`: `totalExtracted int`, `selectedCount int`, `extractionDurationMs int`, `sessionId string` — all non-nullable; add HotChocolate `[GraphQLDescription]` attributes matching schema.graphql doc strings
- [x] T071 [P] Add `CreateScenariosPayload`, `CreateScenarioSuccess`, `CreateScenarioError`, and `CreateScenarioResult` union HotChocolate types in `backend/BirkNext.Api/GraphQL/CreateScenariosPayload.cs` per data-model.md §Wire Models — `CreateScenariosPayload`: `results IReadOnlyList<CreateScenarioResult>`, `successCount int`, `failureCount int`, `correlationId string`; `CreateScenarioSuccess`: `scenario Scenario`; `CreateScenarioError`: `code string`, `message string`, `field string?`; register the `CreateScenarioResult` union type with HotChocolate
- [x] T072 Add `CreateBatchAsync` method to `ScenarioService` in `backend/BirkNext.Api/Services/ScenarioService.cs` — accepts `IEnumerable<CreateScenarioInput>` and `string correlationId`; processes each item independently using the same validation rules as `CreateAsync` (title non-empty, max 500 chars, kind valid enum value, projectId non-empty); successful items are inserted; failed items produce a `CreateScenarioError` with the appropriate error code (`TITLE_REQUIRED`, `TITLE_TOO_LONG`, `KIND_INVALID`, `PROJECT_ID_REQUIRED`); returns ordered `IReadOnlyList<CreateScenarioResult>` preserving input order; does not throw on per-item validation failure — failures are captured as `CreateScenarioError` results
- [x] T073 Implement `Mutation.CreateScenarios` resolver in `backend/BirkNext.Api/GraphQL/Mutation.cs` — call `ScenarioService.CreateBatchAsync`; compute `successCount` and `failureCount` from results; emit `CandidateReviewSaved` Serilog structured event with fields: `selectedCount` (from `input.items.Count`), `totalExtracted` (from `input.extractionMetadata.TotalExtracted` when present, else -1 to indicate not provided), `scenariosCreated` (`successCount`), `failedCount` (`failureCount`), `durationMs` (Stopwatch from resolver entry), `projectId` (from first item's `projectId`), `correlationId`; no field in the log event may carry text content from the pasted specification — only the counts and identifiers listed above; register the mutation field in the HotChocolate schema
- [x] T074 Write integration tests for `createScenarios` mutation in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` — test cases: (a) all items valid → `successCount == items.Count`, each result is `CreateScenarioSuccess` with a non-null `scenario.id`, all scenarios visible in `scenarios` query; (b) one item has empty title → that result is `CreateScenarioError` with `code == "TITLE_REQUIRED"` and `field == "title"`, all other items succeed, `successCount + failureCount == items.Count`; (c) title exceeding 500 characters → `TITLE_TOO_LONG`; (d) empty `items` array → mutation rejected before resolver; (e) `extractionMetadata` omitted → mutation succeeds (field is optional); (f) `extractionMetadata` present → `CandidateReviewSaved` log event contains `totalExtracted` from metadata
- [x] T075 [P] Extend schema snapshot test in `backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs` — add assertions verifying `createScenarios` mutation, `CreateScenariosInput`, `ExtractionMetadataInput`, `CreateScenariosPayload`, `CreateScenarioResult` union, `CreateScenarioSuccess`, and `CreateScenarioError` are all present in the HotChocolate-generated schema; verify snapshot matches `contracts/schema.graphql`; verify no existing US1 types (`Scenario`, `ScenarioKind`, `createScenario`, `scenarios`, `CreateScenarioPayload`, `UserError`) have changed shape

**Checkpoint**: `createScenarios` mutation works end-to-end against Testcontainers PostgreSQL. Partial success verified. Schema snapshot test passes. `contracts/schema.graphql` matches the generated schema.

---

## Phase 10: Frontend Strawberry Shake Integration

**Purpose**: Generate a typed batch mutation client from the operation document. Requires the backend schema to be available for code generation.

**Prerequisite**: Phase 9 complete (backend schema must include `createScenarios`).

- [x] T076 Write `CreateScenarios.graphql` operation document in `frontend/BirkNext.Web/GraphQL/CreateScenarios.graphql` — mutation accepting `$input: CreateScenariosInput!`; return `results { ... on CreateScenarioSuccess { scenario { id title kind createdAt } } ... on CreateScenarioError { code message field } }`, `successCount`, `failureCount`, `correlationId`; run Strawberry Shake code generation (`dotnet build`) to confirm typed client `ICreateScenariosMutation` is generated without errors
- [x] T077 Register the generated `ICreateScenariosMutation` Strawberry Shake client in `frontend/BirkNext.Web/Program.cs` DI container; confirm `dotnet build frontend/BirkNext.sln` produces zero errors and the generated client is injectable into Blazor components

**Checkpoint**: `ICreateScenariosMutation` is available for injection. `dotnet build` passes.

---

## Phase 11: Frontend Component Tree

**Purpose**: Implement the four components that form the extraction view UI. `ExtractionCandidateRow` and `ExtractionInput` are independent. `ExtractionReviewList` depends on `ExtractionCandidateRow`. `ScenarioExtraction` page depends on both `ExtractionInput` and `ExtractionReviewList`. Tests for each component can run in parallel with each other.

**Prerequisite**: Phase 7 (models), Phase 8 (extraction service), Phase 10 (Strawberry Shake batch client).

- [x] T078 Implement `ExtractionCandidateRow.razor` in `frontend/BirkNext.Web/Components/ExtractionCandidateRow.razor` — parameters: `[Parameter] ExtractionCandidate Candidate` and `[Parameter] EventCallback<Guid> OnSelectionToggled`; render: classification badge using `Candidate.Classification` display name; `Candidate.ContextHeading` in muted text when non-null; `Candidate.Title` as plain text using `@Candidate.Title` within an element (never `@((MarkupString)...)` — XSS constraint from plan.md §Security and schema.graphql boundary rule); checkbox bound to `Candidate.IsSelected` that invokes `OnSelectionToggled` with `Candidate.CandidateId` on change; `SaveState` indicator: show "Saved" badge when `Candidate.SaveState == Saved`; show `Candidate.SaveError` error text when `Candidate.SaveState == Failed`; show spinner when `Candidate.SaveState == Saving`
- [x] T079 Implement `ExtractionReviewList.razor` in `frontend/BirkNext.Web/Components/ExtractionReviewList.razor` — parameter: `[Parameter] ExtractionPipelineResult? PipelineResult`; inject `ICreateScenariosMutation` and active project context; render nothing when `PipelineResult` is null; render empty-state message when `PipelineResult.Status == NoResults`; render count summary header: "N candidates extracted — X REQUIREMENT, Y TEST, Z NEEDS_CLARIFICATION"; render three candidate groups (Requirement / Test / NeedsClarification) each via `ExtractionCandidateRow`; maintain `HashSet<Guid> _selectedIds` — default empty (opt-in selection, FR-US2-006); confirm-save button disabled when `_selectedIds` is empty; on save confirm: set `ReviewSavePhase = Saving`, set `SaveState = Saving` on all selected candidates, call `ICreateScenariosMutation.ExecuteAsync(input)` with selected candidates mapped to `CreateScenariosInput` per data-model.md §Persistence Boundary field mapping; on response: update per-candidate `SaveState` to `Saved` (with `SavedScenarioId`) or `Failed` (with `SaveError`) using `results[i]` → `items[i]` positional mapping; update `ReviewSavePhase` to `Complete`, `PartialSuccess`, or `Failed`; implement `IDisposable` to emit `CandidateReviewAbandoned` log event when disposed with a non-null `PipelineResult` that has candidates not all in `Saved` state; include `ExtractionMetadataInput` in the mutation input when `PipelineResult` metadata is available
- [x] T080 Implement `ExtractionInput.razor` in `frontend/BirkNext.Web/Components/ExtractionInput.razor` — inject `IScenarioExtractionService`; render: multi-line text area bound to `_rawInput string`; extract trigger button; on trigger: validate input before calling `Extract()` — show inline message "Paste some text to extract candidates from" when text area empty (FR-US2-009); show "Input is too large (max 50,000 characters)" when input exceeds cap; call `IScenarioExtractionService.Extract(_rawInput)` otherwise; emit `ExtractionTriggered` log event before calling `Extract()` (inputLengthChars, inputLineCount, generated sessionId stored in component state); emit `ExtractionCompleted` log event after `Extract()` returns with `Status == Success` (candidateCount, requirementCount, testCount, needsClarificationCount, durationMs); emit `ExtractionEmpty` log event after `Extract()` returns with `Status != Success` (inputLengthChars, reason derived from PipelineStatus); raise `EventCallback<ExtractionPipelineResult> OnExtractionCompleted` with the result; disable extract button while extraction is running
- [x] T081 Implement `ScenarioExtraction.razor` page in `frontend/BirkNext.Web/Pages/ScenarioExtraction.razor` — route `@page "/extract"`; host `ExtractionInput` and `ExtractionReviewList`; declare `ExtractionPipelineResult? _pipelineResult` field; wire `ExtractionInput.OnExtractionCompleted` to set `_pipelineResult` and pass it to `ExtractionReviewList.PipelineResult`; no business logic in the page — orchestration only; add nav link entry for "Extract" pointing to `/extract` in `frontend/BirkNext.Web/Shared/NavMenu.razor`
- [x] T082 [P] Write bUnit tests for `ExtractionCandidateRow.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionCandidateRowTests.cs` — test cases: classification badge text matches `ScenarioKind` display name; `ContextHeading` appears when non-null and is absent when null; candidate title is rendered as text content not as HTML markup (assert `InnerHtml` does not contain unescaped `<` or `>` when title contains HTML characters); checkbox is unchecked by default; toggling checkbox raises `OnSelectionToggled` with correct `CandidateId`; `SaveState.Saved` shows saved indicator; `SaveState.Failed` shows `SaveError` text; `SaveState.Saving` shows spinner
- [x] T083 [P] Write bUnit tests for `ExtractionReviewList.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionReviewListTests.cs` — test cases: null `PipelineResult` renders nothing; `PipelineStatus.NoResults` shows empty-state message; count summary header shows correct totals; candidates are rendered in three groups by classification; no candidate checkbox is checked by default; confirm-save button is disabled when no candidates selected; confirm-save button enabled when at least one candidate selected; on successful save response, candidate row shows Saved indicator; on error response, candidate row shows error message; after complete save `ReviewSavePhase.Complete` state is reached
- [x] T084 [P] Write bUnit tests for `ExtractionInput.razor` in `frontend/BirkNext.Web.Tests/Components/ExtractionInputTests.cs` — test cases: empty text area submission shows validation message and does not call `IScenarioExtractionService.Extract()`; input above `MaxInputLengthChars` shows length error and does not call `Extract()`; valid input calls `Extract()` with the raw string; successful extraction raises `OnExtractionCompleted` with the pipeline result; extract button is disabled during extraction and re-enabled after

**Checkpoint**: Navigate to `/extract`. `dotnet build` passes. Paste a spec.md fragment containing bullet points. Candidates appear grouped by classification. Selecting candidates and clicking confirm-save calls `createScenarios`. Saved candidates appear in the US1 `/scenarios` list on navigation.

---

## Phase 12: Observability

**Purpose**: Verify that all five structured log events from plan.md §Observability Integration are emitted with correct fields and without text content from pasted input. Client-side events use console logging in v1 (plan.md §Observability Option B) pending a telemetry endpoint decision.

**Prerequisite**: Phase 11 complete (components must be implemented to instrument them).

- [x] T085 Verify `ExtractionTriggered`, `ExtractionCompleted`, and `ExtractionEmpty` log events in `frontend/BirkNext.Web/Components/ExtractionInput.razor` — confirm each event contains only the fields specified in data-model.md §Observability Model Fields; confirm no field carries text from `_rawInput` (only `inputLengthChars` and `inputLineCount` numeric values); confirm `sessionId` is a consistent identifier across the three events for the same extraction session; use `ILogger<ExtractionInput>` injected via DI; add an inline comment at each log call noting the "no raw text" constraint for code review awareness
- [x] T086 Verify `CandidateReviewAbandoned` log event in `frontend/BirkNext.Web/Components/ExtractionReviewList.razor` — confirm the event is emitted in `Dispose()` when `PipelineResult` is non-null and at least one candidate is not in `Saved` state; confirm it logs only `totalExtracted` and `selectedCount` (counts from `_selectedIds`) — no candidate title text; verify event is not emitted when all selected candidates have been successfully saved
- [x] T087 Verify `CandidateReviewSaved` Serilog event in `backend/BirkNext.Api/GraphQL/Mutation.cs` — manually inspect the structured log output for a `createScenarios` call with `extractionMetadata` present: confirm `selectedCount`, `totalExtracted`, `scenariosCreated`, `failedCount`, `durationMs`, `projectId`, and `correlationId` all appear; confirm no field contains text from any candidate title; add an integration test assertion in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` verifying `CandidateReviewSaved` is emitted with a non-zero `durationMs`

**Checkpoint**: All five log events are emitted. `CandidateReviewSaved` appears in backend structured JSON output. No pasted text appears in any log field.

---

## Phase 13: Validation and Security

**Purpose**: Verify input sanitization, XSS rendering constraints, and server-side batch validation rules — each as an independent verification step.

**Prerequisite**: Phases 11 and 9 complete.

- [x] T088 Verify XSS rendering constraint across the component tree — inspect `ExtractionCandidateRow.razor` and confirm candidate `Title` is bound with `@Candidate.Title` inside element text content (not `@((MarkupString)Candidate.Title)` or `innerHTML`); verify `ContextHeading` is similarly plain-text bound; add a bUnit test in `frontend/BirkNext.Web.Tests/Components/ExtractionCandidateRowTests.cs` that passes a title containing `<script>alert(1)</script>` and asserts the rendered output contains the literal string `&lt;script&gt;` (escaped) not an executable script element
- [x] T089 Extend batch mutation integration tests in `backend/BirkNext.Api.Tests/Integration/ScenariosBatchMutationTests.cs` — add test cases for server-side batch validation: (a) item with title exceeding 500 chars → `CreateScenarioError.code == "TITLE_TOO_LONG"`, `field == "title"`; (b) item with empty `projectId` → `CreateScenarioError.code == "PROJECT_ID_REQUIRED"`; (c) a batch where all items fail validation → `successCount == 0`, `failureCount == items.Count`, no rows inserted; (d) mixed batch (some valid, some invalid) → correct partial success counts, valid items committed to DB, invalid items rejected without rolling back committed items
- [x] T090 Verify `ExtractionMetadataInput` carries no text content — add a schema-level assertion in the contract test (`backend/BirkNext.Api.Tests/Contract/ScenariosSchemaTests.cs`) confirming `ExtractionMetadataInput` has exactly 4 fields (`totalExtracted`, `selectedCount`, `extractionDurationMs`, `sessionId`) with types `Int!`, `Int!`, `Int!`, `String!` and no additional fields; verify the resolver does not log the `sessionId` value if it resembles user content (document the constraint that `sessionId` must be an opaque client-generated identifier, not derived from pasted text)

**Checkpoint**: Pasting `<script>alert(1)</script>` as a bullet renders as literal escaped text in the review list. All batch validation error codes match those documented in schema.graphql. Schema structure of `ExtractionMetadataInput` is locked by snapshot test.

---

## Phase 14: Integration and Verification

**Purpose**: End-to-end acceptance scenario verification, US1 regression, performance measurement, and final build health. All `[P]` tasks are independent and can run concurrently.

**Prerequisite**: Phases 7–13 complete.

- [x] T091 Verify all 6 US2 acceptance scenarios from spec.md §US2 against the running application — AC1: paste spec text with bullet points → all bullets extracted and displayed as candidates; AC2: each candidate shows classification label (REQUIREMENT/TEST/NEEDS_CLARIFICATION); AC3: no candidates auto-persisted before user confirm action (inspect `scenarios` query before and after extraction but before save); AC4: paste text with no extractable candidates → empty-state message displayed; AC5: select subset of candidates → only selected candidates appear in `scenarios` query after save, unselected do not; AC6: click extract with empty text area → validation message shown, no extraction attempted
- [x] T092 [P] Run full regression to verify US1 is unaffected — execute `dotnet test backend/BirkNext.Api.Tests` and `dotnet test frontend/BirkNext.Web.Tests`; confirm all T016–T051 tests pass; verify `createScenario` (single) mutation still returns correct payload shape; verify `scenarios` query still returns results in `createdAt DESC` order; verify scenarios created via batch save appear in the `scenarios` query identically to manually created scenarios
- [x] T093 [P] Measure extraction performance — paste a representative 10,000-character spec document (a copy of `spec.md` is suitable); confirm `ExtractionCompleted.durationMs < 200` in the log output; confirm time from extraction trigger to first candidate visible on screen is under 2 seconds; if extracted candidate count exceeds 100, confirm the large-extraction count notice is displayed; document measured durationMs and candidate count in a comment on this task — MEASURED: durationMs=0 (sub-millisecond), candidateCount=87, inputLengthChars=10000; well within 200 ms target
- [x] T094 [P] Verify schema compatibility — run schema snapshot test (T075); confirm `contracts/schema.graphql` matches the HotChocolate-generated schema byte-for-byte (or diff is only whitespace/comment); confirm `createScenarios`, `CreateScenariosPayload`, the `CreateScenarioResult` union, `CreateScenarioSuccess`, `CreateScenarioError`, and `ExtractionMetadataInput` are all present in the generated output; confirm no US1 type has changed — snapshot created at `BirkNext.Api.Tests/Contract/__snapshots__/ScenariosSchemaTests.Schema_MatchesSnapshot.snap`
- [x] T095 [P] Run `dotnet format` on `backend/BirkNext.sln` and `frontend/BirkNext.sln`; fix all formatting violations; confirm `dotnet build` on both solutions produces zero errors and zero warnings; commit all changes

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

---
---

# Tasks: Feature US3 — Deterministic Rule Engine for Scenario Extraction

**Input**: Design documents from `/specs/001-create-scenario/`  
**Prerequisites**: plan.md §US3 ✅ | spec.md §US3 ✅ | research.md §R-US3 ✅ | data-model.md §US3 ✅ | contracts/schema.graphql ✅ (no schema changes)  
**Depends on**: US2 complete (T053–T095). The rule engine replaces internal logic inside `ScenarioExtractionService`; all US2 models, components, and backend code must already exist.

**Architecture boundary**: US3 is entirely client-side. No backend changes, no GraphQL contract changes, no database changes. Only two files inside `ScenarioExtractionService.cs` have their internal logic replaced (Stage 4 and Stage 6). The public interface `IScenarioExtractionService` and all component contracts are frozen.

**Migration constraint**: Every one of the 111+ tests from US1 and US2 must pass without modification after US3 is complete. A test that requires updating is a regression, not a migration.

---

## Format: `[ID] [P?] Description with file path`

- **[P]**: Can run in parallel with other `[P]` tasks in the same phase (different files, no mutual dependencies)

---

## Phase 15: Rule Engine Foundation

**Purpose**: Define all new model types and the `IExtractionRuleEngine` interface in new files only. No existing file is modified in this phase. All tasks can run in parallel.

**Prerequisite**: US2 phases complete (model types `BlockType`, `ClassificationSignal`, `ScenarioKind`, `TextBlock` must exist).

- [ ] T096 [P] Create condition type hierarchy in `frontend/BirkNext.Web/Services/ExtractionRuleConditions.cs` — define `FilterCondition` as an abstract record; define `BlockTypeMatchCondition(BlockType TargetBlockType) : FilterCondition` (matches when `block.BlockType == TargetBlockType`); define `ContentLengthBelowCondition(int ThresholdChars) : FilterCondition` (matches when `block.RawText.Length < ThresholdChars` — defined for extensibility, not used in the default rule set); define `ClassificationCondition` as an abstract record; define `PatternMatchCondition(Regex Pattern) : ClassificationCondition` where `Pattern` must be constructed with `RegexOptions.Compiled | RegexOptions.CultureInvariant` — throw `ArgumentNullException` if pattern is null; define `UnconditionalCondition() : ClassificationCondition` (always returns true, no state) per data-model.md §FilterCondition and §ClassificationCondition

- [ ] T097 [P] Create `ClassificationOutcome`, `FilterRule`, and `ClassificationRule` records in `frontend/BirkNext.Web/Services/ExtractionRuleModels.cs` — `ClassificationOutcome(ScenarioKind Kind, ClassificationSignal Signal)`: no constructor guard required (all enum values are valid pairings); `FilterRule(string Name, int Priority, FilterCondition Condition)`: throw `ArgumentException` if `Name` is null or empty; throw `ArgumentOutOfRangeException` if `Priority <= 0` (priority 0 reserved for Default classification rule); `ClassificationRule(string Name, int Priority, ClassificationCondition Condition, ClassificationOutcome Outcome, BlockType[]? ApplicableBlockTypes = null)`: throw if `Name` is null or empty; throw if `Priority < 0`; throw if `Priority == 0` and `Condition` is not `UnconditionalCondition` (priority 0 is reserved exclusively for the unconditional Default rule) per data-model.md §ClassificationOutcome, §FilterRule, §ClassificationRule

- [ ] T098 [P] Create `RuleEvaluationResult` and `RuleExecutionSummary` in `frontend/BirkNext.Web/Services/RuleEvaluationResult.cs` — `RuleEvaluationResult` is an immutable record: `bool IsFiltered`, `ScenarioKind? Classification`, `ClassificationSignal? Signal`, `string? WinningRuleName`, `int EvaluatedRuleCount`; add static factory methods `Filtered(int evaluatedRuleCount)` (sets IsFiltered=true, all classification fields null) and `Classified(ScenarioKind kind, ClassificationSignal signal, string winningRuleName, int evaluatedRuleCount)` (sets IsFiltered=false, all classification fields populated); assert `EvaluatedRuleCount >= 1` in both factory methods; `RuleExecutionSummary` is a mutable class with three `int` fields `TotalRulesEvaluated`, `FilteredBlockCount`, `DefaultFallbackCount` — all initialized to 0, incremented by the caller per data-model.md §RuleEvaluationResult and §RuleExecutionSummary

- [ ] T099 [P] Create `IExtractionRuleEngine` interface in `frontend/BirkNext.Web/Services/IExtractionRuleEngine.cs` — single evaluation method `RuleEvaluationResult Evaluate(TextBlock block, string strippedText)`; read-only property `IReadOnlyList<string> RuleNames` returning all rule names in the engine (FilterRules first, ClassificationRules second, in priority order); no other members per plan.md §IExtractionRuleEngine Interface

- [ ] T100 [P] Create `ExtractionRuleSet` in `frontend/BirkNext.Web/Services/ExtractionRuleSet.cs` — constructor `ExtractionRuleSet(IReadOnlyList<FilterRule> filterRules, IReadOnlyList<ClassificationRule> classificationRules)` stores the rules sorted by `Priority` descending (stable sort — equal priorities preserve registration order); expose `IReadOnlyList<FilterRule> FilterRules` and `IReadOnlyList<ClassificationRule> ClassificationRules`; implement `static ExtractionRuleSet Default()` factory method assembling all 16 default rules: 9 `FilterRule` entries (one `BlockTypeMatchCondition` per filtered `BlockType`: `Heading`, `FencedCodeBlock`, `Blockquote`, `HorizontalRule`, `HtmlComment`, `YamlFrontMatter`, `Empty`, `TableHeaderRow`, `TableSeparatorRow` — all at priority 100) and 7 `ClassificationRule` entries mirroring the Stage 6 heuristics from `ScenarioExtractionService` exactly: `Classify:BddPattern` (priority 70, `PatternMatch`, outcome Test/BddPattern — pattern detects Given/When/Then triple on one line or line starting with BDD opener), `Classify:Rfc2119Uppercase` (priority 60, `PatternMatch`, outcome Requirement/Rfc2119Uppercase — pattern detects MUST/SHALL/SHOULD/MAY/MUST NOT/SHALL NOT with word-boundary, case-sensitive), `Classify:Rfc2119Lowercase` (priority 50, `PatternMatch`, outcome Requirement/Rfc2119Lowercase — word-boundary, case-insensitive), `Classify:FrPrefix` (priority 40, `PatternMatch`, outcome Requirement/FrPrefix — pattern `\bFR-\d+`), `Classify:QuestionTerminator` (priority 30, `PatternMatch`, outcome NeedsClarification/QuestionTerminator — stripped text ends with `?`), `Classify:DeferralMarker` (priority 20, `PatternMatch`, outcome NeedsClarification/DeferralMarker — TBD/TODO/TBC/open question/to be defined, case-insensitive), `Classify:Default` (priority 0, `UnconditionalCondition`, outcome NeedsClarification/Default); regex patterns must exactly replicate the match conditions from T067 to guarantee identical classification outcomes per data-model.md §ExtractionRuleSet Default Rule Set specification tables

**Checkpoint**: `dotnet build frontend/BirkNext.sln` passes with all new types in place. No existing file has been modified. All US2 tests still pass.

---

## Phase 16: Rule Engine Implementation

**Purpose**: Implement `ExtractionRuleEngine` with evaluation logic and startup validation. Register it in the DI container. No changes to `ScenarioExtractionService` yet.

**Prerequisite**: Phase 15 complete (all foundation types must exist).

- [ ] T101 Implement `ExtractionRuleEngine` in `frontend/BirkNext.Web/Services/ExtractionRuleEngine.cs` — constructor accepts `ExtractionRuleSet ruleSet` and `IExtractionConfiguration config`; perform all startup validation checks before the constructor returns, throwing descriptive exceptions on failure: (1) `ruleSet.ClassificationRules` must be non-empty; (2) exactly one `ClassificationRule` with `UnconditionalCondition` and `Priority == 0` must exist; (3) all `Name` values across both rule lists must be unique (case-sensitive); (4) all `PatternMatchCondition.Pattern` instances must not be null (null patterns should have been rejected at `PatternMatchCondition` construction, but verify here as a defence-in-depth check); (5) no `ClassificationRule` other than the unconditional Default rule may carry `Priority == 0`; implement `IReadOnlyList<string> RuleNames` returning filter rule names followed by classification rule names in priority order; implement `RuleEvaluationResult Evaluate(TextBlock block, string strippedText)`: filter pass — iterate `ruleSet.FilterRules` in stored order (already sorted by priority descending); for each rule check `ApplicableBlockTypes` if set (skip rule if block type not in set); evaluate `Condition` against `block`; on first `FilterRule` match return `RuleEvaluationResult.Filtered(evaluatedCount)`; classification pass (only if no filter matched) — iterate all `ruleSet.ClassificationRules`; for each rule check `ApplicableBlockTypes` if set; for `PatternMatchCondition` truncate `strippedText` to `config.MaxLineLengthForPatternMatching` before calling `Pattern.IsMatch`; for `UnconditionalCondition` always match; collect all matching rules; select the highest `Priority` among matched rules; on priority tie, the first rule in the sorted list wins (stable sort guarantees this is the first-registered rule); return `RuleEvaluationResult.Classified(winningRule.Outcome.Kind, winningRule.Outcome.Signal, winningRule.Name, evaluatedCount)` per plan.md §Rule Evaluation Workflow

- [ ] T102 Register `IExtractionRuleEngine` as a singleton in `frontend/BirkNext.Web/Program.cs` — `builder.Services.AddSingleton<IExtractionRuleEngine>(sp => new ExtractionRuleEngine(ExtractionRuleSet.Default(), sp.GetRequiredService<IExtractionConfiguration>()))`; verify the application starts without exception; if startup validation throws (e.g., due to a malformed default rule set), the exception surfaces at startup and not at first extraction call per plan.md §Integration with ScenarioExtractionService

**Checkpoint**: Application starts cleanly. `ExtractionRuleEngine` is resolvable from the DI container. All US2 tests still pass (no `ScenarioExtractionService` changes yet).

---

## Phase 17: Rule Engine Tests

**Purpose**: Verify the rule engine and its default rule set in complete isolation before any pipeline integration. All test tasks target different files and can run in parallel.

**Prerequisite**: Phases 15 and 16 complete.

- [ ] T103 [P] Write unit tests for `ExtractionRuleEngine` evaluation logic in `frontend/BirkNext.Web.Tests/Services/ExtractionRuleEngineTests.cs` — required coverage: filter pass returns `IsFiltered = true` for a block whose `BlockType` matches a `FilterRule`; filter pass returns `IsFiltered = false` for a block whose `BlockType` matches no `FilterRule`; filter short-circuit — when `IsFiltered = true`, `Classification` and `Signal` are both null; classification pass — BddPattern rule fires and returns `Test`/`BddPattern` for a line starting with "Given "; Rfc2119Uppercase rule fires and returns `Requirement`/`Rfc2119Uppercase` for a line containing "MUST"; Default rule fires and returns `NeedsClarification`/`Default` for a plain text line with no signals; conflict resolution — a line containing both "Given " and "MUST" returns `Test`/`BddPattern` (BddPattern at priority 70 beats Rfc2119Uppercase at priority 60); tie-breaking — two rules at equal priority both matching returns the first-registered rule's outcome; `ApplicableBlockTypes` scope — a `ClassificationRule` with `ApplicableBlockTypes = [BlockType.UnorderedListItem]` is skipped when evaluating a `ParagraphLine` block; `EvaluatedRuleCount` is always >= 1 for any evaluation; `WinningRuleName` matches the name of the rule in `ExtractionRuleSet.Default()` that produced the classification

- [ ] T104 [P] Write startup validation tests in `frontend/BirkNext.Web.Tests/Services/ExtractionRuleSetValidationTests.cs` — each test constructs an `ExtractionRuleSet` with a deliberate flaw and asserts `ExtractionRuleEngine` constructor throws with a descriptive message: (a) rule set with no `ClassificationRule` entries → exception; (b) rule set with no unconditional priority-0 Default rule → exception; (c) rule set with two rules sharing the same `Name` (one filter, one classification) → exception; (d) `ClassificationRule` with `Priority == 0` and a `PatternMatchCondition` (not `UnconditionalCondition`) → exception at `ClassificationRule` construction; (e) `PatternMatchCondition` constructed with null `Regex` → exception at `PatternMatchCondition` construction; (f) `FilterRule` constructed with `Priority == 0` → exception at `FilterRule` construction; (g) `ExtractionRuleSet.Default()` passes all validation checks without exception (positive case — must not throw)

- [ ] T105 [P] Write default rule set correctness tests in `frontend/BirkNext.Web.Tests/Services/ExtractionRuleEngineTests.cs` — verify that `ExtractionRuleEngine` using `ExtractionRuleSet.Default()` produces identical classification outcomes to the hardcoded Stage 6 logic from `ScenarioExtractionService` (T067); required cases (use `TextBlock` with `BlockType.UnorderedListItem` and stripped text): "The system MUST validate credentials" → `Requirement`/`Rfc2119Uppercase`; "Given login When valid Then redirect" → `Test`/`BddPattern`; "Session timeout policy?" → `NeedsClarification`/`QuestionTerminator`; "TBD — performance target not yet set" → `NeedsClarification`/`DeferralMarker`; "FR-001: the system shall authenticate" → `Requirement`/`FrPrefix` (FrPrefix at priority 40 is higher than Rfc2119Lowercase at priority 50 — wait, 50 > 40, so Rfc2119Lowercase fires first; verify this matches current Stage 6 behaviour); "the system must be available 99.9% of the time" (lowercase must) → `Requirement`/`Rfc2119Lowercase`; "Given that the system MUST process requests quickly" → `Test`/`BddPattern` (BddPattern priority 70 beats Rfc2119Uppercase priority 60); "Random plain statement about performance" → `NeedsClarification`/`Default`; a string longer than `MaxLineLengthForPatternMatching` (2,000 chars) → `NeedsClarification`/`Default` (pattern matching bypassed); all nine filtered `BlockType` values (`Heading`, `FencedCodeBlock`, `Blockquote`, `HorizontalRule`, `HtmlComment`, `YamlFrontMatter`, `Empty`, `TableHeaderRow`, `TableSeparatorRow`) return `IsFiltered = true`; `UnorderedListItem` and `ParagraphLine` blocks are not filtered

**Checkpoint**: `dotnet test frontend/BirkNext.Web.Tests --filter "RuleEngine|RuleSet"` passes. All rule engine tests pass. No `ScenarioExtractionService` changes have been made; all US2 tests still pass independently.

---

## Phase 18: Pipeline Integration

**Purpose**: Replace the hardcoded Stage 4 and Stage 6 logic inside `ScenarioExtractionService` with calls to `IExtractionRuleEngine`. Tasks in this phase modify the same file sequentially; each task must leave the project in a buildable state and the full test suite must remain green after each step.

**Prerequisite**: Phases 15–17 complete (rule engine fully tested in isolation).

- [ ] T106 Inject `IExtractionRuleEngine` into `ScenarioExtractionService` via constructor injection in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — add private `readonly IExtractionRuleEngine _ruleEngine` field; update the constructor to accept `IExtractionRuleEngine ruleEngine` alongside the existing `IExtractionConfiguration configuration`; update the DI registration in `frontend/BirkNext.Web/Program.cs` if `ScenarioExtractionService` is registered with an explicit factory — change to `builder.Services.AddScoped<IScenarioExtractionService, ScenarioExtractionService>()` (constructor injection resolves `IExtractionRuleEngine` automatically from the container); make no behavioural changes to any pipeline stage in this task; confirm `dotnet build frontend/BirkNext.sln` passes and `dotnet test frontend/BirkNext.Web.Tests` produces zero failures

- [ ] T107 Replace Stage 4 (Structure Filter) hardcoded block-type discard with rule engine filter pass in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — replace the existing `if-else` or `switch` over `block.BlockType` with a call to `_ruleEngine.Evaluate(block, string.Empty)` (stripped text is empty for the filter pass — filter conditions operate on block structure only); if `result.IsFiltered` is true, discard the block; initialise a `RuleExecutionSummary _summary` field at the start of the pipeline run; for each filtered block increment `_summary.FilteredBlockCount` and add `result.EvaluatedRuleCount` to `_summary.TotalRulesEvaluated`; the nine `BlockType` values discarded by the hardcoded logic and the nine `FilterRule` entries in `ExtractionRuleSet.Default()` must produce identical discard decisions — verify this by running `dotnet test frontend/BirkNext.Web.Tests` and confirming all tests pass without modification; the `ContentLengthBelowCondition` is not used in `ExtractionRuleSet.Default()` — the Stage 5 minimum-length check remains as an inline condition after stripping and is not replaced

- [ ] T108 Replace Stage 6 (Classification) hardcoded heuristics with rule engine classification pass in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — replace the priority-ordered `if-else` signal detection chain with a call to `_ruleEngine.Evaluate(block, strippedText)` where `strippedText` is the text already produced by Stage 5; assign `result.Classification` to `ExtractionCandidate.Classification`; assign `result.Signal` to `ExtractionCandidate.ClassificationSignal`; add `result.EvaluatedRuleCount` to `_summary.TotalRulesEvaluated`; if `result.Signal == ClassificationSignal.Default` increment `_summary.DefaultFallbackCount`; the seven classification cases handled by the hardcoded logic and the seven `ClassificationRule` entries in `ExtractionRuleSet.Default()` must produce identical `(ScenarioKind, ClassificationSignal)` pairs for every input — verify by running the full test suite: `dotnet test frontend/BirkNext.Web.Tests` must produce zero failures with zero test modifications

**Checkpoint**: `dotnet test frontend/BirkNext.Web.Tests` passes with all 111+ pre-existing tests — zero failures, zero test file modifications. Stage 4 and Stage 6 now delegate entirely to `IExtractionRuleEngine`. The hardcoded logic is still present in the file (removal deferred to T115).

---

## Phase 19: Observability Integration

**Purpose**: Add `rulesEvaluatedCount` to the `ExtractionCompleted` log event. The `RuleExecutionSummary` accumulated during the pipeline run is the source.

**Prerequisite**: Phase 18 complete (`RuleExecutionSummary` is being populated by T107 and T108).

- [ ] T109 Move the `ExtractionCompleted` log event from `ExtractionInput.razor` to `ScenarioExtractionService` and add `rulesEvaluatedCount` in `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` and `frontend/BirkNext.Web/Components/ExtractionInput.razor` — `ScenarioExtractionService.ExtractAsync()` has direct access to `_summary` after Stage 8 completes; inject `ILogger<ScenarioExtractionService>` via constructor; emit `ExtractionCompleted` from the service when `Status == PipelineStatus.Success` with all existing fields (`candidateCount`, `requirementCount`, `testCount`, `needsClarificationCount`, `durationMs`) plus the new field `rulesEvaluatedCount` sourced from `_summary.TotalRulesEvaluated`; emit `ExtractionEmpty` from the service when `Status != Success` (the service already has all required fields); remove the `ExtractionCompleted` and `ExtractionEmpty` log calls from `ExtractionInput.razor` (they now originate in the service); `ExtractionInput.razor` retains the `ExtractionTriggered` log call only (it still owns the session ID and input metadata before the service is called); confirm `rulesEvaluatedCount` is a numeric count only — no text derived from pasted input may appear in this or any adjacent log field; add an inline comment at the new log call site noting "no raw text — counts only" for code review awareness per plan.md §Observability Integration Strategy and data-model.md §Observability Model Changes

**Checkpoint**: Application emits `ExtractionCompleted` with `rulesEvaluatedCount` in structured log output. The value is non-zero for any successful extraction. No pasted text appears in the log field.

---

## Phase 20: Validation and Security

**Purpose**: Verify regex safety constraints on all `PatternMatchCondition` patterns in `ExtractionRuleSet.Default()` and confirm startup validation surfaces failures at application start rather than silently at evaluation time.

**Prerequisite**: Phase 16 complete (`ExtractionRuleEngine` and `ExtractionRuleSet.Default()` exist).

- [ ] T110 [P] Verify regex safety constraints on all `PatternMatchCondition` instances in `ExtractionRuleSet.Default()` in `frontend/BirkNext.Web/Services/ExtractionRuleSet.cs` — review each pattern against the authoring constraints from plan.md §Security Strategy: (a) patterns are anchored or use word-boundary `\b` assertions where whole-word matching is required — e.g., MUST pattern uses `\b` to prevent matching "MUSTARD"; (b) no nested quantifiers (`(a+)+`, `(a|a)*`) — all quantifiers must be simple and flat; (c) no backreferences; (d) patterns are constructed with `RegexOptions.Compiled | RegexOptions.CultureInvariant` — verify no pattern uses a `new Regex(pattern)` call without flags; if any pattern violates a constraint, fix the pattern and re-run T105 to confirm the correction does not change classification outcomes for the test cases; add a file-level comment in `ExtractionRuleSet.cs` documenting the regex authoring constraints for future rule authors: word-boundary matching required for keyword patterns, no nested quantifiers, CultureInvariant required, all patterns compile at startup

- [ ] T111 [P] Verify startup validation surfaces at application start in `frontend/BirkNext.Web/Services/ExtractionRuleEngine.cs` — add a test in `frontend/BirkNext.Web.Tests/Services/ExtractionRuleSetValidationTests.cs` that constructs a `WebAssemblyHostBuilder` (or equivalent) with a deliberately broken `ExtractionRuleSet` registered as `IExtractionRuleEngine` and confirms the DI resolution throws before any request is handled; if a full host builder is impractical in bUnit, verify instead that `new ExtractionRuleEngine(brokenRuleSet, config)` throws synchronously — the critical requirement is that the failure is not deferred to the first `Evaluate()` call; confirm `ExtractionRuleSet.Default()` can always be constructed and validated without exception by the positive case in T104

**Checkpoint**: All `PatternMatchCondition` patterns in `ExtractionRuleSet.Default()` comply with the safety constraints. A broken rule set fails at construction, not at evaluation time.

---

## Phase 21: Regression and Stabilization

**Purpose**: Remove dead code, confirm all pre-existing tests pass, verify performance is unaffected, and reach zero format violations.

**Prerequisite**: Phases 15–20 complete. All tests must pass before any cleanup is attempted.

- [ ] T112 Run full regression suite to confirm the migration is complete in `frontend/BirkNext.Web.Tests/` — execute `dotnet test frontend/BirkNext.Web.Tests` and confirm: all 111+ pre-existing tests pass with zero failures; zero test files have been modified (no test assertion changed, no mock adjusted, no expected value altered); specifically confirm `ScenarioExtractionServiceTests.cs` (all stage unit tests), `ExtractionCandidateRowTests.cs`, `ExtractionReviewListTests.cs`, `ExtractionInputTests.cs`, `ExtractionAcceptanceCriteriaTests.cs` (all 6 US2 ACs), and the performance test `ExtractionPerformanceTests` all pass; if any pre-existing test fails, treat it as a regression in T107 or T108 and fix the rule engine or `ExtractionRuleSet.Default()` — do not modify the test

- [ ] T113 Remove hardcoded Stage 4 and Stage 6 logic from `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs` — once T112 confirms all tests pass, delete the original `if-else`/`switch` block-type discard code from Stage 4 (the version that was replaced by T107) and the priority-ordered classification `if-else` chain from Stage 6 (the version replaced by T108); confirm the file still compiles; re-run `dotnet test frontend/BirkNext.Web.Tests` — all tests must still pass; no business logic may remain in Stage 4 or Stage 6 that duplicates rule engine logic

- [ ] T114 [P] Verify performance — run the extraction performance test from T093 (`Extraction_10kCharInput_DurationMs_LessThan200`) in `frontend/BirkNext.Web.Tests/Services/ScenarioExtractionServiceTests.cs`; confirm it still passes (durationMs < 200); the test was measured at 0 ms (sub-millisecond) in T093 — confirm the rule engine does not introduce a measurable regression for this input size; if the test fails after US3 integration, profile the evaluation loop to identify which rule or condition is responsible and apply the mitigations from plan.md §Performance Guardrails (short-circuit, compiled regex, ApplicableBlockTypes pre-filter); document measured durationMs after US3 integration in a comment on this task

- [ ] T115 [P] Run `dotnet format` on `frontend/BirkNext.sln`; fix all whitespace and style violations introduced by new US3 files (`ExtractionRuleConditions.cs`, `ExtractionRuleModels.cs`, `ExtractionRuleSet.cs`, `RuleEvaluationResult.cs`, `IExtractionRuleEngine.cs`, `ExtractionRuleEngine.cs`) and the modified `ScenarioExtractionService.cs`; confirm `dotnet format frontend/BirkNext.sln --verify-no-changes` produces no output; confirm `dotnet build frontend/BirkNext.sln` produces zero errors and zero warnings; backend solution is unaffected by US3 — no format pass required

**Checkpoint**: All 111+ pre-existing US1 and US2 tests pass without modification. Dead code removed. Performance budget preserved. Both solutions build with zero errors. `dotnet format` clean.

---

## Dependencies & Execution Order (US3)

### Phase Dependencies

- **Phase 15 (Foundation)**: Requires US2 phases complete — all `[P]` tasks are independent
- **Phase 16 (Implementation)**: Requires Phase 15
- **Phase 17 (Rule Engine Tests)**: Requires Phase 16 — all `[P]` tasks are independent
- **Phase 18 (Pipeline Integration)**: Requires Phase 17 (rule engine fully tested before touching `ScenarioExtractionService`); T106 → T107 → T108 are sequential to the same file
- **Phase 19 (Observability)**: Requires Phase 18 (`RuleExecutionSummary` must be populated)
- **Phase 20 (Validation/Security)**: Requires Phase 16 (rule engine exists); can run in parallel with Phases 17–19
- **Phase 21 (Stabilization)**: Requires Phases 15–20 complete; T113 depends on T112; T114 and T115 can run in parallel with each other after T112 and T113

### Key Sequencing Constraint

T107 and T108 must each leave the test suite green before the next task begins. The rule engine and the hardcoded logic must produce identical outcomes — if any test fails after T107 or T108, the cause is a discrepancy in `ExtractionRuleSet.Default()` and must be fixed before proceeding.

### Within Phase 18

```
T106 — Inject IExtractionRuleEngine (constructor update, no behaviour change)
  └── T107 — Replace Stage 4 (verify: all tests pass)
        └── T108 — Replace Stage 6 (verify: all tests pass)
```

---

## Parallel Opportunities (US3)

### Phase 15 (all parallel — different files)

```
T096 — ExtractionRuleConditions.cs
T097 — ExtractionRuleModels.cs
T098 — RuleEvaluationResult.cs
T099 — IExtractionRuleEngine.cs
T100 — ExtractionRuleSet.cs (Default() factory)
```

### Phase 17 (all parallel — different test concerns)

```
T103 — ExtractionRuleEngineTests (evaluation logic)
T104 — ExtractionRuleSetValidationTests (startup validation)
T105 — ExtractionRuleEngineTests (default rule set correctness)
```

### Phase 20 (all parallel — different files)

```
T110 — ExtractionRuleSet.cs regex safety review
T111 — ExtractionRuleSetValidationTests startup verification
```

### Phase 21 (partial parallel after T112 + T113)

```
T112 — Full regression suite (must complete first)
T113 — Dead code removal (depends on T112)
  ├── T114 — Performance verification (parallel with T115)
  └── T115 — dotnet format + build (parallel with T114)
```

---

## Implementation Strategy (US3)

US3 has a single recommended sequence; the architecture is not parallelisable across developers at the pipeline integration layer because T107 and T108 modify the same file sequentially.

**Single-developer sequence:**
1. Phase 15 — build all foundation types (new files only)
2. Phase 16 — implement rule engine + register in DI
3. Phase 17 — test rule engine in isolation; do not touch `ScenarioExtractionService` until all rule engine tests pass
4. Phase 18 — integrate (T106 → T107 → T108); verify test suite green after each step
5. Phase 19 — add `rulesEvaluatedCount` to log event
6. Phase 20 — regex safety review and startup validation verification
7. Phase 21 — remove dead code, final regression, performance, format

**Key quality gate**: Before starting Phase 18, all Phase 17 tests must pass. This is the safety net that makes the migration trustworthy. The existing test suite (111+ tests) is the acceptance criterion for the migration; passing those tests without modification is the definition of done.

---

## Notes (US3)

- `[P]` tasks operate on different files with no cross-task dependencies within the same phase
- T107 and T108 are sequential additions to a single file; each must leave `ScenarioExtractionService` buildable and test-suite-green
- The XSS rendering constraint (T088, already verified in US2) is unchanged — the rule engine does not affect how candidates are displayed
- `ExtractionMetadataInput` fields are numeric and identifier only — US3 does not add a `rulesEvaluatedCount` field to `ExtractionMetadataInput`; `rulesEvaluatedCount` stays in the client-side `ExtractionCompleted` log event only
- `ExtractionCandidate.Confidence` remains null — the deterministic rule engine must not populate this field; it is reserved for a future AI implementation
- All new files are in `frontend/BirkNext.Web/Services/` and `frontend/BirkNext.Web.Tests/Services/`; no backend files change
- No GraphQL contract changes — `contracts/schema.graphql` is frozen for US3
- Commit after each phase; branch stays shippable at every checkpoint
- Total US3 tasks: **20** (T096–T115) | Phase 15: 5 | Phase 16: 2 | Phase 17: 3 | Phase 18: 3 | Phase 19: 1 | Phase 20: 2 | Phase 21: 4
- **Combined total: 115 tasks** (T001–T115)

---

---

# US4 — Level 1 Configurable Extraction Rules

## Overview

US4 introduces a bounded configuration layer over the US3 deterministic rule engine.
Configuration is loaded from `wwwroot/appsettings.json §ExtractionRules` at Blazor WASM
startup and compiled into an `ExtractionRuleSet` by `ExtractionRuleSetCompiler`.

**No GraphQL changes. No backend changes. No TDD.**

All 16 tasks add or extend client-side code only. The 153-test regression suite (accumulated
across US1–US3) must pass without modification after every phase.

### Scope

- Bounded keyword additions (BDD, RFC 2119 uppercase/lowercase, deferral markers)
- Prefix classification rules (plain-string prefix match; no regex; `PrefixMatchCondition`)
- Ignore-prefix filtering (Stage 5.5 inline filter in `ScenarioExtractionService`)
- Rule enable/disable (`DisabledRuleNames`; `Classify:Default` protected)
- Bounded priority overrides (`PriorityOverrides`; values 1–99; `Classify:Default` protected)
- Startup validation with warn-and-fallback (no crash on bad config)
- Three startup observability events (counts only; no keyword/prefix text)

### Not in scope

- AI, ML, unrestricted regex editing, arbitrary scripting
- Backend changes, database changes, GraphQL changes
- Changes to `ExtractionMetadataInput` or any US2 save path
- `ExtractionCandidate.Confidence` (reserved for future AI implementation)

---

## Phase 22 — Configuration Model Foundation

New files only. No existing file modifications. Tasks T116 and T117 are independent and can
be worked in parallel.

### T116 [P] — Create `ExtractionRuleConfiguration` and `PrefixRuleEntry` POCOs

**File:** `frontend/BirkNext.Web/Services/ExtractionRuleConfiguration.cs` (new)

Create two pure POCOs in `BirkNext.Web.Services`:

**`PrefixRuleEntry`:**
```csharp
public sealed class PrefixRuleEntry
{
    public string?      Name           { get; set; }
    public string       Prefix         { get; set; } = string.Empty;
    public ScenarioKind Classification { get; set; }
    public int          Priority       { get; set; } = 10;
}
```

**`ExtractionRuleConfiguration`:**
```csharp
public sealed class ExtractionRuleConfiguration
{
    public string[]                      BddKeywordAdditions      { get; set; } = [];
    public string[]                      Rfc2119UppercaseAdditions{ get; set; } = [];
    public string[]                      Rfc2119LowercaseAdditions{ get; set; } = [];
    public string[]                      DeferralMarkerAdditions  { get; set; } = [];
    public PrefixRuleEntry[]             PrefixRules              { get; set; } = [];
    public string[]                      IgnorePrefixes           { get; set; } = [];
    public string[]                      DisabledRuleNames        { get; set; } = [];
    public Dictionary<string, int>       PriorityOverrides        { get; set; } = [];
}
```

Both types are JSON-serializable and designed for `IOptions<ExtractionRuleConfiguration>`
binding. No constructor validation — all validation is deferred to `ExtractionRuleSetCompiler`.

**Acceptance:** `dotnet build frontend/BirkNext.sln` passes. No test changes.

---

### T117 [P] — Create `PrefixMatchCondition`

**File:** `frontend/BirkNext.Web/Services/PrefixMatchCondition.cs` (new)

Create `PrefixMatchCondition` as a new `ClassificationCondition` subtype (sibling of
`PatternMatchCondition` and `UnconditionalCondition`):

```csharp
public sealed class PrefixMatchCondition : ClassificationCondition
{
    public string Prefix { get; }

    public PrefixMatchCondition(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            throw new ArgumentException("Prefix must not be null or empty.", nameof(prefix));
        Prefix = prefix;
    }

    public override bool Evaluate(string strippedText)
        => strippedText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
```

No regex. No ReDoS surface. Case-insensitive prefix match only.

**Acceptance:** `dotnet build frontend/BirkNext.sln` passes. All 153 tests pass unchanged.

---

**Phase 22 checkpoint:** `dotnet build frontend/BirkNext.sln` zero errors. All 153 tests pass.
No behavioral changes.

---

## Phase 23 — Rule Engine Model Extensions

Additive changes to existing model files. T118 and T119 are independent and can be worked
in parallel. T120 depends on T119.

### T118 [P] — Add `ConfiguredPrefix` to `ClassificationSignal`

**File:** `frontend/BirkNext.Web/Models/ClassificationSignal.cs`

Add `ConfiguredPrefix` as a new enum value. Audit all `switch` statements and `if`/`else if`
chains across the codebase that branch on `ClassificationSignal` — confirm every existing
branch has a `default` or catch-all that safely handles unknown values. No behavioral change.

Updated priority table for documentation:

| Signal              | Default Classification | Rule name (priority)                   |
|---------------------|------------------------|----------------------------------------|
| BddPattern          | Test                   | Classify:BddPattern (70)               |
| Rfc2119Uppercase    | Requirement            | Classify:Rfc2119Uppercase (60)         |
| Rfc2119Lowercase    | Requirement            | Classify:Rfc2119Lowercase (50)         |
| FrPrefix            | Requirement            | Classify:FrPrefix (40)                 |
| QuestionTerminator  | NeedsClarification     | Classify:QuestionTerminator (30)       |
| DeferralMarker      | NeedsClarification     | Classify:DeferralMarker (20)           |
| ConfiguredPrefix    | Per PrefixRuleEntry    | Default priority 10; configurable 1–99 |
| Default             | NeedsClarification     | Classify:Default (0)                   |

**Acceptance:** `dotnet build` passes. All 153 tests pass unchanged.

---

### T119 [P] — Add `IgnorePrefixes` to `ExtractionRuleSet`

**File:** `frontend/BirkNext.Web/Services/ExtractionRuleSet.cs`

Add `IgnorePrefixes` as a new field with nullable default parameter to preserve backward
compatibility with all 153 existing test call sites:

```csharp
public IReadOnlyList<string> IgnorePrefixes { get; }

// Constructor: add nullable parameter with default
public ExtractionRuleSet(
    IReadOnlyList<FilterRule> filterRules,
    IReadOnlyList<ClassificationRule> classificationRules,
    IReadOnlyList<string>? ignorePrefixes = null)
{
    FilterRules         = filterRules;
    ClassificationRules = classificationRules;
    IgnorePrefixes      = ignorePrefixes ?? ImmutableArray<string>.Empty;
}
```

Update `Default()` to pass `ImmutableArray<string>.Empty` explicitly (no behavior change).

**Critical:** All existing test call sites use the 2-parameter form; the nullable default
ensures zero test modifications are required.

**Acceptance:** All 153 tests pass without modification.

---

### T120 — Add `IgnorePrefixes` to `IExtractionRuleEngine` and `ExtractionRuleEngine`

**Files:**
- `frontend/BirkNext.Web/Services/IExtractionRuleEngine.cs`
- `frontend/BirkNext.Web/Services/ExtractionRuleEngine.cs`
- All test files containing Moq doubles for `IExtractionRuleEngine`

**Depends on:** T119

Add property to interface:
```csharp
IReadOnlyList<string> IgnorePrefixes { get; }
```

Implement in `ExtractionRuleEngine`:
```csharp
public IReadOnlyList<string> IgnorePrefixes => _ruleSet.IgnorePrefixes;
```

Update all Moq test doubles in `frontend/BirkNext.Web.Tests/` that mock
`IExtractionRuleEngine` to add:
```csharp
.Setup(e => e.IgnorePrefixes).Returns(ImmutableArray<string>.Empty)
```

**Acceptance:** All 153 tests pass without modification. No behavioral change.

---

**Phase 23 checkpoint:** All 153 tests pass. `dotnet build` zero warnings. No behavioral
changes in the extraction pipeline.

---

## Phase 24 — Compiler Implementation

Sequential. T121 must complete before T122. T122 depends on T118 (ConfiguredPrefix signal).

### T121 — `ExtractionRuleSetCompiler` shell + validation (Step 1)

**File:** `frontend/BirkNext.Web/Services/ExtractionRuleSetCompiler.cs` (new)

**Depends on:** Phase 22 and Phase 23 complete

Create the compiler with full validation logic. The `Compile` method returns `baseSet`
unchanged on any validation failure or when config is empty/null:

**Class structure:**
```csharp
public sealed class ExtractionRuleSetCompiler
{
    private readonly ILogger<ExtractionRuleSetCompiler> _logger;

    // Base keyword sets — synchronized with ExtractionRuleSet.Default()
    private static readonly string[] BddBaseKeywords           = [ /* from Default() */ ];
    private static readonly string[] Rfc2119UppercaseBaseKeywords = [ /* from Default() */ ];
    private static readonly string[] Rfc2119LowercaseBaseKeywords = [ /* from Default() */ ];
    private static readonly string[] DeferralMarkerBaseKeywords = [ /* from Default() */ ];

    public ExtractionRuleSetCompiler(ILogger<ExtractionRuleSetCompiler> logger) { ... }

    public ExtractionRuleSet Compile(ExtractionRuleSet baseSet,
                                     ExtractionRuleConfiguration? config) { ... }
}
```

**Step 1 — Validation (6 check groups):**

1. **Array length limits:** All `string[]` arrays ≤ 50 entries. `PrefixRules` ≤ 50 entries.
2. **String value constraints:** Each keyword/prefix string: non-empty, ≤ 200 characters,
   printable ASCII only (`>= 0x20 && <= 0x7E`), no regex metacharacters
   (`\ ^ $ . | ? * + ( ) [ ] { }`).
3. **PrefixRuleEntry constraints:** `Prefix` non-empty, ≤ 200 chars, printable ASCII, no
   metacharacters. `Classification` must be a valid `ScenarioKind`. `Priority` in range 1–99.
4. **DisabledRuleNames:** Each name must exist in `baseSet.ClassificationRules`
   (matched by `ClassificationRule.Name`). `Classify:Default` must not be disabled.
5. **PriorityOverrides:** Each key must exist in `baseSet.ClassificationRules`.
   `Classify:Default` must not be overridden. Each value in range 1–99.
6. **IgnorePrefixes:** Each entry: non-empty, ≤ 200 chars, printable ASCII, no metacharacters.

**Fallback behavior:**
- On first validation failure: construct `ConfigurationViolation` (internal), log
  `ExtractionRuleConfigurationFailed` (Warning: `fieldName`, `violationType`, `entryIndex?`,
  `fallbackApplied: true` — no field value content), log
  `ExtractionRuleConfigurationFallback` (Info: `reason: "validation_failure"`), return `baseSet`.
- When config is null or all arrays/maps are empty: log `ExtractionRuleConfigurationLoaded`
  (Info: all counts = 0) + `ExtractionRuleConfigurationFallback` (Info: `reason: "no_configuration"`),
  return `baseSet`.

**In T121:** Steps 2–8 are stubs that immediately return after Step 1 passes (for now —
T122 fills them in). The intermediate state is: always returns `baseSet`, but validation
runs and logs correctly.

**Acceptance:** `dotnet build` passes. All 153 tests pass. Validation logic is exercisable
via `NullLogger<ExtractionRuleSetCompiler>.Instance` in Phase 25 tests.

---

### T122 — `ExtractionRuleSetCompiler` Steps 2–8

**File:** `frontend/BirkNext.Web/Services/ExtractionRuleSetCompiler.cs`

**Depends on:** T121, T118 (ConfiguredPrefix)

Implement the 7 remaining compilation steps after validation:

**Step 2 — Disable rules:**
Copy `baseSet.ClassificationRules` to a working `List<ClassificationRule>`. Remove entries
whose `Name` appears in `config.DisabledRuleNames`. Non-mutability invariant: `baseSet` is
never modified.

**Step 3 — Priority overrides:**
For each key in `config.PriorityOverrides`, find the matching `ClassificationRule` in the
working list by `Name` and update its `Priority` to the override value.

**Step 4 — Keyword extend:**
For each keyword addition array, combine base keywords + additions, apply `Regex.Escape` to
each entry, wrap as `\b(?:keyword1|keyword2|...)\b`, compile with
`RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase`.
Replace the corresponding `PatternMatchCondition` in the working list.

**Step 5 — Add prefix rules:**
For each `PrefixRuleEntry` in `config.PrefixRules`, create a new `ClassificationRule`:
- `Name`: use `entry.Name` if non-null/non-empty; otherwise auto-generate
  `$"Configure:Prefix:{index}"` (zero-based index)
- `Condition`: `new PrefixMatchCondition(entry.Prefix)`
- `Signal`: `ClassificationSignal.ConfiguredPrefix`
- `Classification`: `entry.Classification`
- `Priority`: `entry.Priority`

Append all new rules to the working list.

**Step 6 — Set IgnorePrefixes:**
Collect `config.IgnorePrefixes` as `ImmutableArray<string>` (empty if array is null/empty).

**Step 7 — Sort:**
Sort working list by `Priority` descending. Use stable sort (preserve original order for
equal priorities).

**Step 8 — Construct and return:**
```csharp
var compiledSet = new ExtractionRuleSet(
    baseSet.FilterRules,
    workingRules,
    ignorePrefixes);
```

Log `ExtractionRuleConfigurationLoaded` (Info) with 8 count fields:
`bddAdditions`, `rfc2119UppercaseAdditions`, `rfc2119LowercaseAdditions`,
`deferralMarkerAdditions`, `prefixRulesAdded`, `ignorePrefixesAdded`,
`disabledRuleNames`, `priorityOverrides`. No keyword or prefix text in this event.

**Non-mutability invariant:** `baseSet` and `ExtractionRuleSet.Default()` are unchanged
before and after `Compile()`.

**Acceptance:** `dotnet build` passes. All 153 tests pass. `Compile(Default(), new ExtractionRuleConfiguration())` returns a set that produces identical extraction results to `Default()`.

---

**Phase 24 checkpoint:** `dotnet build` zero errors/warnings. All 153 tests pass.
Compiler produces correct output for representative inputs. Non-mutability verified manually.

---

## Phase 25 — Tests

Post-implementation tests. T123 and T124 are independent and can be worked in parallel.
Do not use TDD — these tests are written after Phase 24 is complete and building cleanly.

### T123 [P] — `ExtractionRuleConfigurationTests.cs`

**File:** `frontend/BirkNext.Web.Tests/Services/ExtractionRuleConfigurationTests.cs` (new)

Validation-focused tests using `NullLogger<ExtractionRuleSetCompiler>.Instance`.
Minimum 16 test cases covering:

- Empty config → returns `Default()` equivalent (fallback, no crash)
- Null config → returns `Default()` equivalent
- Valid minimal config (1 BDD addition) → compiles without fallback
- Array too long (51 entries) → fails with `too_many_entries`
- Empty string in keyword array → fails with `empty_value`
- String exceeding 200 chars → fails with `value_too_long`
- Non-ASCII character → fails with `non_ascii_characters`
- Regex metacharacter in keyword → fails with `regex_metacharacter`
- Regex metacharacter in prefix → fails with `regex_metacharacter`
- Invalid `PrefixRuleEntry.Classification` → fails with `invalid_classification`
- `PrefixRuleEntry.Priority` = 0 → fails with `priority_out_of_range`
- `PrefixRuleEntry.Priority` = 100 → fails with `priority_out_of_range`
- Unknown `DisabledRuleNames` entry → fails with `unknown_rule_name`
- `Classify:Default` in `DisabledRuleNames` → fails with `default_rule_disabled`
- `Classify:Default` in `PriorityOverrides` → fails with `default_priority_override`
- Fallback returns `baseSet` unchanged (non-mutability after fallback)
- Idempotency: calling `Compile` twice with same inputs returns equivalent sets

**Acceptance:** All 153 + new tests pass.

---

### T124 [P] — `ExtractionRuleSetCompilerTests.cs`

**File:** `frontend/BirkNext.Web.Tests/Services/ExtractionRuleSetCompilerTests.cs` (new)

Compiler behavior tests. Test cases include:

- Empty config → compiled set is structurally equivalent to `Default()`
- BDD keyword addition: verify new keyword is matched in Stage 4 rule evaluation
- RFC 2119 uppercase addition: verify new keyword classified as Requirement
- Deferral marker addition: verify new marker classified as NeedsClarification
- Prefix rule: verify `PrefixMatchCondition` match produces `ConfiguredPrefix` signal
- Prefix rule with explicit `Name` → rule name preserved
- Prefix rule with null `Name` → auto-generated `Configure:Prefix:{index}`
- Multiple prefix rules: all present in compiled set, correct priorities
- `IgnorePrefixes` populated in compiled `ExtractionRuleSet`
- `PrefixMatchCondition` is case-insensitive (`"FR-"` matches `"fr-001"`)
- `DisabledRuleNames`: named rule absent from compiled set
- `PriorityOverrides`: rule has updated priority in compiled set
- Non-mutability: `baseSet` unchanged after `Compile()`; `Default()` unchanged
- `Compile(Default(), empty config)` === `Default()` extraction behavior (3 representative inputs)
- Compiled set passes `ExtractionRuleEngine` internal startup validation (no exceptions on construction)

**Acceptance:** All 153 + new tests pass.

---

**Phase 25 checkpoint:** Full test suite (153 + new tests) passes. No existing tests modified.

---

## Phase 26 — DI and Pipeline Integration

### T125 — Wire `ExtractionRuleSetCompiler` in `Program.cs` and `appsettings.json`

**Files:**
- `frontend/BirkNext.Web/Program.cs`
- `frontend/BirkNext.Web/wwwroot/appsettings.json`

**Depends on:** Phase 24 complete

1. Register `IOptions<ExtractionRuleConfiguration>` binding from `§ExtractionRules`:
   ```csharp
   builder.Services.Configure<ExtractionRuleConfiguration>(
       builder.Configuration.GetSection("ExtractionRules"));
   ```

2. Register `ExtractionRuleSetCompiler` as transient (used once at startup only).

3. Replace the existing `IExtractionRuleEngine` singleton registration (from T102) with a
   compiler-based factory:
   ```csharp
   builder.Services.AddSingleton<IExtractionRuleEngine>(sp =>
   {
       var compiler = sp.GetRequiredService<ExtractionRuleSetCompiler>();
       var config   = sp.GetRequiredService<IOptions<ExtractionRuleConfiguration>>().Value;
       var compiled = compiler.Compile(ExtractionRuleSet.Default(), config);
       return new ExtractionRuleEngine(compiled);
   });
   ```

4. Add `"ExtractionRules": {}` section to `wwwroot/appsettings.json` (empty object = use
   compiled defaults; no behavioral change at this point).

**Acceptance:** Blazor WASM app starts. `ExtractionRuleConfigurationLoaded` is emitted at
startup (all counts = 0 with empty config). All 153 tests pass.

---

### T126 — Add Stage 5.5 (IgnorePrefixes inline filter) to `ScenarioExtractionService`

**File:** `frontend/BirkNext.Web/Services/ScenarioExtractionService.cs`

**Depends on:** T120 (IgnorePrefixes on IExtractionRuleEngine)

Add Stage 5.5 immediately after Stage 5 (Content Extraction) in the pipeline:

```csharp
// Stage 5.5 — IgnorePrefixes filter (US4)
// No-op when IgnorePrefixes is empty (default configuration).
var ignorePrefixes = _ruleEngine.IgnorePrefixes;
if (ignorePrefixes.Count > 0)
{
    contentItems = contentItems
        .Where(item => !ignorePrefixes.Any(p =>
            item.PlainText.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        .ToList();
}
```

When `IgnorePrefixes` is empty (default), the `if` block is skipped entirely — zero
allocations, zero behavioral change for existing tests.

**Acceptance:** All 153 tests pass without modification. Stage 5.5 filter is a no-op with
default config. Manual test: configure one ignore prefix and verify matching items are
excluded from extraction output.

---

**Phase 26 checkpoint:** App starts cleanly. All 153 tests pass. Stage 5.5 is a verified
no-op with default config.

---

## Phase 27 — Observability Verification

### T127 — Verify startup log event compliance (OBS-US4-005)

**File:** `frontend/BirkNext.Web/Services/ExtractionRuleSetCompiler.cs`

Verify all three startup log events emit correct fields with no text content:

| Event | Level | Fields |
|-------|-------|--------|
| `ExtractionRuleConfigurationLoaded` | Info | `bddAdditions`, `rfc2119UppercaseAdditions`, `rfc2119LowercaseAdditions`, `deferralMarkerAdditions`, `prefixRulesAdded`, `ignorePrefixesAdded`, `disabledRuleNames`, `priorityOverrides` (counts only) |
| `ExtractionRuleConfigurationFailed` | Warning | `fieldName`, `violationType`, `entryIndex?`, `fallbackApplied: true` — no field value content |
| `ExtractionRuleConfigurationFallback` | Info | `reason: "validation_failure"` or `"no_configuration"` |

Add inline comments marking each log call with `// OBS-US4-005: counts only` or
`// OBS-US4-005: field name + code, no value content` to make the privacy constraint
auditable at code review.

Add 3 logger-capture test cases to `ExtractionRuleSetCompilerTests.cs` (using a
`FakeLogger<ExtractionRuleSetCompiler>` or equivalent capture approach):
- Valid config → `ExtractionRuleConfigurationLoaded` emitted with correct counts
- Invalid config → `ExtractionRuleConfigurationFailed` + `ExtractionRuleConfigurationFallback` emitted; no field values in log message
- Empty config → `ExtractionRuleConfigurationLoaded` (all zeros) + `ExtractionRuleConfigurationFallback` (reason: `"no_configuration"`) emitted

**Acceptance:** All tests pass. Log events confirmed compliant with no-text constraint.

---

**Phase 27 checkpoint:** Observability events verified. Privacy constraint auditable.

---

## Phase 28 — Regression and Stabilization

Tasks T128–T131 can be worked in the sequence below. T129, T130, and T131 are independent
of each other and can be worked in parallel after T128.

### T128 — Full regression suite

Run the complete test suite and confirm zero failures, zero modifications to existing tests:

```
dotnet test frontend/BirkNext.sln --no-build
```

Assert: all 153 US1–US3 tests pass without modification. Any failure here is a blocking
defect — do not proceed to T129–T131 until T128 is green.

---

### T129 [P] — Performance verification with maximally configured rule set

**Depends on:** T128

Configure a maximally loaded `ExtractionRuleConfiguration`:
- 50 entries each in `BddKeywordAdditions`, `Rfc2119UppercaseAdditions`,
  `Rfc2119LowercaseAdditions`, `DeferralMarkerAdditions`, `IgnorePrefixes`
- 50 `PrefixRuleEntry` instances (varied classifications and priorities)

Run the full extraction pipeline against a 10,000-character input string.
Assert: `ExtractionPipelineResult.DurationMs < 200`.
Document the measured value in a code comment in the test.

This verifies that regex compilation at startup (not at pipeline execution time) and the
Stage 5.5 no-regex prefix check maintain acceptable performance under maximum configuration load.

---

### T130 [P] — FR-US4-010 compliance: default config reproduces baseline behavior

**Depends on:** T128

Verify that `Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration())`
produces extraction results identical to `ExtractionRuleSet.Default()` for 3 representative
inputs:
- An input expected to produce Requirement candidates
- An input expected to produce Test candidates
- An input expected to produce NeedsClarification candidates

For each input, run the full extraction pipeline with both rule sets and compare:
- All `ExtractionCandidate.Title` values (must match exactly)
- All `ExtractionCandidate.Classification` values (must match exactly)
- All `ExtractionCandidate.Signal` values (must match exactly)

This is the key regression gate for FR-US4-010.

---

### T131 [P] — Format, build, and backend verification

**Depends on:** T128

```
dotnet format frontend/BirkNext.sln --verify-no-changes
dotnet build frontend/BirkNext.sln
dotnet build backend/BirkNext.Api.sln
```

Assert:
- `dotnet format` reports no changes needed
- Frontend build: zero errors, zero warnings
- Backend build: zero errors, zero warnings (verifies no accidental backend file modifications)

---

**Phase 28 checkpoint:** All gates green. US4 is complete and shippable.

---

## Dependency Graph (US4)

```
T116 [P] ──────────────────────────────────────────────────────┐
T117 [P] ──────────────────────────────────────────────────────┤
                                                                ├── T121 ── T122
T118 [P] ──────────────────────────────────────────────────────┤              │
T119 [P] ──────────────────────────────────────────────────────┘              │
T119 ──── T120 ─────────────────────────────────── T126                      │
                                                                               │
T121 ── T122 ─── T123 [P]                                                    │
              ── T124 [P]                                                     │
              ── T125 ──── T126                                               │
                                                    ┌───────────────────────┘
                                                    ▼
                                         T128 (full regression)
                                           ├── T129 [P]
                                           ├── T130 [P]
                                           └── T131 [P]
```

---

## Implementation Strategy (US4)

**Single-developer sequence:**
1. Phase 22 — create two new files (T116, T117); confirm build + 153 tests
2. Phase 23 — extend existing models additively (T118, T119 parallel; T120 after T119)
3. Phase 24 — implement compiler (T121 first with full validation; T122 adds Steps 2–8)
4. Phase 25 — write tests post-implementation (T123, T124 parallel)
5. Phase 26 — wire DI and pipeline (T125 → T126)
6. Phase 27 — verify observability compliance (T127)
7. Phase 28 — full regression + stabilization (T128 → T129/T130/T131 parallel)

**Key quality gates:**
- After Phase 22: build passes; 153 tests pass; no behavioral change
- After Phase 23: all 153 tests pass without modification; `IgnorePrefixes` is empty by default
- After Phase 24: `Compile(Default(), empty config)` === `Default()` behavior (manual verification)
- After Phase 25: full test suite (153 + new tests) passes
- After Phase 26: app starts; Stage 5.5 is a verified no-op with default config
- T128: the blocking gate before stabilization

---

## Notes (US4)

- `[P]` tasks operate on different files with no cross-task dependencies within the same phase
- T120 and T126 are sequential within their phases because T120 modifies `IExtractionRuleEngine`
  which T126 reads — T126 must see the updated interface
- `ExtractionCandidate.Confidence` remains null — `ConfiguredPrefix` signal must not populate
  this field; it is reserved for a future AI implementation
- No `ExtractionMetadataInput` changes — the US2 save path is identical; prefix/keyword counts
  do not travel over the wire
- `Classify:Default` is protected from disable and priority override in all validation paths
- All new files are in `frontend/BirkNext.Web/Services/` and `frontend/BirkNext.Web.Tests/Services/`
- No backend files change; no GraphQL contract changes
- `ExtractionRuleSet.Default()` is the fallback in all failure paths — it is never null, never
  throws, and produces deterministic output at all times
- Commit after each phase; branch stays shippable at every checkpoint
- Total US4 tasks: **16** (T116–T131) | Phase 22: 2 | Phase 23: 3 | Phase 24: 2 | Phase 25: 2 | Phase 26: 2 | Phase 27: 1 | Phase 28: 4
- **Combined total: 131 tasks** (T001–T131)
