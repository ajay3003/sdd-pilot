# Implementation Plan: Proxy Service Initial Setup

**Branch**: `001-proxy-initial-setup` | **Date**: 2026-03-24 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-proxy-initial-setup/spec.md`

## Summary

Establish the M2LB.Proxy project from scratch: a YARP-based ASP.NET Core 8 reverse proxy that
serves as the single internet-facing entry point for all M2LB backend services. The setup delivers
a fully wired request pipeline — EntraID JWT validation, correlation ID propagation, structured
logging, Polly v8 resilience policies, and YARP health checks — validated against a stub backend
route before any production service exists.

## Technical Context

**Language/Version**: C# / .NET 8
**Primary Dependencies**: `Microsoft.ReverseProxy` (YARP 2.x), `Microsoft.Identity.Web`,
`Microsoft.Extensions.Http.Resilience` (Polly v8), Serilog, Azure Application Insights SDK,
`Azure.Extensions.AspNetCore.Configuration.Secrets` (Key Vault)
**Storage**: N/A — stateless service; no persistent storage
**Testing**: xUnit, `Microsoft.AspNetCore.Mvc.Testing` (TestServer), WireMock.Net (stub backend)
**Target Platform**: ASP.NET Core web service; Linux container (Azure Container Apps / AKS)
**Project Type**: web-service (reverse proxy)
**Performance Goals**: Proxy overhead <10 ms p95 added latency beyond upstream round-trip
**Constraints**: Stateless (PS-09), TLS 1.2+ on all connections (GL-12), no secrets in
`appsettings.json` (GL-26), correlation ID on every request (GL-28)
**Scale/Scope**: Single proxy covering all current and future M2LB backend routes

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design — see bottom.*

| Principle | Status | Evidence |
|-----------|--------|----------|
| **I. Single Ingress Point** | ✅ Pass | Project's sole purpose; all routes target private cluster addresses; no public backend URLs |
| **II. Token Validation & Identity Propagation** | ✅ Pass | `Microsoft.Identity.Web` JWT middleware rejects unauthenticated requests before YARP forwards; `Authorization` header propagated unchanged via YARP transform |
| **III. No Business Logic** | ✅ Pass | Project contains only routing config, auth middleware, correlation ID middleware, logging, resilience setup — zero domain logic |
| **IV. Observability by Default** | ✅ Pass | Correlation ID middleware + Serilog structured logging + Application Insights + `/healthz` endpoint all required in FR-002–FR-005 |
| **V. Resilience Configuration is Mandatory** | ✅ Pass | FR-006 mandates timeout, retry, and circuit breaker on every route; enforced via `Microsoft.Extensions.Http.Resilience` applied to YARP's HTTP client |

**Constitution Check Result: ALL PASS** — proceeding to Phase 0.

## Project Structure

### Documentation (this feature)

```text
specs/001-proxy-initial-setup/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── health-check.md
│   ├── routing-config.md
│   └── request-headers.md
└── tasks.md             # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
M2LB.Proxy.sln

src/
  M2LB.Proxy.Api/                    ← ASP.NET Core host: middleware pipeline, YARP wiring, DI
    Middleware/
      CorrelationIdMiddleware.cs      ← Read/generate X-Correlation-Id; set TraceIdentifier
    Configuration/
      ResilienceOptions.cs           ← Strongly-typed Polly/timeout options
      YarpConfigExtensions.cs        ← Extension methods for YARP + resilience registration
    HealthChecks/
      YarpClusterHealthCheck.cs      ← Wraps YARP cluster health into IHealthCheck
    Transforms/
      CorrelationIdTransform.cs      ← Propagate X-Correlation-Id to upstream via YARP transform
    Program.cs                       ← DI registration, middleware pipeline, YARP setup
    appsettings.json                 ← Schema-only; no env-specific values
    appsettings.Development.json     ← Local dev config (cluster URLs, EntraID dev tenant)

tests/
  M2LB.Proxy.Unit/
    Middleware/
      CorrelationIdMiddlewareTests.cs
    Configuration/
      ResilienceOptionsTests.cs
  M2LB.Proxy.Integration/
    Pipeline/
      AuthenticationTests.cs         ← Validates 401 on missing/invalid token
      RoutingTests.cs                ← Validates forward to stub backend
      ObservabilityTests.cs          ← Validates correlation ID propagation in logs
    Health/
      HealthCheckTests.cs

.pipeline/
  azure-pipelines.yml

README.md
```

**Structure Decision**: Single `M2LB.Proxy.Api` project with no separate Domain or Infrastructure
project. Rationale: the proxy has no business domain, and infrastructure concerns (Key Vault,
App Configuration) are handled by ASP.NET Core's built-in configuration builder — no separate
project boundary adds value here. This follows the M2LB naming convention (`M2LB.[Modul].Api`)
while correctly omitting the Domain and Infrastructure projects that only apply to services with
a business domain.

## Post-Phase-1 Constitution Re-Check

| Principle | Status | Design Decision |
|-----------|--------|-----------------|
| **I. Single Ingress Point** | ✅ Pass | `appsettings.Development.json` uses `localhost` stub; production config uses private VNet addresses loaded from App Config |
| **II. Token Validation & Identity Propagation** | ✅ Pass | `app.MapReverseProxy().RequireAuthorization()` — all YARP routes require a valid bearer token; `Authorization` forwarded via default YARP header pass-through |
| **III. No Business Logic** | ✅ Pass | No service, repository, or domain classes in project structure |
| **IV. Observability by Default** | ✅ Pass | `CorrelationIdMiddleware` + Serilog request logging + `/healthz` via `MapHealthChecks` + App Insights telemetry |
| **V. Resilience Configuration is Mandatory** | ✅ Pass | `ConfigureHttpClientDefaults` with `AddStandardResilienceHandler()` applied globally; per-route timeout override via `ForwarderRequestConfig.ActivityTimeout`; YARP passive health checks for circuit-breaker-like failover |
