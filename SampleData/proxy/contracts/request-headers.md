# Contract: Request and Response Headers

Defines the headers the proxy reads, generates, and propagates on every request.

---

## Inbound Headers (client → proxy)

| Header | Required | Behaviour |
|--------|----------|-----------|
| `Authorization: Bearer <token>` | Yes (all routes except `/healthz`) | Validated by EntraID JWT middleware. Absent or invalid → HTTP 401. Forwarded unchanged to upstream. |
| `X-Correlation-Id` | No | If present, used as the correlation ID for this request. MUST be a UUID v4. If absent, a new UUID v4 is generated. |

---

## Propagated Headers (proxy → upstream backend)

| Header | Value | Notes |
|--------|-------|-------|
| `Authorization` | Forwarded unchanged from inbound request | Backends use claims to perform their own authorisation checks (PP-02). |
| `X-Correlation-Id` | The resolved correlation ID (inbound value or generated) | Injected via YARP `RequestHeaderTransform` — always present on upstream request. |

---

## Response Headers (proxy → client)

| Header | Value | Notes |
|--------|-------|-------|
| `X-Correlation-Id` | The resolved correlation ID | Echo'd back so clients can correlate responses with requests. |

---

## Headers the Proxy MUST NOT Forward

| Header | Reason |
|--------|--------|
| Any header containing raw token claims decoded by the proxy | Prevents claim spoofing |
| `X-Forwarded-*` internal routing headers added by infrastructure | Infrastructure headers are for internal Azure routing; should not be passed to business backends |

---

## Notes

- YARP default behaviour passes all client headers to the upstream unless explicitly removed.
  Any new sensitive header added by the proxy for internal purposes MUST be explicitly stripped
  before forwarding to upstreams outside the VNet.
- Header names are case-insensitive per HTTP spec; implementation MUST treat `x-correlation-id`
  and `X-Correlation-Id` as equivalent.
