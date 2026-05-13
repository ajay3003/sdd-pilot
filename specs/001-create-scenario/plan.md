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
