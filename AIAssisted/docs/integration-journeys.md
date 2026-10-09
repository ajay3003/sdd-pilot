# Integration journeys

Integration journeys describe a business flow that crosses several integration boundaries and record evidence **per step**. They complement
[Active Event Testing](active-event-testing.md); they do not replace it.

```
Shared infrastructure                                         Domain packs (Services/IntegrationJourneys/Packs)
  Active Event engine (Services/ActiveEventTesting)             SkoleNaervaer  Skolenærvær Testing
    CDC/event execution, Event Hub transport, history/export        Utdanningsdata    (delegated to the Active Event provider skolenaervaer.cdc)
  Integration journey engine (Services/IntegrationJourneys)         Rapportmottak     Altinn → MU → MM → Skolenærværsrapport
    IntegrationJourneyService  readiness, safety gates, start       Periodeavslutning Skolenærværsrapport → Service Bus → Bufdata
    IntegrationJourneyRunner   ordered steps, per-step evidence     Architecture rules (responsibility rule pack)
    IntegrationJourneyRunStore integration_journey_runs (JSON)
    ArchitectureRuleEvaluator  project-neutral rules on source
  Integration Quality Review   the only integration configuration (catalog + message-flow test configuration)
  Source Analysis              snapshots: components, dependencies, messaging channels, XML Schemas
  TargetEnvironments:Trusted   backend-owned trust (DEV/QA only, enrolled integrations)
```

## Rules of the engine

- **One model, many packs.** A pack (compiled, DI-registered) declares journeys, steps, scenarios, IQR integration requirements, domain
  prerequisites and architecture responsibility rules. It owns no endpoint, namespace, topic, consumer group, authentication, environment,
  transport, history or export. Duplicate pack/journey/step/scenario/rule ids fail at registration.
- **Packs cannot weaken safety.** Core prerequisites (trusted non-production environment, enabled and enrolled IQR integrations, an executor
  for the first step and evidence coverage for every mandatory step) are always evaluated; pack prerequisites are added and can only block.
  A run additionally needs a supported scenario, explicit confirmation and the existing `ActiveEventExecute` permission. Blocked attempts are
  recorded; nothing is sent, submitted or published by them.
- **Per-step truth.** Each step reports Configured / Generated / Sent / Accepted / Observed / Verified / Not verified / Unavailable /
  Unexpected result / Not assessed. A run is *Completed* only when every mandatory step is Verified; accepted or observed evidence is
  *Partial*. When an executed step does not progress, or a step observes an unexpected result, later steps are not reached — nothing is
  retried. A missing observer leaves only that step Not assessed.
- **Delegated journeys.** A journey executed by Active Event Testing (CDC) is summarized here; its readiness is taken from the Active Event
  provider's readiness for each matched integration. There is no second CDC runner or Event Hub sender.
- **Architecture rules** ("S must / must not use T") are judged on Source Analysis dependency evidence: resolved HTTP/GraphQL client
  targets, client/configuration references, project/package references — never on file names. No component in the snapshot → Not
  assessed; outbound calls the extractor could not attribute → Not assessed; a match is a *potential deviation* to review, not a violation.
- **Cross-review relations.** `GET api/integration-journeys/catalog` lists journey ids and step edges (component → component) for Test
  Coverage, Traceability and Impact Analysis. These are design-level (suggested) relations, never confirmed links.

## Skolenærvær Testing

| Journey | Configuration source | Today |
| --- | --- | --- |
| Utdanningsdata | IQR Event Hub integration for `<db>.dbo.Utdanning` / `<db>.dbo.ManglendeSkoletilbud` | Executed by Active Event Testing; not ready until its prerequisites exist |
| Rapportmottak | IQR HTTP integration (producer Altinn, consumer MU) + IQR message-flow test configuration | Not ready: no Altinn submission contract, no shared machine-identity/Maskinporten token source, no MU/MM/report source, observer or verifier |
| Periodeavslutning | IQR Service Bus integration (`SkolenaervaersperiodeLukket`, producer Skolenærværsrapport, consumer Bufdata) | Not ready: no safe domain trigger to close a synthetic period, event contract not source verified, no Bufdata verification surface |

Responsibilities checked by the rule pack: MM does no Person lookup; SkoleAdapter does no Person lookup; Utdanning owns the Person lookup for
BiRK education data; Skolenærværsrapport owns the Person lookup for submitted reports. XSD validation is attributed to MM. Altinn accepted ≠
report processed; Service Bus accepted ≠ Bufdata processed. BirkNext never publishes `SkolenaervaersperiodeLukket` itself and never invents
an Altinn payload. The documented message-flow pilot calls the intake "Skolenærværsmottaket" and the report service "Skoletjenesten"; that
correspondence to MM / Skolenærværsrapport is a documentation alias, not source verified.
