# Contract: Health Check Endpoint

**Endpoint**: `GET /healthz`
**Authentication**: None required (excluded from auth middleware)
**Purpose**: Service and cluster health status for infrastructure probes and monitoring

---

## Request

No body. No required headers.

---

## Response

**Content-Type**: `application/json`

| HTTP Status | Meaning |
|-------------|---------|
| `200 OK` | All checks healthy or degraded (service is running) |
| `503 Service Unavailable` | One or more checks report Unhealthy |

### Response Body

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.0123456",
  "entries": {
    "yarp-clusters": {
      "status": "Healthy",
      "duration": "00:00:00.0045678",
      "description": "All 2 destination(s) healthy",
      "data": {
        "totalDestinations": 2,
        "healthyDestinations": 2,
        "degradedDestinations": 0
      }
    }
  }
}
```

### Status Values

| Value | Meaning |
|-------|---------|
| `Healthy` | All destinations reachable and returning success responses |
| `Degraded` | One or more destinations degraded; service still operational |
| `Unhealthy` | No destinations available; service cannot forward requests |

---

## Notes

- The health endpoint MUST respond even when all backend clusters are unhealthy.
- The response MUST NOT include any route configuration, backend URLs, or internal addresses.
- Infrastructure probes (Azure Container Apps, AKS readiness) use `200` to determine readiness.
