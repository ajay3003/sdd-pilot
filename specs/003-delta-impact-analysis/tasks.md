# Tasks: Delta Impact Analysis

**Input**: Design documents from `/specs/003-delta-impact-analysis/`  
**Prerequisites**: spec.md ✅ | data-model.md ✅  
**Depends on**: `002-traceability-coverage` (TraceLink model, TraceLinkService, ImpactService)

**Stack**: ASP.NET Core + HotChocolate 14 (backend) | Blazor WebAssembly + Strawberry Shake 14 (frontend) | EF Core 8 + PostgreSQL 16

---

## Phase 1: Backend

- [x] T001 Add `ImpactService` with `GetImpactSummaryAsync` and `GetRequirementImpactAsync` using `TraceLinkService`
- [x] T002 Add `ImpactSummaryType`, `RequirementRiskType`, `RequirementImpactType`, `RegressionItemType` HotChocolate types
- [x] T003 Add `impactSummary` and `requirementImpact` fields to `Query.cs`
- [x] T004 Register `ImpactService` and new GraphQL types in `Program.cs`
- [x] T005 Update `schema.graphql` with impact analysis types

## Phase 2: Frontend

- [x] T006 Add `GetImpactSummary.graphql` operation
- [x] T007 Add `GetRequirementImpact.graphql` operation
- [x] T008 Add `Pages/ImpactAnalysis.razor` with risk KPI cards, requirement list, and detail panel
- [x] T009 Add **Impact Analysis** nav link to `Layout/NavMenu.razor` under the Analysis section

## Phase 3: Documentation

- [x] T010 Add `docs/impact-analysis-guide.md` covering what the feature does, how it uses Traceability, and manual test steps
- [x] T011 Add `tasks.md` with completion status

---

## Status: Complete

Both backend and frontend build with 0 errors and 0 warnings as of 2026-06-04.

### Remaining limitations (v1 scope)

- `RelatedTo` links do not affect risk or regression recommendations — only `Covers` links count.
- Risk thresholds (0 = High, 1 = Medium, 2+ = Low) are fixed; not user-configurable.
- No commit-level or file-change tracking — impact is based solely on trace links.
- No AI suggestions; the regression recommendation is fully deterministic.
- Scoped to a single project (same hardcoded `ProjectId` pattern as all other pages).
