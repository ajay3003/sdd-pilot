# Contract: Tjeneste BirkTiltakPK Lookup (consumed)

The adapter calls this endpoint synchronously during event processing to resolve `BirkTiltakPK` to `BarnId` and `TjenesteId` (FR-004).

**Authentication**: Azure Managed Identity (bearer token via `DefaultAzureCredential`)  
**Base URL**: Configured via Azure Key Vault at startup  
**Resilience**: Polly pipeline — timeout (10 s) → retry (10x, 5 s–5 min exponential) → circuit breaker (5 failures / 30 s window, 1 min open)  
**Correlation**: `X-Correlation-Id` header set to per-event `CorrelationId`

---

## GET /api/tjeneste/v1/birk/{birkTiltakPK}

Looks up a Tjeneste record by its BiRK tiltaks primary key.

**Path parameter**: `birkTiltakPK` — BiRK numeric key (integer)

**Responses**:

| HTTP | Meaning | Adapter action |
|------|---------|---------------|
| 200 OK | Match found | Extract `barnId` and `tjenesteId` from response; use in delivery |
| 404 Not Found | No match | Deliver event with `BarkId = null`, `BirkTiltakPK` set (FR-005) |
| 5xx | Transient error | Retry with backoff; error queue after max retries |

**Response body** (on 200):

```json
{
  "barnId": "uuid",
  "tjenesteId": "uuid"
}
```

> **Note**: The exact endpoint path is based on the spec assumption. Align with the Tjeneste team's published API contract before implementation. If the path differs, update this document and the `TjenesteHttpClient` configuration.

---

## No-Match Behaviour

When Tjeneste returns 404, the adapter delivers the event with `BarnId = null` and `BirkTiltakPK` set. The adapter has no pending state for unresolved child links — this is Hendelsestjenesten's responsibility (adapter P-04, FR-005). The no-match outcome is logged at information level with the `BirkTiltakPK` and `BirkHendelsesId` for traceability.
