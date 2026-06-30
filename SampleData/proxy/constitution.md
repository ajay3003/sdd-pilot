<!--
SYNC IMPACT REPORT
==================
Version change: [UNVERSIONED TEMPLATE] → 1.0.0
Added sections:
  - Core Principles (I–V)
  - Technology Stack & Constraints
  - Configuration & Secrets
  - Governance
Modified principles: N/A (initial fill)
Removed sections: All placeholder tokens replaced
Templates requiring updates:
  ✅ .specify/templates/plan-template.md — Constitution Check section aligns with principles I–V
  ✅ .specify/templates/spec-template.md — No structural changes required; requirements must reference proxy-specific constraints
  ✅ .specify/templates/tasks-template.md — No structural changes required; Phase 2 foundational tasks must include YARP route config, token validation, and observability setup
Deferred TODOs: None
-->

# M2LB.Proxy Constitution

## Core Principles

### I. Single Ingress Point (NON-NEGOTIABLE)

M2LB.Proxy is the sole internet-facing entry point for all M2LB backend services.
Backend service URLs MUST NOT be publicly reachable by any other path.

- All YARP routes MUST target private VNet addresses only (no public DNS for backends).
- New backend services MUST be registered as YARP cluster destinations before they are used.
- This proxy MUST NOT be bypassed for any reason — including debugging, testing, or emergency access.

**Rationale**: GL-01 and GL-11 — the reverse proxy is the entire network security perimeter.
A gap here voids zero-trust for the entire platform.

---

### II. Token Validation and Identity Propagation (NON-NEGOTIABLE)

All incoming requests MUST carry a valid EntraID JWT bearer token.
Token validation MUST happen in this proxy before the request reaches any backend.

- Invalid or missing tokens MUST be rejected with HTTP 401 before forwarding.
- After successful validation, the proxy MUST propagate the `Authorization` header downstream unchanged so backends can inspect claims.
- The proxy MUST NOT issue, forge, or alter tokens.
- Managed Identity MUST be used for any proxy-to-Azure-service calls (Key Vault, etc.) — no stored secrets.

**Rationale**: PP-02 and PS-01 — authentication is centralised at the gateway; backends trust that traffic arriving from the proxy has been validated. Fail-closed is mandatory (GL-25).

---

### III. No Business Logic

M2LB.Proxy contains routing, authentication enforcement, and cross-cutting infrastructure concerns only.
Zero domain or application business logic belongs here.

- Route configuration (paths, clusters, transforms) is the only application-level concern.
- Header transformations are permitted only to propagate identity context or correlation metadata — never to modify business payloads.
- Rate limiting and request size limits are infrastructure concerns and ARE permitted.

**Rationale**: PP-07 — business logic belongs in the domain layer. A proxy that accumulates business rules becomes a bottleneck that defeats the purpose of a microservice architecture.

---

### IV. Observability by Default

Every request/response cycle MUST be traceable end-to-end.

- A `correlation_id` (UUID v4) MUST be generated for each inbound request if not already present, and forwarded to all upstream calls as `X-Correlation-Id`.
- Structured logging (JSON via Serilog or equivalent) MUST capture: timestamp, `correlation_id`, HTTP method, path, status code, upstream cluster, and latency.
- A liveness endpoint (`/helse/live`) MUST be exposed and return `200 OK` whenever the proxy process is alive, with no dependency on upstream backends.
- A readiness endpoint (`/helse/ready`) MUST be exposed and return aggregate backend cluster health, indicating whether the proxy can forward traffic.
- Metrics (request rate, error rate, p95 latency per cluster) MUST be published to Azure Monitor / Application Insights.
- Sensitive data (tokens, personal identifiers) MUST NOT appear in logs.

**Rationale**: GL-28 and PS-08 — distributed tracing is the only way to diagnose failures in a microservice architecture. Observability is a build-time requirement, not a retrofit.

---

### V. Resilience Configuration is Mandatory

Every YARP cluster MUST have an explicit resilience policy — absence of a policy is a misconfiguration.

- Timeout MUST be set on every route (recommended: 30 s default; override per route where justified).
- Retry with exponential backoff MUST be configured for transient errors (HTTP 502, 503, 504).
- Circuit breaker MUST be configured to prevent cascade failures to degraded backends.
- Retry MUST NOT be applied to non-idempotent methods (POST, PATCH) unless the route is explicitly marked safe.
- Circuit breaker state MUST be surfaced in health metrics and monitored via Azure Monitor alerts.

**Rationale**: GL-29 — in a distributed system, transient failures are the norm. An unconfigured route will propagate failures upstream and can cascade across the platform.

---

## Technology Stack & Constraints

- **Framework**: ASP.NET Core 8+ with YARP (Microsoft.ReverseProxy)
- **Authentication**: `Microsoft.Identity.Web` — EntraID JWT bearer validation
- **Resilience**: `Microsoft.Extensions.Http.Resilience` (Polly v8) — timeout, retry, circuit breaker
- **Logging**: Structured logging with JSON output; `correlation_id` in every log entry
- **Observability**: Azure Application Insights SDK
- **Secrets / Config**: Azure Key Vault via Managed Identity; no secrets in `appsettings.json` or environment variables in production
- **Deployment target**: Azure Container Apps or AKS within M2LB VNet — no public IP on the service itself; terminates TLS at Azure Application Gateway or Azure Front Door
- **TLS**: Minimum TLS 1.2 on all inbound and outbound connections (GL-12)

Route and cluster configuration MAY be defined in `appsettings.json` for local development.
In production, configuration MUST be loaded from Azure App Configuration or Key Vault references.

---

## Configuration Management

- All environment-specific values (backend cluster addresses, EntraID tenant/client IDs) MUST come from Azure App Configuration or Key Vault at runtime.
- `appsettings.json` MUST NOT contain production URLs, secrets, or client credentials.
- YARP cluster destinations in production MUST reference private VNet FQDNs or private endpoint addresses.
- Configuration changes that add or remove routes MUST be reviewed for security impact before deployment.

---

## Governance

This constitution supersedes all other guidance for M2LB.Proxy.
Where the M2LB Platform Constitution (plattformkonstitusjon) and this document overlap, the stricter rule applies.

**Amendment procedure**: Propose change in writing → Architecture review → Approval by M2LB solution architect → Update this document and increment version → Update affected templates.

**Versioning policy**:
- MAJOR: Removal or redefinition of a principle (e.g., adding business logic, removing token validation).
- MINOR: New principle or materially expanded guidance.
- PATCH: Clarifications, wording, non-semantic refinements.

**Compliance review**: Every pull request MUST include a "Constitution Check" confirming no principles are violated. A PR that introduces business logic, bypasses token validation, omits resilience configuration, or exposes backend URLs publicly MUST be rejected.

**Version**: 1.0.1 | **Ratified**: 2026-03-24 | **Last Amended**: 2026-04-20
