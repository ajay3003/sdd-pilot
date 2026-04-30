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
