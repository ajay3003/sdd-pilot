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
