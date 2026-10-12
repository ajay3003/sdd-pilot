# Skolenærvær MU Blob reference audit

Audit date: 2026-10-12\
Scope: repository source inspection only. No MU/MM source repository, Azure account, QA/TT02, or external service was accessed. No tests or builds were run. No production code was changed.

## 1. Executive summary

The repository does **not** contain the Meldingsutveksleren (MU) or Meldingsutvekslermottak (MM) application implementation, their deployment configuration, or a source-verified Service Bus payload contract. Therefore the requested payload chain cannot be completed from this codebase without guessing.

The exact verified answer for the MU/MM payload is:

> storage account: **not found / not proven** → container: **not found / not proven** → Service Bus message type and reference field: **not found / not proven** → MM lookup/download/decrypt/validation algorithm: **not found / not proven**.

BirkNext does have an Azure Blob reader, but it reads **Event Hubs checkpoint/ownership metadata** for CDC runtime evidence. It is not a reader for MU report payloads. The Skolenærvær Integration Journey and message-flow fixture describe an intended path and explicitly mark runtime evidence/configuration unavailable or documented only. Those models are not proof of the MU/MM contract.

Read-only Blob evidence could fit the existing Integration Journey evidence model after the actual DEV/QA endpoint, container, message reference mapping, authorization, and safe metadata policy have been confirmed with source/configuration owners. Today there is not enough evidence to implement a Skolenærvær payload Blob lookup safely.

## 2. Repository safety

- Branch: `008-traceability-first`
- HEAD: `aa261796e0dc32be970c5b6307dd34a479ea2aa4`
- The initial status/diff audit found pre-existing modified and untracked work, including Project Import/Skolenærvær changes, audit documents, generated/build artifacts, and an untracked `NUL`. These were preserved and not edited.
- No staged changes were present at audit start.
- This audit creates only this Markdown report. Production files and all pre-existing dirty/untracked files remain untouched.
- No reset, stash, clean, restore, delete, checkout, commit, push, runtime execution, Azure access, or external HTTP call occurred.

## 3. Code map and current architecture

| File / symbol | Responsibility | Side / type | What it proves |
|---|---|---|---|
| `AIAssisted/shared/IntegrationCatalogContracts.cs` — `IntegrationRuntimeEvidenceSettings` | IQR settings for Event Hub runtime evidence, including checkpoint account endpoint/container | BirkNext shared contract; production | The Blob configuration in this model is a checkpoint source, not MU payload storage. |
| `AIAssisted/backend/BirkNext.Api/Services/Integrations/AzureIntegrationEvidence.cs` — `IntegrationAzureCredential`, `BlobCheckpointEvidenceSource` | Creates an Azure identity and reads Event Processor checkpoint/ownership metadata | BirkNext production, generic CDC evidence | Metadata-only checkpoint access exists; no report payload read/decrypt path is present. |
| `AIAssisted/shared/ServiceBusContracts.cs` | BirkNext Service Bus namespace/entity topology and aggregate runtime evidence DTOs | BirkNext shared contract; production | It is not a `ServiceBusEnvelope` or application message payload contract. |
| `AIAssisted/backend/BirkNext.Api/Services/Integrations/ServiceBus/ServiceBusEvidenceService.cs`, `ArmServiceBusMetadataSource.cs`, `AzureMonitorServiceBusMetricsSource.cs` | Read-only Azure Resource Manager entity metadata and aggregate metrics | BirkNext production, generic | No Service Bus message body receive/peek/settle/send path. |
| `AIAssisted/shared/MessageFlowReviewContracts.cs` — `MessageFlowDefinition`, `MessageFlowHop`, `MessageFlowConfiguration`, `SkolenærværDocumented` | Domain-agnostic message-flow review types and a documented Skolenærvær fixture | BirkNext production modeling/UI contract; fixture is documented-only | `PayloadStoreReference` and related values are configuration references, not resolved MU contract fields. Fixture provenance states no implementation, QA config, or runtime observation is asserted. |
| `AIAssisted/backend/BirkNext.Api/Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs` — `SkoleNaervaerJourneyPack` | Models the three journeys and their evidence steps | BirkNext production journey definition, not MU/MM code | Labels include `mu-receive`, `mm-retrieve`, `mm-decrypt`, and `mm-xsd`; comments say actual endpoints, namespaces, auth and source observers are absent. |
| `AIAssisted/backend/BirkNext.Api.Tests/Services/Integrations/*` | Tests BirkNext checkpoint, Service Bus metadata and message-flow models | BirkNext test code | Does not exercise MU/MM payload storage or lookup. |
| `AIAssisted/backend/BirkNext.Api.Tests/Services/IntegrationJourneys/SkoleNaervaerJourneyPackTests.cs` | Tests modeled journeys and blocked/unassessed evidence states | BirkNext test code | Tests journey definition/readiness, not real MU/MM execution. |

Repository searches for `Meldingsutveksler`, `Meldingsutvekslermottak`, `ServiceBusEnvelope`, `InboundQueueHandler`, `InboundMessageHandler`, `BlobServiceClient`, and the MU/MM handler chain did not locate MU/MM application source. No runtime dependency from BirkNext to a MU/MM code package was found. The closest items are the BirkNext journey fixture, source-analysis rule detectors, and Azure checkpoint evidence adapters. These are not CDCPlay or MU/MM implementations.

## 4. Storage account configuration

### Actual MU payload store

No MU payload storage configuration key, class, deployment file, or environment-specific value was found. The account URL, authentication type, whether an account is shared across channels, and whether payloads are encrypted before upload are therefore **not proven**.

### Similar-looking BirkNext configuration (different purpose)

`IntegrationRuntimeEvidenceSettings` in `AIAssisted/shared/IntegrationCatalogContracts.cs` has:

- `CheckpointBlobEndpoint`: plain Blob **account/service endpoint**.
- `CheckpointContainerName`: checkpoint container name.
- legacy `CheckpointContainerUrl` for prior saved settings.
- `ResolvedCheckpointContainerUrl()`: combines endpoint + container.

The seed in `AIAssisted/backend/BirkNext.Api/Services/Integrations/M2lbDevIntegrationSeed.cs` includes the safe hostname `stm2bbirkdevnwe001.blob.core.windows.net` and container `person-adapter`. This is the M2LB DEV **Event Hubs checkpoint** container. It does not establish the MU payload account or container.

`IntegrationAzureCredential` in `AzureIntegrationEvidence.cs` uses `DefaultAzureCredential` without interactive login when Azure evidence is enabled. This applies to BirkNext's own read-only evidence reader. It does not prove MU/MM's auth method. The repository does not prove a MU storage account is shared across channels.

## 5. Container resolution

For BirkNext CDC checkpoint evidence, the endpoint and container are configured together on the shared Event Hub `IntegrationPlatform.RuntimeEvidence`; `ResolvedCheckpointContainerUrl()` joins them. This is not a per-MessagingChannel MU configuration.

For MU payloads, no channel-specific container property, inbound/outbound container split, hardcoded container, derivation rule, domain mapping, or channel-to-container mapping was found. No exact Skolenærvær/Altinn channel or namespace-to-container mapping is proven. The documented journey's `PayloadStoreReference` is an unresolved reference string in a review configuration, not a discovered Azure container.

**Skolenærvær payload container: not proven.**

## 6. Service Bus message contract and exact reference field

No application `ServiceBusEnvelope` or MU-to-MM message type is present in the repository. `ServiceBusContracts.cs` models infrastructure entities, counts, and aggregate runtime metadata; its `MessageType` is route metadata in the BirkNext review model, not a serialized MU envelope field.

The BirkNext message-flow configuration DTO has fields including `MessageType`, `MuChannelReference`, `QueueReference`, `PayloadStoreReference`, `KeyReference`, `ContractReference`, and correlation mapping/reference properties (`MessageFlowReviewContracts.cs`). These are operator/source-review references. The Skolenærvær fixture marks its flow as documented-only and does not bind these to the real MU message.

Consequently, the following are all **not found / not proven** for the actual Service Bus message:

- exact C# type and serializer;
- serialized property names for message ID, blob/container, blob path, namespace/domain, channel, correlation, sender, receiver;
- whether the payload or only a reference is sent;
- exact field MM reads;
- whether the path is opaque or constructed;
- whether the reference is preserved unchanged by MU;
- whether MM performs a database lookup between the message and Blob.

The only defensible answer to the key question, “which envelope/message property is read?”, is: **no property can be identified from this repository**. Do not implement a lookup based on `MessageId`, channel, correlation ID, URI, or the documented `PayloadStoreReference` without the actual MU/MM contract and code/config evidence.

## 7. MU write path

No MU HTTP/Altinn receiver, encrypted payload writer, Blob upload, blob-name generator, metadata/content-type setter, overwrite policy, or duplicate-message behavior exists in the inspected BirkNext source. The journey model describes MU receiving/storing an encrypted message, but its evidence is a planned/documented step, not observed or source-verified behavior.

Thus payload bytes, encryption-before-upload, key management, naming, metadata, content type, message/correlation metadata, overwrite behavior, and duplicate handling are **unknown**. No exact method chain can be supplied.

## 8. MM read, decrypt, validate, and forward path

The Skolenærvær journey pack models these separate steps: `mu-receive`, `mu-queue`, `mm-retrieve`, `mm-decrypt`, `mm-xsd`, `mm-map`, and report-service receipt. Its text says that MM retrieves, decrypts, validates and maps, while Person lookup belongs to Skolenærværsrapport. This is a domain responsibility model, not a trace to executable MM methods.

No MM queue consumer, envelope deserializer, channel resolver, Blob client, download or stream call, decryptor, XSD validator, report mapper, report-service forwarder, or acknowledgement implementation was found. The following behaviors cannot be asserted: whole-blob vs streaming download; metadata-first lookup; retry on 404; 404/auth distinction; logging of references or payload; deletion after read; Service Bus ack timing; retry/DLQ policy; duplicate handling.

The BirkNext `BlobCheckpointEvidenceSource` does not download payload bytes. It enumerates Blob metadata for Event Processor checkpoint and ownership prefixes and reads checkpoint metadata keys such as sequence number and offset. BirkNext's Service Bus evidence is management-plane/metrics evidence and does not receive or inspect messages.

## 9. Error handling

No MU/MM application implementation is available to prove error behavior for missing/invalid references, wrong container, unauthorized access, decryption/XSD/mapping failures, duplicates, retries, terminal failure/DLQ, or acknowledgement timing. The journey's `retrieval-decrypt-failure` scenario is modeled as `NotAssessed`; it is not evidence of implemented failure handling.

BirkNext's own checkpoint reader maps Azure request/auth/credential/URI failures to typed unavailable evidence and logs sanitized error descriptions. That behavior only applies to CDC checkpoint observation. BirkNext Service Bus tests assert it does not use message data-plane operations. Neither says what MU/MM does.

## 10. Skolenærvær-specific mapping

| Chain item | Finding | Evidence class |
|---|---|---|
| Altinn submission → MU | Described by the domain journey/documentation; no MU receiver implementation found | Documented/modelled only |
| MU channel | No source-verified channel or exact channel config found | Not found |
| Queue/topic | No real MU/MM entity mapping found; BirkNext Service Bus route models are separate generic review metadata | Not found |
| Envelope type | No application message type found | Not found |
| Blob account/container/reference | No MU payload config or contract found | Not found |
| Namespace/schema | Journey/message-flow fixture names `Skolenærværsrapport` and documents an XSD assumption; no actual schema or MM validator was found | Documented only; not source/config/runtime verified |
| MM retrieval/decryption/validation/mapping | Journey steps describe the responsibility; implementation absent | Modelled only |
| Skolenærværsrapport | Journey assigns report persistence and Person lookup to this component; no target service implementation/read API found in this repository | Documented responsibility only |

`AIAssisted/docs/integration-journeys.md` states the Rapportmottak path is not ready and lacks the source contract, auth, MU/MM/report source, and observers/verifiers. The Skolenærvær journey pack likewise says there is no available MU/MM/report/Bufdata source, endpoint, or runtime observer. This corroborates that the fixture must not be treated as a proven route.

## 11. Existing tests

Relevant tests found (not run during this audit):

- `IntegrationEvidenceAndContractTests.CheckpointBlobLayout_MatchesTheEventProcessorClientStore`: checks BirkNext's Event Processor checkpoint prefix layout. It does not check MU payload names or containers.
- `IntegrationEvidenceAndContractTests.AzureEvidence_IsDisabledByDefault_WithTheReason_AndNothingIsContacted`: verifies BirkNext Azure evidence is disabled by default and no contact occurs in that case.
- `IntegrationEvidenceAndContractTests.EvidenceAdapters_HaveNoWriteReceiveOrManagementMutationPath`: source guard for BirkNext evidence adapters; no MU/MM behavior.
- `EventHubRuntimeDefaultsTests.TheCheckpointEndpointIsAPlainAccountEndpoint`: validates the checkpoint endpoint shape.
- `EventHubRuntimeDefaultsTests.TheSeedCarriesTheVerifiedDefaults` and `TheDefaultsApplyOnlyToM2lbDev`: verify the seeded M2LB DEV checkpoint account/container, not Skolenærvær payload storage.
- `EventHubRuntimeDefaultsTests.AzureDisabled_SourcesAreConfiguredButNothingRuns`, `AzureEnabled_TheSeededSourcesAreReady_WithoutALogAnalyticsWorkspace`, and `TheEvidenceAdaptersNeverWrite`: cover BirkNext checkpoint evidence configuration/read-only boundary.
- `ServiceBusEvidenceTests.SeededTopologyIsDeterministicAndServiceBusShaped`, `ServiceBusReviewCodeNeverSendsReceivesSettlesOrMutates`, and `ServiceBusEvidenceCodeUsesNoMessageOperationOrDataPlaneClient`: establish BirkNext Service Bus review is metadata-only.
- `MessageFlowReviewTests` exercises documented flow readiness and manual planning; it does not validate an MU envelope or actual storage reference.
- `SkoleNaervaerJourneyPackTests.Pack_ModelsThreeSeparateJourneys_WithResponsibilitiesFromTheArchitecture`, `UnsupportedScenarios_AreNotAssessed_WithAReason_AndNoneIsInvented`, `WithoutConfiguration_EveryJourneyIsNotConfigured_AndNothingCanRun`, and `ConfiguredIqrIntegrations_AreReused_ButReadinessStaysTruthfullyBlocked_PerJourney`: test the model and truthful blocked readiness, not an MU/MM runtime.

No test was found for the actual MU payload's correct/wrong container, missing blob, reference derivation, encryption/decryption, XSD validation, channel selection, duplicate processing, retry/ack/DLQ, or payload-log leakage. This is because the actual MU/MM implementation is absent from the inspected repository.

## 12. What BirkNext can safely read

Today BirkNext can read checkpoint metadata only. A future payload metadata observer should be built only after the data owner supplies the real contract and environment-bound configuration. It should issue metadata/properties reads only, never download/decrypt payload content by default.

| Candidate datum | Recommendation |
|---|---|
| Storage account hostname | Safe to display/store only after it is resolved from the selected, allowlisted Target Environment. Do not accept a UI-supplied URL. |
| Container | Safe to display/store as configured IQR integration metadata after owner verification. Actual Skolenærvær value is unknown today. |
| Blob reference/path | Treat full value as sensitive because it may contain identifiers. Do not persist/display raw path; if needed, show a redacted form or keyed hash after owner approval. |
| Exists / not found | Safe to persist as timestamped step evidence with target and run identity; it proves object presence only. |
| Size, content type, created/modified time | Potentially safe operational metadata, but persist only an allowlisted subset under the journey's retention policy. Do not interpret as processing evidence. |
| ETag | Unknown sensitivity/correlation value. Prefer not to persist until owner/retention review; can be transient evidence if needed. |
| User metadata keys/values | Unknown; expose only explicitly allowlisted keys and values. Never dump all metadata. |
| Message/correlation ID | Potentially linkable operational identifier. Do not store raw; use approved masking/hash and minimal retention. |
| Encryption indicator | Safe only if sourced from an explicit, trusted metadata/contract field. Never infer encryption from file name, MIME type, or existence. Do not expose key identifiers/material. |
| Payload/body, decrypted XML, secrets/keys/SAS | Sensitive; do not read, store, or display for metadata evidence. |

Blob exists ≠ MM retrieved it ≠ decryption succeeded ≠ XSD passed ≠ report persisted. A checkpoint or Service Bus entity metric cannot establish those downstream facts either.

## 13. Recommended BirkNext integration

The ownership-compatible shape is a shared, read-only Blob evidence provider feeding an existing Integration Journey step and history model, then presented in the Skolenærvær thin shell. Reuse the existing Azure credential/evidence conventions where they actually apply, but do not repurpose the Event Hub checkpoint provider as a payload reader.

Before implementation, obtain and verify: MU/MM source or authoritative contract; DEV/QA account hostname and container; exact message type and reference field/path algorithm; channel/domain mapping; metadata allowlist; correlation/redaction/retention policy; read-only identity/RBAC; configured Target Environment/IQR mapping; and a safe test fixture proving lookup behavior. Backend must resolve storage only from configured and rechecked DEV/QA targets, reject Production, and never accept arbitrary storage URLs/container names from the page.

Do not create a Skolenærvær-specific store/config/browser, second history store, or payload download/decryption feature. Do not let metadata presence mark processing as complete. Add evidence as its own distinct step state with source, timestamp, target binding, and limitations.

## 14. Scenario impact

Scenario descriptions below are taken from the existing capability audit in the workspace; Blob metadata cannot itself prove downstream processing.

| ID | Scenario | Blob metadata value | Limit |
|---|---|---|---|
| E2E-01 | Valid report through the end-to-end flow | **Partial**: object existence/timestamp could support the MU storage step | Does not prove MU accepted the message, MM retrieved/decrypted/validated it, report persistence, or period close. |
| MU-01 | Valid message through MU/MM | **Partial**: presence and basic properties may support a storage/retrieval reference check | Does not prove MM read, decrypted, validated, mapped, or delivered the report. Needs MM observer/report read. |
| MU-03 | Missing/invalid Blob or reference | **Partial**: configured-reference metadata lookup can show not-found or properties for a valid reference | Does not prove MM error handling, retry, or terminal outcome; inducing invalid refs remains environment-controlled. |
| SPOR-01 | Trace one message across the chain | **Partial**: a verified reference plus approved masked correlation can connect the Blob step | No actual correlation mapping is proven; Blob evidence does not cover MM/report/Bufdata. |
| ROB-04 | Retry after transient fault | **No useful direct evidence**: static object metadata does not show per-message attempts/timing | Requires correlated MM telemetry/retry observer and controlled fault injection outside this metadata feature. |
| ROB-05 | Permanent failure to error queue | **No useful direct evidence**: Blob metadata does not establish dead-lettering | Requires verified queue mapping and message-bound DLQ/telemetry evidence. Aggregate DLQ counts alone are not message-level proof. |

## 15. Unknowns and remaining questions

1. Where is the authoritative MU/MM source repository and exact deployed version?
2. Which DEV/QA storage account/container does the Skolenærvær channel use, and what identity grants MM access?
3. What is the serialized Service Bus contract, including exact blob reference field and channel/domain discriminator?
4. Is the reference a URI, container + object name, generated key, database key, or another opaque token?
5. Does MU encrypt before upload? Which format/algorithm/key lifecycle applies? What exact bytes are stored?
6. What metadata/content type is set, and are identifiers embedded in names or metadata?
7. How does MM distinguish missing object from authorization/transient failures, and what are retry/ack/DLQ rules?
8. Is Blob deleted or retained after successful/failed processing, and for how long?
9. Where is the actual XSD, validator, mapping, and report persistence boundary?
10. Which correlation/message identifiers are safe to expose or persist in BirkNext?

## 16. Exact code references

- `AIAssisted/shared/IntegrationCatalogContracts.cs:100-154` — checkpoint runtime evidence settings and endpoint/container composition.
- `AIAssisted/shared/IntegrationCatalogContracts.cs:211-229` — URL/container validation and secret-like value checks.
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/AzureIntegrationEvidence.cs:18-42` — `DefaultAzureCredential` setup.
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/AzureIntegrationEvidence.cs:164-205` — `BlobCheckpointEvidenceSource`, metadata enumeration and checkpoint evidence.
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/M2lbDevIntegrationSeed.cs` — M2LB DEV checkpoint hostname/container seed.
- `AIAssisted/shared/ServiceBusContracts.cs:1-180` — BirkNext Service Bus infrastructure/evidence DTOs, not an application envelope.
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/ServiceBus/ServiceBusEvidenceService.cs` and `.../ServiceBus/ArmServiceBusMetadataSource.cs` — topology/metadata assessment.
- `AIAssisted/shared/MessageFlowReviewContracts.cs:10-60, 75-115, 222-300` — generic message-flow DTOs, configuration references, documented Skolenærvær fixture and its provenance.
- `AIAssisted/backend/BirkNext.Api/Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerJourneyPack.cs:18-22, 69-98, 116-125` — explicit absence of actual integration source/config and modeled journey steps/scenarios.
- `AIAssisted/docs/integration-journeys.md:46-53` — Rapportmottak readiness limits and documented-only aliases.
- `AIAssisted/backend/BirkNext.Api.Tests/Services/Integrations/IntegrationEvidenceAndContractTests.cs:26-90` — checkpoint source and read-only tests.
- `AIAssisted/backend/BirkNext.Api.Tests/Services/Integrations/EventHubRuntimeDefaultsTests.cs:32-53, 159-177, 205-256` — seeded checkpoint settings and endpoint/read-only checks.
- `AIAssisted/backend/BirkNext.Api.Tests/Services/Integrations/ServiceBusEvidenceTests.cs:83-114, 298-320, 488-508` — generic metadata-only Service Bus evidence.
- `AIAssisted/backend/BirkNext.Api.Tests/Services/Integrations/MessageFlowReviewTests.cs` — documented flow readiness/manual-plan behavior.
- `AIAssisted/backend/BirkNext.Api.Tests/Services/IntegrationJourneys/SkoleNaervaerJourneyPackTests.cs:48-188` — journey model and blocked-readiness tests.

## 17. Final chain and confidence

### Exact answer

**MU payload chain:** storage account **not proven** → blob container **not proven** → Service Bus envelope/message type **not found** → exact blob reference field **not found** → MM resolution algorithm **not found**. No actual MU/MM download, decrypt, validate, acknowledge, or retry method chain is present in this repository.

**Distinct BirkNext CDC evidence chain (not the MU payload chain):** configured `IntegrationRuntimeEvidenceSettings.CheckpointBlobEndpoint` + `CheckpointContainerName` → `ResolvedCheckpointContainerUrl()` → `BlobCheckpointEvidenceSource.GetAsync(...)` → Event Processor `checkpoint/` and `ownership/` prefixes → Blob metadata only, using BirkNext's `DefaultAzureCredential` when Azure evidence is enabled.

| Question | Answer | Confidence |
|---|---|---|
| Storage account known? | Actual MU payload account: no. A separate M2LB DEV CDC checkpoint hostname is present and must not be confused with it. | High |
| Skolenærvær container known? | No; the `person-adapter` seed is a CDC checkpoint container. | High |
| Service Bus envelope type known? | No application envelope type found. | High |
| Exact blob reference field known? | No; `PayloadStoreReference` is only a BirkNext review-config property, not proven to be an MU field. | High |
| MM lookup algorithm known? | No; MM source/handler/config is absent. | High |
| Encryption location known? | No actual implementation evidence; only documented/modelled encrypted-reference language. | High |
| XSD validation location known? | The journey assigns the step to MM, but no executable validator/schema path is present. | Medium |
| Safe metadata reader feasible? | Technically feasible after authoritative endpoint/reference/RBAC/redaction facts are supplied; no payload access is needed. | Medium |
| Enough information to implement BirkNext metadata-only Blob evidence? | No, not for Skolenærvær payloads. The actual account/container/reference contract and safe identity are blockers. | High |

Tests/builds executed: none. No tests were needed to establish the absence of an MU/MM implementation in this repository, and no runtime/Azure access was permitted or attempted.
