# Contract: GET /health

**Direction**: Internal (not exposed via YARP reverse proxy)  
**Consumer**: Azure App Service built-in health probe; operations team tooling  
**Spec reference**: FR-008, US4

---

## Endpoint

```
GET /health
```

Available on the service's internal Kestrel port. MUST NOT be routed externally by the YARP
reverse proxy (US4 scenario 4 — returns HTTP 404 from proxy, not from the service).

---

## Response Scenarios

### Scenario 1 — Both dependencies healthy

**HTTP 200**
```json
{
  "status": "Healthy",
  "checks": {
    "serviceBus": "Healthy",
    "blobStorage": "Healthy"
  }
}
```

### Scenario 2 — One or more dependencies unavailable

**HTTP 200**
```json
{
  "status": "Degraded",
  "checks": {
    "serviceBus": "Healthy",
    "blobStorage": "Unhealthy"
  }
}
```

`status` is `"Degraded"` if any check is `"Unhealthy"`. The service is still running.
Individual check values are `"Healthy"` or `"Unhealthy"` — never `"Degraded"`.

### Scenario 3 — Service unavailable

**HTTP 503** (no response body guaranteed)

Returned by the infrastructure layer (App Service, load balancer) when the service process
cannot respond to HTTP requests at all.

---

## Probe Operations

| Check | Operation | SDK Call |
|---|---|---|
| `serviceBus` | Verifies queue exists and Managed Identity has access | `ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync("leselogg")` |
| `blobStorage` | Verifies container is reachable and Managed Identity has access | `BlobContainerClient.GetPropertiesAsync()` |

Both probes use the same `DefaultAzureCredential` as the main service. A permission loss on
Managed Identity will surface as `"Unhealthy"` on the affected check (TEST-E-05).

---

## Response Format Notes

- The `status` and `checks` property names are **camelCase** as shown.
- `Content-Type`: `application/json`
- The response writer maps .NET `HealthStatus.Healthy` → `"Healthy"` and both
  `HealthStatus.Degraded` and `HealthStatus.Unhealthy` → `"Unhealthy"` for individual checks.
  The top-level `status` field is `"Healthy"` only when all checks are `Healthy`;
  otherwise `"Degraded"`.
- No additional fields (timestamps, version, details) are included in the response.

---

## YARP Configuration Note

The YARP reverse proxy routes are path-prefix based. `/health` must not appear as a
registered route in the YARP configuration. Any request reaching the proxy for `/health`
should result in HTTP 404 from the proxy itself, not from this service.
