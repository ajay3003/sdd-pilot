# Data Model: Proxy Service Initial Setup

**Branch**: `001-proxy-initial-setup` | **Date**: 2026-03-24

> The proxy service is stateless and persists no data. This document describes the
> **configuration model** and **runtime context structures** that flow through the service.

---

## Configuration Entities

### RouteDefinition

Represents a single YARP route entry in configuration. One or more routes may target the
same cluster.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `RouteId` | string | yes | Unique identifier, e.g. `person-api-route` |
| `ClusterId` | string | yes | References a `ClusterDefinition.ClusterId` |
| `Match.Path` | string | yes | Path pattern, e.g. `/api/person/{**catch-all}` |
| `Timeout` | duration | no | Per-route timeout override; defaults to `ResilienceOptions.DefaultTimeout` |
| `AuthorizationPolicy` | string | no | Defaults to `Default` (requires valid EntraID token) |

Validation rules:
- `RouteId` MUST be unique across all routes.
- `Match.Path` MUST begin with `/api/`.
- `ClusterId` MUST reference an existing `ClusterDefinition`.

---

### ClusterDefinition

Represents a named group of backend destinations (YARP cluster). All requests matching a
`RouteDefinition` are forwarded to one destination within the cluster.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `ClusterId` | string | yes | Unique identifier, e.g. `person-api` |
| `Destinations` | map<string, DestinationDefinition> | yes | At least one destination |
| `HealthCheck.Passive.Enabled` | bool | no | Defaults to `true` |
| `HealthCheck.Passive.ReactivationPeriod` | duration | no | How long before re-probing a degraded destination; default `00:00:30` |
| `LoadBalancingPolicy` | string | no | Defaults to `RoundRobin` |

Validation rules:
- At least one destination MUST be defined per cluster.
- All destination addresses MUST use HTTPS.

---

### DestinationDefinition

A single backend endpoint within a cluster.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Address` | string (URI) | yes | Base URL of the backend, e.g. `https://person-api.internal/` |
| `Health` | string (URI) | no | Optional active health probe URL |

Validation rules:
- `Address` MUST be an HTTPS URI.
- In production, `Address` MUST resolve to a private VNet FQDN only.

---

### ResilienceOptions

Strongly-typed options class binding to `Resilience` section in configuration.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `DefaultTimeoutSeconds` | int | `30` | Applied to all routes without an explicit timeout |
| `RetryCount` | int | `3` | Number of retry attempts for transient errors (502, 503, 504) |
| `RetryBaseDelaySeconds` | double | `1.0` | Base delay for exponential backoff |
| `CircuitBreakerFailureRatio` | double | `0.5` | Failure ratio before circuit opens |
| `CircuitBreakerSamplingSeconds` | int | `30` | Sampling window for circuit breaker |
| `CircuitBreakerBreakDurationSeconds` | int | `30` | Duration circuit stays open |

---

## Runtime Context Structures

### RequestContext (in-flight, not persisted)

Assembled by `CorrelationIdMiddleware` and available to logging enrichers and YARP transforms.

| Field | Type | Source | Description |
|-------|------|--------|-------------|
| `CorrelationId` | UUID v4 string | `X-Correlation-Id` header or generated | Forwarded upstream and included in all log entries |
| `ClusterName` | string | YARP routing decision | Set after route matching; used in log enrichment |
| `RequestStartTime` | DateTimeOffset | Set by request logging middleware | Used to compute latency |

---

### HealthReport (response, not persisted)

Returned by `/healthz`. Structure follows `Microsoft.Extensions.Diagnostics.HealthChecks`
`HealthReport` format.

| Field | Type | Description |
|-------|------|-------------|
| `status` | string (`Healthy` / `Degraded` / `Unhealthy`) | Overall service status |
| `totalDuration` | timespan | Time taken to evaluate all health checks |
| `entries` | map<string, HealthEntry> | Per-check results keyed by check name |

### HealthEntry

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `Healthy` / `Degraded` / `Unhealthy` |
| `duration` | timespan | Time taken for this check |
| `description` | string | Optional human-readable status detail |
| `data` | map<string, object> | Optional additional context (e.g., destination counts) |
