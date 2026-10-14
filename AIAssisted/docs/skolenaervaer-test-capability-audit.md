# Skolenærvær system-test capability audit

Audit date: 2026-10-12 (first audit), corrected 2026-10-12 (second audit)\
Repository: `BirkNext`\
Branch/HEAD: `008-traceability-first` / `aa261796e0dc32be970c5b6307dd34a479ea2aa4`

> **Superseded in part (2026-10-14).** This audit describes `aa261796`. Since then the Altinn executor (`SkoleNaervaerAltinnExecutor`) and
> instance observer are registered, so Rapportmottak's Altinn steps can run once configured. Period closure is now known from Skoletjenesten's
> source to be operator-only. For the current state, and for what was verified from the Skole and MM sources, see
> [skolenaervaer-cross-repo-verification.md](skolenaervaer-cross-repo-verification.md).

## 0. Correction from first audit

The first audit received only the 39 scenario IDs, without titles, areas, priorities or expected results. Its 0/39 result and its "Definition unavailable" matrix were intentionally conservative, but they were not useful for the actual system-test plan.

This second audit maps the 39 supplied scenario definitions onto the capabilities already audited. Each claim was re-checked against the code at `aa261796`.

The valid technical findings of the first audit (sections 3–8, 10, 12 and 13) are kept, with additions marked *(re-checked)* or *(new)*. Section 9 (matrix), section 11 (roadmap), section 15 (limitations) and section 16 (counts) are replaced.

Two findings are new in the second audit:

1. **SPOR-02:** Source Analysis' observability logging analysis already gives source-level evidence for logs without sensitive data.
2. **The two shared Skolenærvær report journeys can never run today.** Their prerequisites are hard-coded not ready, and no journey step executor or observer is registered anywhere.

"Dekket i eksisterende kode?" is taken verbatim from the supplied definitions. It describes the target system's own automated tests (for example Meldingsutveksler), not BirkNext tests.

## 1. Executive summary

BirkNext has a generic Active Event execution engine, a Skolenærvær CDC provider, generic read-only broker and telemetry evidence readers, an Integration Journey framework with a durable run store, a Skolenærvær journey pack and source-evidence architecture rules. These are useful foundations. A journey definition, descriptor or observer does not mean a target-system run is possible or that a business outcome was verified.

No CDCPlay/CdcReplay implementation exists in BirkNext. Active Event is BirkNext's own engine and does not invoke CDCPlay.

The Skolenærvær provider can generate three kinds of event for `dbo.Utdanning` and `dbo.ManglendeSkoletilbud`:

- a create;
- an exact same-key replay;
- one bounded invalid-then-valid case.

These run only behind backend policy, environment trust, IQR configuration, source contract, CDC capture, a reviewed fixture and a synthetic-identity policy. No deployment has been shown to satisfy those gates. Update, delete, tombstone, snapshot, natural-key duplicate and late linkage are descriptors only: generation refuses them. No downstream verifier is registered, so no Utdanning result can be verified.

Rapportmottak and Periodeavslutning are modelled step by step. They are never runnable: Altinn authentication, the submission contract and the period-close trigger are hard-coded not ready. There is no manual "action performed / result confirmed" capture in the journey model.

**Mapped against the 39 real scenarios, current BirkNext support is as follows:**

| Status | Count | Scenarios |
|---|---:|---|
| Kjørbar | 0 | — |
| Delvis kjørbar | 2 | CDC-01, CDC-02 |
| Evidens/verifikasjon | 1 | SPOR-02 |
| Modellert, ikke kjørbar | 16 | — |
| Ikke implementert | 20 | — |

Meaningful support today therefore covers **3 of 39** scenarios. The recommended "Nå" phase raises that to 12, "Neste" to 31 and "Senere" to 39. At least 35 scenarios still need an external or manual stimulus or confirmation even after implementation. The biggest single lever is a manual guided run on the existing Integration Journey: record the action, record manual confirmation, and use the existing history and export. A second Skolenærvær test framework is not needed.

No production code was changed. This audit file is the only file changed.

## 2. Repository safety audit

Second-audit state:

- **Branch and HEAD:** branch `008-traceability-first`, HEAD `aa261796`. `5ce9ccbd` and `aa261796` are both in history.
- **Diffs:** staged and unstaged tracked diffs are empty.
- **Untracked files:** `AIAssisted/docs/codebase-guide-and-audit.md`, `NUL`, and this audit file (untracked; written by the first audit). The first two were not touched.
- **Changes:** nothing was committed or pushed, no production code changed, and no external system was contacted.

| Commit | Subject |
|---|---|
| aa261796 | Add Skolenærvær testing extension |
| 5ce9ccbd | Support multi-document project imports and module-scoped artifact selection |
| 3b9a0974 | Merge skolenaervaer-journeys: generic Active Event core, Skolenærvær provider and integration journeys |
| d3f4a38d | Merge integrate-008: add Test Coverage & Overlap review onto current 008 |
| 7802b90f | Add Skolenærvær integration journeys for Altinn and Bufdata |
| 66386e5b | Generalize Active Event testing for Skolenærvær scenarios |

## 3. Current architecture and code map

| File / symbols | Responsibility | Kind and scope |
|---|---|---|
| `shared/ActiveEventTestingContracts.cs` | Generic run, event, evidence-stage, readiness and safe-history contracts | Production contract; generic |
| `backend/BirkNext.Api/Services/ActiveEventTesting/ActiveEventProviderContracts.cs` | Provider seam: providers prepare, check and generate; the shared engine owns destination and send | Production; generic |
| `backend/.../ActiveEventTesting/ActiveEventLifecycleService.cs`, `ActiveEventExecutionRunner.cs` | Readiness, start, execution lifecycle, policy and evidence composition | Production; generic |
| `backend/.../ActiveEventTesting/EventHubTestSender.cs` | Single shared Event Hub send path (blocks tombstones) | Production; generic |
| `backend/.../ActiveEventTesting/Observation/EventHubCheckpointContinuityProvider.cs`, `TelemetryConsumerActivityProvider.cs` | Read-only before/after checkpoint continuity; aggregate consumer telemetry (both registered in `Program.cs`) | Production; generic, aggregate or weak attribution |
| `backend/.../ActiveEventTesting/Observation/GatewayReadDownstreamVerifier.cs` | Abstract read-only downstream verifier base. **No implementation is registered** (`Program.cs`: "No IActiveEventDownstreamVerifier is registered") | Production seam; generic |
| `backend/.../Providers/SkoleNaervaer/SkoleNaervaerScenarioProvider.cs`, `SkoleNaervaerFixtures.cs` | Skolenærvær descriptors, synthetic fixtures, readiness | Production provider; domain-specific |
| `shared/ActiveCdcTestContracts.cs`, `LegacyActiveCdcHistory.cs`, `ActiveCdcRunStore.cs` | Read-only projection and migration of older run history | Legacy, Person-shaped |
| `backend/.../IntegrationJourneys/IntegrationJourneyRunner.cs`, `IntegrationJourneyService.cs`, `IntegrationJourneyRunStore.cs` | Runs registered step executors and observers, aggregates per-step evidence, stores runs durably in the database (one payload-free table for all packs) | Production; generic. **No `IJourneyStepExecutor` or `IJourneyStepObserver` implementation is registered** *(new)* |
| `backend/.../IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs` | Three journeys, scenario descriptors, prerequisites and four architecture rules | Production definitions; domain-specific |
| `backend/.../Integrations/AzureIntegrationEvidence.cs` | Event Hub metadata, Blob checkpoint and Log Analytics adapters | Production; generic, read-only |
| `backend/.../Integrations/ServiceBus/ServiceBusEvidenceService.cs`, `ArmServiceBusMetadataSource.cs`, `AzureMonitorServiceBusMetricsSource.cs` | Service Bus topology, entity counts (active, dead-letter, scheduled) and Monitor metrics (IncomingMessages, DeadletteredMessages and others) | Production; generic, read-only, aggregate. **No message peek, receive or send** *(re-checked)* |
| `backend/.../Integrations/ApplicationMessaging/ApplicationMessagingTelemetry.cs`, `WolverineSourceAnalyzer.cs` | Aggregate messaging telemetry; source facts on handlers, retry policy, outbox and error handling | Production; generic |
| `backend/.../SourceAnalysis/Observability/LoggingAnalysis.cs` | Source-level logging analysis: sensitive-named values and payloads written to logs, redaction support, retry and dead-letter observability | Production; generic, source-only *(new for SPOR-02)* |
| `backend/.../SecurityClassification/*` | M2LB "Gradert tilgang" source review (Kode 6/7 model, terminology, CDC facts) | Production extension; source-only; not report-flow or Bufdata-specific *(new)* |
| `backend/.../IntegrationJourneys/ArchitectureRuleEvaluator.cs` | Evaluates pack rules against Source Analysis dependency evidence | Generic evaluator; domain rules come from the pack |
| `frontend/.../ActiveEventTestingPanel.razor`, `IntegrationJourneyPanel.razor`, `Pages/SkoleNaervaerTesting.razor` | Shared execution, readiness, history and export; the Skolenærvær page is a thin shell | Production UI |
| `shared/MessageFlowReviewContracts.cs` | Documented flow and generated manual plan. Readiness always includes "No active external test provider is implemented" | Production review model; generic |

*(re-checked)* A search of all `.cs` and `.razor` files under `backend`, `shared` and `frontend` found no CDCPlay, CdcReplay, `cdc-replay` or `cdc_replay` reference. The labels the first audit found are in non-code test-evidence fixtures only. Active Event does not use them.

## 4. Active Event / CDCPlay / CDC Replay findings

### Execution and safety (re-checked)

`ActiveEventTesting` is a real shared execution path:

- Providers cannot choose a destination or send.
- The lifecycle service applies readiness and policy and invokes the single sender (`/api/active-events`). Execution requires the `ActiveEventExecute` permission.
- Policy is opt-in. Only backend-registered Development/QA targets and allowlisted Event Hubs are allowed. Production and production-like targets are rejected.
- Azure identity is used; connection strings and SAS are not accepted.
- There is no arbitrary-JSON endpoint.
- History stores body hash and size, never the body.

No deployment configuration was queried, so real target readiness is unknown.

### Skolenærvær provider operations (re-checked against `SkoleNaervaerScenarioProvider.cs`)

| Descriptor (per table) | `NotAssessedBecause` | Generator | Events sent |
|---|---|---|---|
| Valid create | — | Yes | 1 × op `c` |
| Exact replay | — | Yes | 2 events. Second has the identical body bytes, same event key (source key) and same values. Different `EventId` (`{run}-A` vs `{run}-A2`), `ReplaysEventId` set, and transport property `BirkNextMessage` = `A`/`A2` |
| Invalid record → valid control | — | Yes | 2 events. First omits the child reference (reviewed invalid case); second is a valid control with a new key |
| Update (before/after) | Outcome not verifiable without a read contract | Refused (`throw`) | 0. Fixture helper `SkoleNaervaerFixtures.Update` exists but is unused |
| Delete | No read contract; no cleanup/retention policy | Refused | 0. Helper `Delete` exists |
| Tombstone after delete | AMQP representation of a Kafka tombstone not verified | Refused; the sender also returns Unavailable for tombstones | 0 |
| Snapshot read (op `r`) | One snapshot event is not a full reload | Refused | 0 |
| Natural-key duplicate | Natural-key semantics undefined; identity is the source key | Refused | 0 |
| Unknown child → late linkage | Needs a verifier, a child-registration source, correlation and cleanup | Refused | 0 |

Identity facts:

- Keys and child references (`BarnFK`/`BarnFk`) come from reserved synthetic ranges (`skolenaervaer.BarnFK` scope).
- The provider's own limitation text says: *"The synthetic child reference does not resolve to a registered child unless one was created; Skoletjenesten holds such a record pending linkage."* **Every create is therefore an unknown-child event unless an operator has registered a synthetic child in that range.** This matters for CDC-01 versus CDC-02 (section 9.1).
- `expectedResourceIdentity` is recorded as "Not derivable from available source", so BirkNext cannot predict the Utdanning record identity.

### Evidence semantics (re-checked)

The stages are `Generated`, `SendAttempted`, `TransportAccepted`, `ConsumerActivityObserved`, `ConsumerContinuityObserved` and `DownstreamVerified`, each recorded separately. There is no separate "persisted" stage: `DownstreamVerified` is the only stage after consumer evidence, and it is never reached today.

Run statuses include `CompletedWithLimitedEvidence`; transport acceptance is never a Skolenærvær pass.

Continuity is measured from Event Hub partition and checkpoint positions before and after the run. It is aggregate, and checkpoint movement is not proof that a message was processed. Telemetry is aggregate and has no guaranteed per-event correlation.

### CDC operation matrix

| Operation | CDCPlay/CdcReplay reference | Current Active Event | Skolenærvær provider | Safe UI exposure now | Backend work | Environment support | Runtime evidence now | Notes |
|---|---|---|---|---|---|---|---|---|
| Insert/create | No code found | Shared send path | Generator exists for both tables | Yes, gated | None in the engine | Trusted DEV/QA target, IQR, source and synthetic configuration | Generated, sent and transport stages; aggregate observation | Not proof of persistence |
| Update | No code found | Generic operation enum only | NotAssessed; generator refuses | No | Generator path plus verifier | Target fixture | None | Fixture helper alone is not enough |
| Delete | No code found | Generic enum only | NotAssessed; refused | No | Generator path, verifier, cleanup policy | Target delete semantics | None | Nothing is ever deleted |
| Tombstone | No code found | Sender blocks tombstones | NotAssessed | No | Transport-specific contract | Kafka-compatible path | None | Not runnable |
| Replay, same source key | No code found | Supported | Exact replay | Yes, bounded | Verifier for any idempotency claim | Same gates | Two send-stage records | Not proof of deduplication |
| Duplicate natural identity, different source key | No code found | Enum exists | NotAssessed | No | Domain identity, provider, verifier | Domain definition | None | Undefined |
| Malformed JSON / unknown field / unsupported type / null field | No code found | No raw payload entry | No | No | Explicit reviewed fixtures | Consumer sandbox | None | Never by implication |
| Missing required field | No code found | Bounded | Only the child reference omitted | Named case only | More reviewed cases | Safe target | Send stages only | Not "any missing field" |
| Consumer or downstream unavailable | No code found | Can be shown absent | No fault injection | Unavailable status only | None (external fault) | Isolated target | Aggregate absence | No stop/restart features |
| Checkpoint observation | No code found | Read-only continuity | Shared | Yes, with caveat | Better attribution | Checkpoint read permission | Aggregate before/after | Movement is not processing |
| Downstream persistence verification | No code found | Abstract seam | None registered | Not as verified | Read-only Utdanning verifier | Read contract and identity | None | No PII in evidence |
| Full reload / reconciliation | No code found | None | Snapshot descriptor only | No | New read-only monitoring and reconciliation | Approved reload protocol | None | Snapshot event is not a reload |

## 5. Integration Journey findings (re-checked)

`IntegrationJourneyRunner` executes only steps that have a registered `IJourneyStepExecutor` or `IJourneyStepObserver`. *(new)* `Program.cs` passes `GetServices<IJourneyStepExecutor>()` and `GetServices<IJourneyStepObserver>()`, and no implementation exists in the codebase. Any journey run therefore yields unavailable or unverified steps.

`Completed` requires every mandatory step to be `Verified`. Step states are `Configured`, `Generated`, `Sent`, `Accepted`, `Observed`, `Verified`, `NotVerified`, `Unavailable`, `UnexpectedResult` and `NotAssessed`. There is no manual-action or manual-confirmation state.

| Journey | Modeled steps | Scenario descriptors | Runnable? | Classification |
|---|---|---|---|---|
| Utdanningsdata (6) | CDC capture → Event Hub → SkoleAdapter consume → Utdanning ingest → Person linkage in Utdanning → stored state | One summary descriptor that delegates to Active Event | Delegated: readiness comes from Active Event | Execution is in Active Event, limited to transport and aggregate evidence |
| Rapportmottak (11) | Altinn submit → Altinn accept → MU receive → MU queue → MM retrieve → MM decrypt → MM XSD → MM map → report receive → Person lookup → report state | `valid-report` (Supported). NotAssessed: `invalid-xsd`, `person-not-found`, `period-locked`, `mapping-validation`, `duplicate-report`, `retrieval-decrypt-failure` | **Never**: `altinn-auth` is always NotConfigured, `submission-contract` is always NotReady, and `report-verifier` is NotAvailable | Modelled, not runnable |
| Periodeavslutning (8) | period close → dataset prepare → publish `SkolenaervaersperiodeLukket` → broker → Bufdata activity → Bufdata processed → dataset verify → dataset free of fnr/DUF | `close-valid-period` (Supported). NotAssessed: `event-emitted`, `event-metadata`, `bufdata-receives`, `pii-absent`, `coverage-metadata` | **Never**: `domain-trigger` is always NotReady; `bufdata-verifier` and `pii-check` are NotAvailable | Modelled, not runnable |

For Rapportmottak, the Altinn endpoint, approved test reporter and synthetic identity set are read from IQR's message-flow configuration. The XSD prerequisite becomes Partial when Source Analysis binds an XML Schema; an existing XSD does not mean a payload was validated. For Periodeavslutning, the `event-contract` prerequisite becomes Ready when Source Analysis finds a Service Bus channel named `SkolenaervaersperiodeLukket`.

Journey runs are stored durably in the database in one payload-free table; Active Event history is separate. The two stores are not linked.

Architecture rules are evaluated from Source Analysis dependency evidence:

- MM must not depend on Person.
- SkoleAdapter must not depend on Person.
- Utdanning must depend on Person.
- Skolenærværsrapport must depend on Person.

Without a source snapshot the rules are NotAssessed. These are source findings, not runtime verification.

## 6. Runtime evidence and reusable connectors

| Existing service | Access and scope | Reuse / limits |
|---|---|---|
| Event Hub metadata | Read-only partition properties | No body or processing evidence |
| Blob checkpoint / `EventHubCheckpointContinuityProvider` | Read-only checkpoint before/after | Aggregate; movement is not processing |
| `TelemetryConsumerActivityProvider` / `ApplicationMessagingTelemetry` | Aggregate App Insights/Log Analytics queries by role | No guaranteed per-event correlation |
| `ServiceBusEvidenceService` (ARM and Monitor) | Topology; active, dead-letter, scheduled and transfer counts; Incoming/Outgoing/DeadLettered metrics | Aggregate per entity; no message content, no peek, no send |
| `GatewayReadDownstreamVerifier` | Abstract authenticated GET verifier | No Utdanning or Skolenærværsrapport implementation; needs a read contract and a machine identity |
| Source Analysis observability (`LoggingAnalysis`) | Source-level SensitiveData and PayloadLogging findings, redaction support, retry/DLQ log observability | Applies to any uploaded component source; it is not inspection of runtime logs |
| Wolverine / application-messaging source facts | Handler, retry policy, outbox and error-handling facts | The configured retry policy is not the observed number of attempts |
| History and export | Active Event and Integration Journey stores and exporters, both durable | Reusable; one journey store for all packs |
| Bufdata / Parquet / dataset reader | **None** | New read-only integration required |

## 7. Bufdata / period-end capability (re-checked)

| Capability | Classification | Evidence |
|---|---|---|
| Expected / reporting-population calculation | Missing | No code |
| Received report count | Missing | No report-service read |
| Rejected report count | Missing | No report-service read |
| Missing report count | Missing | No report-service read |
| Period-close trigger | Missing; should remain external | `domain-trigger` hard-coded NotReady; BirkNext never publishes the event |
| Period status observation | Missing | Needs a report-service read contract |
| `SkolenaervaersperiodeLukket` observation | Partially reusable foundation | Journey steps, Source Analysis channel detection and aggregate Service Bus IncomingMessages metrics; nothing at message level |
| Message-level Service Bus evidence | Missing | No peek or receive code |
| Bufdata access | Missing | No connector |
| Parquet parsing | Missing | No "parquet" reference in code |
| Row comparison | Missing | — |
| BirkID verification | Missing | — |
| fnr/DUF absence check | Missing (step modelled only) | `dataset-pii` step and `pii-check` prerequisite NotAvailable |
| Kode 6/7 exclusion check | Missing at runtime; source-level foundation only | Security Classification gives source facts, not dataset content |
| Expected-vs-actual comparison | Missing | — |

Recommendation: prefer read-only metadata and dataset verification with redacted, aggregate evidence over any mutation. Keep triggering the period close in the domain owner's workflow.

## 8. Manual tester workflow capability (re-checked against the preferred eight-step workflow)

| Step | Exists today? | Where / what is missing |
|---|---|---|
| 1. Select scenario in BirkNext | Partly | Journey scenarios are selectable in the shared panel. Only pack descriptors exist; most of the 39 scenarios are not descriptors; Rapportmottak and Periodeavslutning cannot start a run |
| 2. See prerequisites / test data | Yes, partly | Journey prerequisites, synthetic-data checks, IQR message-flow config and generated manual plan (`MessageFlowReviewContracts`). No per-scenario synthetic-data checklist |
| 3. Perform external action in Altinn | External by design | — |
| 4. Mark action as completed | **Missing** | No manual-action state or endpoint |
| 5. BirkNext gathers safe technical evidence | **Missing** for the report chain | No registered step observers. Active Event covers CDC only |
| 6. Tester sees each journey step | Yes | Per-step status view in `IntegrationJourneyPanel` |
| 7. Tester confirms the result manually | **Missing** | No manual-confirmation state |
| 8. Run, history and export store the evidence | Infrastructure yes; usable no | Durable journey store and export exist, but a Rapportmottak or Periodeavslutning run cannot be started |

Today a non-technical tester cannot complete any of the 39 scenarios inside BirkNext without Azure Portal, database access or raw logs. All 23 Manuell/Begge scenarios are marked as a usability gap in column 17.

## 9. Scenario-by-scenario mapping

### 9.1 CDC / Utdanning (A–H per scenario)

| | CDC-01 Nytt utdanningsforhold | CDC-02 Utdanning for ukjent barn | CDC-03 Utdanning kobles senere | CDC-04 Endre utdanning | CDC-05 Slette/avslutte utdanning |
|---|---|---|---|---|---|
| A. Generate today? | Yes: create (`op c`), when gates pass | Yes in effect: every create uses a reserved synthetic `BarnFK` that is unregistered unless an operator created that child. There is no scenario labelled "unknown child" | No: late-linkage descriptor is NotAssessed and refused | No: update descriptor NotAssessed and refused (helper unused) | No. CDC DELETE is refused. Business "end education" (an update setting `Til`) is refused. Tombstone is refused and blocked by the sender |
| B. Send when gates satisfied? | Yes (single shared Event Hub sender) | Yes (same create path) | First half only (an unknown-child create); there is no linkage step | No | No |
| C. Observe Event Hub acceptance? | Yes: `TransportAccepted` from the send result | Yes | First event only | — | — |
| D. Observe consumer activity? | Aggregate only: checkpoint continuity and telemetry, not event-attributed | Aggregate only | Aggregate only | — | — |
| E. Verify Utdanning persistence? | No: no verifier registered | No ("kept unlinked" cannot be read) | No | No | No |
| F. Verify Person linkage? | No; BirkNext cannot register a synthetic child either | No (cannot show the record is *not* linked) | No | — | — |
| G. Distinguishes generated / sent / accepted / consumer activity / persisted / business result? | Generated, SendAttempted, TransportAccepted, ConsumerActivityObserved and ConsumerContinuityObserved: yes. Persisted and business result: no; `DownstreamVerified` is never reached | Same | Same, for the first event | — | — |
| H. Change that makes it genuinely useful | Read-only Utdanning verifier (`GatewayReadDownstreamVerifier` subclass, synthetic identity only), a "known synthetic child" prerequisite, and per-run event correlation | Explicit "unknown child" descriptor (same generator, guaranteed-unregistered child range), plus a verifier that asserts "stored, unlinked, not propagated" | Two-phase journey scenario: Active Event unknown-child create → manual step "synthetic child registered in BiRK/Person" → verifier polls for linkage | Enable the update generator path (before/after, same key; the fixture helper exists) plus the verifier | Separate three cases: (1) business end = update with `Til` set; (2) CDC DELETE = op `d`; (3) tombstone stays unavailable on AMQP until verified. Generators for 1 and 2 plus a verifier, then a cleanup/retention policy |
| Conclusion | **Delvis kjørbar** (conditional). BiRK→Debezium is bypassed by injecting a Debezium-shaped event; no downstream verification; never "verified" | **Delvis kjørbar** (conditional; input only) | **Modellert, ikke kjørbar** (descriptor only, NotAssessed) | **Modellert, ikke kjørbar** | **Modellert, ikke kjørbar** |

### 9.2 Rapportmottak (E2E-01–05, E2E-07–09, MU-01–05, SPOR-01–02)

Default solution: **no Altinn form submitter.** The tester submits in Altinn's test environment and BirkNext records it.

| ID | Only modelled? | Guides a manual tester? | Records manual completion? | Automatically observes a step? | Reusable generic evidence | Needs |
|---|---|---|---|---|---|---|
| E2E-01 | Yes (`valid-report` plus Periodeavslutning) | Steps, prerequisites and manual plan | No | No | Service Bus aggregate (period step), architecture rules | Manual evidence capture, MU/MM observer, report-service read verifier, Person read verifier, Service Bus evidence, Bufdata reader |
| E2E-02 | Yes (`person-not-found`, NotAssessed) | Description only | No | No | Rule: report service owns Person lookup (source) | Manual capture, report-service read (on-hold state) |
| E2E-03 | No scenario | Chain only | No | No | — | Manual capture (two-phase), Person read verifier, report-service read |
| E2E-04 | No scenario | Chain only | No | No | — | Manual capture, report-service read plus period status |
| E2E-05 | No scenario | Chain only | No | No | Security Classification (source-level Kode 6/7) | Manual capture, Bufdata reader (exclusion), report-service read |
| E2E-07 | No scenario | Chain only | No | No | — | Manual capture, report-service read (version history) |
| E2E-08 | No scenario | Chain only | No | No | — | Manual capture, report-service read (overlap flag) |
| E2E-09 | No scenario | Chain only | No | No | — | Manual capture, report-service read |
| MU-01 | Yes (`valid-report`) | Steps and manual plan | No | No | Bound XSD (Partial), source rules | Manual capture, MU and MM observers (telemetry or Blob metadata), report-service read |
| MU-02 | Yes (`invalid-xsd`, NotAssessed) | Description | No | No | XSD from Source Analysis | Manual capture, MM observer (rejection status) |
| MU-03 | Yes (`retrieval-decrypt-failure` covers retrieval) | Description | No | No | Blob metadata reader (generic) | Manual capture, Blob evidence (metadata only), MM observer |
| MU-04 | Yes (`retrieval-decrypt-failure`) | Description | No | No | — | Manual capture, MM observer (no key material) |
| MU-05 | Yes (`mapping-validation`) | Description | No | No | — | Manual capture, report-service read verifier |
| SPOR-01 | Yes (journey steps; correlation stated as unknown) | Steps | No | No | Active Event correlation (CDC only) | Correlation key from Altinn/MU contract, MU/MM observers, report read, Service Bus evidence, Bufdata reader |
| SPOR-02 | Not a journey step | — | No | **Source-level yes**: `LoggingAnalysis` SensitiveData/PayloadLogging findings for each uploaded component | Source Analysis observability | Upload MU, MM, report and SkoleAdapter source; runtime log sampling remains external |

### 9.3 Reporting basis / period close / Bufdata (COV-01–05, LOCK-01, DATA-01–04)

| ID | Today | Missing | Proposed (read-only first) | External |
|---|---|---|---|---|
| COV-01–04 | Nothing | Reporting-population calculation; received, rejected and missing counts; "manglende skoletilbud" exclusion | Report-service read contract for reporting-basis status per synthetic child (Neste); compare with the expected classification defined in the scenario | Creating reports and children |
| COV-05 | Nothing | Upper-secondary classification rule | Same verifier plus a rule from the domain owner (Senere) | Same |
| LOCK-01 | Modelled (`close-valid-period`); trigger hard-coded NotReady | Period status, lock and counts evidence | Manual "period closed" step plus a read-only period-status observer (Neste) | The period-close trigger |
| DATA-01 | Modelled (`dataset-verify`) | Bufdata access, Parquet, row comparison, BirkID | Read-only dataset-metadata verifier, then row comparison with redacted output (Senere) | Bufdata access approval |
| DATA-02 | Modelled (`dataset-pii`) | Dataset read, fnr/DUF absence assertion | Schema- and column-level absence check; report only counts and column names (Senere) | Access approval |
| DATA-03 | Not modelled | Dataset read; synthetic graded child | Absence check for the synthetic graded child's BirkID (Senere) | Synthetic Kode 6/7 setup (never real) |
| DATA-04 | Modelled (publish and broker steps); Source Analysis channel check; aggregate Service Bus metrics | Message-level period and absence-of-persondata check | Neste: correlate the Service Bus IncomingMessages delta with the period-close window (aggregate). Content check only through a read-only peek on an approved test subscription, after privacy review | Triggering the close |

### 9.4 Robustness (ROB-01–06)

| ID | Can BirkNext create the failure? | Should it? | Can it observe the failure? | Can it observe recovery? | Existing evidence that helps | Operator-controlled |
|---|---|---|---|---|---|---|
| ROB-01 Skolenærværsrapport utilgjengelig | No | No | Only if the MM→report hop is a Service Bus entity in IQR (ActiveMessages build-up); not established | Same, aggregate | Service Bus counts and metrics, telemetry | Making the service unavailable |
| ROB-02 Person utilgjengelig | No (Active Event can supply the CDC *input* only) | No | No | No | Telemetry (aggregate) | Making Person unavailable |
| ROB-03 MM restart | No | No | No | No | — | Restart |
| ROB-04 Retry after transient fault | No | No | No (attempts per message are not observable) | No | Wolverine retry-policy source facts (configured, not observed); Service Bus metrics | Inducing the fault |
| ROB-05 Permanent fault → error queue | No | No | Only if the error queue is a Service Bus entity in IQR (dead-letter count); not established | — | Service Bus dead-letter counts and metrics | Inducing the fault |
| ROB-06 Reprocess from error queue | No | No (no DLQ re-drive button) | Dead-letter count decrease (aggregate, conditional) | "Completed once" is not observable | Same | The re-drive itself |

Recommendation: the fault is induced externally; BirkNext observes read-only and the tester confirms manually. No stop/restart/outage/re-drive buttons.

### 9.5 Duplicate (E2E-06)

1. The Active Event exact replay sends the same body bytes, the same event key (source key) and the same natural identity (identical values). It uses a **different** event id (`{run}-A2`), sets `ReplaysEventId`, and carries a different transport property (`BirkNextMessage`). Because the body is identical, the Debezium source timestamp is identical too.
2. It does **not** correspond to E2E-06. E2E-06 is a duplicate *technical message in the report flow* (Altinn → MU duplicate control). Active Event replay is a CDC message to SkoleAdapter/Utdanning. A same-source-key replay is also not a natural-key duplicate.
3. BirkNext cannot verify "no additional report created".
4. That needs a report-service read verifier that counts reports for the synthetic reporter, period and child before and after (aggregate count only), plus a manual step "same message re-delivered" recorded by the technical tester. Rapportmottak `duplicate-report` is the existing descriptor (NotAssessed).

### 9.6 Full reload (RELOAD-01–02)

The snapshot event is not a full reload: the provider says so explicitly and refuses to generate it.

| | A. Trigger the reload | B. Observe the reload | C. Reconcile the result |
|---|---|---|---|
| Today | No | No (only aggregate Event Hub, checkpoint and telemetry movement, not attributable to a reload) | No reconciliation, no source/destination comparison |
| Recommendation | Operator-owned (Debezium/BiRK ops) | BirkNext could add read-only monitoring (Senere): Event Hub incoming counts and checkpoint progress over the reload window, in one journey run | BirkNext could add read-only reconciliation (Senere): BiRK source counts per table vs Utdanning read-contract counts, aggregate only, no row PII |

### 9.7 Full 39-scenario matrix

Column 6 comes from the supplied definitions (target-system tests).

**How to read the columns:**
- **"BirkNext-status i dag"** uses the five classifications defined in the task.
- **"Implementeringsprioritet"** is the phase in which the scenario's main gap is addressed.
- **"Manuell tester trenger Azure?"** reads "Ja (gap)" when a manual tester involved in the scenario cannot complete it today without Azure Portal, a database, raw logs or JSON.

| ID | Område | Scenario | Prioritet | Testtype | Dekket i eksisterende kode? | BirkNext-status i dag | BirkNext-mekanisme | Hva kan BirkNext gjøre i dag? | Hva mangler for full systemtest? | Kan implementeres i BirkNext? | Anbefalt løsning | Hva bør fortsatt være manuelt/eksternt? | Implementeringsprioritet | Begrunnelse | Hovedtester | Manuell tester trenger Azure? |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| E2E-01 | Hele flyten | Vanlig gyldig rapport | P0 | Begge | Delvis | Modellert, ikke kjørbar | Rapportmottak `valid-report` + Periodeavslutning `close-valid-period` | Show steps, prerequisites and manual plan; run never starts | Manual action and confirmation, MU/MM observation, report read, period and Bufdata evidence, correlation | Delvis | Guided manual journey run with per-step manual evidence; observers added later | Altinn submission, period close, Bufdata check until verifiers exist | Nå | P0 backbone; the manual runner unlocks it without Altinn automation | Begge | Ja (gap) |
| E2E-02 | Rapport | Barn finnes ikke | P0 | Manuell | Nei | Modellert, ikke kjørbar | Rapportmottak `person-not-found` (NotAssessed); report-service Person rule (source) | Description only | Manual run record; report "on hold" state read | Delvis | Manual scenario with a synthetic unknown child; report-state read verifier in Neste | Submission; confirming on-hold until the verifier exists | Nå | P0 manual; only needs the manual runner | Manuell tester | Ja (gap) |
| E2E-03 | Rapport | Barn opprettes senere | P0 | Manuell | Nei | Ikke implementert | Rapportmottak chain only | Nothing scenario-specific | Two-phase scenario; Person and report read | Delvis | Two-phase manual scenario (report → manual "child registered" step → confirm linkage) | Child registration in BiRK/Person; submission | Nå | P0 manual; multi-step manual runner | Manuell tester | Ja (gap) |
| E2E-04 | Rapport | Barn fortsatt ukjent ved periodeavslutning | P0 | Manuell | Nei | Ikke implementert | Rapportmottak + Periodeavslutning chains | Nothing scenario-specific | Cross-journey scenario; report state plus period status | Delvis | Manual cross-journey scenario; period/report read in Neste | Submission, period close | Nå | P0 manual | Manuell tester | Ja (gap) |
| E2E-05 | Personvern | Kode 6/7 | P0 | Begge | Nei | Ikke implementert | Security Classification source facts (foundation only) | Source-level Kode 6/7 model facts, not this flow | Manual run; Bufdata exclusion check (DATA-03) | Delvis | Manual scenario now; automated exclusion check in Senere | Synthetic graded-child setup (never real data) | Nå | P0 privacy; guided evidence first | Begge | Ja (gap) |
| E2E-06 | Duplikathåndtering | Samme tekniske melding to ganger | P0 | Teknisk | Ja | Modellert, ikke kjørbar | Rapportmottak `duplicate-report` (NotAssessed); Active Event replay is CDC, not this flow | Nothing for the report flow | Re-delivery recorded; report-count read verifier | Delvis | Manual "re-delivered" step now; aggregate report-count verifier in Neste | Re-sending the technical message | Nå | P0; MU duplicate control is already unit-tested, only system confirmation remains | Teknisk tester | Ikke relevant |
| E2E-07 | Rapport | Korrigering | P1 | Manuell | Nei | Ikke implementert | Rapportmottak chain only | Nothing | Manual scenario; report version-history read | Delvis | Manual scenario plus report read verifier | Correction submission | Neste | P1; needs report read for versions | Manuell tester | Ja (gap) |
| E2E-08 | Rapport | Overlappende rapporter | P1 | Manuell | Nei | Ikke implementert | Rapportmottak chain only | Nothing | Manual scenario; overlap-flag read | Delvis | Manual scenario plus report read verifier | Submissions | Neste | P1 | Manuell tester | Ja (gap) |
| E2E-09 | Rapport | Flere rapportører | P1 | Manuell | Nei | Ikke implementert | Rapportmottak chain only | Nothing | Manual scenario; second approved test reporter; report read | Delvis | Manual scenario plus report read verifier | Submissions by two test reporters | Neste | P1 | Manuell tester | Ja (gap) |
| CDC-01 | Utdanning | Nytt utdanningsforhold | P0 | Begge | Nei | Delvis kjørbar | Active Event + Skolenærvær provider `create` | Generate and send a synthetic create into Event Hub when all gates pass; transport acceptance; aggregate checkpoint/telemetry | BiRK/Debezium bypassed; Utdanning verifier; registered synthetic child; per-event correlation | Ja | "Known synthetic child" prerequisite and run labelling now; read-only Utdanning verifier in Neste | Registering the synthetic child; target gate configuration | Nå | P0; already conditionally executable, so labelling and readiness make it usable | Begge | Ja (gap) |
| CDC-02 | Utdanning | Utdanning for ukjent barn | P0 | Begge | Nei | Delvis kjørbar | Active Event `create` (synthetic `BarnFK` unregistered) | Send an unknown-child create (implicit) | Explicit scenario label; verifier for "stored unlinked, not used" | Ja | Explicit unknown-child descriptor reusing the create generator; verifier in Neste | Confirming "not used further" until the verifier exists | Nå | P0; a tiny provider change makes it explicit | Begge | Ja (gap) |
| CDC-03 | Utdanning | Utdanning kobles senere | P0 | Manuell | Nei | Modellert, ikke kjørbar | Provider `unknown-child-late-linkage` (NotAssessed, refused) | Nothing executable | Two-phase run; child registration; linkage verifier | Delvis | Journey scenario: Active Event unknown-child create → manual "child registered" step → manual or automated linkage check | Child registration in BiRK/Person | Nå | P0; combines Now items A and B | Manuell tester | Ja (gap) |
| CDC-04 | Utdanning | Endre utdanning | P1 | Begge | Nei | Modellert, ikke kjørbar | Provider `update` (NotAssessed; helper exists) | Nothing executable | Update generator; Utdanning verifier | Ja | Enable the update generator path plus the verifier | None beyond gates | Neste | P1; meaningful only with the verifier | Begge | Ja (gap) |
| CDC-05 | Utdanning | Slette eller avslutte utdanning | P1 | Begge | Nei | Modellert, ikke kjørbar | Provider `delete` / `tombstone` (NotAssessed) | Nothing executable | Separate end / DELETE / tombstone; generators; verifier; cleanup policy | Ja | Generators for end (update `Til`) and DELETE; tombstone stays unavailable; verifier | Cleanup approval | Neste | P1 | Begge | Ja (gap) |
| COV-01 | Rapporteringsgrunnlag | Barn med mottatt rapport | P0 | Manuell | Nei | Ikke implementert | — | Nothing | Reporting-basis read; received classification | Delvis | Report-service reporting-basis read verifier per synthetic child | Creating the report | Neste | P0; needs a read contract that is not in evidence | Manuell tester | Ja (gap) |
| COV-02 | Rapporteringsgrunnlag | Barn med bare avvist rapport | P0 | Manuell | Nei | Ikke implementert | — | Nothing | Same; rejected classification | Delvis | Same verifier | Rejected-report setup | Neste | P0 | Manuell tester | Ja (gap) |
| COV-03 | Rapporteringsgrunnlag | Barn uten rapport | P0 | Manuell | Nei | Ikke implementert | — | Nothing | Same; missing classification | Delvis | Same verifier | Child setup | Neste | P0 | Manuell tester | Ja (gap) |
| COV-04 | Rapporteringsgrunnlag | Manglende skoletilbud | P0 | Manuell | Nei | Ikke implementert | — (Active Event can send `ManglendeSkoletilbud` CDC input) | Possibly the CDC input only (unlabelled) | Exclusion check in the reporting basis | Delvis | Same verifier; optional Active Event ManglendeSkoletilbud create as input | Confirming exclusion until the verifier exists | Neste | P0 | Manuell tester | Ja (gap) |
| COV-05 | Rapporteringsgrunnlag | Videregående skole | P1 | Manuell | Nei | Ikke implementert | — | Nothing | Domain rule plus verifier | Delvis | Verifier plus domain-owner rule | Report submission | Senere | P1; rule undefined | Manuell tester | Ja (gap) |
| LOCK-01 | Periodeavslutning | Komplett periode | P0 | Begge | Nei | Modellert, ikke kjørbar | Periodeavslutning `close-valid-period` (trigger hard-coded NotReady) | Show steps and prerequisites | Manual "period closed" step; period status, lock and counts read | Delvis | Manual trigger step plus read-only period-status observer | The period-close trigger | Neste | P0; needs a report-service read | Begge | Ja (gap) |
| MU-01 | MU/MM | Gyldig melding gjennom MU/MM | P0 | Teknisk | Ja | Modellert, ikke kjørbar | Rapportmottak `valid-report` | Steps, prerequisites, XSD binding (Partial) | Manual submission record; MU/MM observation; delivery to the report service | Delvis | Manual runner now; MU/MM telemetry/Blob-metadata observers in Neste | Altinn submission | Nå | P0; QA delivery confirmation is the only open item | Teknisk tester | Ikke relevant |
| MU-02 | MU/MM | Ugyldig XSD | P0 | Teknisk | Ja | Modellert, ikke kjørbar | Rapportmottak `invalid-xsd` (NotAssessed) | Description; XSD from Source Analysis | Reviewed invalid payload; MM rejection observation | Delvis | Manual runner now (tester submits the reviewed invalid report); MM observer in Neste | Submitting the invalid report | Nå | P0 | Teknisk tester | Ikke relevant |
| MU-03 | MU/MM | Manglende eller ugyldig Blob/referanse | P1 | Teknisk | Delvis | Modellert, ikke kjørbar | Rapportmottak `retrieval-decrypt-failure` | Description | Fault induction; Blob metadata and MM error observation | Delvis | Blob metadata plus MM telemetry observers | Inducing a missing blob or bad reference | Neste | P1 | Teknisk tester | Ikke relevant |
| MU-04 | MU/MM | Dekrypteringsfeil | P1 | Teknisk | Ja | Modellert, ikke kjørbar | Rapportmottak `retrieval-decrypt-failure` | Description | Fault induction; MM error observation (no key material) | Delvis | MM telemetry observer | Inducing the key/channel fault | Neste | P1 | Teknisk tester | Ikke relevant |
| MU-05 | MU/MM | Mapping-feil | P1 | Teknisk | Delvis | Modellert, ikke kjørbar | Rapportmottak `mapping-validation` (NotAssessed) | Description | Report read of mapped values | Delvis | Report-service read verifier | Submission | Neste | P1 | Teknisk tester | Ikke relevant |
| ROB-01 | Robusthet | Skolenærværsrapport utilgjengelig | P0 | Teknisk | Nei | Ikke implementert | Generic Service Bus/telemetry (only if the hop is a configured Service Bus entity) | Nothing scenario-bound | Externally induced outage; backlog and recovery observation | Delvis | Manual "outage started/ended" steps plus aggregate backlog observer | The outage | Neste | P0 | Teknisk tester | Ikke relevant |
| ROB-02 | Robusthet | Person utilgjengelig | P0 | Begge | Nei | Ikke implementert | Active Event can send the CDC input only | Nothing scenario-bound | Externally induced outage; intermediate-state read | Delvis | Manual outage steps plus Utdanning/report read verifiers | The outage | Neste | P0 | Begge | Ja (gap) |
| ROB-03 | Robusthet | MM startes på nytt under behandling | P1 | Teknisk | Nei | Ikke implementert | — | Nothing | Restart; message and duplicate observation | Delvis | Manual restart step plus report-count verifier | The restart | Senere | P1 | Teknisk tester | Ikke relevant |
| ROB-04 | Robusthet | Nytt behandlingsforsøk etter midlertidig feil | P0 | Teknisk | Ja | Ikke implementert | Wolverine retry-policy source facts; Service Bus metrics | Configured retry policy (source) | Attempt count and timing per message | Delvis | Telemetry observer for retry log events (correlated) | Inducing the transient fault | Neste | P0 | Teknisk tester | Ikke relevant |
| ROB-05 | Robusthet | Permanent feil havner i feilkø | P0 | Teknisk | Nei | Ikke implementert | Service Bus dead-letter counts (only if the error queue is a configured Service Bus entity) | Nothing scenario-bound | Fault induction; dead-letter observation for the message | Delvis | Bind the error queue in IQR; dead-letter delta observer in the journey | Inducing the permanent fault | Neste | P0 | Teknisk tester | Ikke relevant |
| ROB-06 | Robusthet | Ny behandling fra feilkø | P1 | Teknisk | Nei | Ikke implementert | Dead-letter counts (aggregate) | Nothing scenario-bound | The re-drive; "completed once" | Delvis | Manual "re-driven" step plus report-count verifier | The re-drive (no BirkNext button) | Senere | P1 | Teknisk tester | Ikke relevant |
| DATA-01 | Bufdata | Riktig periodedata | P0 | Begge | Nei | Modellert, ikke kjørbar | Periodeavslutning `dataset-verify` | Show the step | Bufdata access; Parquet; expected-vs-actual comparison | Delvis | Read-only dataset metadata, then redacted row comparison | Access approval | Senere | P0, but needs new access and privacy design | Begge | Ja (gap) |
| DATA-02 | Personvern | Bufdata uten fødselsnummer/DUF | P0 | Teknisk | Nei | Modellert, ikke kjørbar | Periodeavslutning `dataset-pii` | Show the step | Dataset read; absence check | Delvis | Column/value absence check (counts only) | Access approval | Senere | P0 | Teknisk tester | Ikke relevant |
| DATA-03 | Personvern | Kode 6/7 ikke i Bufdata | P0 | Begge | Nei | Ikke implementert | Security Classification (source-level only) | Nothing for the dataset | Dataset read; synthetic graded child; absence check | Delvis | BirkID absence check for the synthetic graded child | Synthetic graded setup | Senere | P0 | Begge | Ja (gap) |
| DATA-04 | Service Bus | Melding om avsluttet periode | P0 | Teknisk | Nei | Modellert, ikke kjørbar | Periodeavslutning publish/broker steps; Source Analysis channel check; Service Bus aggregate metrics | Show steps; channel presence in source | Message-level period check and absence of persondata | Delvis | Aggregate IncomingMessages delta in Neste; read-only peek on an approved test subscription only after privacy review | Triggering the close | Neste | P0 | Teknisk tester | Ikke relevant |
| SPOR-01 | Sporbarhet | Følge én melding gjennom hele kjeden | P0 | Teknisk | Delvis | Modellert, ikke kjørbar | Journey step chain; Active Event correlation (CDC only) | Show the chain | Correlation key across Altinn/MU/MM/report/Bufdata; observers | Delvis | Correlation id from the MU contract carried through observers; link Active Event and journey runs | Supplying the correlation key from the submission | Neste | P0 | Teknisk tester | Ikke relevant |
| SPOR-02 | Personvern | Logger uten sensitiv informasjon | P0 | Teknisk | Ja | Evidens/verifikasjon | Source Analysis `LoggingAnalysis` (SensitiveData, PayloadLogging, redaction) | Source-level findings for each uploaded component of the chain | Runtime log sampling for the rest of the chain | Delvis | Show per-component logging findings as journey evidence now; optional aggregate telemetry query later | Runtime log review | Nå | P0; evidence exists, only wiring is needed | Teknisk tester | Ikke relevant |
| RELOAD-01 | Utdanning | Full omlasting | P1 | Teknisk | Nei | Ikke implementert | Snapshot descriptor (NotAssessed) | Nothing | Reload observation | Delvis | Read-only reload monitoring run (Event Hub counts, checkpoint progress) | Triggering the reload | Senere | P1 | Teknisk tester | Ikke relevant |
| RELOAD-02 | Utdanning | Avstemming etter full omlasting | P1 | Begge | Nei | Ikke implementert | — | Nothing | BiRK vs Utdanning comparison | Delvis | Read-only aggregate reconciliation (counts per table) | Approving source and destination read access | Senere | P1 | Begge | Ja (gap) |

## 10. Gap analysis

**A. Already possible; mostly UI/evidence wiring:**
- Surface the Source Analysis logging findings as SPOR-02 evidence.
- Label the existing create as known-child / unknown-child.
- Link Active Event runs to journey runs (link, not copy).

**B. Small implementation:**
- Manual action and manual confirmation states in the shared journey model.
- Manual-mode scenario runs.
- Descriptors for the 39 scenarios in the Skolenærvær pack.
- Update and end/DELETE generator paths, without claiming verification.

**C. Medium implementation:**
- Read-only Utdanning verifier (`GatewayReadDownstreamVerifier` subclass).
- Report-service read verifier (state, counts, versions, reporting basis).
- Generic `IJourneyStepObserver`s for Service Bus count deltas, telemetry and Blob metadata.
- Correlation-id propagation.

**D. Large/new external integration:**
- Bufdata read-only dataset and Parquet access.
- Privacy assertions.
- Reload monitoring and reconciliation.
- Message-level Service Bus read (approved test subscription only).

**E. Keep manual/external:**
- Altinn submission.
- Child/Person registration.
- Period-close trigger.
- Outages and restarts.
- DLQ re-drive.
- Reload trigger.
- Real-data or business judgement.

**F. Do not implement in BirkNext:**
- Production execution.
- Arbitrary destinations or raw payloads.
- Real PII.
- Altinn form submitter as the default.
- Stop/restart/outage/re-drive buttons.
- Mutating Bufdata or QA data.
- Treating transport, checkpoint or blob existence as processing.
- Parallel runners, configuration stores or history stores.

## 11. Recommended roadmap

**Phase 1 — Nå (12 scenarios: E2E-01, E2E-02, E2E-03, E2E-04, E2E-05, E2E-06, CDC-01, CDC-02, CDC-03, MU-01, MU-02, SPOR-02)**

- **A. Manual scenario runner on the existing Integration Journey:**
  - scenario selection;
  - prerequisites and synthetic test-data checklist per scenario;
  - a clear external action per step;
  - "Action performed" and "Result confirmed" evidence;
  - the existing durable journey store and export.

  This needs no new store or runner. It covers E2E-01–06, MU-01, MU-02 and CDC-03 (manual step).
- **B. Active Event:**
  - explicit known-child and unknown-child create descriptors (CDC-01, CDC-02);
  - late-linkage as a journey scenario that delegates its first step to the Active Event create (CDC-03).
- **C. Read-only evidence wiring:** Source Analysis logging findings shown per chain component (SPOR-02).

A read-only downstream verifier belongs here only if an Utdanning read contract is supplied. None exists in evidence, so it is planned for Neste.

**Phase 2 — Neste (19 more, 31 in total: E2E-07, E2E-08, E2E-09, CDC-04, CDC-05, COV-01, COV-02, COV-03, COV-04, LOCK-01, MU-03, MU-04, MU-05, SPOR-01, ROB-01, ROB-02, ROB-04, ROB-05, DATA-04)**

- Generic `IJourneyStepObserver`s: Service Bus entity count deltas, telemetry (retry, dead-letter, error), Blob metadata. These make MU/MM evidence possible.
- Read-only Utdanning verifier, plus the update and end/DELETE generators.
- Report-service read verifier: state, versions, overlap, reporting-basis counts, period status.
- Correlation across Active Event and journey runs.
- Aggregate Service Bus evidence for `SkolenaervaersperiodeLukket`.

**Phase 3 — Senere (8 more, 39 in total: COV-05, ROB-03, ROB-06, DATA-01, DATA-02, DATA-03, RELOAD-01, RELOAD-02)**

- Bufdata read-only dataset access and Parquet.
- Expected-vs-actual comparison.
- fnr/DUF and Kode 6/7 absence checks (redacted counts only).
- Reload monitoring and reconciliation.
- Advanced negative technical cases (restart, re-drive), observed after the fact.

## 12. Safety, ownership and implementation design (recommendation only — not implemented)

**Ownership stays as it is:**

| Owner | Responsibility |
|---|---|
| Target Environment | Target configuration |
| Integration Quality Review | Integration configuration, including the message-flow configuration and the error-queue binding |
| Source Analysis | Source evidence (architecture rules, logging findings, XSD, channel names) |
| Active Event | The only active event runner and sender |
| Integration Journey | Scenario/journey coordination, step evidence and history |
| Skolenærvær Testing | Thin domain shell |

Production remains blocked. Only synthetic data is allowed. Read connectors use least-privilege identities and store only redacted or aggregate evidence.

**Manual testing extends the shared Integration Journey model:**

- New step evidence states, distinct from `Verified`:
  - `ActionPerformed`: the tester performed the external action;
  - `ManuallyConfirmed`: the tester confirmed the result.
- A journey or scenario mode `Guided`. A guided run can start without the automation prerequisites; those stay visible as "not automated".
- Two authenticated endpoints on the existing journey API:
  - record a step action;
  - record a step confirmation.

  Both store a short note, the tester identity, a timestamp and optional reference ids. They never store payloads or PII. Records go into the existing `IntegrationJourneyRunStore`.
- Overall result rules:
  - `Completed` stays "every mandatory step automatically Verified".
  - A guided run where all mandatory steps are confirmed shows a separate "Completed (manually confirmed)" result.
- Scenario descriptors for the 39 IDs live in `SkoleNaervaerJourneyPack` (domain content), not in generic code.

**Automated evidence:**

- Observers are generic `IJourneyStepObserver` implementations, bound to a step through the IQR integration key.
- Delegated CDC steps reference the Active Event run id instead of copying its evidence.

**Do not introduce:**

- a Skolenærvær-specific runner or sender;
- a second history store;
- a second configuration store;
- a second source-evidence store.

## 13. Exact code references

- Active Event contracts and evidence stages: `AIAssisted/shared/ActiveEventTestingContracts.cs` (`ActiveEventEvidenceStage`, `ActiveEventRunStatus`).
- Provider boundary and downstream verifier interface: `.../ActiveEventTesting/ActiveEventProviderContracts.cs` (`IActiveEventDownstreamVerifier`).
- Skolenærvær provider and fixtures: `.../Providers/SkoleNaervaer/SkoleNaervaerScenarioProvider.cs` (`Definitions`, `GenerateAsync`, `ExactReplay`), `SkoleNaervaerFixtures.cs`.
- Sender tombstone block: `.../ActiveEventTesting/EventHubTestSender.cs`.
- Registrations: `AIAssisted/backend/BirkNext.Api/Program.cs`:
  - "No IActiveEventDownstreamVerifier is registered";
  - continuity and telemetry providers;
  - journey runner built from empty executor and observer collections.
- Journeys:
  - `.../IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs`: steps, scenarios, hard-coded prerequisites, rules;
  - `IntegrationJourneyRunner.cs`, `IntegrationJourneyService.cs`, `IntegrationJourneyRunStore.cs` (durable);
  - `AIAssisted/shared/IntegrationJourneyContracts.cs` (`JourneyStepState`).
- Service Bus evidence: `.../Integrations/ServiceBus/ServiceBusEvidenceService.cs`, `ArmServiceBusMetadataSource.cs`, `AzureMonitorServiceBusMetricsSource.cs`.
- Logging evidence: `.../SourceAnalysis/Observability/LoggingAnalysis.cs`.
- Gradert tilgang: `.../SecurityClassification/ClassificationSourceAnalyzer.cs`.
- Manual plan: `AIAssisted/shared/MessageFlowReviewContracts.cs`, which always adds the blocker "No active external test provider is implemented."
- UI: `ActiveEventTestingPanel.razor`, `IntegrationJourneyPanel.razor`, `Pages/SkoleNaervaerTesting.razor`.

## 14. Tests/builds executed

**First audit:** one focused backend test command was attempted. It stopped at compilation because of the stale scoped CSS bundle `wwwroot/BirkNext.Web.styles.css`. That file was not changed.

**Second audit:**
- No tests or builds were run. Every finding above comes from source inspection of the cited files at `aa261796`.
- The stale CSS bundle was not touched.
- No external service, target or Azure resource was contacted.

## 15. Known limitations

1. Runtime readiness of any DEV/QA target, IQR entry, source snapshot or authentication was not queried. Only code gates were audited. "Delvis kjørbar" for CDC-01 and CDC-02 is conditional on those gates.
2. Whether MM's input/error queue and the MM→report hop are Service Bus entities is not established. ROB-01, ROB-05 and ROB-06 observation therefore stays conditional.
3. Checkpoint and telemetry evidence is aggregate and cannot prove persistence or domain behaviour.
4. Not found in the code:
   - a downstream Skolenærvær verifier;
   - journey step executors or observers;
   - an Altinn/MU/MM executor;
   - a period-close trigger;
   - a Bufdata reader;
   - a Parquet parser;
   - reporting-population logic.
5. Column 6 ("Dekket i eksisterende kode?") is copied from the supplied definitions; it was not verified against target-system repositories.
6. No tests were run in the second audit (source inspection only).

## 16. Final counts

**BirkNext current capability**

| Classification | Count | Scenarios |
|---|---:|---|
| Kjørbar | 0 | — |
| Delvis kjørbar | 2 | CDC-01, CDC-02 |
| Evidens/verifikasjon | 1 | SPOR-02 |
| Modellert, ikke kjørbar | 16 | E2E-01, E2E-02, E2E-06, CDC-03, CDC-04, CDC-05, LOCK-01, MU-01, MU-02, MU-03, MU-04, MU-05, DATA-01, DATA-02, DATA-04, SPOR-01 |
| Ikke implementert | 20 | E2E-03, E2E-04, E2E-05, E2E-07, E2E-08, E2E-09, COV-01, COV-02, COV-03, COV-04, COV-05, ROB-01, ROB-02, ROB-03, ROB-04, ROB-05, ROB-06, DATA-03, RELOAD-01, RELOAD-02 |
| **Total** | **39** | |

**Implementation feasibility**

| Kan implementeres i BirkNext? | Count | Scenarios |
|---|---:|---|
| Ja | 4 | CDC-01, CDC-02, CDC-04, CDC-05 |
| Delvis | 35 | All others (stimulus or final confirmation stays external) |
| Nei | 0 | — |

**Existing code-test coverage (from the supplied definitions)**

| Coverage | Count | Scenarios |
|---|---:|---|
| Ja | 6 | E2E-06, MU-01, MU-02, MU-04, ROB-04, SPOR-02 |
| Delvis | 4 | E2E-01, MU-03, MU-05, SPOR-01 |
| Nei | 29 | All others |

**Test type**

| Testtype | Count | Scenarios |
|---|---:|---|
| Manuell | 12 | E2E-02, E2E-03, E2E-04, E2E-07, E2E-08, E2E-09, CDC-03, COV-01–05 |
| Teknisk | 16 | E2E-06, MU-01–05, ROB-01, ROB-03, ROB-04, ROB-05, ROB-06, DATA-02, DATA-04, SPOR-01, SPOR-02, RELOAD-01 |
| Begge | 11 | E2E-01, E2E-05, CDC-01, CDC-02, CDC-04, CDC-05, LOCK-01, ROB-02, DATA-01, DATA-03, RELOAD-02 |

Hovedtester follows the test type: 12 Manuell tester, 16 Teknisk tester and 11 Begge.

Manuell tester trenger Azure today: **Ja (usability gap) for all 23 Manuell/Begge scenarios**, and Ikke relevant for the 16 Teknisk scenarios. After Nå, manual testers can record actions and confirmations without Azure. They still depend on the technical tester, or on Neste/Senere verifiers, to see downstream state.

**Priority:** P0 26, P1 13.

**Implementation phase:** Nå 12, Neste 19, Senere 8, Ikke anbefalt 0.

**Support over time:**

| Measure | Count | Notes |
|---|---:|---|
| A. Meaningful support today (Kjørbar + Delvis kjørbar + Evidens/verifikasjon) | 3 | CDC-01, CDC-02, SPOR-02 |
| B. Supported after "Nå" | 12 | The three above are inside the Nå set |
| C. Supported after "Neste" | 31 | |
| Supported after "Senere" | 39 | |
| D. Still requiring external/manual action after implementation | 35 | All except CDC-01, CDC-02, CDC-04 and CDC-05, where BirkNext both sends the event and, once the Utdanning verifier exists, verifies the result. Even those need operator-configured gates and synthetic-child setup. |

"Supported after a phase" means BirkNext gives the scenario at least a guided run with recorded action and confirmation evidence (Delvis kjørbar), or execution or evidence. It does not mean automated verification.

**Semantics preserved:**
- Each stage is distinct: Generated, SendAttempted, TransportAccepted, ConsumerObserved, Processed, Persisted and DomainVerified.
- Checkpoint movement does not mean the message was processed.
- Service Bus acceptance does not mean Bufdata processed the event.
- A blob existing does not mean the report was processed.
- Configured does not mean observed, and source evidence does not mean runtime verification.
- No evidence does not mean passed; NotAssessed and NotTested do not mean Failed.
- A manual action completed does not mean the result is verified, and a manual confirmation is not an automated verification.
