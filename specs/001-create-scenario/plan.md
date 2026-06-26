# Implementation Plan: Scenario Management

**Branch**: `001-create-scenario` | **Date**: 2026-04-30 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/001-create-scenario/spec.md`

## Summary

Implement a Scenario Management feature for a web application allowing users to create structured scenarios (title, description, type) and view them in a list. The backend is an ASP.NET Core Web API exposing a HotChocolate GraphQL endpoint backed by EF Core and PostgreSQL. The frontend is a Blazor WebAssembly SPA using Strawberry Shake (the HotChocolate typed client generator) to call the `scenarios` query and `createScenario` mutation. All code is C# / .NET 8 across both tiers. Implementation follows Test-First Development: acceptance tests are written before any feature code.

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 (backend and frontend — all C#)  
**Primary Dependencies**:
- Backend: ASP.NET Core, HotChocolate 14 (GraphQL server), Entity Framework Core 8, Npgsql.EntityFrameworkCore.PostgreSQL, Serilog
- Frontend: Blazor WebAssembly (.NET 8), Strawberry Shake 14 (typed GraphQL client, code-generated from schema)  
**Storage**: PostgreSQL 16  
**Testing**: xUnit, FluentAssertions, HotChocolate.Testing, Microsoft.AspNetCore.Mvc.Testing (backend); bUnit, Moq (frontend Blazor components)  
**Target Platform**: Linux server / Docker (backend API); static file hosting / CDN (Blazor WASM assets)  
**Project Type**: web-service (ASP.NET Core + HotChocolate) + web-application (Blazor WebAssembly); separately deployable  
**Performance Goals**: p95 GraphQL response ≤ 200 ms; scenario list visible within 3 seconds of successful mutation (SC-002)  
**Constraints**: Offline capability not required; no pagination in v1; no edit/delete in v1  
**Scale/Scope**: Small team; ~100–500 scenarios per project workspace

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence / Action Required |
|-----------|--------|---------------------------|
| **I. Test-First Development** | ✅ PASS | Spec defines 5 acceptance scenarios for US1, 3 for US2, 3 for US3. These map 1:1 to xUnit (backend) and bUnit (Blazor component) test cases. Tests MUST be written and failing before any implementation begins. |
| **II. Observability** | ✅ PASS | Spec §Observability requires logging of: successful creation, validation failures, and technical errors with request context. Serilog structured JSON with correlation IDs on every request; OpenTelemetry traces at the GraphQL boundary. |
| **III. Security-First** | ✅ PASS | Auth is assumed external (spec assumption). All GraphQL input types validated server-side (title non-empty, type enum). No secrets in VCS — connection strings via environment variables / `dotnet user-secrets`. CORS configured for known frontend origin only. |
| **Development Standards** | ✅ PASS | GraphQL schema contract defined in `contracts/schema.graphql` before implementation. Blazor components contain no business logic — services (Strawberry Shake generated clients) own all data access. Independent test suites for backend and frontend. |
| **Quality Gates** | ✅ PASS | CI must pass: unit + integration + contract tests, `dotnet format` with zero errors, observability instrumentation verified, peer review, no breaking schema change without a documented migration plan. |

**No violations. Complexity Tracking table not required.**

**Post-Phase-1 re-check**: Confirmed after data model and schema design — no new violations introduced.

## Project Structure

### Documentation (this feature)

```text
specs/001-create-scenario/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── schema.graphql   # Phase 1 output — canonical GraphQL schema
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
backend/
├── BirkNext.Api/
│   ├── GraphQL/
│   │   ├── Query.cs                  # scenarios(projectId) query resolver
│   │   ├── Mutation.cs               # createScenario mutation resolver
│   │   ├── ScenarioObjectType.cs     # HotChocolate object type definition
│   │   └── CreateScenarioInput.cs    # HotChocolate input type
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   ├── Models/
│   │   ├── Scenario.cs
│   │   └── ScenarioKind.cs           # enum: Requirement | Test | NeedsClarification
│   ├── Services/
│   │   └── ScenarioService.cs
│   ├── Middleware/
│   │   └── CorrelationIdMiddleware.cs
│   ├── appsettings.json
│   └── Program.cs
└── BirkNext.Api.Tests/
    ├── Unit/
    │   └── ScenarioServiceTests.cs
    ├── Integration/
    │   └── ScenariosMutationTests.cs  # full GQL request → DB round trip
    └── Contract/
        └── ScenariosSchemaTests.cs    # HotChocolate schema snapshot tests

frontend/
├── BirkNext.Web/                      # Blazor WebAssembly project
│   ├── Pages/
│   │   └── Scenarios.razor            # host page for form + list
│   ├── Components/
│   │   ├── ScenarioForm.razor
│   │   └── ScenarioList.razor
│   ├── GraphQL/
│   │   ├── GetScenarios.graphql       # query document (Strawberry Shake input)
│   │   └── CreateScenario.graphql     # mutation document (Strawberry Shake input)
│   ├── wwwroot/
│   └── Program.cs                     # registers Strawberry Shake client + DI
└── BirkNext.Web.Tests/                # bUnit test project
    ├── Components/
    │   ├── ScenarioFormTests.cs
    │   └── ScenarioListTests.cs
    └── Pages/
        └── ScenariosPageTests.cs
```

**Structure Decision**: Web application (Option 2 — separate backend and frontend). Backend is a standalone ASP.NET Core project exposing a single GraphQL endpoint via HotChocolate (`/graphql`). Frontend is a standalone Blazor WebAssembly project using Strawberry Shake's code-generated, strongly typed C# client. Both reside in the monorepo under `backend/` and `frontend/`, independently buildable and testable. All client–server communication goes through the schema defined in `contracts/schema.graphql`.

## Complexity Tracking

> No constitution violations requiring justification.

---

# Plan: US2 — Deterministic Scenario Extraction

**Date**: 2026-05-13 | **Spec**: [spec.md §US2](spec.md) | **Research**: [research.md §R-US2](research.md)

---

## Summary

US2 adds a deterministic scenario extraction capability to BirkNext. Users paste specification text into a dedicated extraction view. A client-side extraction pipeline — a pure, stateless C# service running in Blazor WASM — applies deterministic rules to the pasted text, identifies candidate scenarios, classifies each candidate as REQUIREMENT, TEST, or NEEDS_CLARIFICATION, and presents the full result set for user review. No data is transmitted to the server or persisted until the user explicitly selects candidates and confirms a save action. Saving calls an additive batch mutation that extends the US1 GraphQL schema. Raw pasted text never reaches the server.

The extraction pipeline is designed as a replaceable component: in a future version, an AI-assisted classifier can implement the same interface contract with no changes to the review workflow, the save path, or the frontend component tree.

---

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 — consistent with US1 across both tiers  
**Extraction runtime**: Blazor WebAssembly (client-side, no server round-trip for extraction)  
**Save path**: HotChocolate 14 batch mutation (additive extension to US1 schema)  
**Storage**: PostgreSQL 16 via EF Core 8 — no schema changes; candidates become Scenarios via the existing data model  
**Logging**: Serilog (server-side events); client-side event strategy defined in §Observability  
**Target input size**: Up to 10,000 characters for v1 performance target; hard cap at 50,000 characters  
**Performance goal**: Extraction pipeline completes in ≤ 200 ms for 10,000-character input; first candidate displayed within 2 seconds of extraction trigger  
**Constraints**: No file upload; no AI/ML in extraction or classification; no automatic persistence; no draft state between sessions

---

## Constitution Check

| Principle | Status | Evidence / Action Required |
|---|---|---|
| **I. Test-First Development** | ✅ PASS | Spec §US2 defines 6 acceptance scenarios. The extraction pipeline is a pure function (text in → candidates out) with no side effects, making it exhaustively unit-testable. The review-before-save boundary and the batch save path are independently testable. |
| **II. Observability** | ✅ PASS | Five structured log events are defined in §Observability Integration. Extraction metadata (counts, durations) is instrumented at every pipeline boundary. The batch mutation emits a `CandidateReviewSaved` event consistent with US1's `ScenarioCreated` pattern. |
| **III. Security-First** | ✅ PASS | Pasted text is treated as hostile at entry. Client-side plain-text rendering prevents XSS. The server never receives raw pasted content. The batch mutation inherits US1's auth assumptions and HotChocolate input validation. ReDoS mitigated by input cap and anchored pattern rules. |
| **Development Standards** | ✅ PASS | Extraction service is independently testable without the backend. The batch mutation is additive — no breaking changes to the existing schema. Frontend components contain no business logic; the extraction service is a separate injectable class. |
| **Quality Gates** | ✅ PASS | Extraction service: unit-testable. Batch mutation: integration-testable against a real DB (Testcontainers). Schema snapshot tests cover the additive mutation. Observability instrumentation verified for each new event boundary. |

**Complexity justification (two decisions require tracking):**

| Decision | Complexity added | Justification |
|---|---|---|
| Client-side extraction (no server round-trip) | Extraction logic lives in WASM, not on a centralized server | Keeps untrusted text out of the server; eliminates network latency from the extraction critical path; makes the extractor independently testable as a pure function |
| Batch mutation (Approach B over N × createScenario) | New mutation added to the schema | A single call enables coherent observability (one `CandidateReviewSaved` event), atomic error reporting, and avoids N concurrent mutation race conditions in the client |

---

## Extraction Pipeline Architecture

The pipeline is a single-pass, deterministic transform: same input always produces the same output. It runs entirely in the Blazor WASM process. It has no network dependency, no database dependency, and no randomness. All stages complete synchronously in v1.

### Pipeline Stages

**Stage 1 — Input Validation Gate**  
Runs before any text processing. Checks: input is non-empty; input length does not exceed the hard cap (50,000 characters). On failure, surfaces an inline error and halts. No downstream stage runs on invalid input. This is the only stage that produces user-visible validation errors.

**Stage 2 — Normalization**  
Normalizes `\r\n` to `\n`. Strips a UTF-8 BOM if present. Does not alter content beyond line-ending consistency. Output is a single normalized string.

**Stage 3 — Block Partitioning**  
Splits the normalized input into structural blocks using a token-based approach (Research §R-US2-1, Option B). Recognized block types: heading, list item (unordered and ordered), fenced code block, blockquote, table row, horizontal rule, paragraph line, and empty. This stage does not apply markdown AST parsing; it uses sequential line inspection with look-ahead for multi-line constructs (fenced code blocks, table bodies). Output is an ordered sequence of typed blocks.

**Stage 4 — Structure Filter**  
Discards blocks that are never extraction candidates: headings, fenced code blocks, blockquotes, horizontal rules, HTML comment blocks, YAML/TOML front matter, and empty blocks. Retains: list items (all depths), paragraph lines, and qualifying table body rows. Output is a filtered sequence of candidate-eligible blocks.

**Stage 5 — Content Extraction**  
For each retained block, strips markdown syntax to produce clean candidate text: removes list markers (`-`, `*`, `+`, `N.`), strips inline code backticks (retaining the inner text), strips link syntax (retaining display text), and strips image syntax entirely. Applies a minimum content length check — blocks whose stripped text falls below a threshold (exact value: Phase 1 calibration) are discarded. Output is an ordered sequence of plain-text candidate strings with their source block type preserved as context.

**Stage 6 — Classification**  
Applies classification heuristics to each candidate string in strict priority order:

1. **TEST** — BDD triple detected (Given / When / Then on the same line or line starts with a BDD opener). Near-zero false-positive rate.
2. **REQUIREMENT** — RFC 2119 uppercase modal verb detected (MUST, SHALL, SHOULD, MAY, MUST NOT, SHALL NOT) or a functional requirement prefix (FR-NNN pattern).
3. **NEEDS_CLARIFICATION** — line ends with `?`, or contains a deferral marker (TBD, TODO, TBC, open question), or matched no signal in steps 1–2.
4. **Default** — NEEDS_CLARIFICATION. No candidate is left unclassified; the safer fallback over REQUIREMENT.

When signals from two priority levels are both detected on the same line, the higher-priority classification wins. The classification rationale (which signal matched) is retained as internal metadata but is not surfaced to the user in v1.

**Stage 7 — Deduplication**  
Removes candidates whose normalized text (trimmed, case-folded) is an exact match to a prior candidate in the same extraction run. Preserves the first occurrence; discards subsequent duplicates.

**Stage 8 — Result Assembly**  
Packages the deduplicated, classified candidates into an ordered list of `ExtractionCandidate` records. Each record carries: extracted title text, classification, and the nearest preceding heading text as document context (to mitigate the context-loss UX risk identified in Research §R-US2-6). Output is the extraction result delivered to the frontend component.

---

## Component Responsibilities

### Frontend (Blazor WASM)

**`ScenarioExtractionService`** (injectable C# class, no Blazor dependency)
- Owns the complete extraction pipeline (Stages 1–8).
- Public contract: accepts a raw string, returns an `ExtractionPipelineResult` (list of `ExtractionCandidate` plus a pipeline status: `Success`, `EmptyInput`, `InputTooLarge`, `NoResults`).
- No network calls. No DI dependencies beyond an `IExtractionConfiguration` interface for the length cap and minimum line length (allows testing with non-default values).
- This is the extensibility seam: a future AI-assisted classifier replaces this class while the interface contract remains stable.

**`ExtractionInput.razor`** (component)
- Renders the text area and the extract trigger button.
- Passes the raw pasted string to `ScenarioExtractionService` on trigger.
- Surfaces Stage 1 validation errors inline at the text area.
- Emits the `ExtractionTriggered` and `ExtractionCompleted` (or `ExtractionEmpty`) log events.

**`ExtractionReviewList.razor`** (component)
- Renders the extracted candidate list, grouped by classification: REQUIREMENT group, then TEST group, then NEEDS_CLARIFICATION group.
- Uses opt-in selection (nothing pre-selected by default) — FR-US2-006 alignment.
- Maintains user selection state in component memory only; no persistence.
- Surfaces the count summary header: "N candidates extracted — X REQUIREMENT, Y TEST, Z NEEDS_CLARIFICATION."
- Provides a confirm-save action that collects selected candidates and delegates to the GraphQL save path.
- Emits `CandidateReviewAbandoned` if the component is disposed with unsaved selected candidates.

**`ExtractionCandidateRow.razor`** (component)
- Renders a single candidate: classification badge, candidate title (plain text, never markup), document context heading, and selection checkbox.
- Renders candidate title with `@candidate.Title` (string interpolation into text node), not `@((MarkupString)candidate.Title)`.

**`ScenarioExtraction.razor`** (page)
- Hosts `ExtractionInput` and `ExtractionReviewList`.
- Coordinates state between the two: extraction result from `ExtractionInput` flows into `ExtractionReviewList`.
- No business logic; orchestrates component communication only.

### Backend (ASP.NET Core / HotChocolate)

**`Mutation.cs`** (extended from US1)
- Adds a `createScenarios` batch mutation resolver.
- Accepts an array of `CreateScenarioInput` (the same input type used by US1's single-scenario mutation; no new input type needed if the array element shape is unchanged).
- Returns an array of mutation result items — either a created `Scenario` or a per-item validation error.
- Emits the `CandidateReviewSaved` structured log event after processing.
- Applies the same server-side validation as US1's `createScenario` (title non-empty, type is valid enum value).

**No new database entities, no new EF Core migrations.** Saved candidates become `Scenario` records via the existing data model.

### Extraction Service Interface (extensibility seam)

```text
IScenarioExtractionService
  ExtractionPipelineResult Extract(string rawInput)

ExtractionPipelineResult
  Status: Success | EmptyInput | InputTooLarge | NoResults
  Candidates: IReadOnlyList<ExtractionCandidate>

ExtractionCandidate
  Title: string
  Classification: ScenarioKind (Requirement | Test | NeedsClarification)
  ContextHeading: string?
```

The interface maps `Classification` to the existing `ScenarioKind` enum from US1 (`Requirement` / `Test` / `NeedsClarification`). No new domain type is introduced. The REQUIREMENT / TEST / NEEDS_CLARIFICATION labels in the UI are display names for the existing enum values.

---

## Review-Before-Save Workflow Boundaries

The extraction and save steps are explicitly separated at the component boundary. They share no implicit state channel.

```
[User pastes text]
       │
       ▼
[ExtractionInput] ──triggers──▶ [ScenarioExtractionService.Extract()]
                                          │
                              ExtractionPipelineResult
                                          │
       ┌───────────────────────────────────┘
       ▼
[ExtractionReviewList] ─── holds candidate list in component state ───
       │
  User selects candidates
       │
  User confirms save
       │
       ▼
[GraphQL: createScenarios mutation] ──▶ [Backend] ──▶ [PostgreSQL]
```

**Hard boundaries:**
- `ScenarioExtractionService` never writes to any store. It returns a value; the component decides what to do with it.
- `ExtractionReviewList` never persists without an explicit user confirm action.
- The page has no auto-save, no debounce-save, and no navigation-guard-save.
- If the user navigates away from `ScenarioExtraction.razor`, the extraction result is discarded silently. No warning prompt in v1 (navigating away while candidates are selected is a v2 concern).

---

## GraphQL Integration Strategy

**Additive schema extension** — US1 schema is not modified. One new mutation is added.

**Batch mutation direction**: `createScenarios(input: [CreateScenarioInput!]!): CreateScenariosPayload`

- Input element type reuses the existing `CreateScenarioInput` from US1. If the US1 input type requires a change to support batch semantics (e.g., an optional idempotency key), that change is backward-compatible.
- Payload carries a results array: each element is either a successfully created `Scenario` or a `UserError` with field-level detail.
- Partial success is supported: some candidates may fail validation while others succeed. The frontend surfaces per-candidate error state in the review list.
- Exact mutation name, payload type shape, and error model are deferred to Phase 1 schema design.

**Existing mutations and queries unchanged.** The `scenarios` query and `createScenario` mutation from US1 are not modified. The `Scenario` object type is not modified.

**Schema contract**: The batch mutation signature must be documented in `contracts/schema.graphql` before implementation begins (Development Standards gate).

---

## Validation Boundaries

| Boundary | What is validated | Where |
|---|---|---|
| Input Validation Gate (Stage 1) | Empty input; input exceeds length cap | Client — `ScenarioExtractionService` |
| Content Extraction (Stage 5) | Minimum candidate text length after stripping | Client — pipeline internal |
| Save submission | Title non-empty; type is a valid enum value | Server — HotChocolate input type validation (FR-002, FR-003 inherited from US1) |

**What is not validated:**
- Classification accuracy. The heuristic assigns a classification; the user reviews it; the server accepts whatever type is submitted. Mis-classification is a UX concern, not a validation concern.
- Candidate uniqueness against existing scenarios. Duplicate detection (same title already in the scenario list) is deferred to a future version.

**Client validation does not replace server validation.** The server validates every candidate at the API boundary regardless of client-side state.

---

## Error Handling Strategy

### Extraction errors (client-side, no server involved)

| Condition | User-facing response | Component state |
|---|---|---|
| Empty input | Inline message at text area; extract button disabled | No result list rendered |
| Input over length cap | Inline error at text area; extraction halted | No result list rendered |
| No candidates found | Empty-state message in review area (FR-US2-008) | Empty result list rendered |
| Pipeline internal failure | Generic error message; exception logged client-side | No result list rendered |

### Save errors (server-side, after user confirms)

| Condition | User-facing response | Component state |
|---|---|---|
| Per-candidate validation failure | Error indicator on the failing candidate row in the review list | Failed candidates remain in the list; successful candidates are marked saved |
| Backend unavailable | Retry-able error banner (consistent with US1 error handling) | Review list preserved; user can retry |
| Partial batch failure | Per-item result surfaced in the review list | Failed candidates remain selectable; user can retry only failed items |
| Complete batch failure | Error banner; review list preserved | Review list intact; no candidates lost |

**No silent data loss on any error path.** The review list remains intact and actionable until the user explicitly navigates away or the session ends.

---

## Observability Integration Strategy

**Structured log events** (Serilog, consistent with US1 event schema):

| Event | Tier | Key fields | Constraint |
|---|---|---|---|
| `ExtractionTriggered` | Client | `inputLengthChars`, `inputLineCount`, `sessionId` | No raw text |
| `ExtractionCompleted` | Client | `candidateCount`, `requirementCount`, `testCount`, `needsClarificationCount`, `durationMs` | Aggregates only |
| `ExtractionEmpty` | Client | `inputLengthChars`, `reason` (`empty_input` / `no_candidates_found` / `input_too_large`) | No raw text |
| `CandidateReviewSaved` | Server | `selectedCount`, `totalExtracted`, `scenariosCreated`, `failedCount`, `durationMs`, `projectId`, `correlationId` | Consistent with `ScenarioCreated` pattern |
| `CandidateReviewAbandoned` | Client | `totalExtracted`, `selectedCount` | Count only |

**Client-side event shipping strategy (Phase 1 decision):**  
Three options exist for getting client-side events into the server-side structured log:
- **Option A**: A dedicated lightweight telemetry endpoint (`POST /telemetry/events`) that accepts structured event payloads and writes to Serilog server-side.
- **Option B**: Browser console logging only for client events in v1; deferred to a future observability story.
- **Option C**: Piggyback client events onto the correlation ID of the save mutation — the batch mutation resolver logs both the save outcome and the preceding client-side extraction metadata passed as mutation input metadata.

The choice between these options is deferred to Phase 1. Option A is the most complete but adds a new endpoint (security review required). Option B is the lowest-cost v1 choice if server-side correlation of extraction events is not a launch requirement.

**Acceptance rate metric:**  
`selectedCount / totalExtracted` per session is the primary proxy for extraction quality. A value consistently below 0.3 indicates high false-positive extraction and should trigger a heuristic review.

---

## Security Strategy

### Input handling pipeline

```
Pasted text enters text area
       │ (treated as hostile)
       ▼
Stage 1: Length cap enforced before any parsing
       │
Stage 2: Line ending normalization (no content change)
       │
Stages 3–7: Extraction and classification (no rendering)
       │
Stage 8: Plain-text candidate records assembled
       │
UI: Blazor renders candidate.Title as a text node (never MarkupString)
```

**XSS prevention**: The critical control is at the rendering layer. `ExtractionCandidateRow.razor` must bind candidate title to a text interpolation (e.g., `@candidate.Title` inside a `<span>`), never to `@((MarkupString)candidate.Title)`. This renders HTML entities as literal characters. This constraint must be carried into implementation and verified in code review.

**ReDoS prevention**: All regex patterns in the classification stage use simple, anchored patterns. No nested quantifiers. A per-line length sub-cap (to be calibrated in Phase 1, suggested: 2,000 characters per line) is enforced before any pattern with quantifiers runs. Lines above the sub-cap are classified as NEEDS_CLARIFICATION without pattern matching.

**Server boundary**: The server receives only `CreateScenarioInput` values — typed, validated objects. Raw pasted text never crosses the network boundary. This is the primary security simplification of the client-side extraction architecture.

**No new auth surface**: The batch mutation endpoint is behind the same authentication as US1 mutations. No new auth middleware, no new permission scope.

---

## Performance Strategy

### V1 target

| Metric | Target | Measurement point |
|---|---|---|
| Pipeline execution (Stages 1–7) | ≤ 200 ms | `ExtractionCompleted.durationMs` |
| Time to first candidate displayed | ≤ 2 seconds | From user trigger to first rendered row |
| Maximum input size | 50,000 chars hard cap | Stage 1 enforcement |
| V1 comfortable input size | ≤ 10,000 chars | Spec performance expectation |

### Rendering bottleneck mitigation

For inputs that produce more than 100 candidates, synchronous rendering of the full review list may cause a perceptible UI freeze in Blazor WASM. The following strategy is applied in v1:

1. Display the count summary header immediately after extraction completes (zero render cost).
2. Render the candidate list in the next render cycle, allowing the browser to paint the summary first.
3. If candidate count exceeds 100 (Phase 1 calibration target), display an inline notice ("Large extraction — scroll to review all N candidates") before the list.

Virtual scrolling and progressive rendering are deferred enhancements. They are not required for the v1 10,000-character performance target but are explicitly identified as the first performance scalability levers.

### Classification performance

Classification runs O(N candidates × M patterns). For v1, M (the number of classification patterns) is expected to remain below 20. At 10,000 characters with dense bullets (~100–150 candidates), total classification time is negligible relative to UI rendering cost.

---

## Extensibility Boundaries

### Stable interface (must not change without a versioned migration)

- `IScenarioExtractionService` — the extraction service contract
- `ExtractionCandidate` — the candidate record type (title, classification, context heading)
- `ExtractionPipelineResult` — the pipeline output type (status + candidate list)
- The three classification values map 1:1 to the existing `ScenarioKind` enum — no new domain type introduced

### AI integration seam

The pipeline's Stage 6 (Classification) is the intended AI integration point. Extraction (Stages 3–5, finding candidate text) is likely to remain deterministic even in a future version. Classification (assigning REQUIREMENT / TEST / NEEDS_CLARIFICATION) is where probabilistic models add the most value. 

A future `AiScenarioExtractionService` implements `IScenarioExtractionService`. Stages 1–5 and 7–8 remain unchanged. The review-before-save workflow, the UI, and the save path are entirely unaffected. The AI version surfaces the same `ExtractionCandidate` type; it may additionally populate the `confidence` field (reserved, not surfaced in v1 UI) to allow the review UI to sort or highlight low-confidence candidates in a future iteration.

### Explicitly not extensible in v1

- Classification rules are hardcoded in the extraction service; no external configuration or plugin mechanism.
- The review workflow is fixed at opt-in selection; no user-configurable default selection mode.
- No custom extraction rule authoring by users.

---

## Project Structure (US2 additions)

### Documentation

```text
specs/001-create-scenario/
├── plan.md              # This file (updated)
├── research.md          # Updated with R-US2 sections
└── spec.md              # Updated with US2 feature section
```

### Source Code (additions to existing structure)

```text
frontend/BirkNext.Web/
├── Pages/
│   └── ScenarioExtraction.razor         # Extraction view page
├── Components/
│   ├── ExtractionInput.razor            # Text area + trigger button
│   ├── ExtractionReviewList.razor       # Candidate review list, grouped by classification
│   └── ExtractionCandidateRow.razor     # Single candidate row with checkbox + context
├── Services/
│   └── ScenarioExtractionService.cs     # Pure extraction pipeline (Stages 1–8)
└── Models/
    └── ExtractionCandidate.cs           # Client-side candidate record type

frontend/BirkNext.Web.Tests/
└── Services/
    └── ScenarioExtractionServiceTests.cs  # Unit tests for extraction pipeline

backend/BirkNext.Api/
└── GraphQL/
    └── Mutation.cs                      # Extended with createScenarios batch resolver

backend/BirkNext.Api.Tests/
└── Integration/
    └── ScenariosBatchMutationTests.cs   # Batch mutation → real DB round trip
```

**No new database migration.** The `Scenario` entity and `ScenarioKind` enum from US1 are unchanged. Extracted candidates that the user saves become `Scenario` records via the existing persistence path.

**Schema contract extension**: The `createScenarios` batch mutation signature is added to `contracts/schema.graphql` in Phase 1 before implementation begins.

---

## Implementation Sequencing

No implementation tasks are generated here. The following phases sequence the work at planning granularity.

**Phase 1 — Design (before any code)**
- Calibrate Stage 5 minimum line length and Stage 6 per-line sub-cap against representative spec documents.
- Define the `createScenarios` batch mutation signature in `contracts/schema.graphql`.
- Decide on the client-side event shipping strategy (Options A, B, or C from §Observability).
- Resolve open questions R-US2-10 items 3, 4, 5, 6, 7 from research.md.

**Phase 2 — Extraction Service**
- Implement `ScenarioExtractionService` as a pure class with unit tests covering all pipeline stages and edge cases from Research §R-US2-5.
- No Blazor dependency; runs and tests independently of the frontend component tree.

**Phase 3 — Frontend Components**
- Implement `ExtractionCandidateRow`, `ExtractionReviewList`, `ExtractionInput`, and `ScenarioExtraction` page.
- Wire `ScenarioExtractionService` via DI.
- Component tests via bUnit with mocked extraction service.

**Phase 4 — Backend Batch Mutation**
- Implement `createScenarios` resolver and integration tests against a real Testcontainers PostgreSQL instance.
- Emit `CandidateReviewSaved` structured log event.

**Phase 5 — Observability and Security Validation**
- Instrument client-side events per the chosen shipping strategy.
- Security review of the rendering path (XSS constraint verification).
- Performance measurement at 10,000-character input.

---

# Plan: US3 — Deterministic Rule Engine for Scenario Extraction

**Date**: 2026-05-21 | **Spec**: [spec.md §US3](spec.md) | **Research**: [research.md §R-US3](research.md)

---

## Summary

US3 replaces the hardcoded extraction heuristics in `ScenarioExtractionService` with a structured, injectable rule engine. Two pipeline stages change internally: Stage 4 (Structure Filter) and Stage 6 (Classification). All other stages — input validation, normalization, block partitioning, content extraction, deduplication, and result assembly — are untouched. The public interface (`IScenarioExtractionService`), the output model (`ExtractionPipelineResult`, `ExtractionCandidate`), the review-before-save workflow, and the GraphQL contract are all unchanged. A new `IExtractionRuleEngine` interface is introduced as the rule engine seam; `ScenarioExtractionService` delegates to it rather than executing inline logic.

The default rule set — `ExtractionRuleSet.Default()` — encodes exactly the rules that the current US2 pipeline enforces, expressed as named, weighted, testable rule objects. All US2 acceptance criteria and all US2 unit tests must pass without modification after US3 is complete.

---

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 — consistent with US1 and US2  
**Rule engine runtime**: Blazor WebAssembly (client-side only — no server change)  
**New abstractions**: `IExtractionRuleEngine`, `ExtractionRuleSet`, `FilterRule`, `ClassificationRule`, `RuleEvaluationResult`  
**Changed components**: `ScenarioExtractionService` (Stages 4 and 6 delegated); DI composition root (new registrations)  
**Unchanged components**: All US1 and US2 backend code; GraphQL schema; `IScenarioExtractionService`; all review UI components; save path  
**Testing**: Existing `ScenarioExtractionServiceTests.cs` and `ExtractionAcceptanceCriteriaTests.cs` are the regression gate; new rule engine unit tests are additive  
**Performance goal**: End-to-end extraction ≤ 200 ms for 10,000-character input — inherited from US2; rule engine overhead must not meaningfully contribute to this budget  
**Constraints**: No AI, no ML, no data files, no external services; rule definitions live in code only for US3

---

## Constitution Check

| Principle | Status | Evidence / Action Required |
|---|---|---|
| **I. Test-First Development** | ✅ PASS | The existing US2 test suite (111 tests) is the regression safety net. New tests for the rule engine itself follow the same Test-First order: failing test before implementation. No US2 test may be modified to pass; any test that breaks is a regression, not a test update. |
| **II. Observability** | ✅ PASS | Rule execution adds one new field (`rulesEvaluatedCount`) to the existing `ExtractionCompleted` log event. The `ClassificationSignal` field on `ExtractionCandidate` already records which rule won; no new model fields are required. The rule engine carries no raw text in any log output. |
| **III. Security-First** | ✅ PASS | Regex patterns in rule definitions are compiled at rule construction time, subject to the existing 2,000-character per-line sub-cap, and governed by the existing ReDoS mitigation policy. Rule definitions are developer-authored code only — never user-authored. No new server surface is introduced. |
| **Development Standards** | ✅ PASS | `IExtractionRuleEngine` is an injectable interface defined before any implementation. All rules in `ExtractionRuleSet.Default()` are individually testable. No business logic is added to Blazor components. The GraphQL schema contract is not modified. |
| **Quality Gates** | ✅ PASS | All existing US2 tests pass without modification (regression gate). New rule engine unit tests added. Startup validation added (fail-fast on incoherent rule set). Performance measurement confirms ≤ 200 ms budget preserved. |

**Complexity justification:**

| Decision | Complexity added | Justification |
|---|---|---|
| `IExtractionRuleEngine` as a new injectable seam | New interface + implementation + DI registration | The seam is the mechanism that makes Stage 4 and Stage 6 logic independently testable and extensible without re-opening `ScenarioExtractionService` for every rule change |
| `ExtractionRuleSet.Default()` as the sole rule source | Single static factory that assembles all default rules | Keeps all rule definitions in one auditable location; guarantees the default set is always coherent |
| Startup validation of rule set | Constructor-time validation of rule coherence | Converts a class of silent runtime misbehaviour (missing Default rule, duplicate names, invalid regex) into fast, descriptive failures at application startup |

---

## Architecture: Rule Engine

### Rule Categories

Two first-class rule categories are defined. Each category maps to exactly one pipeline stage.

**`FilterRule`** — maps to Stage 4 (Structure Filter)  
Determines whether a `TextBlock` is candidate-eligible. A matching filter rule causes the block to be discarded before any classification is attempted. The filter short-circuit is an engine invariant: once a `FilterRule` matches, no `ClassificationRule` is evaluated for that block.

**`ClassificationRule`** — maps to Stage 6 (Classification)  
Assigns a `ScenarioKind` and records a `ClassificationSignal` for a candidate that has passed all filter rules. Classification rules are evaluated against the stripped candidate text produced by Stage 5, not against the raw block text.

Section-aware context tracking (the `PrecedingHeading` → `ContextHeading` flow) is not expressed as a rule category in US3. It remains an implicit accumulator maintained by Stage 3 Block Partitioning and carried through to Stage 8 Result Assembly — identical to the current US2 implementation. A `ContextRule` abstraction is noted as a future extensibility item and is explicitly deferred.

### Rule Structure

**`FilterRule`**

| Field | Type | Description |
|---|---|---|
| `Name` | `string` | Unique identifier; used in log output and diagnostics. Convention: `Filter:<BlockTypeName>` (e.g., `Filter:Heading`). |
| `Priority` | `int` | Evaluation order within the filter pass. Higher value = evaluated first. Tie on priority: first-registered rule wins. |
| `Condition` | `FilterCondition` | The match predicate. For US3, the two supported condition types are: `BlockTypeMatch` (matches a specific `BlockType`) and `ContentLengthBelow` (matches when stripped text length is below a threshold). |

**`ClassificationRule`**

| Field | Type | Description |
|---|---|---|
| `Name` | `string` | Unique identifier. Convention: `Classify:<SignalName>` (e.g., `Classify:Rfc2119Uppercase`). |
| `Priority` | `int` | Conflict-resolution weight. Higher value = wins over lower-priority matching rules. The unconditional Default rule carries priority 0. |
| `ApplicableBlockTypes` | `BlockType[]?` | Optional scope constraint. If null, the rule applies to all candidate-eligible block types. If set, the rule is evaluated only for blocks of the listed types. |
| `Condition` | `ClassificationCondition` | The match predicate. For US3, the two supported condition types are: `PatternMatch` (compiled regex applied to stripped text) and `Unconditional` (always fires; used only by the Default rule). |
| `Outcome` | `(ScenarioKind, ClassificationSignal)` | The classification assigned when this rule wins. |

### Rule Priority Weights (Default Rule Set)

Priority weights are spaced by 10, leaving room to insert new rules between existing ones without renumbering.

| Rule Name | Category | Priority | Outcome |
|---|---|---|---|
| `Classify:BddPattern` | ClassificationRule | 70 | TEST / BddPattern |
| `Classify:Rfc2119Uppercase` | ClassificationRule | 60 | REQUIREMENT / Rfc2119Uppercase |
| `Classify:Rfc2119Lowercase` | ClassificationRule | 50 | REQUIREMENT / Rfc2119Lowercase |
| `Classify:FrPrefix` | ClassificationRule | 40 | REQUIREMENT / FrPrefix |
| `Classify:QuestionTerminator` | ClassificationRule | 30 | NEEDS_CLARIFICATION / QuestionTerminator |
| `Classify:DeferralMarker` | ClassificationRule | 20 | NEEDS_CLARIFICATION / DeferralMarker |
| `Classify:Default` | ClassificationRule | 0 | NEEDS_CLARIFICATION / Default |

Filter rules use priority 100 for all `BlockTypeMatch` entries (same priority, applied in registration order) and priority 90 for the `ContentLengthBelow` entry. Filter rule priorities are relative only within the filter pass; they are never compared against classification rule priorities.

### `IExtractionRuleEngine` Interface

The rule engine is expressed as an injectable interface. `ScenarioExtractionService` depends on this interface, not on any concrete rule set.

```text
IExtractionRuleEngine
  RuleEvaluationResult Evaluate(TextBlock block, string strippedText)
  IReadOnlyList<string> RuleNames { get; }   // all registered rule names, for startup diagnostics
```

`RuleEvaluationResult`:

| Field | Type | Description |
|---|---|---|
| `IsFiltered` | `bool` | True if a FilterRule matched; the block is not a candidate. |
| `Classification` | `ScenarioKind?` | Null when `IsFiltered` is true; populated by the winning ClassificationRule otherwise. |
| `Signal` | `ClassificationSignal?` | Null when `IsFiltered` is true; the winning rule's signal otherwise. |
| `WinningRuleName` | `string?` | Null when `IsFiltered` is true; the name of the winning ClassificationRule otherwise. Logged in `ExtractionCompleted` event (aggregated, not per-candidate). |
| `EvaluatedRuleCount` | `int` | Total number of rules evaluated for this block (filter + classification). Summed across all candidates to produce `rulesEvaluatedCount` in the `ExtractionCompleted` log event. |

### `ExtractionRuleSet`

A value object that holds the ordered, validated rule lists. Constructed once at application startup; immutable after construction.

```text
ExtractionRuleSet
  IReadOnlyList<FilterRule> FilterRules          // ordered by Priority descending
  IReadOnlyList<ClassificationRule> ClassificationRules  // ordered by Priority descending; Default rule always last
  static ExtractionRuleSet Default()             // produces the rule set replicating exact US2 behaviour
```

`ExtractionRuleSet.Default()` is the single authoritative source of all extraction rules for US3. It encodes all nine filter rules and all seven classification rules from the US2 pipeline.

### Startup Validation

The `ExtractionRuleEngine` constructor validates the provided `ExtractionRuleSet` before the application serves any requests. Validation failures are thrown as exceptions at startup, not swallowed silently at runtime.

**Validation checks:**
1. The rule set contains at least one `ClassificationRule`.
2. Exactly one `ClassificationRule` with `Condition == Unconditional` exists (the Default rule). This guarantees every candidate-eligible block produces a classification.
3. No two rules share the same `Name` across both filter and classification lists (names are globally unique within a rule set).
4. All `PatternMatch` conditions compile to a valid `Regex` without throwing. Compilation happens at validation time, and the compiled `Regex` instances are cached on the rules.
5. No `ClassificationRule` has a `Priority` of 0 except the Default rule. This reserves 0 exclusively for the unconditional fallback.

---

## Rule Evaluation Workflow

The following sequence describes how the rule engine evaluates a single `TextBlock`:

```
TextBlock (from Stage 3 Block Partitioning)
       │
       ├── FilterRule pass (all FilterRules, in Priority order, highest first)
       │       │
       │       ├── FilterRule matches?  ──YES──▶ RuleEvaluationResult { IsFiltered = true }
       │       │                                  (no ClassificationRules evaluated)
       │       │
       │       └── No FilterRule matches
       │
Stage 5 Content Extraction (caller, not rule engine — strips markdown syntax)
       │
Stripped candidate text
       │
       ├── ClassificationRule pass (all ClassificationRules evaluated; results collected)
       │       │
       │       ├── Rule has ApplicableBlockTypes set?
       │       │       ├── Block type in set? ──NO──▶ skip rule
       │       │       └── YES ──▶ evaluate condition
       │       │
       │       ├── Condition matches? ──YES──▶ add to matched rules list
       │       └── Condition does not match ──▶ continue
       │
       ├── Select winning rule from matched rules list
       │       ├── Highest Priority wins
       │       └── Priority tie: first-registered rule wins (stable, deterministic)
       │
       └── RuleEvaluationResult { IsFiltered = false, Classification, Signal, WinningRuleName, EvaluatedRuleCount }
```

**Invariants enforced by the engine (not by callers):**
- The Default rule (`Unconditional` condition, priority 0) always appears in the matched rules list for any block that passed the filter pass. This guarantees `Classification` is never null for a candidate-eligible block.
- `EvaluatedRuleCount` is always the count of rules evaluated, including the Default rule. This ensures the count is non-zero for every non-filtered block.
- Filter short-circuit: the classification pass never runs for a filtered block. This is not a performance optimisation — it is a semantic invariant. A block that is filtered does not have a classification.

---

## Integration with `ScenarioExtractionService`

`ScenarioExtractionService` is the only caller of `IExtractionRuleEngine`. The integration is confined to Stage 4 and Stage 6; all other stages are untouched.

**Stage 4 — Structure Filter (changed)**

Current behaviour: inline `switch` or `if-else` over `block.BlockType`.

New behaviour: call `_ruleEngine.Evaluate(block, strippedText: string.Empty)` for filter determination only. If `result.IsFiltered`, discard the block and continue. Because filter evaluation does not require stripped text (filter conditions match on block structure, not content), an empty string is passed for the `strippedText` parameter during the filter pass. The engine's filter rules never inspect `strippedText`.

**Stage 6 — Classification (changed)**

Current behaviour: inline priority-ordered signal detection against stripped candidate text.

New behaviour: after Stage 5 has produced stripped candidate text, call `_ruleEngine.Evaluate(block, strippedText)` with the full stripped text. Use `result.Classification` and `result.Signal` to populate `ExtractionCandidate.Classification` and `ExtractionCandidate.ClassificationSignal`. Accumulate `result.EvaluatedRuleCount` across all candidates for the `ExtractionCompleted` log event.

**Dependency injection:**

`ScenarioExtractionService` receives `IExtractionRuleEngine` via constructor injection alongside the existing `IExtractionConfiguration` dependency. The concrete `ExtractionRuleEngine` is registered as a singleton in the Blazor WASM composition root (`Program.cs`), constructed with `ExtractionRuleSet.Default()`.

```text
Services.AddSingleton<IExtractionRuleEngine>(
    new ExtractionRuleEngine(ExtractionRuleSet.Default()));
```

For tests that need a custom rule set, the test host constructs an `ExtractionRuleEngine` with an explicit rule set and registers it directly — the same DI pattern already used in `ExtractionAcceptanceCriteriaTests.cs` for mocking `IScenarioExtractionService`.

---

## Separation of Concerns

The following table is the authoritative statement of which component owns each responsibility after US3.

| Responsibility | Owner after US3 | Owner in US2 |
|---|---|---|
| Input validation (empty / too large) | `ScenarioExtractionService` Stage 1 | `ScenarioExtractionService` Stage 1 (unchanged) |
| Line-ending normalization | `ScenarioExtractionService` Stage 2 | Unchanged |
| Block partitioning and `PrecedingHeading` tracking | `ScenarioExtractionService` Stage 3 | Unchanged |
| Block-type filter (which blocks are candidates) | **`IExtractionRuleEngine`** via FilterRules | `ScenarioExtractionService` Stage 4 (hardcoded) |
| Markdown stripping (list markers, backticks, links) | `ScenarioExtractionService` Stage 5 | Unchanged |
| Minimum content length check | `ScenarioExtractionService` Stage 5 (inline check, not a FilterRule) | Unchanged |
| Classification (REQUIREMENT / TEST / NEEDS_CLARIFICATION) | **`IExtractionRuleEngine`** via ClassificationRules | `ScenarioExtractionService` Stage 6 (hardcoded) |
| Deduplication | `ScenarioExtractionService` Stage 7 | Unchanged |
| Result assembly (`ExtractionCandidate` construction) | `ScenarioExtractionService` Stage 8 | Unchanged |
| Rule set definition | `ExtractionRuleSet.Default()` | Embedded in Stage 4 and Stage 6 code |
| Rule coherence validation | `ExtractionRuleEngine` constructor | None (no validation existed) |
| Observability (log events, counts) | `ScenarioExtractionService` + `IExtractionRuleEngine.EvaluatedRuleCount` | `ScenarioExtractionService` |
| UI rendering (plain-text safety) | `ExtractionCandidateRow.razor` | Unchanged |
| Save path (GraphQL mutation) | Backend `Mutation.cs` | Unchanged |

**Key invariant**: `IExtractionRuleEngine` is a pure function. `Evaluate(block, strippedText)` depends only on its arguments and the compiled rule set. It reads no shared mutable state, modifies no shared mutable state, and produces no side effects. This invariant is what makes individual rules independently testable and guarantees deterministic behaviour across runs.

---

## Observability Integration Strategy

US3 adds one new observable dimension — rule evaluation counts — without changing the existing event schema.

**Changes to existing log events:**

| Event | Change | New field |
|---|---|---|
| `ExtractionCompleted` | Add one field | `rulesEvaluatedCount: int` — sum of `RuleEvaluationResult.EvaluatedRuleCount` across all candidates in the extraction run |

All other event fields from US2 (`candidateCount`, `requirementCount`, `testCount`, `needsClarificationCount`, `durationMs`) are unchanged.

**No new log events** are introduced by US3. The existing five events from US2 (`ExtractionTriggered`, `ExtractionCompleted`, `ExtractionEmpty`, `CandidateReviewSaved`, `CandidateReviewAbandoned`) remain the complete event vocabulary.

**`ClassificationSignal` as the rule-fired indicator:**  
`ExtractionCandidate.ClassificationSignal` already records which rule won for each candidate. In the US2 model, this field was populated by hardcoded conditional logic. In US3, it is populated from `RuleEvaluationResult.Signal` — the same field, the same enum values, populated by the same priority logic, now driven by the rule engine. No change to consumers of `ClassificationSignal`.

**`WinningRuleName` (internal only):**  
`RuleEvaluationResult.WinningRuleName` is available for diagnostic use. It is not added to any log event in US3. It is reserved for a future diagnostic mode or developer tooling. Its presence in the result model does not add log payload.

**Privacy constraint (unchanged):**  
`rulesEvaluatedCount` is a numeric count; it carries no text derived from pasted content. The existing constraint — no raw pasted text in any log field — is not affected by US3.

---

## Security Strategy

### Regex Pattern Safety

All `PatternMatch` conditions in classification rules use compiled `Regex` instances. The following constraints apply to every pattern in `ExtractionRuleSet.Default()` and to any rule added in future iterations:

- Patterns are anchored or use word-boundary assertions (`\b`) to prevent prefix matches on longer words (e.g., `\bMUST\b` rather than `MUST`).
- No nested quantifiers (`(a+)+`, `(a|a)*`). All quantifiers are simple and bounded.
- No backreferences. Backreferences interact poorly with backtracking and are not needed for the current classification vocabulary.
- The 2,000-character per-line sub-cap (enforced in Stage 5 via `IExtractionConfiguration.MaxLineLengthForPatternMatching`) bounds the maximum input length any pattern sees. This bounds worst-case backtracking time even for imperfect patterns.
- Patterns are compiled at rule construction time (startup) using `RegexOptions.Compiled | RegexOptions.CultureInvariant`. Runtime compilation during evaluation is not permitted.

These constraints are documented as rule authoring guidelines in the rule set source file. They are not automatically enforced by the type system; they are enforced by code review. A future quality gate (static ReDoS analysis) is noted but not required for US3.

### Rule Definition Safety

Rule definitions are developer-authored code. They are not user-configurable, not loaded from external files, and not injected at runtime from any network source. The security properties of rule definitions are equivalent to the security properties of any other source code in the repository.

The startup validation step (described in the Architecture section) provides a fast-fail guard against patterns that fail to compile. A pattern that compiles but has safety issues (nested quantifiers) is a code review concern, not a runtime concern.

### Unchanged Security Properties

All security properties from US2 are preserved unchanged:
- Pasted text is treated as hostile input from Stage 1.
- Raw pasted text never reaches the server.
- The extraction pipeline produces plain-text candidate records; XSS prevention is enforced at the rendering layer in `ExtractionCandidateRow.razor`.
- No new server surface is introduced.

---

## Performance Strategy

### Budget Allocation

The 200 ms end-to-end extraction budget (inherited from US2) is allocated as follows after US3:

| Step | US2 measured | US3 expected | Margin |
|---|---|---|---|
| Stages 1–3 (validation, normalization, partitioning) | ~0 ms (sub-ms) | Unchanged | Unchanged |
| Stage 4 (filter — now rule engine) | ~0 ms | ~0 ms (see analysis) | Unchanged |
| Stage 5 (content extraction + stripping) | ~0 ms | Unchanged | Unchanged |
| Stage 6 (classification — now rule engine) | ~0 ms | ~0 ms (see analysis) | Unchanged |
| Stages 7–8 (deduplication, assembly) | ~0 ms | Unchanged | Unchanged |
| **Total pipeline** | **0 ms (sub-ms)** | **< 1 ms expected** | **199 ms available** |
| UI rendering (candidate list) | Variable (bottleneck) | Unchanged | Unchanged |

**Rule engine overhead analysis:**  
For a 10,000-character input producing 87 candidates (T093 baseline) with 16 default rules (9 filter + 7 classification):
- Filter pass: up to 9 × 87 = 783 `BlockType` comparisons (integer equality; nanoseconds each).
- Classification pass: up to 7 × 87 = 609 regex / condition evaluations against strings of at most 2,000 characters each.
- Total evaluations: ~1,392 per extraction run.
- Expected overhead: measured in microseconds. Negligible relative to the 200 ms ceiling.

For a rule set growing to 50 rules with 500 candidates: ~25,000 evaluations. Still expected to be well below 1 ms given the simplicity of the conditions (compiled regex over short strings).

### Performance Guardrails

1. **Regex compilation at construction time**: All `PatternMatch` conditions compile their patterns in the `ExtractionRuleEngine` constructor (triggered by startup validation). Zero compilation occurs during evaluation.

2. **Filter short-circuit**: Filter rules are evaluated before classification rules. A filtered block exits immediately with `IsFiltered = true`, incurring zero classification rule evaluation cost. This is the most impactful optimisation for block types that are always filtered (Heading, FencedCodeBlock, etc.) which represent a significant fraction of blocks in typical spec documents.

3. **ApplicableBlockTypes pre-filter**: A `ClassificationRule` with `ApplicableBlockTypes` set skips evaluation if the current block type is not in the set. This is a fast list membership check before any pattern matching.

4. **Performance regression guard**: The existing performance test (`Extraction_10kCharInput_DurationMs_LessThan200`) in `ScenarioExtractionServiceTests.cs` is the automated regression guard. It must continue to pass after US3 is integrated. No new performance test is required for the rule engine overhead alone, because the end-to-end test covers the combined pipeline.

5. **Rule count monitoring**: If the rule set grows to 100 or more rules, a dedicated performance test measuring rule engine throughput specifically (rules × candidates × iterations) should be added as a follow-on task. For US3, the default rule set has 16 rules — well within the range where overhead is negligible.

---

## Rule Registration and Extensibility Boundaries

### Adding a New Rule (post-US3)

Adding a new classification rule to the system:
1. Define the rule as a new `ClassificationRule` in `ExtractionRuleSet.Default()`.
2. Assign a priority weight using the gap convention (weights are multiples of 10; insert between existing weights as needed without renumbering).
3. Write a unit test for the new rule: a candidate that matches the rule is classified correctly; a candidate that should not match the rule is unaffected.
4. Run the full test suite. All existing tests must still pass.

Adding a new filter rule:
1. Define the rule as a new `FilterRule` in `ExtractionRuleSet.Default()`.
2. Assign a priority of 100 (same tier as other block-type filters) unless ordering within the filter pass matters.
3. Write a unit test: a block of the filtered type is not returned as a candidate.
4. Run the full test suite.

No changes to `ScenarioExtractionService`, `IScenarioExtractionService`, `IExtractionRuleEngine`, or any component are required to add a rule. This is the extensibility goal of US3.

### Stable Interfaces (must not change without a versioned migration)

| Interface / type | Change policy |
|---|---|
| `IScenarioExtractionService` | Frozen. US1 and US2 consumers depend on this; any change is a breaking migration. |
| `ExtractionPipelineResult` | Frozen. Shape and factory methods established in US2. |
| `ExtractionCandidate` (public fields) | Additive changes only. New optional fields (e.g., `MatchedRuleNames`) may be added; existing fields may not be renamed or removed. |
| `IExtractionRuleEngine` | Stable after US3 ships. The `Evaluate` method signature is the seam for AI integration; changing it requires a versioned plan. |
| `ExtractionRuleSet.Default()` | The method signature is stable. The rule set contents evolve as rules are added or adjusted. |
| `ClassificationSignal` enum | Additive only. New values may be added (e.g., `AiClassifier`); existing values may not be renamed or removed. |
| `BlockType` enum | Additive only. |

### AI Integration Seam

The `IExtractionRuleEngine` interface is designed to be implementable by an AI-backed classifier in a future version. The contract is:

```text
RuleEvaluationResult Evaluate(TextBlock block, string strippedText)
```

A future `AiExtractionRuleEngine` implementing this interface would:
- Receive the same `TextBlock` and stripped text as the deterministic engine.
- Return a `RuleEvaluationResult` with a `Signal` of `AiClassifier` (a future `ClassificationSignal` value).
- Optionally populate `ExtractionCandidate.Confidence` (the reserved field from US2 data-model.md).

Nothing about US3 — not the interface, not the pipeline stages, not the result model — requires modification to accommodate this. The review-before-save workflow, the UI, and the save path are entirely unaffected by which engine implementation is active.

The `ExtractionCandidate.Confidence` field (currently reserved, always null) MUST NOT be populated by the deterministic rule engine. A deterministic rule engine has no confidence concept. Populating this field is reserved for AI implementations.

---

## Migration Strategy

US3 is a direct replacement of the hardcoded Stage 4 and Stage 6 logic. No parallel-running comparison mode is planned.

**Rationale**: The existing test suite — 111 frontend tests including 8 extraction pipeline unit tests per stage, 6 acceptance criteria tests, and 1 performance test — constitutes a comprehensive regression safety net. If the rule engine replicates the US2 rules correctly, all tests pass. If any test fails, there is a regression that must be fixed before the migration is complete.

**Migration sequence:**
1. Implement the rule engine in isolation (new files only; no changes to `ScenarioExtractionService`).
2. Test the rule engine in isolation: each rule in `ExtractionRuleSet.Default()` is tested independently.
3. Integrate the rule engine into `ScenarioExtractionService` Stages 4 and 6.
4. Run the full test suite. All 111 existing tests must pass without modification.
5. Remove the hardcoded Stage 4 and Stage 6 logic once all tests pass.
6. Add `rulesEvaluatedCount` to the `ExtractionCompleted` log event.
7. Run `dotnet format` on both solutions.

**The single acceptance criterion for migration completion**: All US2 tests pass without modification. No test may be modified to accommodate US3; a test that breaks is a regression.

---

## Project Structure (US3 additions)

### New files

```text
frontend/BirkNext.Web/
└── Services/
    ├── ExtractionRuleEngine.cs          # IExtractionRuleEngine implementation
    ├── ExtractionRuleSet.cs             # FilterRule, ClassificationRule, ExtractionRuleSet.Default()
    └── RuleEvaluationResult.cs          # Result record returned by IExtractionRuleEngine.Evaluate()

frontend/BirkNext.Web.Tests/
└── Services/
    ├── ExtractionRuleEngineTests.cs     # Unit tests for default rule set (each rule fires correctly)
    └── ExtractionRuleSetValidationTests.cs  # Startup validation tests (incoherent sets rejected)
```

### Changed files

```text
frontend/BirkNext.Web/
├── Services/
│   └── ScenarioExtractionService.cs    # Stage 4 and Stage 6 delegated to IExtractionRuleEngine
└── Program.cs                          # Register IExtractionRuleEngine singleton
```

### Unchanged files

```text
frontend/BirkNext.Web/
├── Pages/
│   └── ScenarioExtraction.razor        # Unchanged
├── Components/
│   ├── ExtractionInput.razor           # Unchanged
│   ├── ExtractionReviewList.razor      # Unchanged
│   └── ExtractionCandidateRow.razor    # Unchanged
├── Models/
│   └── ExtractionCandidate.cs          # Unchanged (Confidence field remains reserved/null)
└── GraphQL/                            # Unchanged — no schema changes

backend/                                # Entirely unchanged
specs/001-create-scenario/contracts/    # Unchanged — no GraphQL contract changes
```

---

## Implementation Sequencing

No implementation tasks are generated here. The following phases sequence the work at planning granularity.

**Phase 1 — Rule Engine in Isolation (before touching `ScenarioExtractionService`)**  
- Implement `FilterRule`, `ClassificationRule`, and `RuleEvaluationResult` types.
- Implement `IExtractionRuleEngine` interface.
- Implement `ExtractionRuleEngine` with startup validation and evaluation logic.
- Implement `ExtractionRuleSet.Default()` encoding all current US2 rules.
- Write `ExtractionRuleEngineTests.cs`: each default rule fires on matching input; each default rule does not fire on non-matching input; filter short-circuit is verified; Default fallback fires when no other rule matches; conflict resolution (two rules match → highest priority wins).
- Write `ExtractionRuleSetValidationTests.cs`: missing Default rule → startup failure; duplicate rule name → startup failure; invalid regex → startup failure.

**Phase 2 — Pipeline Integration**  
- Inject `IExtractionRuleEngine` into `ScenarioExtractionService`.
- Replace Stage 4 hardcoded block-type filter with rule engine filter pass.
- Replace Stage 6 hardcoded classification logic with rule engine classification pass.
- Run full test suite: all 111 existing tests must pass without modification.

**Phase 3 — Observability**  
- Accumulate `RuleEvaluationResult.EvaluatedRuleCount` across Stage 6 calls.
- Add `rulesEvaluatedCount` to the `ExtractionCompleted` structured log event.
- Verify the `CandidateReviewSaved` and other US2 log events are unaffected.

**Phase 4 — Registration and Cleanup**  
- Register `IExtractionRuleEngine` in `Program.cs` (Blazor WASM composition root).
- Remove old hardcoded Stage 4 and Stage 6 logic from `ScenarioExtractionService` (dead code elimination).
- Run `dotnet format` on both solutions.
- Run the full test suite one final time: all tests pass, zero format violations.

---

# Plan: US4 — Level 1 Configurable Extraction Rules

**Date**: 2026-05-21 | **Spec**: [spec.md §US4](spec.md) | **Research**: [research.md §R-US4](research.md)

---

## Summary

US4 adds a bounded configuration layer over the US3 rule engine. Teams can extend the extraction vocabulary — BDD openers, RFC-2119 keywords, deferral markers, prefix-based classification rules, and ignore prefixes — using plain strings in `appsettings.json`, without writing code or regex. Rule group toggles and bounded priority overrides allow existing default rules to be suppressed or reordered.

A new `ExtractionRuleSetCompiler` class reads an `ExtractionRuleConfiguration` POCO and produces a configured `ExtractionRuleSet` from `ExtractionRuleSet.Default()` at application startup. When no configuration is present, the compiler returns `ExtractionRuleSet.Default()` unchanged and extraction behavior is identical to US3. When configuration fails validation, the compiler logs a structured Warning and falls back to `ExtractionRuleSet.Default()`. The `IExtractionRuleEngine`, `ExtractionRuleEngine`, `ScenarioExtractionService` public interface, all UI components, and the GraphQL schema are unchanged.

---

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 — consistent with US1, US2, and US3  
**Configuration source**: `wwwroot/appsettings.json` via .NET `IOptions<ExtractionRuleConfiguration>`  
**Compilation point**: Application startup, before the first extraction session  
**Changed components**: `Program.cs` (DI registration); `ScenarioExtractionService` (Stage 5.5 ignore-prefix filter); `ExtractionRuleSet` (new `IgnorePrefixes` field); `IExtractionRuleEngine` (new `IgnorePrefixes` property); `ClassificationSignal` enum (new `ConfiguredPrefix` value)  
**New components**: `ExtractionRuleConfiguration`, `PrefixRuleEntry`, `PrefixMatchCondition`, `ExtractionRuleSetCompiler`  
**Unchanged components**: `IExtractionRuleEngine` evaluation contract; `ExtractionRuleEngine`; all US1/US2/US3 backend code; GraphQL schema; all review UI components; save path; `ExtractionRuleSet.Default()`  
**Testing**: All 153 existing tests are the regression gate; new compiler and configuration tests are additive  
**Performance goal**: 200 ms extraction ceiling inherited from US2/US3; compiler overhead is startup-only  
**Constraints**: No AI, no ML, no unrestricted regex, no scripting; keywords and prefixes are plain strings only; configuration is application-wide in MVP

---

## Constitution Check

| Principle | Status | Evidence / Action Required |
|---|---|---|
| **I. Test-First Development** | ✅ PASS | All 153 existing tests are the regression safety net and must pass without modification. New compiler and configuration validation tests follow the same test-first order. No US3 test may be modified to accommodate US4. |
| **II. Observability** | ✅ PASS | Two new structured startup events (`ExtractionRuleConfigurationLoaded`, `ExtractionRuleConfigurationFailed`) carry configuration counts and failure reasons with no keyword content. All extraction-time events are unchanged. |
| **III. Security-First** | ✅ PASS | All configured values are validated at startup before any extraction begins. `Regex.Escape` is applied to all keyword values before pattern incorporation. Regex metacharacters are prohibited at validation time. The 2,000-character per-line sub-cap from US3 applies to all extended patterns. No new server surface. |
| **Development Standards** | ✅ PASS | `ExtractionRuleSetCompiler` is an injectable class independently testable with no Blazor dependency. `IExtractionRuleEngine` and `ExtractionRuleEngine` are unchanged. The GraphQL contract is not modified. No business logic is added to UI components. |
| **Quality Gates** | ✅ PASS | All 153 existing tests pass without modification (regression gate). New compiler unit tests added. Startup validation unchanged in semantics; new validation layer added. 200 ms performance ceiling verified by existing performance test. |

**Complexity justification:**

| Decision | Complexity added | Justification |
|---|---|---|
| `ExtractionRuleSetCompiler` as a separate class | New class with validation, pattern extension, and assembly logic | Keeps `ExtractionRuleSet` as a pure data container and `ExtractionRuleEngine` as a pure evaluator; makes the compilation step independently testable |
| `PrefixMatchCondition` as a new `ClassificationCondition` subtype | One new condition type | Prefix-based classification uses `StartsWith` semantics, not regex; a dedicated condition type encapsulates this without regex involvement and makes it testable in isolation |
| Stage 5.5 ignore-prefix filter in `ScenarioExtractionService` | One new inline filter step | Ignore prefix matching must operate on stripped text, which is not available to `FilterRule` conditions (which receive raw `TextBlock`); placing the check in the service after Stage 5 is the architecturally correct location |
| `ClassificationSignal.ConfiguredPrefix` | One additive enum value | Unambiguously identifies prefix-rule classifications in `ClassificationSignal` without reusing semantically unrelated existing values; additive and non-breaking |

---

## Architecture: Configuration Model

### `ExtractionRuleConfiguration`

The configuration POCO bound from the `ExtractionRules` section of `appsettings.json` via `IOptions<ExtractionRuleConfiguration>`. All fields have empty default values so that an absent or empty configuration section is indistinguishable from "no configuration" and extraction behavior is identical to US3 Default.

| Field | Type | Default | Description |
|---|---|---|---|
| `BddKeywordAdditions` | `string[]` | `[]` | Additional words added to the BDD opener set (`Given`, `When`, `Then`, etc.); matched word-boundary, case-insensitive |
| `Rfc2119UppercaseAdditions` | `string[]` | `[]` | Additional uppercase keywords added to the RFC-2119 uppercase set (`MUST`, `SHALL`, etc.); matched word-boundary, case-sensitive |
| `Rfc2119LowercaseAdditions` | `string[]` | `[]` | Additional lowercase keywords added to the RFC-2119 lowercase set (`must`, `shall`, etc.); matched word-boundary, case-insensitive |
| `DeferralMarkerAdditions` | `string[]` | `[]` | Additional words added to the deferral marker set (`TBD`, `TODO`, etc.); matched word-boundary, case-insensitive |
| `PrefixRules` | `PrefixRuleEntry[]` | `[]` | New prefix-based classification rules; each maps a literal stripped-text prefix to a `ScenarioKind` outcome |
| `IgnorePrefixes` | `string[]` | `[]` | Literal prefixes; candidates whose stripped text begins with a listed prefix are excluded before classification |
| `DisabledRuleNames` | `string[]` | `[]` | Names of default rules to exclude from the compiled rule set; must match names in `ExtractionRuleSet.Default()` exactly |
| `PriorityOverrides` | `Dictionary<string, int>` | `{}` | Priority values to assign to named rules; keys must match default rule names; values must be in the range 1–99 |

**Lifecycle**: Bound by `IOptions<ExtractionRuleConfiguration>` at startup. Read once by `ExtractionRuleSetCompiler.Compile()`. Not read after the compiled `ExtractionRuleSet` is registered. The configuration object is discarded after compilation; only the compiled rule set persists.

**Privacy constraint**: `ExtractionRuleConfiguration` fields contain developer-authored vocabulary entries. They must not contain any text from user-pasted specification content. This is an authoring constraint, not a runtime check.

---

### `PrefixRuleEntry`

A sub-model within `ExtractionRuleConfiguration.PrefixRules`. Each entry produces one `ClassificationRule` in the compiled rule set.

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `Name` | `string?` | No | auto | Rule name for `WinningRuleName` diagnostics and enable/disable support. Auto-generated as `Configure:Prefix:{index}` (zero-based) when null or empty |
| `Prefix` | `string` | Yes | — | Literal prefix matched against stripped candidate text using `StringComparison.OrdinalIgnoreCase` |
| `Classification` | `ScenarioKind` | Yes | — | The `ScenarioKind` assigned when this rule wins |
| `Priority` | `int` | No | `10` | Evaluation priority within the compiled rule set; must be in range 1–99 |

**Name auto-generation**: When `Name` is null or empty, the compiler generates `Configure:Prefix:{index}` where `index` is the zero-based position of the entry in `PrefixRules`. This name appears in `WinningRuleName` when the rule fires and in startup observability logs. Auto-generated names are stable within a single application instance (same configuration produces same names) but may change if entries are reordered in configuration.

---

### `PrefixMatchCondition`

A new `ClassificationCondition` subtype introduced by US4. Implements prefix-based candidate matching without regex.

| Field | Type | Description |
|---|---|---|
| `Prefix` | `string` | The literal prefix to match; stored exactly as supplied after validation (already guaranteed metacharacter-free) |

**Match behavior**: Returns `true` when `strippedText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)`. Returns `false` otherwise.

**No regex involvement**: `PrefixMatchCondition` performs a direct string comparison. `Regex.Escape` is not applied because no regex is used. This is a deliberate design choice: prefix rules operate in a domain that requires no regex, and keeping them regex-free eliminates the regex attack surface for this rule type entirely.

**Lifecycle**: Constructed by `ExtractionRuleSetCompiler` once per `PrefixRuleEntry` at startup. Immutable. Shared across all extraction runs for the application lifetime.

---

### `ClassificationSignal.ConfiguredPrefix` (additive)

A new value added to the existing `ClassificationSignal` enum. Marks candidates classified by a configured prefix rule.

| Value | Classification produced | Trigger |
|---|---|---|
| `ConfiguredPrefix` | The `ScenarioKind` specified in the `PrefixRuleEntry` | Stripped candidate text starts with the configured prefix |

**Additive change**: Adding this value is non-breaking. The `ClassificationSignal` field on `ExtractionCandidate` is already opaque in the UI (not displayed to users in v1). Existing switch statements over `ClassificationSignal` in the codebase must be audited at implementation time to ensure the new value is handled. Any `_` or `default` catch-all handles it automatically.

**Keyword addition wins**: When a configured keyword addition fires (via an extended `Classify:Rfc2119Uppercase` rule, for example), the `ClassificationSignal` remains `Rfc2119Uppercase` — the existing signal for the rule group. `ConfiguredPrefix` is used only when a `PrefixMatchCondition` fires.

---

### Existing Model Changes

**`ExtractionRuleSet`** — one additive field:

| New Field | Type | Default in `Default()` | Description |
|---|---|---|---|
| `IgnorePrefixes` | `IReadOnlyList<string>` | Empty (`ImmutableArray<string>.Empty`) | Compiled ignore prefix list. Read by `ScenarioExtractionService` at Stage 5.5. An empty list produces behavior identical to US3. |

This field is added to `ExtractionRuleSet` alongside the existing `FilterRules` and `ClassificationRules` fields. The `ExtractionRuleSet.Default()` factory method returns a set with `IgnorePrefixes` empty, preserving US3 behavior.

**`IExtractionRuleEngine`** — one additive property:

| New Property | Type | Description |
|---|---|---|
| `IgnorePrefixes` | `IReadOnlyList<string>` | Exposes `_ruleSet.IgnorePrefixes` to callers. Allows `ScenarioExtractionService` to read the compiled ignore prefix list without a direct dependency on `ExtractionRuleSet`. |

`IExtractionRuleEngine` is `internal`; this additive property does not affect any public API. `ExtractionRuleEngine` returns `_ruleSet.IgnorePrefixes`. Test doubles (mocks) must implement this property; the `Moq` default returns `null` unless configured, so tests that exercise Stage 5.5 must set up this property explicitly.

---

## Architecture: Rule Set Compiler

### `ExtractionRuleSetCompiler`

The compiler is a single-responsibility class that transforms `ExtractionRuleSet.Default()` and an `ExtractionRuleConfiguration` into a configured `ExtractionRuleSet`. It is the exclusive locus of configuration validation, keyword pattern extension, prefix rule construction, ignore prefix assembly, disable list application, and priority override application.

The compiler's public contract:

```text
ExtractionRuleSetCompiler
  Compile(ExtractionRuleSet baseSet, ExtractionRuleConfiguration config) : ExtractionRuleSet
```

The compiler accepts a `ILogger<ExtractionRuleSetCompiler>` via constructor injection for startup log events.

**Key guarantee**: `Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration())` returns a rule set that produces byte-for-byte identical extraction results to `ExtractionRuleSet.Default()`. This is the definitive mechanism for FR-US4-010 compliance.

---

### Compilation Sequence

The compiler applies configuration in a defined, deterministic sequence. Each step operates on the rule lists from the previous step.

```
Input: baseSet (ExtractionRuleSet.Default()), config (ExtractionRuleConfiguration)
       │
Step 1 — Validate
       │   Check all config fields against validation rules (see §Configuration Validation)
       │   On any failure: log ExtractionRuleConfigurationFailed Warning → return baseSet unchanged
       │
Step 2 — Disable
       │   Remove all FilterRule and ClassificationRule entries whose Name appears in
       │   config.DisabledRuleNames from the working rule lists.
       │   Classify:Default is never removed (guaranteed by validation).
       │
Step 3 — Priority override
       │   For each name in config.PriorityOverrides: find the rule with that name in the
       │   working lists and replace it with a new rule instance at the overridden priority.
       │   Rules are immutable records; replacement constructs a new instance.
       │
Step 4 — Keyword extend
       │   For each keyword group with non-empty additions:
       │     BddKeywordAdditions      → rebuild Classify:BddPattern pattern
       │     Rfc2119UppercaseAdditions → rebuild Classify:Rfc2119Uppercase pattern
       │     Rfc2119LowercaseAdditions → rebuild Classify:Rfc2119Lowercase pattern
       │     DeferralMarkerAdditions  → rebuild Classify:DeferralMarker pattern
       │   Each rebuild: combine base keywords with configured additions → compile new Regex
       │     → replace rule in working list with new ClassificationRule (same name, priority,
       │       outcome; new PatternMatchCondition).
       │   If the target rule was disabled in Step 2, the addition is silently skipped
       │     (no error; no effect on behavior).
       │
Step 5 — Add prefix classification rules
       │   For each PrefixRuleEntry: construct a new ClassificationRule with:
       │     Name = entry.Name ?? "Configure:Prefix:{index}"
       │     Priority = entry.Priority (default 10)
       │     Condition = new PrefixMatchCondition(entry.Prefix)
       │     Outcome = (entry.Classification, ClassificationSignal.ConfiguredPrefix)
       │   Append to the classification rule working list.
       │
Step 6 — Set ignore prefixes
       │   Set IgnorePrefixes = config.IgnorePrefixes on the compiled set.
       │
Step 7 — Sort
       │   Sort FilterRules by Priority descending (stable).
       │   Sort ClassificationRules by Priority descending (stable).
       │   The Default rule (priority 0) always sorts last; its unconditional condition
       │     guarantees it fires when no higher-priority rule matches.
       │
Step 8 — Construct and return
       │   Construct new ExtractionRuleSet(filteredRules, classificationRules, ignorePrefixes).
       │   Log ExtractionRuleConfigurationLoaded Info event.
       │   Return. The ExtractionRuleEngine constructor validates the result at startup.
```

**Non-mutability invariant**: The compiler never modifies `baseSet`. Steps 2–8 operate on working copies of the rule lists. `ExtractionRuleSet.Default()` is the same instance before and after compilation.

**Keyword base sets**: The compiler holds the base keyword sets for each configurable rule group as constants. These must remain synchronized with `ExtractionRuleSet.Default()`. If a base keyword is changed in `ExtractionRuleSet.Default()`, the corresponding base set in the compiler must be updated in the same commit.

---

## Configuration Validation

All validation runs in Step 1 of compilation, before any modification is applied. A single validation failure causes the entire configuration to be rejected; no partial application occurs.

### String value checks (applied to all keyword additions, prefix values, and ignore prefixes)

| Check | Constraint | Rejection reason code |
|---|---|---|
| Non-empty | Value must not be null, empty, or whitespace | `empty_value` |
| Maximum length | Value must not exceed 200 characters | `value_too_long` |
| Printable ASCII | Value must contain only printable ASCII characters (0x20–0x7E) | `non_ascii_characters` |
| No regex metacharacters | Value must not contain `\ ^ $ . | ? * + ( ) [ ] { }` | `regex_metacharacter` |
| Post-compile check | After `Regex.Escape` and word-boundary wrapping, the assembled group pattern must compile without exception | `pattern_compile_failure` |

### Per-group count limits

| Group | Maximum additions |
|---|---|
| `BddKeywordAdditions` | 50 |
| `Rfc2119UppercaseAdditions` | 50 |
| `Rfc2119LowercaseAdditions` | 50 |
| `DeferralMarkerAdditions` | 50 |
| `PrefixRules` | 50 |
| `IgnorePrefixes` | 50 |

### `PrefixRuleEntry` checks

| Check | Constraint | Rejection reason code |
|---|---|---|
| `Prefix` non-empty | Required | `empty_value` |
| `Prefix` string checks | Same as keyword checks above | (as above) |
| `Classification` valid | Must be a valid `ScenarioKind` value | `invalid_classification` |
| `Priority` in range | Must satisfy `1 ≤ priority ≤ 99` | `priority_out_of_range` |

### `DisabledRuleNames` checks

| Check | Constraint | Rejection reason code |
|---|---|---|
| Name exists | Each name must match a rule name in `baseSet.FilterRules` or `baseSet.ClassificationRules` (case-sensitive) | `unknown_rule_name` |
| Default rule protected | `Classify:Default` must not appear in the disabled list | `default_rule_disabled` |

### `PriorityOverrides` checks

| Check | Constraint | Rejection reason code |
|---|---|---|
| Name exists | Key must match a rule name in `baseSet` (case-sensitive) | `unknown_rule_name` |
| Default rule protected | `Classify:Default` must not appear as a key | `default_priority_override` |
| Value in range | Must satisfy `1 ≤ value ≤ 99` | `priority_out_of_range` |

### Fallback behavior

On any validation failure, the compiler:
1. Emits `ExtractionRuleConfigurationFailed` Warning log event with `fieldName` and `violationType` (no field value content).
2. Returns `baseSet` unchanged (`ExtractionRuleSet.Default()`).
3. Logs `ExtractionRuleConfigurationFallback` Info event identifying that the Default rule set is active.

The application continues operating. All extraction sessions use the default rule set until the application is restarted with a corrected configuration.

---

## Integration with `ScenarioExtractionService`

### Stage 5.5 — Ignore Prefix Filter (new)

After Stage 5 produces the `contents` list (`List<ContentItem>`), the service applies the ignore prefix check:

```
contents (List<ContentItem>, stripped PlainText available)
       │
       ├── For each item: _ruleEngine.IgnorePrefixes.Any(p => item.PlainText.StartsWith(p, OrdinalIgnoreCase))
       │       ├── TRUE  → skip item (not added to the filtered list)
       │       └── FALSE → keep item
       │
filtered contents (List<ContentItem>, candidates that survived ignore prefix check)
       │
Stage 6 — Classification (unchanged)
```

When `_ruleEngine.IgnorePrefixes` is empty (the default), this step is a no-op — a single empty-collection check adds negligible overhead. No changes to Stage 6 logic or the engine's `Evaluate` method.

### All other stages — unchanged

Stages 1–5 and Stages 6–8 are unchanged. The service receives the same `IExtractionRuleEngine` interface. Configured keyword extensions and prefix classification rules are transparent to the service — they are compiled into the rule set and evaluated through the existing `Evaluate` path.

---

## Separation of Concerns

The following table is the authoritative statement of which component owns each responsibility after US4.

| Responsibility | Owner after US4 | Owner in US3 |
|---|---|---|
| Configuration model definition | `ExtractionRuleConfiguration`, `PrefixRuleEntry` (new POCOs) | N/A |
| Configuration loading from appsettings | `IOptions<ExtractionRuleConfiguration>` (standard .NET) | N/A |
| Configuration validation | `ExtractionRuleSetCompiler.Compile()` — at startup | N/A |
| Keyword pattern extension | `ExtractionRuleSetCompiler` — rebuilds patterns with additions | N/A |
| Prefix rule construction | `ExtractionRuleSetCompiler` — creates ClassificationRules with PrefixMatchCondition | N/A |
| Ignore prefix list compilation | `ExtractionRuleSetCompiler` — sets ExtractionRuleSet.IgnorePrefixes | N/A |
| Rule enable/disable application | `ExtractionRuleSetCompiler` — removes named rules from working lists | N/A |
| Priority override application | `ExtractionRuleSetCompiler` — replaces rules with adjusted priority | N/A |
| Fallback to Default on config failure | `ExtractionRuleSetCompiler` — returns baseSet on validation failure | N/A |
| Fallback and config load logging | `ExtractionRuleSetCompiler` — structured startup events | N/A |
| Startup validation of compiled rule set | `ExtractionRuleEngine` constructor — unchanged | `ExtractionRuleEngine` constructor |
| Rule evaluation (filter + classify) | `ExtractionRuleEngine` — unchanged | `ExtractionRuleEngine` |
| Ignore prefix filtering (Stage 5.5) | `ScenarioExtractionService` — reads `IExtractionRuleEngine.IgnorePrefixes` | N/A |
| Block-type filter (Stage 4) | `IExtractionRuleEngine` via FilterRules — unchanged | Unchanged |
| Markdown stripping (Stage 5) | `ScenarioExtractionService` Stage 5 — unchanged | Unchanged |
| Classification (Stage 6) | `IExtractionRuleEngine` via ClassificationRules — unchanged | Unchanged |
| Default rule set definition | `ExtractionRuleSet.Default()` — unchanged | `ExtractionRuleSet.Default()` |
| DI composition | `Program.cs` — extended with compiler registration | `Program.cs` |

**Key invariant (unchanged from US3)**: `IExtractionRuleEngine.Evaluate(block, strippedText)` is a pure function. It reads only its arguments and the compiled rule set. It produces no side effects. This invariant is preserved: the `ExtractionRuleSetCompiler` operates only at startup; no compilation work occurs during evaluation.

---

## DI Composition Root

`Program.cs` is extended to bind and compile the extraction rule configuration before registering the rule engine.

```text
// Bind ExtractionRuleConfiguration from appsettings.json §ExtractionRules
builder.Services.Configure<ExtractionRuleConfiguration>(
    builder.Configuration.GetSection("ExtractionRules"));

// Register rule engine with configured rule set
builder.Services.AddSingleton<IExtractionRuleEngine>(sp => {
    var ruleConfig = sp.GetRequiredService<IOptions<ExtractionRuleConfiguration>>().Value;
    var compiler = new ExtractionRuleSetCompiler(
        sp.GetRequiredService<ILogger<ExtractionRuleSetCompiler>>());
    var ruleSet = compiler.Compile(
        ExtractionRuleSet.Default(), ruleConfig);
    return new ExtractionRuleEngine(
        ruleSet,
        sp.GetRequiredService<IExtractionConfiguration>(),
        sp.GetRequiredService<ILogger<ExtractionRuleEngine>>());
});
```

When no `ExtractionRules` section exists in `appsettings.json`, `IOptions<ExtractionRuleConfiguration>.Value` returns a default-constructed `ExtractionRuleConfiguration` with all arrays empty. The compiler treats this identically to an explicitly empty configuration section and returns `ExtractionRuleSet.Default()` unchanged.

**`IOptions<>` vs `IOptionsSnapshot<>`**: `IOptions<>` is appropriate because the rule configuration is compiled once at startup into an immutable rule set. Hot-reload via `IOptionsSnapshot<>` is not supported in MVP — configuration changes require a restart. This is documented as an operational constraint, not a limitation.

---

## Observability Integration Strategy

### New startup log events

| Event | Level | Fields | Constraint |
|---|---|---|---|
| `ExtractionRuleConfigurationLoaded` | Info | `bddKeywordAdditionCount`, `rfc2119UppercaseAdditionCount`, `rfc2119LowercaseAdditionCount`, `deferralMarkerAdditionCount`, `prefixRuleCount`, `ignorePrefixCount`, `disabledRuleCount`, `priorityOverrideCount` | Counts only; no keyword or prefix text content |
| `ExtractionRuleConfigurationFailed` | Warning | `fieldName` (which config field failed), `violationType` (rejection reason code), `fallbackApplied: true` | No field value content |
| `ExtractionRuleConfigurationFallback` | Info | `reason: "validation_failure"` or `"no_configuration"` | Emitted when Default rule set is used |

`ExtractionRuleConfigurationLoaded` is emitted at the end of a successful compilation (including zero-configuration compilations where all counts are 0). `ExtractionRuleConfigurationFallback` is emitted in all cases where `ExtractionRuleSet.Default()` is the active rule set — both on validation failure and when no configuration section is present.

### Extraction-time observability — no changes

- `rulesEvaluatedCount` in `ExtractionCompleted` already counts all evaluations including configured rules and prefix rules.
- `WinningRuleName` in `RuleEvaluationResult` identifies the winning rule. For prefix rules it carries the rule's name (e.g., `Configure:Prefix:0`). For keyword-extended rules it carries the original rule name (e.g., `Classify:Rfc2119Uppercase`).
- `ClassificationSignal.ConfiguredPrefix` appears in `ExtractionCandidate.ClassificationSignal` for candidates classified by a configured prefix rule.

### Privacy constraint (unchanged)

No log event may carry keyword values, prefix values, or any text from configured vocabulary entries. All log fields are counts, rule names, violation codes, or boolean flags. This applies to all new startup events. Rule names are developer-assigned identifiers, not extraction content.

---

## Security Strategy

### Keyword pattern extension safety

The compiler applies the following steps when building an extended pattern for a keyword group:

1. Start with the known base keyword set (constants in the compiler).
2. Append validated additions from configuration.
3. Apply `Regex.Escape` to every keyword in the combined set (base and configured). For validated keywords this is a no-op, but the application of `Regex.Escape` is unconditional as defense-in-depth.
4. Wrap the combined set in a word-boundary alternation: `\b(?:keyword1|keyword2|...)\b`.
5. Compile the assembled pattern with `RegexOptions.Compiled | RegexOptions.CultureInvariant`.

Because metacharacter validation runs at Step 1 (validation), all configured keywords are guaranteed to be metacharacter-free before Step 3. The `Regex.Escape` call is a belt-and-suspenders measure.

**Per-line sub-cap**: All `PatternMatchCondition` instances — including those rebuilt by the compiler — are subject to `IExtractionConfiguration.MaxLineLengthForPatternMatching` (2,000 characters). The compiler does not change this cap. Extended patterns operate on the same length-bounded input as the original US3 patterns.

### Prefix rule safety

`PrefixMatchCondition` uses `string.StartsWith` — no regex, no escaping, no backtracking. The only input the condition receives is the stripped candidate text (which has already passed Stage 1 input validation, Stage 2 normalization, and Stage 5 markdown stripping). There is no ReDoS surface for prefix rules.

### Configuration visibility (WASM constraint)

`wwwroot/appsettings.json` is a publicly served static file in Blazor WASM. Configured keywords and prefixes are readable by any client. For MVP, these are treated as non-sensitive vocabulary entries. If the configured vocabulary represents confidential internal terminology, a server-side configuration delivery mechanism (API endpoint behind authentication) should replace direct appsettings delivery. This is an operational constraint documented here, not a code-level concern for US4.

### No new server surface

US4 is entirely client-side. No new server endpoints, no new GraphQL types, no new auth surfaces. The server boundary properties from US2/US3 are unchanged.

---

## Performance Strategy

### Startup compilation overhead

Keyword extension requires rebuilding up to four `PatternMatchCondition` regex instances (one per extendable keyword group). For a maximally configured set (50 additions per group), this adds four additional `Regex` compilations at startup. All other compilation steps (disable, priority override, prefix rule construction) involve list operations only. Total startup overhead: immeasurable in practice relative to Blazor WASM initialization time.

### Extraction-time overhead

| Added evaluation cost | Analysis |
|---|---|
| Extended keyword patterns | Same number of regex evaluations; slightly more complex alternations. Overhead in nanoseconds per candidate. Negligible. |
| Configured prefix rules | N × `string.StartsWith` per candidate (N ≤ 50). At 50 prefix rules × 87 candidates: 4,350 comparisons. Expected overhead: < 0.5 ms. |
| Ignore prefix filter (Stage 5.5) | M × `string.StartsWith` per content item (M ≤ 50). At 50 ignore prefixes × 87 items: 4,350 comparisons. Expected overhead: < 0.5 ms. |
| Disabled rules | Zero cost — disabled rules are absent from the compiled rule set. |
| Priority overrides | Zero cost — sorting happens at startup, not at evaluation time. |
| **Total additional overhead** | **< 1 ms for maximally configured extraction** |

The 200 ms extraction performance ceiling established in US2 and confirmed in US3 is not threatened by US4 configuration at any supported scale.

### Performance regression guard

The existing performance test (`Extraction_10kCharInput_DurationMs_LessThan200`) is the automated regression guard. It runs with `ExtractionRuleSet.Default()` (zero configuration). A second performance test with a maximally configured rule set (50 additions per group, 50 prefix rules) should be added to verify that the ceiling holds under full configuration.

---

## Determinism Guarantees Under Configuration

All US3 determinism guarantees extend to US4:

**No randomness**: All compilation steps are deterministic. Pattern alternation order is determined by the sequence of the combined keyword list (base keywords first, configured additions in declaration order). `PrefixMatchCondition` is `StringComparison.OrdinalIgnoreCase` — deterministic. No random number generators or time-dependent logic.

**No external state at evaluation time**: Configuration is consumed by the compiler at startup to produce an immutable `ExtractionRuleSet`. `IExtractionRuleEngine.Evaluate()` reads only its arguments and the compiled rule set. No `IOptions<>` reads, no file reads, and no network calls occur during evaluation.

**Stable candidate ordering**: Determined by source position in the input text. Unchanged by configuration.

**Idempotency**: The compiled rule set is immutable after startup. Running the same input through the same configured rule set twice produces identical output.

**Rule isolation**: `PrefixMatchCondition` and extended `PatternMatchCondition` instances are stateless. They read no shared mutable state and modify no shared mutable state.

**Compiled set validity**: The compiler guarantees that its output passes `ExtractionRuleEngine` startup validation (exactly one Default rule, no duplicate names, all patterns compile). If a compiled set were to fail this validation, it indicates a compiler bug — the compiler's own test suite guards against this.

---

## Migration Strategy

US4 is an additive extension to the US3 baseline. No existing behavior is removed.

**Default path (unchanged)**: When `ExtractionRuleConfiguration` is empty, the compiler returns `ExtractionRuleSet.Default()` unchanged. All 153 existing tests pass on this path without modification. This is the definitive regression test for the migration.

**Configuration path (new)**: When configuration is present, the compiler produces a modified rule set. The US4-specific tests cover this path.

**No parallel running required**: The existing 153-test suite is comprehensive. If compilation with empty configuration passes all 153 tests, the migration is correct by construction. No A/B running or shadow comparison mode is needed.

**Staged delivery**: Because the compiler is decoupled from `ExtractionRuleEngine`, the compiler can be delivered in isolation (Phase 1 and 2 below) with full test coverage before any changes to `ScenarioExtractionService`, `IExtractionRuleEngine`, or `Program.cs`. This limits the risk surface during development.

---

## Stable Interfaces (post-US4)

| Interface / type | Change policy |
|---|---|
| `IScenarioExtractionService` | Frozen. Unchanged by US4. |
| `ExtractionPipelineResult` | Frozen. Unchanged by US4. |
| `ExtractionCandidate` (public fields) | Additive only. Unchanged by US4. |
| `IExtractionRuleEngine.Evaluate()` | Frozen. Unchanged by US4. |
| `IExtractionRuleEngine.IgnorePrefixes` | New property added by US4; stable thereafter. |
| `IExtractionRuleEngine.RuleNames` | Stable. Returns all registered rule names including configured rules. |
| `ExtractionRuleSet.Default()` | Stable. Returns the US3-equivalent rule set with empty IgnorePrefixes. |
| `ExtractionRuleSet.FilterRules` / `.ClassificationRules` | Stable. Unchanged. |
| `ExtractionRuleSet.IgnorePrefixes` | New field added by US4; stable thereafter. |
| `ExtractionRuleSetCompiler.Compile()` | Stable after US4 ships. |
| `ClassificationSignal` enum | Additive only. `ConfiguredPrefix` added by US4. |
| `BlockType` enum | Additive only. Unchanged by US4. |
| `ScenarioKind` enum | Frozen. Unchanged by US4. |

---

## Project Structure (US4 additions)

### New files

```text
frontend/BirkNext.Web/
└── Services/
    ├── ExtractionRuleConfiguration.cs    # IOptions POCO: ExtractionRuleConfiguration + PrefixRuleEntry
    ├── ExtractionRuleSetCompiler.cs      # Compiler: validate + Compile(baseSet, config) → ExtractionRuleSet
    └── PrefixMatchCondition.cs           # New ClassificationCondition subtype: StartsWith matching

frontend/BirkNext.Web.Tests/
└── Services/
    ├── ExtractionRuleConfigurationTests.cs   # Validation: each constraint rejects correctly;
    │                                          # valid inputs accepted; edge cases (empty string,
    │                                          # max-length, metacharacter boundary)
    └── ExtractionRuleSetCompilerTests.cs     # Compilation: empty config → identical to Default;
                                               # keyword addition fires on new keyword;
                                               # prefix rule fires on prefix match;
                                               # ignore prefix suppresses candidate;
                                               # disable removes rule from compiled set;
                                               # priority override reorders rules;
                                               # validation failure falls back to Default;
                                               # Classify:Default cannot be disabled
```

### Changed files

```text
frontend/BirkNext.Web/
├── Services/
│   ├── ExtractionRuleSet.cs              # Add IgnorePrefixes: IReadOnlyList<string> field;
│   │                                      # update Default() factory to return empty IgnorePrefixes
│   └── ScenarioExtractionService.cs      # Add Stage 5.5 ignore-prefix filter between Stage 5 and Stage 6;
│                                          # reads _ruleEngine.IgnorePrefixes
├── GraphQL/
│   └── [ClassificationSignal source]     # Add ConfiguredPrefix enum value
│                                          # (additive; audit all switch statements for exhaustiveness)
└── Program.cs                            # Register IOptions<ExtractionRuleConfiguration>;
                                           # insert ExtractionRuleSetCompiler step in IExtractionRuleEngine factory
```

```text
frontend/BirkNext.Web/Services/
└── ExtractionRuleEngine.cs               # Implement IExtractionRuleEngine.IgnorePrefixes property:
                                           # return _ruleSet.IgnorePrefixes
```

### Unchanged files

```text
frontend/BirkNext.Web/
├── Services/
│   └── ExtractionRuleEngine.cs           # Evaluate() method: unchanged
├── Pages/
│   └── ScenarioExtraction.razor          # Unchanged
├── Components/
│   ├── ExtractionInput.razor             # Unchanged
│   ├── ExtractionReviewList.razor        # Unchanged
│   └── ExtractionCandidateRow.razor      # Unchanged
└── GraphQL/                              # No schema changes

backend/                                  # Entirely unchanged
specs/001-create-scenario/contracts/      # No GraphQL schema changes
```

---

## Implementation Sequencing

No implementation tasks are generated here. The following phases sequence the work at planning granularity.

**Phase 1 — Configuration Model and Compiler (new files only; no changes to existing files)**
- Implement `ExtractionRuleConfiguration`, `PrefixRuleEntry`, `PrefixMatchCondition` in new files.
- Implement `ExtractionRuleSetCompiler` with: validation (all checks from §Configuration Validation), keyword extension, prefix rule construction, disable application, priority override application, ignore prefix assembly, stable sort, and fallback logging.
- Write `ExtractionRuleConfigurationTests.cs`: each validation constraint rejects the correct violation; valid inputs pass; count limits enforce correctly; protected rules cannot be disabled or have priority overridden.
- Write `ExtractionRuleSetCompilerTests.cs`: empty config produces a rule set identical in behavior to `ExtractionRuleSet.Default()`; each configuration feature (keyword addition, prefix rule, ignore prefix, disable, priority override) produces the expected compiled set; validation failure returns `Default()` unchanged; combination of features compiles correctly.
- Run `ExtractionRuleEngineTests.cs` and `ExtractionRuleSetValidationTests.cs` in full — they must pass without modification.

**Phase 2 — Model Extensions (additive changes to existing types)**
- Add `IgnorePrefixes: IReadOnlyList<string>` to `ExtractionRuleSet`; update `Default()` factory to return empty list; update constructor.
- Add `IgnorePrefixes: IReadOnlyList<string>` property to `IExtractionRuleEngine`; implement in `ExtractionRuleEngine`.
- Add `ClassificationSignal.ConfiguredPrefix` enum value; audit all switch statements over `ClassificationSignal` in the codebase.
- Run full test suite (153 tests) — all must pass without modification. These are all additive changes.

**Phase 3 — Service and DI Integration**
- Add Stage 5.5 ignore-prefix filter to `ScenarioExtractionService` between Stage 5 and Stage 6.
- Update `Program.cs`: bind `IOptions<ExtractionRuleConfiguration>`, insert compiler step in `IExtractionRuleEngine` factory.
- Run full test suite (153 + Phase 1 new tests) — all must pass.

**Phase 4 — Observability**
- Emit `ExtractionRuleConfigurationLoaded` Info event in `ExtractionRuleSetCompiler` at end of successful compilation.
- Emit `ExtractionRuleConfigurationFailed` Warning event on validation failure.
- Emit `ExtractionRuleConfigurationFallback` Info event when Default rule set is active.
- Verify no log event carries keyword or prefix text content.

**Phase 5 — Regression and Stabilization**
- Run the full test suite including all new tests; all pass.
- Add a performance test for maximally configured extraction (50 additions per group, 50 prefix rules) confirming sub-200 ms execution.
- Run `dotnet format` on `frontend/BirkNext.sln`; zero violations.
- Confirm `ExtractionRuleSet.Default()` behavior is unchanged by running US2/US3 acceptance criteria tests in isolation.
