# Feature Specification: Proxy Service Initial Setup

**Feature Branch**: `001-proxy-initial-setup`
**Created**: 2026-03-24
**Status**: Draft
**Input**: User description: "this feature will be the initial setup for the proxy service. We will need to set up an initial project with a best-practice folder structure and naming scheme."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Developer Onboards and Runs the Service Locally (Priority: P1)

A developer clones the repository for the first time and needs to get a working, runnable service
up in their local environment. The project structure must be immediately navigable: they should
understand the purpose of each folder before reading a single line of code, and the service must
start successfully with only minimal local configuration.

**Why this priority**: Without a runnable baseline, no other work on the proxy can begin.
This is the unblocking prerequisite for all future features.

**Independent Test**: Clone the repo, supply the minimal required configuration, run the service,
and confirm it starts without errors and responds to a health-check request.

**Acceptance Scenarios**:

1. **Given** a fresh clone with no prior environment setup,
   **When** a developer follows the setup instructions and starts the service,
   **Then** the service starts without errors and the health-check endpoint returns a healthy response.

2. **Given** the running service,
   **When** a developer inspects the folder structure,
   **Then** every folder and project name follows the M2LB naming convention and its responsibility
   is self-evident from its name and position in the hierarchy.

3. **Given** the running service without any valid token,
   **When** a request is sent to any proxied route,
   **Then** the service rejects the request with an authentication error before forwarding it upstream.

---

### User Story 2 - Developer Verifies End-to-End Routing and Observability (Priority: P2)

A developer working on the proxy needs confidence that routing, token forwarding, correlation ID
propagation, and structured logging all work together correctly — before any production backend
is available. A sample/stub route configuration should demonstrate the full request lifecycle.

**Why this priority**: Establishing observable, verifiable routing behavior is the proof-of-concept
that the foundational infrastructure is wired together correctly.

**Independent Test**: Start the service, send an authenticated request to a configured stub route,
and confirm the request is logged with a correlation ID, the upstream receives the forwarded auth
header, and the response is returned to the caller.

**Acceptance Scenarios**:

1. **Given** a service with a sample YARP route configured against a reachable stub,
   **When** an authenticated request is made,
   **Then** the response is returned successfully, the log output contains a `correlation_id`,
   and the upstream stub confirms it received the `Authorization` header unchanged.

2. **Given** the running service,
   **When** an inbound request does not already carry a correlation header,
   **Then** the service generates one and propagates it to the upstream and includes it in the
   log entry for that request.

3. **Given** a configured route with explicit timeout and retry policy,
   **When** the upstream is unreachable,
   **Then** the service retries according to the configured policy and ultimately returns a
   meaningful error response — it does not hang indefinitely.

---

### User Story 3 - Operator Monitors Service Health (Priority: P3)

An operator or deployment pipeline needs a reliable way to verify that the proxy service and its
configured backend clusters are in a good state, without triggering any authenticated business
logic or generating spurious audit log entries.

**Why this priority**: A health endpoint is required for deployment readiness checks and
infrastructure monitoring, and it is significantly simpler than P1/P2.

**Independent Test**: Start the service and query the health endpoint without supplying
authentication. Confirm a structured health response is returned for the service and for each
configured cluster.

**Acceptance Scenarios**:

1. **Given** the running service with one or more clusters configured,
   **When** the health endpoint is queried without a token,
   **Then** a structured response is returned indicating overall service health and the
   status of each backend cluster.

2. **Given** a backend cluster that is unreachable,
   **When** the health endpoint is queried,
   **Then** the response reflects the degraded state of that cluster without affecting
   the health reporting of other clusters.

---

### Edge Cases

- What happens when the configuration source (Key Vault / App Configuration) is unreachable at startup? The service MUST fail to start rather than run with stale or partial configuration.
- What happens when an inbound request already carries a `X-Correlation-Id` header? The service MUST forward the existing value rather than overwriting it.
- What happens if a route is requested that has no matching YARP cluster? The service MUST return a clear error response and log the unmatched route.
- What happens when the EntraID validation endpoint is temporarily unavailable? The service MUST reject all authenticated requests with an appropriate error rather than allowing them through.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The project MUST follow the M2LB folder and naming convention:
  `src/M2LB.Proxy.Api/`, with supporting projects (e.g., infrastructure concerns) in their own
  appropriately named sub-projects, and tests in `tests/`.
- **FR-002**: The service MUST expose a health-check endpoint that returns the health of the
  service and the status of all configured backend clusters, and MUST NOT require authentication.
- **FR-003**: The service MUST validate all inbound JWT tokens issued by EntraID before forwarding
  any request to a backend cluster. Requests with missing or invalid tokens MUST be rejected.
- **FR-004**: The service MUST attach a `correlation_id` to every request (generating one if absent)
  and propagate it to upstream services and include it in all log entries for that request.
- **FR-005**: The service MUST emit structured logs (JSON format) for every request/response cycle,
  including: timestamp, correlation ID, HTTP method, path, HTTP status code, target cluster name,
  and latency. Tokens, personal identifiers, and payload content MUST NOT appear in logs.
- **FR-006**: Every YARP route configuration MUST declare explicit timeout, retry, and circuit
  breaker policies. A route without all three policies MUST NOT be accepted.
- **FR-007**: All environment-specific configuration values (cluster URLs, EntraID tenant/client IDs)
  MUST be loaded from an external configuration source at startup, not hardcoded in source files.
  For local development, loading from `appsettings.Development.json` is acceptable.
- **FR-008**: The service MUST return HTTP 401 for unauthenticated requests and HTTP 503 for
  requests that cannot be forwarded due to a circuit breaker being open or a timeout being exceeded.
- **FR-009**: The service MUST expose readiness for proxy-forwarding of at least one sample/stub
  route in the initial setup to validate that the full request pipeline is wired correctly.

### Assumptions

- The initial setup targets local development and CI validation; production deployment configuration
  (Key Vault references, VNet addresses) is out of scope for this feature and will be addressed in a
  dedicated infrastructure/deployment feature.
- A sample stub route targeting a local or test URL is sufficient to validate end-to-end behavior;
  no real backend service needs to be available for this feature.
- Managed Identity configuration for proxy-to-Azure-service calls (Key Vault access) is scaffolded
  but may fall back to developer credentials locally; production Managed Identity binding is a
  deployment-level concern.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer with no prior context on this repository can navigate to the correct
  file for any proxy concern (routing, auth, logging, resilience) within 2 minutes, based solely
  on folder and file names.
- **SC-002**: The service starts and passes its health check within 30 seconds on a standard
  development machine after supplying the required configuration.
- **SC-003**: 100% of requests to authenticated routes without a valid token are rejected before
  reaching any upstream service — verifiable by confirming zero forwarded requests in upstream logs
  when unauthenticated requests are sent.
- **SC-004**: Every request generates exactly one structured log entry containing a `correlation_id`,
  with no sensitive data present — verifiable by inspecting log output for a set of test requests.
- **SC-005**: The project builds and all tests pass in a clean CI environment with no manual
  steps beyond standard dependency restore.
