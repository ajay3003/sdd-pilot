# Data Model: Scenario Management

**Phase**: 1 | **Branch**: `001-create-scenario` | **Date**: 2026-04-30

---

## Entities

### Scenario

The single domain entity for this feature. Represents a captured specification or QA scenario scoped to a project workspace.

| Field | C# Type | DB Column | Constraints | Notes |
|-------|---------|-----------|-------------|-------|
| `Id` | `Guid` | `id` (PK) | NOT NULL, generated | Client-generated or server-generated UUID |
| `Title` | `string` | `title` | NOT NULL, max 500 chars | FR-002: must be non-empty |
| `Description` | `string?` | `description` | NULL allowed | Optional free text; no max enforced in v1 |
| `Kind` | `ScenarioKind` | `kind` | NOT NULL, stored as `varchar` | FR-003: Requirement \| Test \| NeedsClarification |
| `ProjectId` | `string` | `project_id` | NOT NULL, max 200 chars | FR-010: scopes the scenario to a workspace |
| `CreatedAt` | `DateTimeOffset` | `created_at` | NOT NULL, default `now()` | Set by server on creation; not client-supplied |

**Table name**: `scenarios`  
**Default sort**: `created_at DESC` (spec assumption — most recent first)

---

### ScenarioKind (enum)

```csharp
public enum ScenarioKind
{
    Requirement,
    Test,
    NeedsClarification
}
```

Stored in PostgreSQL as a `varchar` (EF Core value converter) so the column remains readable without joining an enum table.

---

## EF Core Entity Class

```csharp
public class Scenario
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ScenarioKind Kind { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

---

## Validation Rules

| Field | Rule | Error message |
|-------|------|---------------|
| `Title` | Required, 1–500 chars | "Title is required" / "Title must not exceed 500 characters" |
| `Kind` | Must be a valid `ScenarioKind` value | "A valid type must be selected" |
| `ProjectId` | Required (supplied by caller, not user) | N/A — server-side assertion only |
| `Description` | No validation in v1 | — |

---

## State Transitions

Scenarios are immutable after creation in v1. No state machine applies.

---

## Database Schema (EF Core migration target)

```sql
CREATE TABLE scenarios (
    id          uuid         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    title       varchar(500) NOT NULL,
    description text,
    kind        varchar(30)  NOT NULL,
    project_id  varchar(200) NOT NULL,
    created_at  timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX ix_scenarios_project_id_created_at
    ON scenarios (project_id, created_at DESC);
```

The index on `(project_id, created_at DESC)` satisfies the default list query pattern (FR-010 scoping + reverse-chronological order) in a single index scan.

---

## GraphQL Type Mapping

| Entity field | GraphQL field | GraphQL type |
|-------------|---------------|-------------|
| `Id` | `id` | `ID!` |
| `Title` | `title` | `String!` |
| `Description` | `description` | `String` |
| `Kind` | `kind` | `ScenarioKind!` (enum) |
| `ProjectId` | `projectId` | `String!` |
| `CreatedAt` | `createdAt` | `DateTime!` |

---

## Out of Scope (v1)

- Edit / update scenario
- Delete scenario
- Pagination or cursor-based list
- Scenario relationships or parent/child nesting

---

# Data Model: US2 — Deterministic Scenario Extraction

**Phase**: 1 | **Date**: 2026-05-13 | **Plan**: [plan.md §US2](plan.md) | **Spec**: [spec.md §US2](spec.md)

---

## Model Overview

US2 introduces no new persisted entities and no database migration. All new models are either transient (pipeline-internal or UI-state-lifetime) or wire models (cross the network boundary but are not stored). The only objects that reach the database are existing `Scenario` records (US1), created via the save path using field values mapped from the transient `ExtractionCandidate` model.

Models are grouped by their tier and lifetime:

| Model | Tier | Lifetime | Persisted |
|---|---|---|---|
| `TextBlock` | Pipeline-internal | Pipeline execution only | No |
| `BlockType` | Pipeline-internal enum | Pipeline execution only | No |
| `ClassificationSignal` | Pipeline-internal enum | Pipeline execution only | No |
| `ExtractionCandidate` | Client UI state | Session (component lifetime) | No — unless user saves |
| `CandidateSaveState` | Client UI state enum | Session (component lifetime) | No |
| `ExtractionPipelineResult` | Client UI state | Session (component lifetime) | No |
| `PipelineStatus` | Client UI state enum | Session (component lifetime) | No |
| `ExtractionReviewState` | Client UI state | Session (component lifetime) | No |
| `ReviewSavePhase` | Client UI state enum | Session (component lifetime) | No |
| `CreateScenariosInput` | Wire (client → server) | In-flight only | No |
| `ExtractionMetadataInput` | Wire (client → server) | In-flight only | No |
| `CreateScenariosPayload` | Wire (server → client) | In-flight only | No |
| `CreateScenarioResult` | Wire (server → client) | In-flight only | No |
| `Scenario` (US1, unchanged) | Persisted | Permanent | **Yes** |
| `ScenarioKind` (US1, unchanged) | Persisted enum | Permanent | **Yes** |

---

## Pipeline-Internal Models

These models exist only within `ScenarioExtractionService` during a single pipeline execution. They do not cross any component boundary and are never observable outside the service.

---

### TextBlock

The unit produced by Stage 3 (Block Partitioning). Each block represents one structural element of the pasted input. Consumed by Stage 4 (Structure Filter) and Stage 5 (Content Extraction). Discarded after Stage 5.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `RawText` | `string` | No | Original text of the block exactly as it appeared in the normalized input, before any markdown stripping |
| `BlockType` | `BlockType` | No | Structural classification of the block |
| `IndentationLevel` | `int` | No | Nesting depth for list items (0 = top-level bullet). Always 0 for non-list block types |
| `PrecedingHeading` | `string` | Yes | Text of the most recent heading block seen before this block in document order; null if no heading has appeared yet |

**Invariant**: `IndentationLevel` is always 0 for non-list `BlockType` values.

**Lifecycle**: Created in Stage 3. Consumed and discarded within Stage 5. Never stored, never returned from `ScenarioExtractionService`.

---

### BlockType (enum)

Classifies each `TextBlock` by its structural role in the input document.

| Value | Markdown source | Extractable |
|---|---|---|
| `Heading` | `#`, `##`, `###`, etc. | No — filtered in Stage 4 |
| `UnorderedListItem` | `-`, `*`, `+` prefix | Yes |
| `OrderedListItem` | `1.`, `2.`, etc. | Yes |
| `FencedCodeBlock` | ` ``` ` ... ` ``` ` | No — filtered in Stage 4 |
| `Blockquote` | `>` prefix | No — filtered in Stage 4 |
| `TableBodyRow` | `|...|` (not separator) | Yes — pipe syntax stripped in Stage 5 |
| `TableHeaderRow` | First `|...|` row | No — filtered in Stage 4 |
| `TableSeparatorRow` | `|---|---|` | No — filtered in Stage 4 |
| `HorizontalRule` | `---`, `***`, `___` | No — filtered in Stage 4 |
| `ParagraphLine` | Non-blank prose line | Yes — if it contains a classification signal |
| `YamlFrontMatter` | `---` document-open block | No — filtered in Stage 4 |
| `HtmlComment` | `<!-- ... -->` | No — filtered in Stage 4 |
| `Empty` | Blank line | No — filtered in Stage 4 |

**Design note**: `ParagraphLine` is extractable only if Stage 5 detects a classification signal in its content. A bare prose line with no RFC 2119 keywords, no BDD pattern, and no question mark is discarded at Stage 5 minimum-content checks.

---

### ClassificationSignal (enum)

Records which heuristic rule caused the classification decision for an `ExtractionCandidate`. Retained on the candidate record for observability and future AI integration. Not surfaced in the v1 review UI.

| Value | Classification produced | Trigger condition |
|---|---|---|
| `BddPattern` | `Test` | Line contains Given/When/Then triple, or starts with a BDD section opener |
| `Rfc2119Uppercase` | `Requirement` | Line contains MUST, SHALL, SHOULD, MAY, MUST NOT, or SHALL NOT (uppercase) |
| `Rfc2119Lowercase` | `Requirement` | Line contains must, shall, required, or is required to (lowercase) |
| `FrPrefix` | `Requirement` | Line contains a functional requirement prefix matching `FR-[0-9]+` |
| `QuestionTerminator` | `NeedsClarification` | Line ends with `?` |
| `DeferralMarker` | `NeedsClarification` | Line contains TBD, TODO, TBC, open question, or to be defined |
| `Default` | `NeedsClarification` | No signal matched; fallback applied |

**Priority**: When multiple signals are detected on the same line, the signal with the highest priority wins. Priority order (highest to lowest): `BddPattern` → `Rfc2119Uppercase` → `Rfc2119Lowercase` → `FrPrefix` → `QuestionTerminator` → `DeferralMarker` → `Default`.

**Extensibility note**: A future AI-assisted classifier may introduce an `AiClassifier` value. Because this field is opaque to the UI in v1, adding values is non-breaking.

---

## Client UI State Models

These models live in the Blazor WASM process. Their lifetime is the lifetime of the component tree that holds them. Navigating away from `ScenarioExtraction.razor` destroys all instances. No client-side storage (local storage, session storage, IndexedDB) is used.

---

### ExtractionCandidate

The primary unit of the review workflow. Created by Stage 8 (Result Assembly) of the extraction pipeline. Held in the `ExtractionReviewList` component state. A subset of candidates — those the user selects and successfully saves — are converted to `CreateScenarioInput` values and sent to the server.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `CandidateId` | `Guid` | No | Client-generated identifier for UI keying and selection tracking. **Never sent to the server. Never persisted.** Distinct from `Scenario.Id`. |
| `Title` | `string` | No | Extracted candidate text, stripped of all markdown syntax (list markers, inline code backticks, link syntax). The value that will become `Scenario.Title` if saved. |
| `Classification` | `ScenarioKind` | No | Classification assigned by Stage 6. Maps 1:1 to the existing `ScenarioKind` enum values (`Requirement` / `Test` / `NeedsClarification`). No new enum values introduced. |
| `ClassificationSignal` | `ClassificationSignal` | No | Which heuristic produced the classification. Internal metadata; not displayed in v1. |
| `ContextHeading` | `string` | Yes | Nearest preceding document heading, carried from `TextBlock.PrecedingHeading`. Displayed in the review UI to provide document context. Not sent to the server. Not persisted. |
| `SourceBlockType` | `BlockType` | No | The structural block type the candidate was extracted from. Internal metadata; not displayed in v1. |
| `IsSelected` | `bool` | No | Whether the user has checked this candidate for inclusion in the save. Default: `false` (opt-in selection — FR-US2-006). |
| `SaveState` | `CandidateSaveState` | No | Tracks the candidate's position in the save lifecycle. Default: `Pending`. |
| `SaveError` | `string` | Yes | Server-returned error message if `SaveState` is `Failed`. Null in all other states. |
| `SavedScenarioId` | `Guid` | Yes | The `Scenario.Id` assigned by the server after a successful save. Null until `SaveState` is `Saved`. Allows the review UI to link to the newly created scenario in the US1 list. |

**Validation constraint**: `Title` must be non-empty after markdown stripping. Candidates with an empty `Title` after Stage 5 processing are dropped before Stage 6 and never become `ExtractionCandidate` instances.

**Lifecycle**:
1. Created by Stage 8 with `IsSelected = false`, `SaveState = Pending`, `SaveError = null`, `SavedScenarioId = null`.
2. `IsSelected` toggled by user interaction in `ExtractionCandidateRow`.
3. On user save confirm: `SaveState` transitions to `Saving` for all selected candidates.
4. On server response: `SaveState` transitions to `Saved` (with `SavedScenarioId` populated) or `Failed` (with `SaveError` populated).
5. On component disposal: all instances are garbage collected. No state is persisted.

**US1 compatibility**: `Classification` is typed as `ScenarioKind`, the same enum US1 uses for `Scenario.Kind`. No type conversion is needed at the save boundary.

---

### CandidateSaveState (enum)

Tracks the per-candidate lifecycle during the save phase.

| Value | Meaning | `SaveError` | `SavedScenarioId` |
|---|---|---|---|
| `Pending` | Selected but save not yet attempted | null | null |
| `Saving` | Batch mutation in flight | null | null |
| `Saved` | Successfully persisted | null | **populated** |
| `Failed` | Server returned an error for this candidate | **populated** | null |
| `Retrying` | User re-triggered save for this failed candidate | null | null |

**Terminal states**: `Saved` and `Failed` (unless the user retries a failed candidate, which moves it to `Retrying` → `Saving`). `Saved` is final within the session.

**Invariant**: `SavedScenarioId` is non-null if and only if `SaveState == Saved`. `SaveError` is non-null if and only if `SaveState == Failed`.

---

### ExtractionPipelineResult

The output of `IScenarioExtractionService.Extract()`. Returned to the calling component. The `Candidates` list is handed to `ExtractionReviewList`; the metadata fields are used for observability logging and the count summary display.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Status` | `PipelineStatus` | No | Overall outcome of the pipeline execution |
| `Candidates` | `IReadOnlyList<ExtractionCandidate>` | No | Ordered candidate list. Empty (not null) when `Status` is not `Success`. |
| `InputLengthChars` | `int` | No | Character count of the raw input before normalization. Used in `ExtractionTriggered` and `ExtractionCompleted` log events. |
| `InputLineCount` | `int` | No | Line count after normalization. Used in log events. |
| `DurationMs` | `long` | No | Total pipeline execution time in milliseconds. Used in `ExtractionCompleted` log event. |
| `RequirementCount` | `int` | No | Count of candidates classified as `Requirement`. Must equal `Candidates.Count(c => c.Classification == Requirement)`. |
| `TestCount` | `int` | No | Count of candidates classified as `Test`. |
| `NeedsClarificationCount` | `int` | No | Count of candidates classified as `NeedsClarification`. |

**Invariant**: `RequirementCount + TestCount + NeedsClarificationCount == Candidates.Count` always.

**Invariant**: When `Status != Success`, `Candidates` is empty and all count fields are 0.

**Lifecycle**: Created by `ScenarioExtractionService.Extract()`. Held by `ExtractionInput` until the result is passed to `ExtractionReviewList`. The `Candidates` list is held in `ExtractionReviewList` state until the component is disposed or extraction is re-triggered. The metadata fields (`InputLengthChars`, `DurationMs`, etc.) are consumed immediately for logging and display; they are not retained beyond the component render cycle that processes them.

---

### PipelineStatus (enum)

The top-level outcome of a pipeline execution.

| Value | Condition | Candidates non-empty |
|---|---|---|
| `Success` | Stages 1–8 completed; one or more candidates found | Yes |
| `EmptyInput` | Stage 1: input is empty or whitespace only | No |
| `InputTooLarge` | Stage 1: input exceeds the 50,000 character hard cap | No |
| `NoResults` | Stages 1–7 completed successfully; no candidates survived all filters | No |

---

### ExtractionReviewState

The top-level state of the `ExtractionReviewList` component. Documented here as a model to make the component's state transitions explicit and testable in isolation.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `PipelineResult` | `ExtractionPipelineResult` | Yes | Null until the parent page passes a completed extraction result |
| `SavePhase` | `ReviewSavePhase` | No | Current state of the save lifecycle. Default: `Idle`. |
| `SelectedCandidateIds` | `HashSet<Guid>` | No | Set of `ExtractionCandidate.CandidateId` values the user has checked. Updated on each checkbox toggle. |

**Derived property**: `SelectedCandidates` — the subset of `PipelineResult.Candidates` whose `CandidateId` appears in `SelectedCandidateIds`. Used to build the `CreateScenariosInput` on save confirm.

**Lifecycle**: Created when `ExtractionReviewList` is initialized with an empty `SelectedCandidateIds` set and `SavePhase = Idle`. Destroyed on component disposal. Re-initialized if the parent triggers a new extraction.

---

### ReviewSavePhase (enum)

Top-level state of the save lifecycle, held by `ExtractionReviewState`.

| Value | Meaning |
|---|---|
| `Idle` | No save has been attempted in this session |
| `Saving` | Batch mutation is in flight |
| `PartialSuccess` | Some selected candidates saved; one or more failed |
| `Complete` | All selected candidates saved successfully |
| `Failed` | All selected candidates failed to save |

**Note**: `ReviewSavePhase` is distinct from `CandidateSaveState`. `ReviewSavePhase` is the aggregate view; `CandidateSaveState` is the per-candidate view. Both must be updated together after the server responds.

---

## Wire Models

These models cross the client-server boundary. They exist only in flight and are not stored on either side. They define the contract for the `createScenarios` batch mutation.

---

### CreateScenariosInput

The GraphQL input for the batch mutation. Wraps the existing `CreateScenarioInput` element type from US1 in an array — no new per-item fields.

| Field | GraphQL type | Nullable | Description |
|---|---|---|---|
| `items` | `[CreateScenarioInput!]!` | No | Ordered array of scenario inputs to create. Each element maps from one selected `ExtractionCandidate`. Order matches the review list order for predictable per-item error reporting. |
| `extractionMetadata` | `ExtractionMetadataInput` | Yes | Optional extraction session context. When present, allows the server to log a fully contextual `CandidateReviewSaved` event without a separate telemetry endpoint (Plan §Observability, Option C). |

**Mapping from `ExtractionCandidate` to `CreateScenarioInput`**:

| `ExtractionCandidate` field | `CreateScenarioInput` field | Notes |
|---|---|---|
| `Title` | `title` | Direct; validated non-empty on server |
| `Classification` | `kind` | `ScenarioKind` value; same enum, same values |
| _(none)_ | `description` | Set to empty string `""`; candidates have no description |
| _(session context)_ | `projectId` | Supplied by the component from the active project context; not from the candidate |

**Constraint**: `CandidateId`, `ContextHeading`, `SourceBlockType`, `ClassificationSignal`, `IsSelected`, `SaveState`, `SaveError`, and `SavedScenarioId` are never included in the wire payload. They are UI-only fields.

---

### ExtractionMetadataInput

An optional companion to `CreateScenariosInput` that carries client-side extraction context to the server for observability purposes. Carrying this with the save mutation eliminates the need for a separate telemetry endpoint in v1.

| Field | GraphQL type | Nullable | Description |
|---|---|---|---|
| `totalExtracted` | `Int!` | No | Total candidates produced by the extraction pipeline (`ExtractionPipelineResult.Candidates.Count`), including those the user did not select |
| `selectedCount` | `Int!` | No | Number of candidates the user selected (`SelectedCandidateIds.Count`). Must equal `items` array length |
| `extractionDurationMs` | `Int!` | No | Pipeline execution duration in milliseconds (`ExtractionPipelineResult.DurationMs`) |
| `sessionId` | `String!` | No | Client-generated session identifier for correlating `ExtractionTriggered` / `ExtractionCompleted` events with `CandidateReviewSaved` |

**Security constraint**: `ExtractionMetadataInput` MUST NOT carry any text from the pasted input. It carries only numeric and identifier metadata. This constraint must be verified in code review.

---

### CreateScenariosPayload

The GraphQL output of the batch mutation. One `CreateScenarioResult` per input item, in the same order as `CreateScenariosInput.items`. Order preservation is required for the frontend to map results back to `ExtractionCandidate` instances by position.

| Field | GraphQL type | Nullable | Description |
|---|---|---|---|
| `results` | `[CreateScenarioResult!]!` | No | Per-item results in input order. Length equals `CreateScenariosInput.items` length. |
| `successCount` | `Int!` | No | Number of items where `CreateScenarioResult` is a success. Derived; included for client convenience and server-side log emission. |
| `failureCount` | `Int!` | No | Number of items where `CreateScenarioResult` is an error. |

**Invariant**: `successCount + failureCount == results.Length`.

---

### CreateScenarioResult

A per-item discriminated union in the batch mutation response. Each element is either a successful creation or a structured error.

**Success variant** (`CreateScenarioSuccess`):

| Field | GraphQL type | Nullable | Description |
|---|---|---|---|
| `scenario` | `Scenario!` | No | The newly created `Scenario` object (US1 type, unchanged). The client reads `scenario.id` and stores it as `ExtractionCandidate.SavedScenarioId`. |

**Error variant** (`CreateScenarioError`):

| Field | GraphQL type | Nullable | Description |
|---|---|---|---|
| `message` | `String!` | No | Human-readable error description. Stored as `ExtractionCandidate.SaveError`. |
| `field` | `String` | Yes | The field that caused the validation failure, if applicable (e.g., `"title"`, `"kind"`). |
| `code` | `String!` | No | Machine-readable error code for client-side error handling (e.g., `VALIDATION_FAILED`, `TITLE_REQUIRED`). |

**GraphQL representation**: `CreateScenarioResult` is modelled as a union type in the schema (`CreateScenarioSuccess | CreateScenarioError`). Exact GraphQL union or error-interface strategy is deferred to schema design; the payload fields above are stable regardless of the schema pattern chosen.

---

## Persistence Boundary

This section is the authoritative statement of what is and is not persisted by US2.

### What is persisted

When a user selects one or more `ExtractionCandidate` instances and confirms the save action, each selected candidate is mapped to a `Scenario` record in the existing `scenarios` table via the `createScenarios` mutation. No new table, no new column, no migration.

**Field mapping at the persistence boundary**:

| `ExtractionCandidate` field | → | `Scenario` field | Notes |
|---|---|---|---|
| `Title` | → | `Title` | Validated non-empty by the server |
| `Classification` | → | `Kind` | Same `ScenarioKind` enum, same values; no conversion |
| _(not present)_ | → | `Description` | Set to `string.Empty`; candidates carry no description |
| _(session context)_ | → | `ProjectId` | Supplied from the active project context, same as US1 |
| _(server-generated)_ | → | `Id` | New `Guid` assigned by the server; returned as `CreateScenarioSuccess.scenario.id` |
| _(server-generated)_ | → | `CreatedAt` | Set to `DateTimeOffset.UtcNow` on the server at mutation time |

### What is never persisted

The following fields exist on `ExtractionCandidate` for UI and pipeline purposes only. They are explicitly excluded from all server payloads and all database writes.

| Field | Why it is not persisted |
|---|---|
| `CandidateId` | UI key only; the server assigns `Scenario.Id` |
| `ContextHeading` | Display metadata; not a scenario attribute |
| `SourceBlockType` | Pipeline provenance; not a scenario attribute |
| `ClassificationSignal` | Internal heuristic metadata; not a scenario attribute |
| `IsSelected` | Transient UI state |
| `SaveState` | Transient UI state |
| `SaveError` | Transient error state |
| `SavedScenarioId` | Read back from the server response; redundant once the scenario is in US1 list |

The `ExtractionPipelineResult`, `ExtractionReviewState`, and all pipeline-internal models (`TextBlock`, enums) are equally never persisted.

---

## Validation Rules (US2 models)

### ExtractionCandidate validation (client-side, pipeline)

| Field | Rule | Enforcement point |
|---|---|---|
| `Title` | Non-empty after markdown stripping | Stage 5 (Content Extraction) — candidates with empty stripped text are discarded before Stage 6 |
| `Classification` | Must be a valid `ScenarioKind` value | Stage 6 always assigns one; `Default` fallback prevents unclassified candidates |
| `CandidateId` | Must be unique within an `ExtractionPipelineResult` | Stage 8 (Result Assembly) |

### CreateScenariosInput validation (server-side)

| Field | Rule | Enforcement point |
|---|---|---|
| `items` | Non-empty array | HotChocolate input type (server rejects empty batch) |
| `items[n].title` | Non-empty, max 500 chars | HotChocolate input type — same rule as FR-002 (US1) |
| `items[n].kind` | Valid `ScenarioKind` value | HotChocolate input type — same rule as FR-003 (US1) |
| `items[n].projectId` | Non-empty | HotChocolate input type — same rule as US1 |
| `extractionMetadata` | Optional; if present, `totalExtracted ≥ 0`, `selectedCount ≥ 0`, `extractionDurationMs ≥ 0` | HotChocolate input type |

**Client validation does not replace server validation.** Every `CreateScenarioInput` element is validated by the server regardless of client-side pipeline guarantees.

---

## Observability Model Fields

These fields appear in structured log events (Serilog). They are never stored in the database but are part of the model design because their values are derived from the data models above.

| Log event | Source model | Fields logged |
|---|---|---|
| `ExtractionTriggered` | Raw input + `PipelineStatus.EmptyInput` / `InputTooLarge` gate | `inputLengthChars`, `inputLineCount`, `sessionId` |
| `ExtractionCompleted` | `ExtractionPipelineResult` | `candidateCount`, `requirementCount`, `testCount`, `needsClarificationCount`, `durationMs` |
| `ExtractionEmpty` | `ExtractionPipelineResult` where `Status != Success` | `inputLengthChars`, `reason` (derived from `PipelineStatus`) |
| `CandidateReviewSaved` | `CreateScenariosPayload` + `ExtractionMetadataInput` | `selectedCount`, `totalExtracted`, `scenariosCreated` (`successCount`), `failedCount`, `durationMs`, `projectId`, `correlationId` |
| `CandidateReviewAbandoned` | `ExtractionReviewState` at component disposal | `totalExtracted` (`PipelineResult.Candidates.Count`), `selectedCount` (`SelectedCandidateIds.Count`) |

**Privacy constraint**: No log event may carry any field derived from the raw pasted text content. `ExtractionMetadataInput` must not carry text fields (enforced at code review).

---

## Extensibility: Reserved Fields for Future AI Integration

The following fields are defined on current models but intentionally left unused in v1. They are reserved to avoid a breaking model change when AI-assisted classification is introduced.

| Model | Reserved field | Type | Purpose when activated |
|---|---|---|---|
| `ExtractionCandidate` | `Confidence` | `float?` | Classification confidence score (0.0–1.0) from an AI classifier. Null in v1 (deterministic classifier has no confidence concept). Used in a future version to sort or highlight low-confidence candidates in the review UI. |
| `ClassificationSignal` | `AiClassifier` | enum value | Marks candidates classified by an AI model rather than a deterministic rule. Adding this value is non-breaking because the field is opaque to the v1 UI. |

No AI-specific models are introduced. The `IScenarioExtractionService` interface contract (`Extract(string) → ExtractionPipelineResult`) is the stable extensibility seam. An AI-backed implementation returns the same model types, including `Confidence` populated with a real value.

---

## US1 Compatibility Statement

The US1 data model is unchanged. The following invariants hold after US2 is delivered:

- The `Scenario` entity: no new fields, no removed fields, no type changes.
- The `ScenarioKind` enum: no new values, no renamed values.
- The `scenarios` table: no new columns, no changed columns, no new indexes.
- The `createScenario` (single) mutation: signature unchanged, behavior unchanged.
- The `scenarios` query: unchanged.

Scenarios created via the US2 batch save path are indistinguishable from scenarios created manually via the US1 form. They appear in the same list, carry the same structure, and have no provenance marker in v1.

---

## Out of Scope (US2)

- Draft persistence for the extraction review state between browser sessions
- Provenance tracking (recording that a Scenario originated from an extraction)
- Candidate descriptions (extraction produces titles only; `Scenario.Description` is always empty on batch-save)
- Candidate deduplication against existing `Scenario` records
- User-configurable classification rules or thresholds
- AI confidence scores surfaced in the v1 review UI
- Reclassification of individual candidates before save

---

# Data Model: US3 — Deterministic Rule Engine for Scenario Extraction

**Phase**: Design | **Date**: 2026-05-21 | **Plan**: [plan.md §US3](plan.md) | **Spec**: [spec.md §US3](spec.md)

---

## Model Overview

US3 introduces no new persisted entities and no database migration. All new models are either rule engine configuration (application-lifetime, constructed once at startup and shared across all extraction runs) or pipeline-internal transients (execution-lifetime, created per extraction run and discarded on completion).

The two pipeline stages that change behaviour — Stage 4 (Structure Filter) and Stage 6 (Classification) — delegate to the rule engine. All other stages are untouched. The external shape of `ScenarioExtractionService`, `IScenarioExtractionService`, `ExtractionCandidate`, and `ExtractionPipelineResult` is unchanged.

Models are grouped by their tier and lifetime:

| Model | Tier | Lifetime | Persisted |
|---|---|---|---|
| `FilterCondition` | Rule engine configuration | Application lifetime | No |
| `BlockTypeMatchCondition` | Rule engine configuration | Application lifetime | No |
| `ContentLengthBelowCondition` | Rule engine configuration | Application lifetime | No |
| `ClassificationCondition` | Rule engine configuration | Application lifetime | No |
| `PatternMatchCondition` | Rule engine configuration | Application lifetime | No |
| `UnconditionalCondition` | Rule engine configuration | Application lifetime | No |
| `ClassificationOutcome` | Rule engine configuration | Application lifetime | No |
| `FilterRule` | Rule engine configuration | Application lifetime | No |
| `ClassificationRule` | Rule engine configuration | Application lifetime | No |
| `ExtractionRuleSet` | Rule engine configuration | Application lifetime | No |
| `RuleEvaluationResult` | Pipeline-internal | Pipeline execution only | No |
| `RuleExecutionSummary` | Pipeline-internal | Pipeline execution only | No |

---

## Rule Engine Configuration Models

These models exist for the lifetime of the Blazor WASM application process. They are constructed once — during DI composition at application startup — and are immutable thereafter. They do not change between extraction runs, and they are not observable outside the rule engine and the pipeline stage code that calls it.

---

### FilterCondition

The abstract base type for all filter rule match predicates. A `FilterCondition` evaluates a `TextBlock` (raw, before content extraction) and returns a boolean match result. Two concrete subtypes are defined for US3.

`FilterCondition` subtypes operate on block structure, not on stripped content. They receive the `TextBlock` produced by Stage 3 Block Partitioning — not the stripped text produced by Stage 5 Content Extraction.

---

#### BlockTypeMatchCondition

A `FilterCondition` that matches when a block's `BlockType` equals a specific target value.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `TargetBlockType` | `BlockType` | No | The `BlockType` value that triggers a match. |

**Match behaviour**: Returns `true` when `block.BlockType == TargetBlockType`. Returns `false` otherwise.

**Usage in default rule set**: One `BlockTypeMatchCondition` per filtered block type. Produces all nine `Filter:*` rules in `ExtractionRuleSet.Default()`.

---

#### ContentLengthBelowCondition

A `FilterCondition` that matches when a block's raw text length is below a threshold. Operates on `TextBlock.RawText` length before markdown stripping.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `ThresholdChars` | `int` | No | Minimum character count (exclusive). Blocks with `RawText.Length < ThresholdChars` match this condition. |

**Match behaviour**: Returns `true` when `block.RawText.Length < ThresholdChars`. Returns `false` otherwise.

**Usage in default rule set**: Not used. This condition type is defined for extensibility — a future rule could pre-filter very short raw blocks before Stage 5 processing. The current Stage 5 minimum-length check operates on stripped text after markdown removal, which is a distinct and finer-grained check that is not expressible as a `FilterCondition` operating on raw block text. The two checks are complementary, not redundant.

---

### ClassificationCondition

The abstract base type for all classification rule match predicates. A `ClassificationCondition` evaluates the stripped candidate text produced by Stage 5 Content Extraction and returns a boolean match result. Two concrete subtypes are defined for US3.

`ClassificationCondition` subtypes operate on stripped text. They receive the text after list markers, inline code backticks, link syntax, and other markdown symbols have been removed by Stage 5.

---

#### PatternMatchCondition

A `ClassificationCondition` that matches when the stripped candidate text satisfies a compiled regular expression.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Pattern` | `Regex` | No | Pre-compiled regular expression. Compiled at rule construction time (startup), not at evaluation time. Must use `RegexOptions.Compiled \| RegexOptions.CultureInvariant`. |

**Match behaviour**: Returns `true` when `Pattern.IsMatch(strippedText)` returns `true`. Returns `false` otherwise.

**Safety invariants** (enforced at rule construction time, verified by startup validation):
- Pattern must not contain nested quantifiers (`(a+)+`, `(a|a)*`).
- Pattern must use word-boundary assertions (`\b`) where whole-word matching is required.
- No backreferences permitted.
- Evaluation is bounded by `IExtractionConfiguration.MaxLineLengthForPatternMatching` (2,000 characters). Text exceeding this length is truncated before pattern evaluation. This bound applies to all `PatternMatchCondition` instances uniformly; it is not a per-rule configuration value.

**Usage in default rule set**: Used by all named classification rules: `Classify:BddPattern`, `Classify:Rfc2119Uppercase`, `Classify:Rfc2119Lowercase`, `Classify:FrPrefix`, `Classify:QuestionTerminator`, `Classify:DeferralMarker`.

---

#### UnconditionalCondition

A `ClassificationCondition` that always matches, regardless of the stripped candidate text. Used exclusively by the Default fallback rule.

No fields. This type carries no state.

**Match behaviour**: Always returns `true`.

**Invariant**: Exactly one `ClassificationRule` in any valid `ExtractionRuleSet` may carry an `UnconditionalCondition`. That rule must have `Priority == 0`. Startup validation enforces this.

**Purpose**: Guarantees that every candidate-eligible block receives a classification. The Default rule is the terminal safety net; a block that matches no named rule will always receive `(ScenarioKind.NeedsClarification, ClassificationSignal.Default)`.

---

### ClassificationOutcome

The result pair produced by a winning `ClassificationRule`. Pairs a `ScenarioKind` value (the classification) with a `ClassificationSignal` value (the observability record of which rule fired).

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Kind` | `ScenarioKind` | No | The classification assigned to the candidate. One of `Requirement`, `Test`, `NeedsClarification`. Maps 1:1 to `ExtractionCandidate.Classification`. |
| `Signal` | `ClassificationSignal` | No | The signal that produced this classification. Maps 1:1 to `ExtractionCandidate.ClassificationSignal`. Each named rule carries a distinct `Signal` value. The Default rule carries `ClassificationSignal.Default`. |

**Invariant**: `Kind` and `Signal` must be consistent. The pairing is fixed at rule definition time; it cannot be split by rule engine evaluation. The following pairings are valid in the default rule set:

| `Signal` | `Kind` |
|---|---|
| `BddPattern` | `Test` |
| `Rfc2119Uppercase` | `Requirement` |
| `Rfc2119Lowercase` | `Requirement` |
| `FrPrefix` | `Requirement` |
| `QuestionTerminator` | `NeedsClarification` |
| `DeferralMarker` | `NeedsClarification` |
| `Default` | `NeedsClarification` |

A future AI-assisted rule would carry `(NeedsClarification | Requirement | Test, AiClassifier)` where `AiClassifier` is a future `ClassificationSignal` value not yet defined.

---

### FilterRule

A rule that determines whether a `TextBlock` is candidate-eligible. A `FilterRule` match causes the block to be discarded before any classification is attempted.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Name` | `string` | No | Unique identifier within the `ExtractionRuleSet`. Convention: `Filter:<BlockTypeName>` (e.g., `Filter:Heading`). Used in diagnostic output and rule engine startup validation. |
| `Priority` | `int` | No | Evaluation order within the filter pass. Higher value = evaluated first. All block-type filter rules in the default set share priority 100 and are applied in registration order. |
| `Condition` | `FilterCondition` | No | The match predicate. Evaluated against the raw `TextBlock`; never against stripped text. |

**Lifecycle**: Created once in `ExtractionRuleSet.Default()` at application startup. Immutable thereafter. Shared across all extraction runs for the lifetime of the process.

**Invariant**: `Name` is unique across all `FilterRule` and `ClassificationRule` instances in the same `ExtractionRuleSet`. Names are compared case-sensitively.

**Invariant**: `Priority` must be a positive integer (greater than 0). Priority 0 is reserved exclusively for the unconditional Default `ClassificationRule`.

---

### ClassificationRule

A rule that assigns a classification to a candidate that has passed all filter rules.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Name` | `string` | No | Unique identifier within the `ExtractionRuleSet`. Convention: `Classify:<SignalName>` (e.g., `Classify:Rfc2119Uppercase`). |
| `Priority` | `int` | No | Conflict-resolution weight. Higher value = wins when multiple rules match the same candidate. The Default rule carries priority 0; all named rules carry positive values. Priority spacing of 10 is used in the default rule set to allow future rules to be inserted without renumbering. |
| `ApplicableBlockTypes` | `BlockType[]?` | Yes | Optional scope constraint. When `null`, the rule is evaluated for all candidate-eligible block types. When set, the rule is skipped for any block whose `BlockType` is not in the array. |
| `Condition` | `ClassificationCondition` | No | The match predicate. Evaluated against the stripped candidate text from Stage 5. |
| `Outcome` | `ClassificationOutcome` | No | The `(ScenarioKind, ClassificationSignal)` pair assigned when this rule wins conflict resolution. |

**Lifecycle**: Created once in `ExtractionRuleSet.Default()` at application startup. Immutable thereafter.

**Invariant**: `Priority == 0` if and only if `Condition` is `UnconditionalCondition`. No named rule may carry priority 0. No unconditional rule may carry a positive priority.

**Invariant**: `Name` is unique across both `FilterRule` and `ClassificationRule` lists in the same `ExtractionRuleSet`.

---

### ExtractionRuleSet

The container that holds the complete, validated, ordered set of rules for one rule engine instance. Constructed once at application startup by the static factory method `ExtractionRuleSet.Default()`.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `FilterRules` | `IReadOnlyList<FilterRule>` | No | All filter rules, sorted by `Priority` descending at construction time. The sort is stable: rules with equal priority appear in registration order. |
| `ClassificationRules` | `IReadOnlyList<ClassificationRule>` | No | All classification rules, sorted by `Priority` descending at construction time. The Default rule (priority 0) always appears last after sorting. |

**Lifecycle**: Created once by `ExtractionRuleSet.Default()`. Passed to the `ExtractionRuleEngine` constructor. The rule engine validates it at construction time (startup). Immutable after construction. Shared across all extraction runs for the lifetime of the process.

**`ExtractionRuleSet.Default()` — default rule set specification:**  
This factory method produces the rule set that replicates exact US2 pipeline behaviour. Every rule in the default set is listed below.

*Filter rules (Stage 4 equivalents):*

| Rule Name | Condition Type | Target / Threshold | Priority |
|---|---|---|---|
| `Filter:Heading` | `BlockTypeMatch` | `BlockType.Heading` | 100 |
| `Filter:FencedCodeBlock` | `BlockTypeMatch` | `BlockType.FencedCodeBlock` | 100 |
| `Filter:Blockquote` | `BlockTypeMatch` | `BlockType.Blockquote` | 100 |
| `Filter:HorizontalRule` | `BlockTypeMatch` | `BlockType.HorizontalRule` | 100 |
| `Filter:HtmlComment` | `BlockTypeMatch` | `BlockType.HtmlComment` | 100 |
| `Filter:YamlFrontMatter` | `BlockTypeMatch` | `BlockType.YamlFrontMatter` | 100 |
| `Filter:Empty` | `BlockTypeMatch` | `BlockType.Empty` | 100 |
| `Filter:TableHeaderRow` | `BlockTypeMatch` | `BlockType.TableHeaderRow` | 100 |
| `Filter:TableSeparatorRow` | `BlockTypeMatch` | `BlockType.TableSeparatorRow` | 100 |

*Classification rules (Stage 6 equivalents):*

| Rule Name | Priority | Condition Type | `ApplicableBlockTypes` | Outcome Kind | Outcome Signal |
|---|---|---|---|---|---|
| `Classify:BddPattern` | 70 | `PatternMatch` | `null` (all) | `Test` | `BddPattern` |
| `Classify:Rfc2119Uppercase` | 60 | `PatternMatch` | `null` (all) | `Requirement` | `Rfc2119Uppercase` |
| `Classify:Rfc2119Lowercase` | 50 | `PatternMatch` | `null` (all) | `Requirement` | `Rfc2119Lowercase` |
| `Classify:FrPrefix` | 40 | `PatternMatch` | `null` (all) | `Requirement` | `FrPrefix` |
| `Classify:QuestionTerminator` | 30 | `PatternMatch` | `null` (all) | `NeedsClarification` | `QuestionTerminator` |
| `Classify:DeferralMarker` | 20 | `PatternMatch` | `null` (all) | `NeedsClarification` | `DeferralMarker` |
| `Classify:Default` | 0 | `Unconditional` | `null` (all) | `NeedsClarification` | `Default` |

**Startup validation** (enforced in `ExtractionRuleEngine` constructor, not in `ExtractionRuleSet` itself):

| Check | Failure message |
|---|---|
| At least one `ClassificationRule` is present | "Rule set contains no classification rules" |
| Exactly one `ClassificationRule` with `UnconditionalCondition` and `Priority == 0` | "Rule set must contain exactly one Default (unconditional, priority-0) classification rule" |
| All rule `Name` values are unique (case-sensitive, across both lists) | "Duplicate rule name: {name}" |
| All `PatternMatchCondition.Pattern` instances compile without exception | "Rule '{name}' has an invalid regex pattern: {exception}" |
| No `ClassificationRule` other than the Default rule has `Priority == 0` | "Rule '{name}' has reserved priority 0; only the Default rule may use priority 0" |

---

## Pipeline-Internal Models (Rule Execution)

These models exist only during a single pipeline execution. They are produced by the rule engine during Stage 4 and Stage 6 evaluation, consumed within `ScenarioExtractionService`, and discarded at the end of the pipeline run. They do not cross any component boundary and are never observable outside the pipeline.

---

### RuleEvaluationResult

The output of `IExtractionRuleEngine.Evaluate(block, strippedText)` for a single `TextBlock`. Produced once per block that is processed by the rule engine.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `IsFiltered` | `bool` | No | `true` if a `FilterRule` matched this block. When `true`, all classification fields are null and the block is not a candidate. |
| `Classification` | `ScenarioKind?` | Yes | The winning classification. Null when `IsFiltered` is `true`; populated when `IsFiltered` is `false`. Maps to `ExtractionCandidate.Classification`. |
| `Signal` | `ClassificationSignal?` | Yes | The winning rule's signal. Null when `IsFiltered` is `true`; populated when `IsFiltered` is `false`. Maps to `ExtractionCandidate.ClassificationSignal`. |
| `WinningRuleName` | `string?` | Yes | The `Name` of the winning `ClassificationRule`. Null when `IsFiltered` is `true`. Reserved for diagnostic use; not included in any log event in US3. |
| `EvaluatedRuleCount` | `int` | No | Total number of rules evaluated for this block across both the filter pass and the classification pass. Zero is not a valid value: the filter pass always evaluates at least one rule, and if the filter pass produces no match, the classification pass always evaluates at least the Default rule. Accumulated into `RuleExecutionSummary.TotalRulesEvaluated`. |

**Invariants:**
- `Classification` is non-null if and only if `IsFiltered == false`.
- `Signal` is non-null if and only if `IsFiltered == false`.
- `WinningRuleName` is non-null if and only if `IsFiltered == false`.
- `EvaluatedRuleCount ≥ 1` always.
- When `IsFiltered == false`, `Classification` and `Signal` always form a valid `ClassificationOutcome` pair (guaranteed by the Default fallback rule invariant in `ExtractionRuleSet`).

**Lifecycle**: Created inside `ExtractionRuleEngine.Evaluate()`. Consumed by `ScenarioExtractionService` in Stage 4 (filter check only — `IsFiltered` is read; classification fields are not needed at Stage 4) and Stage 6 (classification fields are read to populate `ExtractionCandidate`). `EvaluatedRuleCount` is accumulated into `RuleExecutionSummary` by `ScenarioExtractionService`. The result object is discarded after these reads complete.

---

### RuleExecutionSummary

An aggregation of all `RuleEvaluationResult` values produced during a single pipeline execution. Accumulated by `ScenarioExtractionService` as it processes each `TextBlock` through the rule engine. Consumed to populate the `rulesEvaluatedCount` field of the `ExtractionCompleted` structured log event and to derive quality metrics.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `TotalRulesEvaluated` | `int` | No | Sum of `RuleEvaluationResult.EvaluatedRuleCount` across all blocks processed by the rule engine in this extraction run (filtered and non-filtered). Published in the `ExtractionCompleted` log event as `rulesEvaluatedCount`. |
| `FilteredBlockCount` | `int` | No | Number of blocks discarded by a `FilterRule` in this run. Equal to the count of `RuleEvaluationResult` instances where `IsFiltered == true`. Not currently logged but available for future telemetry. |
| `DefaultFallbackCount` | `int` | No | Number of candidates whose winning rule was `Classify:Default` (signal `ClassificationSignal.Default`). A high proportion of default-classified candidates relative to total candidates is a quality signal indicating that classification heuristics are not matching well for this input. Not currently logged; reserved for future quality dashboards. |

**Derived invariant**: `FilteredBlockCount + CandidateClassificationCount == total blocks evaluated by the rule engine`, where `CandidateClassificationCount` is the number of blocks that were not filtered. This count is derivable as `TotalRulesEvaluated > 0 ? (blocks submitted) - FilteredBlockCount : 0`; it is not stored separately.

**Lifecycle**: Initialised with all-zero fields at the start of Stage 4 processing. Incremented during each Stage 4 filter check (`FilteredBlockCount`) and each Stage 6 classification evaluation (`TotalRulesEvaluated`, `DefaultFallbackCount`). Read once at Stage 8 result assembly to populate the `ExtractionCompleted` log payload. Discarded after the `ExtractionPipelineResult` is returned from `ScenarioExtractionService.ExtractAsync()`.

**Privacy constraint**: `RuleExecutionSummary` contains no text derived from the pasted input. All fields are numeric counts. This invariant holds for all possible inputs and is not conditional on input content.

---

## Existing Models — Changes and Compatibility

The following US2 models are referenced by the US3 rule engine but are not changed by US3.

### TextBlock (unchanged)

`TextBlock` is the input to `IExtractionRuleEngine.Evaluate()`. The fields `BlockType` and `RawText` are read by `FilterCondition` subtypes during the filter pass. `PrecedingHeading` is not read by the rule engine; it is carried forward by Stage 8 Result Assembly as it was in US2. No new fields, no removed fields, no type changes.

### BlockType enum (unchanged)

`BlockType` is referenced in `BlockTypeMatchCondition.TargetBlockType` and in `ClassificationRule.ApplicableBlockTypes`. No new values are added by US3. The full enum is unchanged.

If a new `BlockType` value is added in a future version, a corresponding `FilterRule` with a `BlockTypeMatchCondition` must be added to `ExtractionRuleSet.Default()` if the new block type should be non-extractable. Omitting the filter rule would cause blocks of the new type to flow through to classification — the Default fallback rule would classify them as `NeedsClarification`. This is a rule set completeness concern documented here as an authoring guideline.

### ClassificationSignal enum (unchanged)

`ClassificationSignal` is referenced in `ClassificationOutcome.Signal`. No new values are added by US3. The `AiClassifier` value (reserved for future AI integration) is not added at this stage. All seven existing values (`BddPattern`, `Rfc2119Uppercase`, `Rfc2119Lowercase`, `FrPrefix`, `QuestionTerminator`, `DeferralMarker`, `Default`) map 1:1 to the seven `ClassificationRule` entries in `ExtractionRuleSet.Default()`. No value is orphaned; no rule carries a signal value that has no corresponding enum member.

### ScenarioKind enum (unchanged)

`ScenarioKind` is referenced in `ClassificationOutcome.Kind`. No new values. The three values (`Requirement`, `Test`, `NeedsClarification`) cover all possible rule outcomes in the default rule set.

### ExtractionCandidate (unchanged — two fields now populated by rule engine)

The `ExtractionCandidate` model is structurally unchanged. Two fields that were previously populated by hardcoded Stage 4 and Stage 6 logic are now populated from `RuleEvaluationResult`:

| Field | Previously populated by | Now populated by |
|---|---|---|
| `Classification` | Hardcoded Stage 6 `if-else` chain | `RuleEvaluationResult.Classification` |
| `ClassificationSignal` | Hardcoded Stage 6 `if-else` chain | `RuleEvaluationResult.Signal` |

No other `ExtractionCandidate` fields are affected. The `Confidence` field (reserved, always `null`) remains null — the deterministic rule engine does not populate confidence scores. The type, constraints, lifecycle, and observability model for `ExtractionCandidate` are otherwise identical to the US2 specification.

### ExtractionPipelineResult (unchanged)

`ExtractionPipelineResult` fields, factory methods, invariants, and lifecycle are unchanged. The `DurationMs` field continues to measure the complete pipeline execution time including rule engine evaluation overhead.

### IExtractionConfiguration (unchanged — one field used by rule engine)

`IExtractionConfiguration.MaxLineLengthForPatternMatching` is the per-line length cap enforced before any `PatternMatchCondition` evaluation. This field already exists from US2 (plan.md §Security Strategy). No new configuration fields are added by US3. The existing `MaxInputLengthChars` and `MinCandidateLengthChars` fields are unchanged.

---

## Observability Model Changes

### ExtractionCompleted log event (one new field)

| Log event | Change | New field | Source |
|---|---|---|---|
| `ExtractionCompleted` | Add one field | `rulesEvaluatedCount: int` | `RuleExecutionSummary.TotalRulesEvaluated` |

All other fields of `ExtractionCompleted` (`candidateCount`, `requirementCount`, `testCount`, `needsClarificationCount`, `durationMs`) are unchanged.

No other log events are changed. `RuleExecutionSummary.FilteredBlockCount` and `RuleExecutionSummary.DefaultFallbackCount` are available for future log event additions but are not logged in US3.

**Privacy constraint**: `rulesEvaluatedCount` is a numeric count. It carries no text content derived from the pasted input. This is consistent with the existing constraint that all log fields in extraction events are counts, durations, or opaque identifiers — never text.

---

## Extensibility: Reserved Fields for Future AI Integration

The following fields and types from existing models are relevant to the AI integration seam established by US2 and preserved by US3.

| Model | Reserved field / value | Status in US3 | Future activation |
|---|---|---|---|
| `ExtractionCandidate` | `Confidence: float?` | Null; not populated by deterministic rule engine | An AI-backed `IExtractionRuleEngine` implementation would populate this from a model confidence score |
| `ClassificationSignal` | `AiClassifier` | Not added in US3 | A future enum value; marks candidates classified by an AI rule rather than a deterministic rule |

The `IExtractionRuleEngine` interface is the explicit AI integration seam. A future `AiExtractionRuleEngine` implementing this interface would:
- Accept the same `TextBlock` and stripped text inputs.
- Return a `RuleEvaluationResult` with `Signal = AiClassifier` (once that enum value exists).
- Optionally populate `ExtractionCandidate.Confidence` from a model-derived score.

No model changes are needed to accommodate this future implementation. The `RuleEvaluationResult` model as defined accommodates any implementation of `IExtractionRuleEngine`; the deterministic and AI-backed variants are indistinguishable at the interface boundary.

---

## Validation Rules (US3 rule engine models)

### ExtractionRuleSet validation (at `ExtractionRuleEngine` construction time)

| Rule | Enforcement point |
|---|---|
| At least one `ClassificationRule` present | `ExtractionRuleEngine` constructor |
| Exactly one `ClassificationRule` with `UnconditionalCondition` and `Priority == 0` | `ExtractionRuleEngine` constructor |
| All rule `Name` values unique (case-sensitive, across `FilterRules` and `ClassificationRules`) | `ExtractionRuleEngine` constructor |
| All `PatternMatchCondition.Pattern` values compile without exception | Rule construction time (not deferred to constructor) |
| No `ClassificationRule` other than the Default rule has `Priority == 0` | `ExtractionRuleEngine` constructor |

### FilterRule validation

| Rule | Enforcement point |
|---|---|
| `Name` is non-empty | `FilterRule` construction |
| `Priority > 0` | `FilterRule` construction |
| `Condition` is non-null | `FilterRule` construction |

### ClassificationRule validation

| Rule | Enforcement point |
|---|---|
| `Name` is non-empty | `ClassificationRule` construction |
| `Priority ≥ 0` | `ClassificationRule` construction |
| `Priority == 0` only if `Condition` is `UnconditionalCondition` | `ClassificationRule` construction |
| `Condition` is non-null | `ClassificationRule` construction |
| `Outcome.Kind` is a valid `ScenarioKind` value | `ClassificationOutcome` construction |
| `Outcome.Signal` is a valid `ClassificationSignal` value | `ClassificationOutcome` construction |

---

## US2 Compatibility Statement

The US2 data model is unchanged. The following invariants hold after US3 is delivered:

- `TextBlock`, `BlockType`, `ClassificationSignal`, `ScenarioKind`: no new values, no renamed values, no type changes.
- `ExtractionCandidate`: no new public fields, no removed fields. `Classification` and `ClassificationSignal` fields are populated by the same logic (priority-ordered signal detection) expressed through the rule engine rather than inline code. Values produced are identical.
- `ExtractionPipelineResult`: unchanged.
- `CandidateSaveState`, `ReviewSavePhase`: unchanged.
- `CreateScenariosInput`, `ExtractionMetadataInput`, `CreateScenariosPayload`, `CreateScenarioResult`: unchanged. GraphQL contract is unmodified.
- `Scenario` entity, `ScenarioKind` enum, `scenarios` table: unchanged.
- `IScenarioExtractionService` interface: unchanged.

`ExtractionCompleted` log event gains one new field (`rulesEvaluatedCount`). Existing consumers of this log event that do not read the new field are unaffected.

---

## Out of Scope (US3)

- Database schema changes: none required; extraction is entirely client-side
- GraphQL contract changes: none required; the rule engine is an internal pipeline concern
- `ContextRule` as a first-class rule type: section-aware context tracking remains an implicit accumulator in Stage 3/8; deferred to a future version
- `RuleEvaluationResult.MatchedRuleNames` diagnostic field (list of all rules that matched, not just the winner): deferred; `WinningRuleName` is sufficient for US3
- User-configurable rules: rule definitions are developer-authored code only; runtime user configuration is not in scope
- Data-file rule definitions (JSON/YAML): deferred; code-defined rules are the chosen approach for US3
- AI confidence scores (`ExtractionCandidate.Confidence`): reserved field; not populated by the deterministic rule engine
- `ClassificationSignal.AiClassifier` enum value: not added in US3; reserved for a future AI integration iteration

---

# Data Model: US4 — Level 1 Configurable Extraction Rules

**Phase**: Design | **Date**: 2026-05-21 | **Plan**: [plan.md §US4](plan.md) | **Spec**: [spec.md §US4](spec.md)

---

## Model Overview

US4 introduces no new persisted entities and no database migration. All new models fall into one of three tiers:

1. **Configuration models** — POCOs bound from `appsettings.json` at application startup, consumed entirely within `ExtractionRuleSetCompiler.Compile()`, and discarded after the compiled `ExtractionRuleSet` is produced. These models are never readable at rule evaluation time.
2. **Rule engine configuration extensions** — additive fields, properties, and a new condition type added to the application-lifetime rule engine layer. Constructed once at startup; immutable thereafter.
3. **Compiler-internal transients** — exist only within the validation step of the compiler. Not observable outside the compiler.

The pipeline-internal models from US3 (`RuleEvaluationResult`, `RuleExecutionSummary`) are unchanged. The US3 `ExtractionRuleSet`, `FilterRule`, `ClassificationRule`, `ClassificationCondition` hierarchy, and all downstream models are unchanged except for the explicit additive fields listed below.

| Model | Tier | Lifetime | Persisted |
|---|---|---|---|
| `ExtractionRuleConfiguration` | Configuration (appsettings binding) | Startup only — discarded after compilation | No |
| `PrefixRuleEntry` | Configuration (sub-model of above) | Startup only — discarded after compilation | No |
| `ConfigurationViolation` | Compiler-internal transient | Validation step only | No |
| `PrefixMatchCondition` | Rule engine configuration (new condition type) | Application lifetime | No |
| `ExtractionRuleSet.IgnorePrefixes` | Rule engine configuration (new field on existing type) | Application lifetime | No |
| `IExtractionRuleEngine.IgnorePrefixes` | Rule engine interface (new property on existing interface) | Application lifetime | No |
| `ClassificationSignal.ConfiguredPrefix` | Pipeline-internal enum (additive value) | Pipeline execution only | No |

---

## Lifecycle and Ownership Boundaries

The three-tier lifecycle is the central architectural invariant of US4. Configuration models must not bleed into the compiled rule set tier; compiled rules must not retain references to configuration models; runtime evaluation state must not reference either.

```
Application startup
  │
  ├── IOptions<ExtractionRuleConfiguration> binds from appsettings.json §ExtractionRules
  │       ExtractionRuleConfiguration lifetime begins
  │
  ├── ExtractionRuleSetCompiler.Compile(baseSet, config)
  │   │
  │   ├── Step 1 — Validation
  │   │       If violation found: ConfigurationViolation constructed
  │   │           → ExtractionRuleConfigurationFailed Warning logged
  │   │           → ConfigurationViolation discarded
  │   │           → ExtractionRuleConfigurationFallback Info logged
  │   │           → baseSet (ExtractionRuleSet.Default()) returned immediately
  │   │
  │   ├── Steps 2–7 — Compilation (working List<> copies; discarded on method return)
  │   │       PrefixMatchCondition instances constructed (application lifetime begins)
  │   │       Extended PatternMatchCondition instances constructed (application lifetime begins)
  │   │
  │   └── Step 8 — Construct and return ExtractionRuleSet (configured)
  │               ExtractionRuleConfigurationLoaded Info logged
  │
  ├── ExtractionRuleConfiguration → no longer referenced; eligible for GC
  │
  └── IExtractionRuleEngine singleton registered with compiled ExtractionRuleSet
          Application lifetime for compiled rule set begins

Extraction session (runtime, per call to IScenarioExtractionService.ExtractAsync)
  │
  ├── Stage 5.5: ScenarioExtractionService reads _ruleEngine.IgnorePrefixes
  │       List<ContentItem> items filtered inline; no new model instance created
  │
  ├── Stage 4 + Stage 6: IExtractionRuleEngine.Evaluate(block, strippedText)
  │       RuleEvaluationResult constructed, consumed, discarded (pipeline execution only)
  │
  └── Stage 8: RuleExecutionSummary consumed, discarded
```

**Key invariant**: `IExtractionRuleEngine.Evaluate()` never reads `ExtractionRuleConfiguration`. All configuration is encoded in the `ExtractionRuleSet` at startup. No configuration object is reachable at evaluation time. This invariant preserves the US3 guarantee that evaluation is a pure function of its arguments and the compiled rule set.

---

## Configuration Models

Configuration models have startup-only lifetime. They are created by the .NET configuration binding system, consumed within `ExtractionRuleSetCompiler.Compile()`, and are not referenced after the compiled `ExtractionRuleSet` is registered. No rule engine type holds a reference to any configuration model.

---

### ExtractionRuleConfiguration

The top-level configuration POCO. Bound by `IOptions<ExtractionRuleConfiguration>` from the `ExtractionRules` section of `appsettings.json`.

| Field | Type | Default | Description |
|---|---|---|---|
| `BddKeywordAdditions` | `string[]` | `[]` | Additional words added to the BDD opener set (`Given`, `When`, `Then`, etc.). Matched word-boundary, case-insensitive. |
| `Rfc2119UppercaseAdditions` | `string[]` | `[]` | Additional uppercase keywords added to the RFC-2119 uppercase set (`MUST`, `SHALL`, etc.). Matched word-boundary, case-sensitive. |
| `Rfc2119LowercaseAdditions` | `string[]` | `[]` | Additional lowercase keywords added to the RFC-2119 lowercase set (`must`, `shall`, etc.). Matched word-boundary, case-insensitive. |
| `DeferralMarkerAdditions` | `string[]` | `[]` | Additional words added to the deferral marker set (`TBD`, `TODO`, etc.). Matched word-boundary, case-insensitive. |
| `PrefixRules` | `PrefixRuleEntry[]` | `[]` | New prefix-based classification rules. Each entry compiles to one `ClassificationRule` with a `PrefixMatchCondition`. |
| `IgnorePrefixes` | `string[]` | `[]` | Literal prefixes; candidates whose stripped text begins with a listed prefix are excluded at Stage 5.5 before classification. |
| `DisabledRuleNames` | `string[]` | `[]` | Names of default rules to exclude from the compiled rule set. Must exactly match names in `ExtractionRuleSet.Default()`. |
| `PriorityOverrides` | `Dictionary<string, int>` | `{}` | Priority values to assign to named rules. Keys must match default rule names. Values must be in range 1–99. |

**Empty-configuration invariant**: When all fields carry their empty defaults — either because no `ExtractionRules` section exists in `appsettings.json` or the section is present but empty — `ExtractionRuleSetCompiler.Compile()` returns `ExtractionRuleSet.Default()` unchanged. Extraction behavior is byte-for-byte identical to US3. This is the definitive mechanism for FR-US4-010 compliance.

**Null-array binding**: When the `ExtractionRules` section is absent, `IOptions<ExtractionRuleConfiguration>.Value` returns a default-constructed instance. Array fields may be `null` after binding (not `[]`) depending on the binding implementation. The compiler normalizes `null` arrays to empty arrays before validation; a null array is not itself a validation error.

**Privacy constraint**: These fields contain developer-authored vocabulary entries. They must not contain text from user-pasted specification content. This is an authoring constraint, not a runtime enforcement point.

**Lifecycle**: Created by `IOptions<>` at startup. Read once by `ExtractionRuleSetCompiler.Compile()`. Not read again after compilation. Eligible for garbage collection after `Program.cs` composition completes.

---

### PrefixRuleEntry

A sub-model within `ExtractionRuleConfiguration.PrefixRules`. Each entry compiles to exactly one `ClassificationRule` in the configured `ExtractionRuleSet`.

| Field | Type | Required | Default | Description |
|---|---|---|---|---|
| `Name` | `string?` | No | Auto-generated | Developer-assigned identifier used in `WinningRuleName` diagnostics and `DisabledRuleNames` targeting. When null or empty, the compiler generates `Configure:Prefix:{index}` (zero-based position in `PrefixRules`). |
| `Prefix` | `string` | Yes | — | Literal string matched against stripped candidate text via `StringComparison.OrdinalIgnoreCase`. No regex; no metacharacters. |
| `Classification` | `ScenarioKind` | Yes | — | The `ScenarioKind` assigned to candidates whose stripped text begins with `Prefix`. |
| `Priority` | `int` | No | `10` | Evaluation priority in the compiled rule set. Must satisfy `1 ≤ Priority ≤ 99`. Default priority 10 places prefix rules below all named default rules (lowest named default: `Classify:DeferralMarker` at 20) but above the unconditional fallback `Classify:Default` (priority 0). |

**Name auto-generation**: `Configure:Prefix:{index}` where `index` is the zero-based position in `PrefixRules`. Auto-generated names are stable for a given configuration but may change if entries are reordered. When a stable rule name is required (e.g., to target the rule in `DisabledRuleNames`), an explicit `Name` must be provided.

**One-to-one compilation**: Each `PrefixRuleEntry` produces exactly one `ClassificationRule`. The compiler never merges, splits, or reuses entries. The compiled rule carries `Outcome = (Classification, ClassificationSignal.ConfiguredPrefix)`.

**Lifecycle**: Sub-model within `ExtractionRuleConfiguration`. Same lifetime as parent; consumed at compilation, discarded after the compiled rule set is produced.

---

## Compiler-Internal Transient Models

These models exist only within `ExtractionRuleSetCompiler.Compile()` during the validation step. They are never returned from the compiler and are not observable outside it.

---

### ConfigurationViolation

An internal record capturing the failure context when a configuration validation rule is violated. Constructed on the first failure; its fields are transferred to the `ExtractionRuleConfigurationFailed` structured log event; the record is discarded immediately after the log call returns.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `FieldName` | `string` | No | Structural name of the configuration field that failed (e.g., `"BddKeywordAdditions"`, `"PrefixRules[2].Prefix"`, `"PriorityOverrides"`). Identifies the field precisely enough to locate the offending entry in `appsettings.json`. |
| `ViolationType` | `string` | No | Machine-readable rejection reason code. See §Validation Rules (US4) for all defined codes. |
| `EntryIndex` | `int?` | Yes | For array field violations: zero-based index of the offending entry within the array. Null for non-array fields and for dictionary key violations (which are identified by the key embedded in `FieldName`). |

**Privacy constraint**: `ConfigurationViolation` MUST NOT capture the value of the offending field — only the field name, rejection code, and optional index. This ensures that configured vocabulary entries do not appear in log output, consistent with the no-raw-text constraint on all extraction pipeline events.

**Fail-fast behavior**: The compiler constructs a single `ConfigurationViolation` for the first failure encountered and returns immediately. Validation is fail-fast, not accumulate-and-report. A single violation causes the entire configuration to be rejected.

**Lifecycle**: Constructed within the validation step of `ExtractionRuleSetCompiler.Compile()`. Fields written to structured log parameters in the `ExtractionRuleConfigurationFailed` event. Eligible for GC as soon as the log call returns.

---

## Rule Engine Configuration Extensions

These are additive changes to the rule engine layer. They are application-lifetime: constructed or populated once by the compiler at startup and never changed thereafter.

---

### PrefixMatchCondition

A new concrete subtype of `ClassificationCondition` introduced by US4. Implements prefix-based candidate text matching without regex.

| Field | Type | Nullable | Description |
|---|---|---|---|
| `Prefix` | `string` | No | The literal prefix to match. Stored exactly as provided after validation (non-empty, metacharacter-free, printable ASCII). |

**Match behaviour**: Returns `true` when `strippedText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)`. Returns `false` otherwise. The input is the stripped candidate text from Stage 5 — not raw block text.

**No regex involvement**: `PrefixMatchCondition` is a direct string comparison. `Regex.Escape` is not applied because no regex is used. There is no ReDoS surface for prefix rules. Evaluation terminates at prefix length regardless of input length — there is no input-length bound equivalent to `MaxLineLengthForPatternMatching`.

**Comparison to `PatternMatchCondition`**:

| Aspect | `PatternMatchCondition` | `PrefixMatchCondition` |
|---|---|---|
| Match mechanism | Compiled `Regex.IsMatch` | `string.StartsWith` |
| Input length bound | Capped at `MaxLineLengthForPatternMatching` (2,000 chars) | Not required — terminates at prefix length |
| ReDoS surface | Mitigated by metacharacter prohibition + cap | None |
| Construction cost | Regex compilation at startup | None |
| Signal produced | Rule-group-specific signal (e.g., `Rfc2119Uppercase`) | `ConfiguredPrefix` |

The two condition types are not interchangeable. A `PrefixMatchCondition` cannot be substituted with a `PatternMatchCondition` anchored at `^` without reintroducing a regex surface; conversely, a `PatternMatchCondition` cannot be substituted with a `PrefixMatchCondition` for rules requiring word-boundary or mid-line matching.

**Hierarchy placement**: Sibling of `PatternMatchCondition` and `UnconditionalCondition` in the `ClassificationCondition` abstract hierarchy. Does not inherit from either concrete type.

**Lifecycle**: Constructed by `ExtractionRuleSetCompiler` once per `PrefixRuleEntry` at startup. Held inside a `ClassificationRule` in `ExtractionRuleSet.ClassificationRules`. Immutable. Shared across all extraction runs for the application lifetime.

---

### ExtractionRuleSet — `IgnorePrefixes` (additive field)

One new field is added to the existing `ExtractionRuleSet` model alongside the existing `FilterRules` and `ClassificationRules` fields.

| New Field | Type | Default in `Default()` | Description |
|---|---|---|---|
| `IgnorePrefixes` | `IReadOnlyList<string>` | `ImmutableArray<string>.Empty` | The compiled ignore prefix list. Read by `ScenarioExtractionService` at Stage 5.5. An empty list is a no-op with zero evaluation overhead. |

**Ownership**: Populated exclusively by `ExtractionRuleSetCompiler` from `ExtractionRuleConfiguration.IgnorePrefixes` after validation. `ExtractionRuleSet.Default()` always returns `ImmutableArray<string>.Empty` for this field; the default factory method is unchanged.

**Consumption**: Read by `ScenarioExtractionService` via `IExtractionRuleEngine.IgnorePrefixes` at Stage 5.5. Not read by `ExtractionRuleEngine.Evaluate()`. Not accessible to any condition type. No candidate model field is populated from this list — the ignore-prefix check is a discard gate, not a classification.

**Ordering**: Entries appear in the same order as `ExtractionRuleConfiguration.IgnorePrefixes`. Because the Stage 5.5 check uses `Any()`, the first matching prefix causes the item to be discarded. Declaration order is preserved for predictability; there is no semantic ordering requirement.

---

### `IExtractionRuleEngine` — `IgnorePrefixes` (additive property)

One new read-only property is added to the existing `IExtractionRuleEngine` interface.

| New Property | Type | Description |
|---|---|---|
| `IgnorePrefixes` | `IReadOnlyList<string>` | Exposes the compiled ignore prefix list to the pipeline. Implemented as `{ get => _ruleSet.IgnorePrefixes; }` in `ExtractionRuleEngine`. |

**Scope**: `IExtractionRuleEngine` is `internal`. Adding this property has no impact on any public API. No external consumer of `BirkNext.Web` is affected.

**Test double requirement**: Any mock or stub of `IExtractionRuleEngine` used in tests that exercise the Stage 5.5 code path must configure this property. Tests that do not enter Stage 5.5 are unaffected. The correct Moq setup for the default (no-ignore-prefix) case is `.Setup(e => e.IgnorePrefixes).Returns(ImmutableArray<string>.Empty)`.

---

## Enum Extensions

---

### `ClassificationSignal.ConfiguredPrefix` (additive)

One new value is added to the existing `ClassificationSignal` enum.

| Value | Classification produced | Trigger |
|---|---|---|
| `ConfiguredPrefix` | The `ScenarioKind` from the matching `PrefixRuleEntry` | Stripped candidate text begins with a configured prefix and the `PrefixMatchCondition` fires as the winning rule |

**Updated full enum with priority order** (highest to lowest):

| Value | Classification | Priority source |
|---|---|---|
| `BddPattern` | `Test` | `Classify:BddPattern` — priority 70 |
| `Rfc2119Uppercase` | `Requirement` | `Classify:Rfc2119Uppercase` — priority 60 |
| `Rfc2119Lowercase` | `Requirement` | `Classify:Rfc2119Lowercase` — priority 50 |
| `FrPrefix` | `Requirement` | `Classify:FrPrefix` — priority 40 |
| `QuestionTerminator` | `NeedsClarification` | `Classify:QuestionTerminator` — priority 30 |
| `DeferralMarker` | `NeedsClarification` | `Classify:DeferralMarker` — priority 20 |
| `ConfiguredPrefix` | Per `PrefixRuleEntry.Classification` | Default priority 10; configurable 1–99 |
| `Default` | `NeedsClarification` | `Classify:Default` — priority 0 |

**Keyword addition signals are unchanged**: When a configured keyword addition fires — for example, a custom BDD opener added via `BddKeywordAdditions` — the signal remains `BddPattern` (the signal of the rule group whose pattern was extended). `ConfiguredPrefix` is emitted only when a `PrefixMatchCondition` is the winning condition.

**Additive and non-breaking**: `ClassificationSignal` is not surfaced in the v1 review UI. Existing `switch` statements over `ClassificationSignal` with a `_` or `default` catch-all handle `ConfiguredPrefix` without modification. All enum-consuming code must be audited at implementation time to confirm coverage.

---

## Compiled Rule Set Shape

This section makes the compiler's output contract explicit: what a fully configured `ExtractionRuleSet` looks like after all compilation steps have been applied.

### `FilterRules` (unchanged unless a filter rule is disabled or overridden)

The compiled `FilterRules` list is derived from `ExtractionRuleSet.Default().FilterRules` by:
1. Removing rules whose `Name` appears in `DisabledRuleNames`.
2. Replacing rules whose `Name` appears in `PriorityOverrides` with new instances carrying the overridden priority.
3. Sorting by `Priority` descending (stable sort).

When no filter rule is disabled or overridden, the compiled `FilterRules` list is structurally identical to the default — same rule instances in the same order.

### `ClassificationRules` (extended by US4)

The compiled `ClassificationRules` list is derived from `ExtractionRuleSet.Default().ClassificationRules` by:
1. Removing rules whose `Name` appears in `DisabledRuleNames`.
2. Replacing rules whose `Name` appears in `PriorityOverrides` with new instances at the overridden priority.
3. Rebuilding `PatternMatchCondition` for each keyword group with non-empty additions: the rule's `Name` and `Priority` are unchanged; only the `Condition` field holds a new `Regex` instance with the extended alternation (base keywords + configured additions, all `Regex.Escape`d, `\b(?:...)\b` wrapped).
4. Appending one new `ClassificationRule` per `PrefixRuleEntry`: `Name = entry.Name or auto`, `Priority = entry.Priority`, `Condition = new PrefixMatchCondition(entry.Prefix)`, `Outcome = (entry.Classification, ConfiguredPrefix)`.
5. Sorting by `Priority` descending (stable sort).

`Classify:Default` (unconditional, priority 0) is always present and always last after sorting. The startup validation in `ExtractionRuleEngine` enforces this invariant on the compiled set.

**Keyword extension identity**: A keyword-extended rule (e.g., `Classify:BddPattern` with an added opener) has the same `Name`, `Priority`, `Outcome`, and `ApplicableBlockTypes` as the corresponding default rule. Only `Condition.Pattern` differs — the new `Regex` incorporates the additional keywords. `WinningRuleName` continues to report `Classify:BddPattern`; `ClassificationSignal` continues to report `BddPattern`. `ConfiguredPrefix` is never emitted by a keyword-extended rule.

### Illustration: configured rule set with representative extensions

```
Input configuration:
  BddKeywordAdditions: ["Explore"]
  PrefixRules: [{ Prefix: "REQ-", Classification: Requirement },
                { Prefix: "INFEASIBLE:", Classification: NeedsClarification }]
  IgnorePrefixes: ["NOTE:"]

Compiled FilterRules (9 rules, unchanged):
  Filter:Heading (100), Filter:FencedCodeBlock (100), Filter:Blockquote (100),
  Filter:HorizontalRule (100), Filter:HtmlComment (100), Filter:YamlFrontMatter (100),
  Filter:Empty (100), Filter:TableHeaderRow (100), Filter:TableSeparatorRow (100)

Compiled ClassificationRules (7 default + 2 configured = 9 rules, sorted):
  Classify:BddPattern        (70)  PatternMatchCondition → extended pattern includes "Explore"
  Classify:Rfc2119Uppercase  (60)  PatternMatchCondition → unchanged
  Classify:Rfc2119Lowercase  (50)  PatternMatchCondition → unchanged
  Classify:FrPrefix          (40)  PatternMatchCondition → unchanged
  Classify:QuestionTerminator(30)  PatternMatchCondition → unchanged
  Classify:DeferralMarker    (20)  PatternMatchCondition → unchanged
  Configure:Prefix:0         (10)  PrefixMatchCondition("REQ-") → (Requirement, ConfiguredPrefix)
  Configure:Prefix:1         (10)  PrefixMatchCondition("INFEASIBLE:") → (NeedsClarification, ConfiguredPrefix)
  Classify:Default            (0)  UnconditionalCondition → (NeedsClarification, Default)

IgnorePrefixes: ["NOTE:"]
```

When two configured prefix rules share the same priority (both at 10), the stable sort preserves declaration order. `Configure:Prefix:0` is evaluated before `Configure:Prefix:1`. A candidate whose stripped text begins with `"REQ-"` is classified by `Configure:Prefix:0`; a candidate beginning with `"INFEASIBLE:"` is classified by `Configure:Prefix:1`. A candidate beginning with `"NOTE:"` is discarded at Stage 5.5 and never reaches classification.

---

## Observability-Safe Metadata Models

All startup log events introduced by US4 carry counts and codes only. No configuration value text — no keyword text, no prefix text, no disabled rule name values — is ever included in any log field.

### New Startup Log Events

| Event name | Level | Fields |
|---|---|---|
| `ExtractionRuleConfigurationLoaded` | Info | `bddKeywordAdditionCount: int`, `rfc2119UppercaseAdditionCount: int`, `rfc2119LowercaseAdditionCount: int`, `deferralMarkerAdditionCount: int`, `prefixRuleCount: int`, `ignorePrefixCount: int`, `disabledRuleCount: int`, `priorityOverrideCount: int` |
| `ExtractionRuleConfigurationFailed` | Warning | `fieldName: string`, `violationType: string`, `entryIndex: int?`, `fallbackApplied: true` |
| `ExtractionRuleConfigurationFallback` | Info | `reason: string` — one of `"validation_failure"` or `"no_configuration"` |

`ExtractionRuleConfigurationLoaded` is emitted after every successful compilation, including when all counts are 0. `ExtractionRuleConfigurationFallback` is emitted whenever the Default rule set is active, regardless of reason. `ExtractionRuleConfigurationFailed` is emitted only on validation failure and is always followed by `ExtractionRuleConfigurationFallback`.

**`fieldName` format**: A structural path, not a human label. For array fields: `"PrefixRules[2].Prefix"`. For top-level scalar fields: `"BddKeywordAdditions"`. For dictionary key violations: `"PriorityOverrides"` (the key that failed is not logged). `fieldName` identifies where to look in `appsettings.json`; it never contains the value that failed.

### Extraction-Time Events (unchanged)

| Event | Change in US4 |
|---|---|
| `ExtractionCompleted` | None. `rulesEvaluatedCount` (added in US3) already counts all rule evaluations including configured prefix rules. |
| `ExtractionEmpty` | None. |
| `ExtractionTriggered` | None. |
| `CandidateReviewSaved` | None. |
| `CandidateReviewAbandoned` | None. |

`RuleEvaluationResult.WinningRuleName` carries `Configure:Prefix:{index}` for prefix-rule wins and the original rule name (e.g., `Classify:BddPattern`) for keyword-extended rule wins. These are developer-assigned identifiers, not extraction content, and do not violate the no-raw-text constraint.

---

## Validation Rules (US4)

All validation runs in Step 1 of `ExtractionRuleSetCompiler.Compile()`. Fail-fast: the first violation produces a `ConfigurationViolation`, emits the Warning event, and causes the method to return `ExtractionRuleSet.Default()` immediately.

### String value checks

Applied to every string element in `BddKeywordAdditions`, `Rfc2119UppercaseAdditions`, `Rfc2119LowercaseAdditions`, `DeferralMarkerAdditions`, `IgnorePrefixes`, and `PrefixRuleEntry.Prefix`.

| Check | Constraint | Violation code |
|---|---|---|
| Non-null and non-empty | Value must not be null, empty, or whitespace-only | `empty_value` |
| Maximum length | ≤ 200 characters | `value_too_long` |
| Printable ASCII | Every character in range 0x20–0x7E | `non_ascii_characters` |
| No regex metacharacters | Must not contain `\ ^ $ . | ? * + ( ) [ ] { }` | `regex_metacharacter` |
| Post-compile check (keyword additions only) | After `Regex.Escape` and `\b(?:...)\b` wrapping, the assembled group pattern must compile without exception. Not applicable to `PrefixMatchCondition` values (which are never incorporated into regex). | `pattern_compile_failure` |

### Per-group count limits

| Config field | Maximum entries | Violation code |
|---|---|---|
| `BddKeywordAdditions` | 50 | `too_many_entries` |
| `Rfc2119UppercaseAdditions` | 50 | `too_many_entries` |
| `Rfc2119LowercaseAdditions` | 50 | `too_many_entries` |
| `DeferralMarkerAdditions` | 50 | `too_many_entries` |
| `PrefixRules` | 50 | `too_many_entries` |
| `IgnorePrefixes` | 50 | `too_many_entries` |

Count is checked before per-entry string validation. An array with 51 entries fails on `too_many_entries`; no per-entry checks are run.

### `PrefixRuleEntry` field checks

| Field | Check | Violation code |
|---|---|---|
| `Prefix` | Non-null and non-empty | `empty_value` |
| `Prefix` | All string value checks above | (same codes) |
| `Classification` | Must be a valid `ScenarioKind` value (`Requirement`, `Test`, or `NeedsClarification`) | `invalid_classification` |
| `Priority` | Must satisfy `1 ≤ Priority ≤ 99` | `priority_out_of_range` |

### `DisabledRuleNames` checks

| Check | Constraint | Violation code |
|---|---|---|
| Name exists in Default | Each name must exactly match a `FilterRule.Name` or `ClassificationRule.Name` in `ExtractionRuleSet.Default()` (case-sensitive) | `unknown_rule_name` |
| Default rule protected | `Classify:Default` must not appear in the list | `default_rule_disabled` |

### `PriorityOverrides` checks

| Check | Constraint | Violation code |
|---|---|---|
| Key exists in Default | Each key must exactly match a rule name in `ExtractionRuleSet.Default()` (case-sensitive) | `unknown_rule_name` |
| Default rule protected | `Classify:Default` must not appear as a key | `default_priority_override` |
| Value in range | Must satisfy `1 ≤ value ≤ 99` | `priority_out_of_range` |

### Fallback behaviour on validation failure

1. `ConfigurationViolation` is constructed with `FieldName`, `ViolationType`, and `EntryIndex`.
2. `ExtractionRuleConfigurationFailed` Warning is emitted. No field value content is included.
3. `ExtractionRuleSet.Default()` (`baseSet`) is returned unchanged.
4. `ExtractionRuleConfigurationFallback` Info is emitted with `reason: "validation_failure"`.

The application continues operating. All extraction sessions for the lifetime of the process use the default rule set. No partial application of invalid configuration is permitted.

---

## Persistence Boundary (US4)

US4 introduces no database changes.

| What | Status |
|---|---|
| `scenarios` table | Unchanged |
| `ExtractionRuleConfiguration` | Not persisted — `appsettings.json` file only |
| `PrefixRuleEntry` | Not persisted — sub-model in `appsettings.json` |
| Compiled `ExtractionRuleSet` (with `IgnorePrefixes`) | Not persisted — in-memory only; reconstructed from configuration on each restart |
| GraphQL schema | Unchanged |

**Future persistence note**: Project-level configuration (per-`projectId` rule configuration stored in the database) would require a new table, a schema migration, and new GraphQL mutations. `ExtractionRuleConfiguration` and `PrefixRuleEntry` are designed as portable value POCOs that could serialise to/from a JSON column without modification — no constructor logic, no DI dependencies, no circular references. The field shape is forward-compatible with a future project-level configuration entity.

---

## Separation of Configuration, Compiled Rules, and Runtime Evaluation State

This table is the authoritative statement of the three tiers, their boundaries, and who may access what.

| Tier | Models | Lifetime | Mutability | Access path |
|---|---|---|---|---|
| **Configuration** | `ExtractionRuleConfiguration`, `PrefixRuleEntry` | Application startup only; discarded after compilation | Mutable during binding; read-only during compilation | `IOptions<>` → `ExtractionRuleSetCompiler.Compile()` only |
| **Compiled rules** | `ExtractionRuleSet` (with `FilterRules`, `ClassificationRules`, `IgnorePrefixes`), `PrefixMatchCondition` instances, extended `PatternMatchCondition` instances | Application lifetime | Immutable after startup | `IExtractionRuleEngine` — evaluation and `IgnorePrefixes` property |
| **Runtime evaluation state** | `RuleEvaluationResult`, `RuleExecutionSummary` (US3 unchanged); Stage 5.5 uses no model — inline `Any()` check only | Pipeline execution only | Mutable during execution; discarded on completion | `ScenarioExtractionService` only |

**Cross-tier dependency rules**:
- Compiled rules may not hold references to configuration models.
- Runtime evaluation state may not reference configuration models.
- `ExtractionRuleSetCompiler` is the only component permitted to read `ExtractionRuleConfiguration`; it does so exclusively at startup.
- `IExtractionRuleEngine.Evaluate()` reads only its arguments and the compiled `ExtractionRuleSet`; it has no path to `ExtractionRuleConfiguration`.

---

## Extensibility: Future Project-Level Configuration

US4 is application-level configuration. The model design anticipates future project-level extension without structural breaks.

| Future capability | Required change |
|---|---|
| Project-level `ExtractionRuleConfiguration` stored per `projectId` | New DB table with a JSON column holding the `ExtractionRuleConfiguration` shape; new GraphQL mutation for project administrators to update; EF Core migration |
| Per-extraction rule set selection by `projectId` | Rule set cache keyed by `projectId`; `IScenarioExtractionService.ExtractAsync` accepts an optional `ExtractionRuleSet` or project identifier |
| Rule set versioning | `version: string` field added to `ExtractionRuleConfiguration`; surfaced in `ExtractionRuleConfigurationLoaded` log event and potentially in `ExtractionMetadataInput` |
| Configuration UI — read-only viewer | No model changes; UI reads compiled rule set field counts from startup log or an added `IExtractionRuleEngine.RuleCount` property |
| Configuration UI — write interface | New GraphQL mutations on the project-level table; standard CQRS pattern on top of the existing persistence layer |

---

## US3 Compatibility Statement

The US3 data model is unchanged. The following invariants hold after US4 is delivered:

- `FilterCondition`, `ClassificationCondition`, `PatternMatchCondition`, `UnconditionalCondition`, `BlockTypeMatchCondition`, `ContentLengthBelowCondition`: no changes to structure or semantics.
- `FilterRule`, `ClassificationRule`, `ClassificationOutcome`: no field or semantic changes. Keyword-extended rules share the same type and name as their default counterparts; only `Condition.Pattern` differs.
- `ExtractionRuleSet.FilterRules` and `.ClassificationRules`: shape unchanged. `IgnorePrefixes` is additive.
- `ExtractionRuleSet.Default()`: returns the same rule set as US3. `IgnorePrefixes` is `ImmutableArray<string>.Empty`. No behavioral change.
- `RuleEvaluationResult`, `RuleExecutionSummary`: unchanged.
- `TextBlock`, `BlockType`: unchanged.
- `ExtractionCandidate`, `ExtractionPipelineResult`, `PipelineStatus`, `CandidateSaveState`, `ReviewSavePhase`, `ExtractionReviewState`: unchanged.
- `CreateScenariosInput`, `ExtractionMetadataInput`, `CreateScenariosPayload`, `CreateScenarioResult`: unchanged. GraphQL contract unmodified.
- `Scenario` entity, `ScenarioKind` enum, `scenarios` table: unchanged.
- `IScenarioExtractionService` interface: unchanged.
- `IExtractionRuleEngine.Evaluate()`: signature and semantics unchanged.
- `ClassificationSignal`: `ConfiguredPrefix` is additive. All existing values and their semantics are unchanged.
- All 153 existing tests must pass without modification after US4 is delivered. Passing those tests with an empty `ExtractionRuleConfiguration` is the regression gate for the migration.

---

## Out of Scope (US4)

- Database schema changes: none required; configuration is `appsettings.json`-only in MVP
- GraphQL contract changes: none required; configurable rules are an internal pipeline concern
- Per-user rule variation: configuration is application-wide in US4 MVP
- Hot-reload of configuration without restart: `IOptions<>` (not `IOptionsSnapshot<>`) is used; restart required for configuration changes to take effect
- Configuration UI for viewing or editing active rules: deferred
- Level 2 configurability (custom regex with static ReDoS analysis): deferred
- Level 3 configurability (user-defined rule types or DSL): deferred
- `ContextRule` type or section-aware classification rules: deferred
- `RuleEvaluationResult.MatchedRuleNames` (list of all matching rules, not just the winner): deferred
- Project-level rule configuration stored in the database: deferred to a future user story
- Rule set versioning and configuration export/import: deferred
- `ExtractionCandidate.Confidence` population: not set by prefix rules or keyword-extended rules; reserved for a future AI implementation
