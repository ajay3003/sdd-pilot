# Authorisation Module Integration Contract

**Branch**: `001-person-module` | **Date**: 2026-03-06

This document describes how the Person module integrates with the Authorisation module
for access decisions. This is the Person module's side of the integration contract.

---

## Core Principle

The Person module makes **no local access decisions**. All access decisions are evaluated
by the Authorisation module (`POST /api/autorisasjon/v1/evaluer`) per PP-02.

The Person module's auth client (`IAutorisasjonClient`) is a typed HTTP client with:
- Managed Identity authentication (PS-02)
- Polly retry: 2 retries, exponential backoff (50ms, 100ms)
- Per-attempt timeout: 500ms
- Circuit breaker: opens after 5 failures in 30s
- Fail-closed: any failure → `AuthorisasjonException` → HTTP 503 to caller (FR-031)

---

## Auth Call Patterns

### Pattern 1: General Operation Check (per org unit)

Used for: `soekBarn` (Person:SoekBarn), `hentBarn` for Nivå 0/1 (Person:SeBarnGrunnprofil)

```
POST /api/autorisasjon/v1/evaluer
Body: {
  "BrukerId": "<UUID from JWT>",
  "OperasjonId": "Person:SoekBarn",
  "OrgEnhetId": "<UUID from JWT claims or query context>"
}
Response: { "Tillatt": true/false }
```

---

### Pattern 2: Child-Specific Operation Check

Used for: `hentBarn` (Person:SeBarnProfil), national ID reveal (Person:SeFullIdentitet)

```
POST /api/autorisasjon/v1/evaluer
Body: {
  "BrukerId": "<UUID from JWT>",
  "OperasjonId": "Person:SeBarnProfil",
  "BarnId": "<PersonId of the child>"
}
Response: { "Tillatt": true/false }
```

---

### Pattern 3: Batch SeGradertBarn Check (search optimisation)

Used for: `soekBarn` — fetches all Kode 6/7 children the user can see in one call.
This enables the security filter to be applied in the SQL query (O(1) calls per search).

```
POST /api/autorisasjon/v1/evaluer/batch
Body: {
  "BrukerId": "<UUID from JWT>",
  "OperasjonId": "Person:SeGradertBarn"
}
Response: {
  "TillatteEntiteter": ["<UUID>", "<UUID>", ...]
}
```

The returned UUIDs are used in the SQL WHERE clause:
```sql
WHERE (s.Nivaa < 2) OR (b.BarnRegistreringId IN @tillatteEntiteter)
```

---

### Pattern 4: Access Grant Creation (US3)

Used for: `tildelGradertBarntilgang` mutation

The Person module orchestrates (FR-033):
1. Verify granting user has `Person:AdministerGradertBarntilgang` for target child (Pattern 2)
2. Verify child exists and has KreverGradertTilgang = true
3. Enforce self-assignment check (FR-015)
4. Call Authorisation module to create the grant:

```
POST /api/autorisasjon/v1/tilganger/barn
Body: {
  "TildelAv": "<granting user UUID>",
  "TildelTil": "<recipient user UUID>",
  "BarnId": "<PersonId>",
  "RolleId": "<UUID for 'Gradert barn — lesetilgang' role>",
  "GyldigTil": "2026-06-01T00:00:00Z"  // optional
}
Response: { "TilgangId": "<UUID>", "GyldigFra": "...", "GyldigTil": "..." }
```

---

### Pattern 5: Access List Query (US3)

Used for: `hentGradertBarntilgang`

```
GET /api/autorisasjon/v1/tilganger/barn/{barnId}
Response: [
  { "BrukerId": "<UUID>", "RolleId": "<UUID>", "GyldigFra": "...", "GyldigTil": "..." },
  ...
]
```

Display names are fetched from Microsoft Graph using BrukerId.

---

## Fail-Closed Behavior (FR-031)

If any auth call fails (timeout, circuit open, 5xx response):
1. Throw `AuthorisasjonException` in the application layer
2. Middleware catches and returns HTTP 503 with generic error message
3. Never return data assuming access is granted
4. Other concurrent requests are unaffected

The circuit breaker prevents repeated calls to a downed Auth module — it opens after 5 consecutive failures and stays open for 30 seconds before attempting recovery.

---

## Token Propagation

The user's EntraID JWT is validated by the reverse proxy (YARP) before reaching the Person module. The Person module reads claims from the validated token:
- `oid` claim → `BrukerId` (user UUID)
- `tid` claim → tenant ID
- Additional claims as required by the Authorisation module contract

For service-to-service calls (ingestion API from BiRK adapter), Managed Identity is used — no user JWT. `UtfoertAv` is set to the adapter's Managed Identity object ID.
