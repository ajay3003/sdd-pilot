# Implementation Plan: Traceability & Coverage

**Branch**: `002-traceability-coverage`  
**Spec**: `spec.md`  
**Data model**: `data-model.md`  
**Schema**: `schema.graphql`

---

## Commit Strategy

| # | Commit message | Files |
|---|---|---|
| 1 | `feat: add TraceLink model, TraceLinkType enum, and TraceLinkArtifactKind constants` | 3 new model files |
| 2 | `feat: add trace_links EF Core config and migration` | AppDbContext.cs + generated migration |
| 3 | `feat: add TraceLinkService with coverage calculation logic` | TraceLinkService.cs |
| 4 | `feat: add TraceLink GraphQL types, inputs, payloads, queries, and mutations` | 7 new GraphQL files + Query.cs + Mutation.cs |
| 5 | `feat: register TraceLinkService and new GraphQL types in Program.cs` | Program.cs |
| 6 | `feat: add frontend GraphQL operations for traceability` | 4 .graphql files |
| 7 | `feat: add Traceability & Coverage page with matrix and summary cards` | Traceability.razor + .css |
| 8 | `feat: add Traceability nav item to sidebar` | NavMenu.razor + components.css |
| 9 | `test: add TraceLinkService unit tests` | TraceLinkServiceTests.cs |
| 10 | `docs: add design artifacts for traceability feature` | This directory |

---

## File Inventory

### Backend — new
- `BirkNext.Api/Models/TraceLink.cs`
- `BirkNext.Api/Models/TraceLinkType.cs`
- `BirkNext.Api/Models/TraceLinkArtifactKind.cs`
- `BirkNext.Api/Services/TraceLinkService.cs`
- `BirkNext.Api/GraphQL/CreateTraceLinkInput.cs`
- `BirkNext.Api/GraphQL/DeleteTraceLinkInput.cs`
- `BirkNext.Api/GraphQL/CreateTraceLinkPayload.cs`
- `BirkNext.Api/GraphQL/DeleteTraceLinkPayload.cs`
- `BirkNext.Api/GraphQL/TraceLinkObjectType.cs`
- `BirkNext.Api/GraphQL/TraceabilityMatrixRowObjectType.cs`
- `BirkNext.Api/GraphQL/CoverageSummaryObjectType.cs`
- `BirkNext.Api/Data/Migrations/20260604120000_AddTraceLinks.cs` (EF-generated)

### Backend — modified
- `BirkNext.Api/Data/AppDbContext.cs` — DbSet + Fluent API
- `BirkNext.Api/GraphQL/Query.cs` — 2 new queries
- `BirkNext.Api/GraphQL/Mutation.cs` — 2 new mutations
- `BirkNext.Api/Program.cs` — service + 4 ObjectTypes

### Frontend — new
- `BirkNext.Web/GraphQL/GetTraceabilityMatrix.graphql`
- `BirkNext.Web/GraphQL/GetCoverageSummary.graphql`
- `BirkNext.Web/GraphQL/CreateTraceLink.graphql`
- `BirkNext.Web/GraphQL/DeleteTraceLink.graphql`
- `BirkNext.Web/Pages/Traceability.razor`
- `BirkNext.Web/Pages/Traceability.razor.css`

### Frontend — modified
- `BirkNext.Web/Layout/NavMenu.razor`
- `BirkNext.Web/wwwroot/css/components.css` — nav icon

### Tests — new
- `BirkNext.Api.Tests/Unit/TraceLinkServiceTests.cs`

---

## Verification Checklist

- [ ] `dotnet build` passes in BirkNext.Api (no errors)
- [ ] `dotnet ef migrations list` shows `20260604120000_AddTraceLinks`
- [ ] GraphQL schema at `/graphql` includes `traceabilityMatrix`, `coverageSummary`, `createTraceLink`, `deleteTraceLink`
- [ ] `dotnet build` passes in BirkNext.Web (Strawberry Shake codegen succeeds)
- [ ] `dotnet test` passes all new unit tests in BirkNext.Api.Tests
- [ ] `/traceability` page loads in browser
- [ ] Create a link → requirement turns "Covered", summary cards update
- [ ] Remove a link → requirement reverts to "Missing Test Coverage"
- [ ] Rejected scenarios absent from matrix

---

## Risks

- **Strawberry Shake codegen order**: build backend schema first, frontend second
- **EF migration**: use `dotnet ef migrations add AddTraceLinks` — do not hand-write Designer.cs
- **`UserError` record**: defined in ScenarioService.cs — do not redefine
- **Scenarios.razor coverage map**: existing partial view — do not modify
- **`CandidateReviewStatus.Accepted`**: exact enum value to filter accepted scenarios
