# Research: Traceability & Coverage

**Date**: 2026-06-04  
**Branch**: `002-traceability-coverage`

---

## Existing Backend Patterns

### Entity conventions
- `Guid Id { get; init; } = Guid.NewGuid()` — init-only, auto-generated
- `DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow` — init-only
- Required strings default to `string.Empty`; optional use `?`
- Enums stored as `varchar` via `.HasConversion<string>()`
- Table names: snake_case (`scenarios`, `candidate_links`)
- Column names: snake_case (`project_id`, `created_at`)

### Closest analogue: `CandidateLink`
- Stores two cross-references (`SourceCandidateRef`, `TargetCandidateRef`) as strings
- No FK constraint — integrity enforced in service layer
- Composite index on `(project_id, session_id)`
- Session-scoped batch-replace pattern

`TraceLink` differs: uses Guid IDs (not string refs) and is persistent (not session-scoped).

### Service pattern
- Constructor takes `AppDbContext` + `ILogger<T>`
- Result type classes at top of service file: `XyzResult { T? Entity; IReadOnlyList<UserError> Errors; }`
- `UserError(Code, Message, Field?)` record — already defined in `ScenarioService.cs`, do not redefine
- Validate → operate → log (structured, no raw text, always include correlationId)
- All methods: `async Task<T>` with `CancellationToken ct = default`

### GraphQL pattern (Hot Chocolate 14)
- Input: `record CreateXyzInput(...)` with XML doc comment
- Payload: `class CreateXyzPayload { T? Entity; IReadOnlyList<UserError> Errors; string CorrelationId; }`
- ObjectType: `sealed class XyzObjectType : ObjectType<Xyz>`, override `Configure`, explicit field list
- Query/Mutation: public async method, `[Service]` injection, correlationId from `IHttpContextAccessor`
- `UseXmlDocumentation = true` → all public members need XML doc comments

### Migration naming
- Format: `YYYYMMDDHHmmss_FeatureName`
- Most recent: `20260528140209_AddScenarioDisplayOrder`
- New: `20260604120000_AddTraceLinks`
- **Generate with EF CLI** — do not hand-write the Designer snapshot file

---

## Existing Frontend Patterns

### CSS classes already available (no new CSS needed)
| Class | Source | Use |
|---|---|---|
| `.mapping-row`, `.mapping-covered`, `.mapping-uncovered` | Scenarios.razor.css | Matrix rows |
| `.mapping-requirement`, `.mapping-tests`, `.mapping-test-pill` | Scenarios.razor.css | Row columns |
| `.traceability-panel`, `.traceability-stats` | Scenarios.razor.css | Overview panel |
| `.kpi-card`, `.kpi-card-traceability`, `.kpi-card-coverage` | dashboard.css | Summary cards |
| `.library-filter-chip`, `.filter-chip-grid`, `.is-active` | Scenarios.razor.css | Filter chips |
| `.state-covered`, `.state-uncovered` | ScenarioList.razor.css | Status pills |
| `.artifact-state-pill` | ScenarioList.razor.css | Pill wrapper |
| `.badge-requirement`, `.badge-test` | components.css | Type badges |
| `.empty-state`, `.empty-state-dashed` | components.css | Empty states |
| `.notification`, `.notification-error` | components.css | Error messages |

### New CSS required (Traceability.razor.css)
- `.state-orphan` — amber/orange pill for orphan test state
- `.trace-matrix-action` — small inline link/unlink button inside test pill row
- `.link-select-row` — inline `<select>` + confirm button for link creation

### Strawberry Shake pattern
```csharp
var result = await Client.GetTraceabilityMatrix.ExecuteAsync(ProjectId);
if (result.Errors is { Count: > 0 }) { _loadError = "..."; return; }
_matrix = result.Data?.TraceabilityMatrix ?? [];
```

### Blazor page structure
```razor
@page "/traceability"
@using BirkNext.Web.Components
@using BirkNext.Web.GraphQL
@inject IBirkNextClient Client

<!-- markup -->

@code {
    private bool _isLoading = true;
    private string? _loadError;
    // ... state fields
    
    protected override async Task OnInitializedAsync() => await LoadDataAsync();
    
    private async Task LoadDataAsync() { ... }
}
```

### NavMenu structure
Three existing sections: Review, Library, Analysis.  
Add Traceability under **Analysis** section with a new `.nav-icon-traceability` CSS class.

### Scenarios.razor — coverage map already exists
Scenarios.razor contains a partial `.traceability-panel` / `.mapping-row` coverage view.  
The new `/traceability` page is an additive, dedicated deeper view — **do not modify Scenarios.razor**.

---

## Hardcoded Project ID

The frontend uses a hardcoded `ProjectId` constant (likely in `_Imports.razor` or a constants file).  
The Traceability page should use the same constant/pattern as the Scenarios page.

---

## Key Files to Modify

| File | What changes |
|---|---|
| `BirkNext.Api/Data/AppDbContext.cs` | Add `DbSet<TraceLink>` + Fluent API config |
| `BirkNext.Api/GraphQL/Query.cs` | Add 2 query methods |
| `BirkNext.Api/GraphQL/Mutation.cs` | Add 2 mutation methods |
| `BirkNext.Api/Program.cs` | Register service + 4 ObjectTypes |
| `BirkNext.Web/Layout/NavMenu.razor` | Add nav item |
| `BirkNext.Web/wwwroot/css/components.css` | Add `.nav-icon-traceability` |
