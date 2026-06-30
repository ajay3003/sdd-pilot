---
description: "Task list for Proxy Service Initial Setup"
---

# Tasks: Proxy Service Initial Setup

**Input**: Design documents from `/specs/001-proxy-initial-setup/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅

**Tests**: Included — PP-09 and SC-005 require automated tests. Tests are written alongside
implementation (not strictly TDD, but each story phase includes its test tasks).

**Organization**: Tasks are grouped by user story to enable independent implementation and
testing of each story.

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)

---

## Phase 1: Setup (Project Initialization)

**Purpose**: Create the solution and project files; install all NuGet packages; establish the
folder skeleton that all subsequent tasks build on.

- [ ] T001 Create solution file and top-level folder structure: run `dotnet new sln -n M2LB.Proxy` at repo root, then `mkdir -p src/M2LB.Proxy.Api/Middleware src/M2LB.Proxy.Api/Configuration src/M2LB.Proxy.Api/HealthChecks src/M2LB.Proxy.Api/Transforms tests/M2LB.Proxy.Unit/Middleware tests/M2LB.Proxy.Unit/Configuration tests/M2LB.Proxy.Integration/Pipeline tests/M2LB.Proxy.Integration/Health .pipeline`
- [ ] T002 [P] Create M2LB.Proxy.Api project and add all NuGet packages: `dotnet new web -n M2LB.Proxy.Api -o src/M2LB.Proxy.Api`, then add `Microsoft.ReverseProxy`, `Microsoft.Identity.Web`, `Microsoft.Extensions.Http.Resilience`, `Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.ApplicationInsights`, `Microsoft.ApplicationInsights.AspNetCore`, `Azure.Extensions.AspNetCore.Configuration.Secrets`; add project to solution
- [ ] T003 [P] Create M2LB.Proxy.Unit test project: `dotnet new xunit -n M2LB.Proxy.Unit -o tests/M2LB.Proxy.Unit`, add project reference to `M2LB.Proxy.Api`, add package `Microsoft.AspNetCore.TestHost`, add to solution
- [ ] T004 [P] Create M2LB.Proxy.Integration test project: `dotnet new xunit -n M2LB.Proxy.Integration -o tests/M2LB.Proxy.Integration`, add project reference to `M2LB.Proxy.Api`, add packages `Microsoft.AspNetCore.Mvc.Testing` and `WireMock.Net`, add to solution

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that ALL user stories depend on — configuration schema,
DI registration extensions, and the Program.cs skeleton. Must complete before story work begins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T005 Create `src/M2LB.Proxy.Api/appsettings.json` with the full configuration schema (keys present, no env-specific values): `ReverseProxy` section (Routes + Clusters schema), `AzureAd` section, `ApplicationInsights` section, `Resilience` section — values are empty strings or zero. This file documents the shape of config; it does not store secrets.
- [ ] T006 [P] Create `src/M2LB.Proxy.Api/appsettings.Development.json.example` with commented documentation of each field; include a sample stub cluster pointing to `https://localhost:7200/` and a matching route at `/api/stub/{**catch-all}`; add `appsettings.Development.json` (without `.example`) to `.gitignore`
- [ ] T007 [P] Create `src/M2LB.Proxy.Api/Configuration/ResilienceOptions.cs`: public class `ResilienceOptions` with properties matching data-model.md — `DefaultTimeoutSeconds` (int, 30), `RetryCount` (int, 3), `RetryBaseDelaySeconds` (double, 1.0), `CircuitBreakerFailureRatio` (double, 0.5), `CircuitBreakerSamplingSeconds` (int, 30), `CircuitBreakerBreakDurationSeconds` (int, 30); add `[BindRequired]` attributes
- [ ] T008 Create `src/M2LB.Proxy.Api/Configuration/YarpConfigExtensions.cs`: static class with extension method `AddYarpWithResilience(this IServiceCollection services, IConfiguration configuration)` — calls `services.AddReverseProxy().LoadFromConfig(configuration.GetSection("ReverseProxy"))`, then `services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler())`, then binds `ResilienceOptions` from configuration; depends on T007
- [ ] T009 Create `src/M2LB.Proxy.Api/Program.cs` skeleton: call `AddYarpWithResilience`, register `AddAuthentication`/`AddAuthorization` placeholders, register health checks placeholder, configure Serilog placeholder, configure middleware pipeline order (`UseAuthentication` → `UseAuthorization` → `MapHealthChecks` → `MapReverseProxy`); the file MUST compile but individual features are wired in story phases; depends on T008

**Checkpoint**: `dotnet build` passes. Foundation ready for user story implementation.

---

## Phase 3: User Story 1 — Developer Onboards and Runs the Service Locally (Priority: P1) 🎯 MVP

**Goal**: The service starts, serves a health-check (even if degraded), and rejects
unauthenticated requests with HTTP 401.

**Independent Test**: `dotnet run` in `src/M2LB.Proxy.Api` → service starts without errors →
`curl /healthz` returns 200 → `curl /api/stub/test` without token returns 401.

### Implementation for User Story 1

- [ ] T010 [P] [US1] Wire Microsoft.Identity.Web JWT bearer validation in `src/M2LB.Proxy.Api/Program.cs`: call `builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration)` using the `AzureAd` config section; ensure `app.UseAuthentication()` and `app.UseAuthorization()` are in the correct pipeline position (before `MapReverseProxy`)
- [ ] T011 [US1] Apply `RequireAuthorization()` to `MapReverseProxy()` in `src/M2LB.Proxy.Api/Program.cs` so all YARP routes require a valid EntraID bearer token; depends on T010
- [ ] T012 [US1] Create `src/M2LB.Proxy.Api/appsettings.Development.json` (from the `.example` file) with stub cluster and route populated; ensure the file is excluded from Git (verify `.gitignore`); depends on T006
- [ ] T013 [P] [US1] Create `README.md` at repo root with: prerequisites (.NET 8 SDK, EntraID app registration), local setup steps (copy config example, fill in AzureAd values, `dotnet run`), health-check verification command, test run command — content derived from `specs/001-proxy-initial-setup/quickstart.md`
- [ ] T014 [P] [US1] Create `.pipeline/azure-pipelines.yml` skeleton: trigger on `main`, steps: `dotnet restore`, `dotnet build --no-restore -warnaserror`, `dotnet test --no-build --logger trx`

### Tests for User Story 1

- [ ] T015 [P] [US1] Write integration tests in `tests/M2LB.Proxy.Integration/Pipeline/AuthenticationTests.cs` using `WebApplicationFactory<Program>`: (1) request with no `Authorization` header → assert HTTP 401; (2) request with malformed `Bearer invalid-token` → assert HTTP 401; (3) request to `/healthz` with no token → assert HTTP 200 (health check must be excluded from auth)

**Checkpoint**: US1 complete — service runs, auth enforced, health check responds.

---

## Phase 4: User Story 2 — Developer Verifies End-to-End Routing and Observability (Priority: P2)

**Goal**: Authenticated requests are forwarded to the stub backend with `Authorization` and
`X-Correlation-Id` propagated; structured logs capture `correlation_id`; resilience policies
are active; unreachable backend returns 503, not a hang.

**Independent Test**: Start service + WireMock.Net stub → send authenticated request →
inspect log output for `correlationId` field → inspect WireMock stub log for forwarded
`Authorization` and `X-Correlation-Id` headers.

### Implementation for User Story 2

- [ ] T016 [P] [US2] Create `src/M2LB.Proxy.Api/Middleware/CorrelationIdMiddleware.cs`: on each request, read `X-Correlation-Id` header; if absent generate `Guid.NewGuid().ToString()`; set `HttpContext.TraceIdentifier` to the resolved ID; add `X-Correlation-Id` to response headers; call `next()`
- [ ] T017 [P] [US2] Create `src/M2LB.Proxy.Api/Transforms/CorrelationIdTransform.cs`: implement `RequestTransform` (YARP) that reads `HttpContext.TraceIdentifier` and sets (or overwrites) the `X-Correlation-Id` request header on the upstream request
- [ ] T018 [US2] Register `CorrelationIdMiddleware` (early in pipeline, before auth) and register `CorrelationIdTransform` with YARP in `src/M2LB.Proxy.Api/Program.cs`; call `AddTransforms(builder => builder.AddRequestTransform<CorrelationIdTransform>())`; depends on T016, T017
- [ ] T019 [US2] Configure Serilog in `src/M2LB.Proxy.Api/Program.cs`: `UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))`, `app.UseSerilogRequestLogging(opts => opts.EnrichDiagnosticContext = (diag, http) => { diag.Set("correlationId", http.TraceIdentifier); diag.Set("clusterName", ...); })`, JSON formatter for console output; add Serilog config section to `appsettings.json`; depends on T018
- [ ] T020 [US2] Bind `ResilienceOptions` from configuration in `src/M2LB.Proxy.Api/Configuration/YarpConfigExtensions.cs` and apply per-property values to the `StandardResilienceOptions` pipeline: set `TotalRequestTimeout`, `Retry.MaxRetryAttempts` and `Retry.Delay`, `CircuitBreaker.FailureRatio`, `CircuitBreaker.SamplingDuration`, `CircuitBreaker.MinimumThroughput`; add YARP passive health check config to cluster definitions; depends on T008

### Tests for User Story 2

- [ ] T021 [P] [US2] Write unit tests in `tests/M2LB.Proxy.Unit/Middleware/CorrelationIdMiddlewareTests.cs`: (1) no inbound `X-Correlation-Id` → middleware generates a UUID v4 and sets `TraceIdentifier`; (2) inbound `X-Correlation-Id` present → middleware uses the existing value unchanged; (3) response contains `X-Correlation-Id` header in both cases
- [ ] T022 [US2] Write integration test in `tests/M2LB.Proxy.Integration/Pipeline/ObservabilityTests.cs` using `WebApplicationFactory` + WireMock.Net stub: send authenticated request → assert WireMock stub received `X-Correlation-Id` header matching the value echoed in the response header; test both generated and pre-supplied correlation IDs; depends on T021
- [ ] T023 [US2] Write integration test in `tests/M2LB.Proxy.Integration/Pipeline/RoutingTests.cs`: (1) authenticated request to stub route → assert stub received unchanged `Authorization` header; (2) stub returns 200 → client receives 200; (3) stub is stopped before request → assert client receives 503 within `DefaultTimeoutSeconds + retry backoff` window (not a hang)

**Checkpoint**: US2 complete — full observable request lifecycle verified.

---

## Phase 5: User Story 3 — Operator Monitors Service Health (Priority: P3)

**Goal**: `/healthz` returns a structured JSON response reflecting per-cluster health,
accessible without authentication, and correctly reports degraded state when a cluster
is unreachable.

**Independent Test**: Start service (no stub backend running) → `curl /healthz` without
`Authorization` → receive JSON with `"status": "Degraded"` and per-cluster entry.

### Implementation for User Story 3

- [ ] T024 [US3] Create `src/M2LB.Proxy.Api/HealthChecks/YarpClusterHealthCheck.cs`: implement `IHealthCheck`; inject `IProxyStateLookup`; iterate all clusters and destinations; return `Healthy` if all destinations healthy, `Degraded` if some degraded, `Unhealthy` if no destinations reachable; include `data` dictionary with `totalDestinations`, `healthyDestinations`, `degradedDestinations` counts
- [ ] T025 [US3] Register `YarpClusterHealthCheck` and map `/healthz` in `src/M2LB.Proxy.Api/Program.cs`: `builder.Services.AddHealthChecks().AddCheck<YarpClusterHealthCheck>("yarp-clusters")`; map with `app.MapHealthChecks("/healthz", new HealthCheckOptions { ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse })` — or use the standard JSON writer per contracts/health-check.md; ensure `/healthz` route is registered BEFORE `RequireAuthorization` applies; depends on T024
- [ ] T026 [US3] Write integration tests in `tests/M2LB.Proxy.Integration/Health/HealthCheckTests.cs`: (1) no token → GET `/healthz` → HTTP 200; (2) response body is valid JSON matching the schema in `contracts/health-check.md`; (3) with stub backend stopped → response `status` is `Degraded` or `Unhealthy`; (4) health endpoint response MUST NOT contain any route configuration or backend addresses

**Checkpoint**: All three user stories independently functional and tested.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final hardening, edge-case handling, and CI validation.

- [ ] T027 [P] Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and `<Nullable>enable</Nullable>` to `src/M2LB.Proxy.Api/M2LB.Proxy.Api.csproj` and both test projects; fix any resulting warnings
- [ ] T028 [P] Verify edge cases from spec.md are handled: (1) confirm missing `appsettings.Development.json` causes a startup exception (fail-fast on missing config); (2) confirm inbound `X-Correlation-Id` is forwarded unchanged (covered by T021/T022); (3) confirm unmatched routes return a clear 404 from YARP without a stack trace; add these assertions to the appropriate existing test files
- [ ] T029 Run full `dotnet build` and `dotnet test` and confirm all pass; fix any failures before closing this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 completion — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Phase 2
- **US2 (Phase 4)**: Depends on Phase 3 (routing builds on a running, authenticated service)
- **US3 (Phase 5)**: Depends on Phase 2; can run in parallel with Phase 4 if team capacity allows
- **Polish (Phase 6)**: Depends on all story phases complete

### Within Each User Story

- Middleware/transform/check classes first (can be parallel)
- Registration in `Program.cs` after their dependencies
- Tests written alongside or immediately after implementation
- Commit after each checkpoint

### Parallel Opportunities

Within Phase 1: T002, T003, T004 are independent — run in parallel.
Within Phase 2: T006, T007 are independent — run in parallel with T005.
Within Phase 3: T010, T013, T014 are independent — run in parallel.
Within Phase 4: T016, T017 are independent — run in parallel.
Within Phase 5: No tasks within US3 are parallelizable (sequential dependency chain).
Within Phase 6: T027, T028 are independent — run in parallel.

---

## Dependencies Graph

```
Phase 1: T001 → T002, T003, T004 (parallel after T001)
Phase 2: T005, T006, T007 (parallel) → T008 → T009
Phase 3: T009 → T010, T013, T014 (parallel) → T011 → T012 → T015
Phase 4: T011 → T016, T017 (parallel) → T018 → T019 → T020; T016 → T021 → T022, T023
Phase 5: T009 → T024 → T025 → T026
Phase 6: T022, T023, T026 → T027, T028 (parallel) → T029
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: `dotnet run` → health check responds → unauthenticated request returns 401
5. Proceed to US2 once US1 is independently verified

### Incremental Delivery

1. Setup + Foundational → compilable skeleton
2. US1 → runnable, auth-enforced service (MVP)
3. US2 → full observable request pipeline
4. US3 → health monitoring endpoint
5. Polish → CI-ready, hardened

---

## Notes

- `[P]` tasks operate on different files with no incomplete task dependencies
- `Program.cs` is modified across multiple stories — T009 (skeleton), T010/T011 (auth), T018/T019/T020 (observability/resilience), T025 (health). Each story phase adds to it incrementally; do not overwrite earlier additions.
- Integration tests require `WebApplicationFactory<Program>` — ensure `Program.cs` uses top-level statements so the implicit `Program` class is accessible to tests (add `public partial class Program {}` at the bottom of `Program.cs` if needed)
- WireMock.Net stubs are started/stopped per test using `IDisposable` or `IAsyncLifetime` — do not reuse stubs across test classes
- Do NOT commit `appsettings.Development.json` — only commit `appsettings.Development.json.example`
