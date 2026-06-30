# Contract: Health Endpoints

**Adapter**: BiRK Person-adapter
**Reference**: FR-019, FK-7.3, ADR-026
**Authentication**: None required
**Gateway**: MUST NOT be routed via YARP gateway

---

## GET /health/live — Liveness

Returns healthy if the application process is running and able to respond to requests.
No dependency checks are performed. Always returns HTTP 200.

### Response

| HTTP status | Body | Meaning |
|-------------|------|---------|
| `200 OK` | `{"status":"Healthy","timestamp":"..."}` | Process is alive |

---

## GET /health/ready — Readiness

Returns the aggregate dependency health status. Implemented via ASP.NET health checks
tagged `"ready"` (`personmodul`, `feilkoe`).

### Response

| HTTP status | Body `status` | Meaning |
|-------------|---------------|---------|
| `200 OK` | `Healthy` | All dependencies healthy |
| `200 OK` | `Degraded` | PersonModule API temporarily unavailable |
| `503 Service Unavailable` | `Unhealthy` | PersonModule or feilkoe database unreachable |

### Response Body

```json
{
  "status": "Healthy | Degraded | Unhealthy",
  "timestamp": "2026-06-26T10:00:00+00:00"
}
```

### Status Mapping

| `HealthCheckResult` | JSON `status` | HTTP status |
|---------------------|---------------|-------------|
| `Healthy` | `Healthy` | 200 |
| `Degraded` | `Degraded` | 200 |
| `Unhealthy` | `Unhealthy` | 503 |

### Dependency Health Logic

| Dependency | Healthy | Degraded | Unhealthy |
|------------|---------|----------|-----------|
| `personmodul` | PersonModule API reachable | API returning 5xx or timeout | Connection refused |
| `feilkoe` | Azure SQL `feilkoe` table reachable | — | SQL connection lost |

**Note:** `EventHubsHealthCheck` is not wired to the `/health/ready` probe because
`EventProcessorClient` instances are created per-hub via factory and are not registered
as DI singletons.
