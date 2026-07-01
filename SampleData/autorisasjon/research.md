# Research: SCIM User Synchronization Adapter

## SCIM PatchOp Parsing

**Decision**: Hand-roll a minimal PatchOp parser — no external SCIM library.

**Rationale**: Entra's provisioning engine sends exactly one operation shape: `{"op":"Replace","path":"active","value":bool}`. ASP.NET Core's `JsonSerializer` can deserialize the `Operations` array directly into a record with `Op`, `Path`, and `Value` (JsonElement). The adapter only acts on `op=Replace` + `path=active`; all other operations are silently ignored per spec (FR-002 — unknown attributes ignored).

**Alternatives considered**: `Scim2.Models` NuGet package. Rejected — the package is not actively maintained and pulls in significant dependencies for a feature set we use ~5% of. The spec's PATCH surface is deliberately minimal.

---

## SCIM Filter Parsing

**Decision**: Parse `filter` query parameter with a simple string equality parser.

**Rationale**: Entra's provisioning engine only sends two filter forms during reconciliation:
- `filter=externalId eq "..."` → query `KjentBrukere` by `ExternalId`
- `filter=userName eq "..."` → query `KjentBrukere` by `UserName`

Both are SCIM §3.4.2.2 `eq` comparisons on indexed columns. A regex match (`^(\w+) eq "(.*)"$`) covers the entire needed surface without a full SCIM filter parser.

**Alternatives considered**: Full ANTLR-based SCIM filter grammar. Rejected — overkill; spec and Entra docs confirm these are the only two filter types the adapter needs to support (FR-005).

---

## Polly Retry for Service Bus Publish

**Decision**: Use `Polly` v8.x (`Microsoft.Extensions.Resilience` or standalone `Polly`) with `ResiliencePipelineBuilder`, wrapping each `EventPublisher.PublishAsync` call.

**Rationale**: .NET 10 includes `Microsoft.Extensions.Http.Resilience` as the idiomatic Polly integration, but that targets `HttpClient`. For direct Service Bus SDK calls, a standalone `ResiliencePipeline` is simpler. The retry policy: 3 attempts, 500ms base delay, exponential backoff factor 2 (delays: 500ms → 1s → 2s = max ~3.5s total). This fits within Entra's 30-second request timeout.

**Alternatives considered**: `Polly.Contrib.WaitAndRetry` jitter helper. Deferred — jitter is valuable for high-concurrency systems; since ScimAdapter runs as a single replica (spec clarification), simple exponential backoff without jitter is sufficient.

---

## Custom Bearer Token Authentication

**Decision**: Implement `ScimBearerAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>`, mirroring the existing `DevAuthHandler` pattern.

**Rationale**: Entra's provisioning engine authenticates with an opaque provisioning credential, not an EntraID JWT. `Microsoft.Identity.Web` / JWKS validation is inappropriate here. The handler reads `Authorization: Bearer <secret>` and compares against the configured `Scim:ProvisioningSecret` (resolved from Key Vault at startup). Returns `AuthenticateResult.Success` (anonymous identity — the adapter has no concept of which user is making the request) or `AuthenticateResult.Fail`.

**Constitutional deviation (PS-01)**: Justified. PS-01 mandates EntraID for **user** authentication. The SCIM endpoint is a machine-to-machine interface between Entra's provisioning engine and this adapter — there is no human user in the loop. The provisioning credential is functionally equivalent to a managed identity secret and is stored in Key Vault per FR-016.

---

## IUserContext in ScimAdapter

**Decision**: Register a `ScimAdapterUserContext : IUserContext` that returns a fixed system UUID (`Guid.Parse("00000000-0000-0000-0000-000000000001")` — a well-known service identity) and an empty `CorrelationId`.

**Rationale**: `AuditInterceptor` depends on `IUserContext` via constructor injection, so `AutorisasjonsDbContext` cannot be resolved without it. `KjentBruker` is not in `AuditInterceptor.TrackedEntityTypes`, so no audit rows are generated — but the DI graph still requires the service to exist. A `ScimAdapterUserContext` satisfies the interface without introducing complexity.

**CorrelationId**: Middleware sets the CorrelationId per-request (as in the main Api). The `ScimAdapterUserContext` returns null from the interface, while structured logging captures the CorrelationId via the middleware's log scope — same pattern as the main service.

---

## KjentBruker Placement in Project Structure

**Decision**: `KjentBruker` entity class lives in `Autorisasjon.Infrastructure` (not `Autorisasjon.Domain`).

**Rationale**: `KjentBruker` is not a domain concept — it is an adapter-specific sync-state record. Putting it in Domain would pollute the pure domain model with adapter plumbing. Infrastructure already owns the DbContext and EF configurations; adding an entity here is cohesive. `Autorisasjon.ScimAdapter` references Infrastructure, so it can access `KjentBruker` through that reference.

**Consequences**: `AuditInterceptor.TrackedEntityTypes` does NOT include `KjentBruker` — intentional. The event stream (Service Bus) is the authoritative audit trail for user sync state changes.

---

## Transactional Outbox vs Polly + 5xx

**Decision**: Use Polly retry on publish + 5xx response to Entra on exhaustion. No outbox table.

**Rationale**: The spec explicitly rules out an outbox table (clarification: "no separate outbox or FailedEvent database table"). The adapter's reliability model relies on:
1. Polly (3 retries) covering transient Service Bus failures (network blips of seconds)
2. Entra's provisioning engine retrying on 5xx responses (its built-in retry schedule)
3. Full reconciliation `GET /Users` as the recovery mechanism after extended downtime
4. Azure Monitor alert on DLQ depth for dead consumer events

**Constitutional deviation (GL-20)**: GL-20 requires transactional outbox for guaranteed delivery. Justified: the spec's clarification explicitly overrides this for the SCIM adapter, accepting the reliability model above as sufficient. The alternative (adding an outbox table) would add significant complexity and was explicitly rejected by the product decision in the spec clarification session.

**Implementation sequence**: Read KjentBruker → detect state change → begin DB transaction → update KjentBruker (not saved yet) → publish to SB with Polly → on success: `SaveChangesAsync` + commit → return 2xx. On SB failure after retries: rollback → return 5xx. This ensures SQL and SB either both succeed or both rollback.

---

## Key Vault Secret Naming (ScimAdapter)

**Decision**: Use Key Vault prefix `AutorisasjonScimAdapter--` for the new adapter's secrets, matching the pattern of the main service (`AutorisasjonTjeneste--`). Connection strings shared with the main service (SQL Server, Service Bus) are referenced by the same Key Vault secret name.

**Rationale**: The SCIM adapter is a separate container, potentially with its own Managed Identity. Using a distinct prefix allows fine-grained Key Vault access policies. The provisioning secret is unique to the adapter and gets key `AutorisasjonScimAdapter--Scim--ProvisioningSecret`.

---

## EF Core Migration

**Decision**: Migration is added to `Autorisasjon.Infrastructure` using `Autorisasjon.Api` as the startup project (as documented in CLAUDE.md). The new migration only adds the `KjentBrukere` table.

**Command** (from repo root):
```bash
dotnet ef migrations add AddKjentBruker --project src/Autorisasjon.Infrastructure --startup-project src/Autorisasjon.Api
```

---

## SCIM Idempotency Logic

**Decision**: Idempotency is enforced by comparing the requested `active` state to the current `KjentBruker.IsActive` value. No separate event deduplication store is needed for inbound SCIM requests (contrast with Service Bus consumers which use Redis for event deduplication).

**Flow**:
- If `KjentBruker` not found: treat as new user, create record, publish `BrukerAktivert` or `BrukerDeaktivert` depending on `active` state.
- If found and `IsActive == requested active`: no-op; return 200/204 without publishing.
- If found and `IsActive != requested active`: state change detected; update record + publish event.

This suppresses duplicate events on Entra retries where the previous attempt succeeded (both SQL and SB committed).

**Edge case — POST /Users for unknown-but-active=false user**: Publish `BrukerDeaktivert` even for unknown users (per spec edge cases). Create KjentBruker record with IsActive=false.
