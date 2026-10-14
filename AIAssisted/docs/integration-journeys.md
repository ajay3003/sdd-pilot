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
| Rapportmottak | IQR HTTP integration (producer Altinn, consumer MU) + IQR message-flow test configuration | Altinn steps run through the TT02 App API once ID-porten TEST, reporter, synthetic child and trusted target are configured; MU, MM and report steps stay Not assessed (no observer or report read contract), so a run reaches Partial at most |
| Periodeavslutning | IQR Service Bus integration (`SkolenaervaersperiodeLukket`, producer Skolenærværsrapport, consumer Bufdata) | Not runnable — operator required: Skoletjenesten locks on its own schedule, its drift endpoint only postpones the lock, and it does not yet write the dataset or publish the event (see [cross-repo verification](skolenaervaer-cross-repo-verification.md)) |

Responsibilities checked by the rule pack: MM does no Person lookup; SkoleAdapter does no Person lookup; Utdanning owns the Person lookup for
BiRK education data; Skolenærværsrapport owns the Person lookup for submitted reports. XSD validation is attributed to MM. Altinn accepted ≠
report processed; Service Bus accepted ≠ Bufdata processed. BirkNext never publishes `SkolenaervaersperiodeLukket` itself and never invents
an Altinn payload. The documented message-flow pilot calls the intake "Skolenærværsmottaket" and the report service "Skoletjenesten"; that
correspondence to MM / Skolenærværsrapport is a documentation alias, not source verified.

### Scenario capability

The journey panel's **Scenario capability** tab lists every scenario of the existing catalog. For a delegated journey such as Utdanningsdata,
the list is the Active Event provider's own scenario catalog. Each scenario shows two things separately:

- **Execution:** whether BirkNext can start it now.
- **Result verification:** how far its result can be verified. A runnable scenario is not a passed one, and Not assessed is never a pass.

Each scenario is in exactly one of these statuses:

| Status | Meaning |
|---|---|
| Runnable | Every applicable mandatory prerequisite is ready (for CDC: Active Event readiness allows the run). Starting still needs confirmation. |
| Conditionally runnable | Only configuration is missing (Not configured / Partial prerequisites, or no applicable integration yet). |
| Operator required | A human operator must act in the target system, for example the period lock; BirkNext never does it. |
| Not assessed | The scenario is not supported yet, with the reason. |
| Blocked | A prerequisite other than configuration is not met (for example an incomplete app contract or no executor). |

Statuses are computed from the readiness the journey service already evaluates; there is no second rule. For each scenario, the pack declares
its system, existing execution method, required configuration, test data and access, result verification, missing evidence, responsible team
and next action (`JourneyScenarioCapabilityProfile`). The tab also lists the pack's **evidence limitations**:

- Altinn TT02 configuration status, derived from the current prerequisites;
- MM → Skoletjenesten contract not runtime verified;
- SkoleAdapter source availability, derived from Source Analysis;
- period lock operator-only;
- Bufdata not verifiable beyond prepared rows;
- GraphQL coverage needs an approved client and role;
- Skoletjenesten and MM automated tests discovered, not executed.

Reading the tab sends nothing and starts nothing.

### Payload & Mapping Flow (Rapportmottak)

A Rapportmottak dry run, the last run and every history row (**View**) show a *Payload & Mapping Flow*:
Altinn → Meldingsutveksleren (MU) → Meldingsutvekslermottaket (MM) → Skolenærværsrapport (Skoletjenesten). Each stage states its own
evidence level and source; nothing is collapsed into one green or red status.

| Level | Meaning |
|---|---|
| Generated | Built by BirkNext, not sent (dry run). |
| Submitted | Sent and accepted by the receiving API (Altinn stored the form data). |
| Confirmed | Read back from the system itself (Altinn shows the instance completed). |
| Expected | What the contract, configuration or documented design says should happen. Nothing was observed. |
| Observed | Seen at runtime by an evidence reader during this run. |
| Not observed | No runtime evidence for the stage. This is not a failure. |
| Not evaluated | The run did not get far enough to say. |
| Failed | The stage answered with a failure. |

- **Altinn** is the only stage BirkNext can generate, submit or confirm.
  - The payload is built from the live contract (data type, task, namespace and operation all come from the deployed metadata).
  - Run history stores a **redacted payload evidence snapshot**. The original unredacted form payload is never persisted. See
    [Redacted payload evidence](#redacted-payload-evidence) below.
  - "Generated and submitted" is shown only because the executor sends exactly the body it built.
- **MU, MM and Skolenærværsrapport** stay *Expected* in a dry run and *Not observed* in a run: BirkNext has no reader for them.
  - Their expectations come from the Integration Quality Review configuration (message type, MU channel, contract, correlation mapping), a
    bound XSD in Source Analysis (source-derived), and the documented journey steps.
  - For a recorded run they are labelled *current expected contract*, not something recorded during that run.
- **Mapping:** Test input → Altinn FormData comes from BirkNext's form binding (Configured), with transformations shown only where the app's
  schema requires them (date → date-time, number → integer). No field mapping exists for the downstream hops, so none is shown.
- **Not invented:** no Skoletjenesten endpoint, downstream payload, timestamp, correlation id or XSD result. XML schema validation is
  *Not evaluated*: BirkNext checks field paths and types against the app's form schema, not the XML against an XSD. Required header fields
  BirkNext does not fill (Meldingshode) are listed as *Not supplied by BirkNext*.
- Previewed payload ≠ downstream observed payload; expected mapping ≠ observed mapping; schema-valid ≠ business-valid.
- Runs recorded before payload capture existed show their recorded Altinn steps only; nothing is reconstructed.

#### Redacted payload evidence

Run history stores a redacted payload evidence snapshot. The original unredacted form payload is never persisted.

- **Redaction happens before persistence.** The executor builds the body, sends exactly that string, and creates the snapshot from it before
  the step result exists. Only the snapshot reaches the run record. The unredacted body lives in memory for the request only; it is not
  stored, logged or hashed.
- **What the snapshot holds:** the format, content type, data type and XML namespace; the business fields (the child as `synthetic-child-1
  (masked)`, plus the period and hours); the redacted technical payload (rendered as text, and its copy action copies only the redacted text);
  the Test input → Altinn FormData mapping; the byte count of the body that was built; and whether it was sent unchanged.
- **Never in it:** a fødselsnummer, D-number or DUF number, the reporter's identifier (only the alias `reporter-a` appears, in the run
  summary), tokens, the client secret, an `Authorization` header, cookies, the party id or the instance GUID. The run keeps only its short
  `inst-xxxxxxxx` reference.
- **RedactionVersion** (currently `v1`) names the fixed redaction rules that produced the snapshot. Version 1 masks the child's field before
  serialization, masks the child's raw value anywhere else it appears, and masks any remaining 11-digit run. It is a constant: it is never
  derived from time and cannot be edited.
- **CapturedAt** is when the snapshot was created, immediately before the form-data request. Dry-run snapshots are not timestamped.
- **Contract fingerprint** identifies the contract snapshot whose form schema built the payload (for example the live discovered public TT02
  metadata). It is stored in groups of eight hex characters (`xxxxxxxx-xxxxxxxx-…`), so a run record never contains an identifier-shaped
  11-digit run, even by chance. It identifies the contract, not the payload.
- **No payload hash.** An unkeyed hash such as SHA-256 of the original payload is deliberately not stored. The snapshot shows every value
  except the child's identifier, and that identifier has only about 10⁷ valid values, so the hash could be brute-forced back to it. Even a
  hash would only show that a payload corresponds to what BirkNext built. It would never show that MU, MM or Skolenærværsrapport received it.
- **Older runs** may have a snapshot with no redaction version (shown as *Legacy / unknown*), no capture time (*Not recorded*) and no contract
  fingerprint, or no snapshot at all (*No payload recorded for this run*). Nothing is backfilled from the current configuration.
- **Current expected downstream contract ≠ historical runtime evidence.** For a recorded run, the MU, MM and Skolenærværsrapport
  expectations are today's configuration and contracts, labelled *Current expected contract and configuration — not recorded during this
  run*. Storing payload evidence does not move any downstream stage beyond *Not observed*.
