# Active Event Testing

Active Event Testing sends bounded, synthetic events to a real non-production destination and records what can be observed afterwards.
There is one engine for every scenario provider; providers add scenarios, never another runner, sender, safety model, history or export.

```
Shared core (Services/ActiveEventTesting)                     Providers (Services/ActiveEventTesting/Providers)
  ActiveEventLifecycleService  ── the only production entry      M2lbPerson     Person CDC scenarios (dbo.Person)
  TrustedExecutionEnvironmentRegistry (backend-owned trust)      SkoleNaervaer  Skolenærvær Testing (dbo.Utdanning,
  ActiveEventPolicy (DEV/QA, target URL, names, allowlist)                      dbo.ManglendeSkoletilbud)
  ActiveEventScenarioRegistry / ActiveEventTransportRegistry
  ActiveEventExecutionRunner   ── the only runner             Observation (Services/ActiveEventTesting/Observation)
  EventHubActiveEventTransportProvider → AzureEventHubTestSender  EventHubCheckpointContinuityProvider (baseline + progression)
  SyntheticIdentityReservation (active_event_synthetic_identities) TelemetryConsumerActivityProvider (aggregate only)
  ActiveEventRunStore (active_event_runs, body-free JSON)          GatewayReadDownstreamVerifier (base; no domain registered)
  DebeziumEventBuilder / SourceContractEvaluator
```

## Lifecycle

trusted environment → IQR integration → approved destination → core checks → provider readiness → durable intent → generic runner
(generate → send once per event, never resent → continuity → consumer activity → downstream verification) → generic history → generic export.

- **Trust is backend-owned.** `TargetEnvironments:Trusted` records (id, display name, type, `ExecutionAllowed`, target URL, enrolled
  `IntegrationIds`, monitoring references) are the only authority for an environment's type and URL. The browser profile only selects an
  id. Missing, duplicated, incomplete or not-allowed records fail closed. The earlier `ActiveCdcTests:TrustedTargets` section is retired.
- **Destination** comes from the IQR integration (namespace, hub, consumer group) and must pass `ActiveEventPolicy`: Development or QA only,
  `*.servicebus.windows.net`, no production marker in names or in the trusted target URL, the environment marker present, and exact
  enrollment in `ActiveEventTesting:AllowedDestinations` (final allowlist). It is re-checked immediately before every send.
- **Providers cannot weaken safety.** Their readiness checks are appended after the core checks and can only block. A provider owns its
  source requirements, its synthetic identity scopes and fixture generation; the runner validates every generated event (ownership,
  declared operation, size, SHA-256; only an explicit tombstone may be bodiless and it must carry a key).
- **Status is conservative.** Transport acceptance is never Completed. Completed needs every event accepted, observed continuity when the
  scenario requires it, and a verified downstream result for every event when it requires one; otherwise *limited evidence*.
  Checkpoint progression never sets downstream verified.
- **Authorization.** Execution, cancellation, run detail and history need `ActiveEventExecute` (Entra `scp` or `roles`; inbound claim
  mapping is off). Trust, providers, scenarios and readiness are readable configuration metadata. Integration catalog writes need
  `IntegrationConfiguration.Write`; in Development only, `Authorization:LocalConfigurationWrites=true` also allows loopback writes.
- **Frontend.** With `Authentication:Entra` (tenant, client, API scope) the browser uses MSAL and attaches tokens to protected calls;
  without it the app works and execution shows *Authentication not configured*.

## Skolenærvær Testing

Scenarios: valid create, exact replay (replay sent ≠ idempotency verified), reviewed invalid record (child reference missing) → valid
control; update, delete, tombstone, snapshot read, natural-key duplicate and unknown-child/late-linkage are declared **Not assessed**.
Fixtures carry only key, child reference, period and type codes (external SkoleAdapter specification); never names, remarks, contacts or
national ids. Readiness stays Not ready until: a SkoleAdapter source snapshot confirms the CDC fields, analyzed source declares CDC
capture of the table, an Event Hub integration exists for `<db>.dbo.Utdanning` / `<db>.dbo.ManglendeSkoletilbud`, the fixture is
reviewed against that archive, and synthetic ranges (`skolenaervaer.<Table>.<Key>`, `skolenaervaer.BarnFK`) and reference codes are
configured. No Utdanning verifier is registered: there is no read contract and no machine-identity token source yet.
