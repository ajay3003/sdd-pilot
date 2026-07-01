# Research: Proxy Service Initial Setup

**Branch**: `001-proxy-initial-setup` | **Date**: 2026-03-24

---

## Decision 1: Project Structure — Single vs Multi-Project

**Decision**: Single `M2LB.Proxy.Api` project (no separate Domain or Infrastructure project).

**Rationale**: The M2LB development guidelines define `M2LB.[Modul].Domain/` and
`M2LB.[Modul].Infrastructure/` for services that have business domain logic and a separate
data layer. The proxy service has neither. All infrastructure concerns — Key Vault and Azure
App Configuration integration — are handled by ASP.NET Core's built-in `IConfigurationBuilder`
extensions (`AddAzureKeyVault`, `AddAzureAppConfiguration`) wired directly in `Program.cs`.
No separate project boundary adds clarity or enforces a meaningful separation.

**Alternatives considered**:
- `M2LB.Proxy.Infrastructure/` for Key Vault integration — rejected: overkill for one-liner
  builder extensions; adds a project reference with no reuse benefit.
- `M2LB.Proxy.Domain/` for route configuration models — rejected: YARP's own config models
  (`RouteConfig`, `ClusterConfig`) are sufficient; wrapping them in domain types adds indirection
  for zero business value.

---

## Decision 2: YARP Version and Resilience Approach

**Decision**: YARP 2.3+ with `Microsoft.Extensions.Http.Resilience` (Polly v8) via
`ConfigureHttpClientDefaults` and YARP passive health checks for failover.

**Rationale**: YARP 2.x integrates with .NET 8's `IHttpClientBuilder` pipeline. Calling
`builder.Services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler())` applies
Polly v8 retry, circuit breaker, and timeout to the HTTP client used by the YARP forwarder.
This is the idiomatic .NET 8 approach and avoids the older Polly v7 `AddPolicyHandler` pattern.

`ForwarderRequestConfig.ActivityTimeout` provides a per-route timeout override at the YARP
routing level, which is evaluated before the HTTP client timeout — useful for routes with
different latency expectations.

YARP passive health checks (`HealthCheckConfig`) automatically mark destinations as degraded
when they return persistent errors, providing circuit-breaker-like behaviour without an explicit
Polly circuit breaker per destination. Both mechanisms complement each other.

**Alternatives considered**:
- Polly v7 `AddPolicyHandler` on `IHttpClientBuilder` — rejected: deprecated pattern in .NET 8;
  `Microsoft.Extensions.Http.Resilience` supersedes it.
- Manual circuit breaker per cluster via `IProxyConfigFilter` — rejected: more complexity than
  combining Polly v8 `AddStandardResilienceHandler` with YARP passive health.

---

## Decision 3: Correlation ID Propagation

**Decision**: Custom `CorrelationIdMiddleware` + YARP `RequestHeaderTransform`.

**Rationale**: The middleware runs early in the ASP.NET Core pipeline, reads `X-Correlation-Id`
from the inbound request, generates a `Guid.NewGuid().ToString()` if absent, sets it on
`HttpContext.TraceIdentifier` (so it is available to Serilog's request logging enricher), and
adds it to `HttpContext.Response.Headers`. The YARP transform propagates it upstream explicitly
to ensure it is always forwarded even when YARP strips unlisted headers.

Using `Activity.Current.TraceId` (W3C `traceparent`) was considered but rejected for this
initial setup because M2LB specifies `correlation_id` as a UUID v4 in GL-28 — the `traceparent`
format differs. The two can coexist; `Activity` based tracing can be layered on top later.

**Alternatives considered**:
- `CorrelationIdMiddleware` from NuGet (`CorrelationId` package) — viable, but adds a dependency
  for a ~30-line middleware; implementing inline avoids an external dependency with no other value.
- W3C `traceparent` / `Activity.Current` only — rejected: M2LB requires `correlation_id` UUID v4
  specifically (GL-28); `TraceId` format differs.

---

## Decision 4: Authentication Approach

**Decision**: `Microsoft.Identity.Web` JWT bearer validation via
`AddMicrosoftIdentityWebApiAuthentication`; all YARP routes protected via
`.RequireAuthorization()` on `MapReverseProxy`.

**Rationale**: `Microsoft.Identity.Web` is the standard library for EntraID-backed JWT validation
in ASP.NET Core. It handles token signature verification, issuer validation, audience validation,
and OIDC metadata endpoint caching. Applying `.RequireAuthorization()` to `MapReverseProxy()`
means all routes are protected with a single call — no per-route attribute needed, reducing the
risk of accidentally leaving a route unauthenticated.

**Alternatives considered**:
- Raw `AddJwtBearer` configuration — viable but more boilerplate; `Microsoft.Identity.Web`
  wraps this with sensible defaults for EntraID and is the recommended approach.
- Per-route `[Authorize]` attributes — rejected: proxy routes are YARP config, not controllers;
  `.RequireAuthorization()` on the `MapReverseProxy()` call is the correct YARP pattern.

---

## Decision 5: Structured Logging

**Decision**: Serilog with `WriteTo.Console(formatter: new JsonFormatter())` for local/CI, and
`WriteTo.ApplicationInsights(telemetryConfiguration)` for production. Serilog request logging
middleware (`UseSerilogRequestLogging`) enriched with `correlation_id` from `HttpContext`.

**Rationale**: Serilog is the de-facto standard for structured logging in the ASP.NET Core
ecosystem. `UseSerilogRequestLogging` replaces ASP.NET Core's default request log lines with
a single structured event per request, which can be enriched with custom properties (correlation
ID, cluster name, upstream latency). Application Insights sink forwards telemetry to Azure
Monitor, satisfying the observability requirement (GL-28, PS-08).

**Alternatives considered**:
- `Microsoft.Extensions.Logging` only — rejected: does not produce structured JSON out of the
  box; requires additional setup to avoid flat string log lines.
- OpenTelemetry + OTLP exporter — viable future direction; not chosen for initial setup because
  the M2LB platform standard (PS-08) defers to IT-department policy on observability platform,
  and Application Insights is the established channel.

---

## Decision 6: Health Check Endpoint

**Decision**: `Microsoft.Extensions.Diagnostics.HealthChecks` + `MapHealthChecks("/healthz")`
with custom `YarpClusterHealthCheck` that reports per-cluster status.

**Rationale**: The standard ASP.NET Core health checks infrastructure integrates with Azure
Container Apps / AKS liveness and readiness probes out of the box. YARP exposes destination
health via `IProxyStateLookup`; wrapping it in `IHealthCheck` surfaces cluster-level degradation
in the `/healthz` response. The endpoint is explicitly excluded from auth middleware so that
infrastructure (load balancers, deployment pipelines) can poll it without a token.

**Alternatives considered**:
- YARP's built-in active health check endpoint — suitable for destination-level probes but does
  not provide the standardised `HealthReport` JSON expected by Azure infrastructure tooling.
- Custom controller endpoint — rejected: `MapHealthChecks` is the idiomatic pattern and avoids
  adding a controller project dependency.
