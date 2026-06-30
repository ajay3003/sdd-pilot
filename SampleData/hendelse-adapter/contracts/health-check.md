# Contract: Health Check Endpoint (exposed)

The adapter exposes a health check endpoint via .NET health check middleware (FR-018). Used by Azure Container App's liveness/readiness probes and by the operations team.

---

## GET /health

Returns the aggregate health status of the adapter and all its dependencies.

**Authentication**: None (unauthenticated — internal network only via VNet)  
**Exposed via**: Kestrel (separate port from event processing)

**Response** (200 OK — healthy):

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.123",
  "entries": {
    "event-hubs": {
      "status": "Healthy",
      "duration": "00:00:00.020"
    },
    "hendelsestjenesten": {
      "status": "Healthy",
      "duration": "00:00:00.045"
    },
    "tjeneste": {
      "status": "Healthy",
      "duration": "00:00:00.030"
    },
    "azure-sql": {
      "status": "Healthy",
      "duration": "00:00:00.028"
    }
  }
}
```

**Response** (503 Service Unavailable — degraded or unhealthy):

```json
{
  "status": "Unhealthy",
  "totalDuration": "00:00:05.001",
  "entries": {
    "event-hubs": { "status": "Healthy", "duration": "00:00:00.020" },
    "hendelsestjenesten": {
      "status": "Unhealthy",
      "duration": "00:00:05.001",
      "description": "Connection timeout"
    },
    "tjeneste": { "status": "Healthy", "duration": "00:00:00.030" },
    "azure-sql": { "status": "Healthy", "duration": "00:00:00.028" }
  }
}
```

**Health checks**:

| Check name | What it verifies |
|------------|-----------------|
| `event-hubs` | Can retrieve Event Hub properties via SDK (authenticated read) |
| `hendelsestjenesten` | HTTP GET to Hendelsestjenesten's own health endpoint or a lightweight probe |
| `tjeneste` | HTTP GET to Tjeneste's own health endpoint or a lightweight probe |
| `azure-sql` | `SELECT 1` against the adapter's Azure SQL database |

**Additional reported data** (via OpenTelemetry to Azure Monitor — FR-017, FR-018):
- Timestamp of last successful Event Hubs read
- Stream lag (current offset vs. latest offset per partition)
- Error queue depth
- Messages processed per table (TvangsProtokoll, Rømming)
- Successful/updated/unchanged delivery counts
- Error counts
