# External message integration audit and test plan

Audit date: 2026-10-02. This is a generic integration checklist, not evidence that Skolenærvær uses a particular Altinn or Fiks product.

## Current BirkNext capability

Source Analysis previously did not recognize XSD. It now emits bounded, snapshot-local XML Schema evidence in Contracts: target namespace, explicit schema version only, global/nested elements, qualified names, declared type, min/max occurrence, nillable/default/fixed values, documentation, simple-type facets, named types, and import/include/redefine file-presence. The parser prohibits DTDs and external resolution. It reports partial structural evidence; it does not compile the schema, expand referenced groups/types, resolve external schemas, validate XML instances, or apply business rules. XSD comparison reports structural changes and conservative concerns (`PotentiallyBreaking`, `PotentiallyCompatible`, `NeedsReview`), not a definitive compatibility verdict. Schema annotations/defaults/facets are redacted before display.

IQR has manual single-integration definitions and source-derived integration-path evidence. The latter distinguishes source state from runtime state, but its current path analyzer recognizes a specialized internal event flow; it is not a generic Altinn/Fiks multi-hop message/error workflow editor. Existing evidence can describe integrations and messaging, but this workspace contains no Skolenærvær repository, XSD, endpoint, Altinn/Fiks configuration, or runtime observation. Thus the exact sender, transport, channel, hop ownership, retry/parking/idempotency behavior, and correlation propagation remain unverified. No Altinn-specific transport, active sender, or project-specific propagation rule was added.

## Official environment references

Retrieved 2026-10-02; verify the linked provider documentation and project-specific onboarding before execution.

- [Fiks test environment](https://developers.fiks.ks.no/felles/testmiljo/index.html): Fiks test connects to external test systems including Altinn; use test data only, request access through KS, and do not treat it as production-equivalent. The docs describe a test portal and ID-porten test-user access.
- [Fiks I/O](https://developers.fiks.ks.no/tjenester/fiksprotokoll/fiksio/index.html) and [message handling](https://developers.fiks.ks.no/tjenester/fiksprotokoll/meldingshandtering/index.html): I/O is asynchronous message transport. The docs distinguish `melding-id`, caller-supplied retry identifier `klientMeldingId`, and `klientKorrelasjonsId`; consumers must implement idempotency. Acknowledgement/redelivery/dead-letter behavior depends on client/protocol use and must be checked against the chosen client and receiver.
- [Fiks integration setup](https://developers.fiks.ks.no/felles/integrasjoner/index.html): test access can require a test enterprise certificate, organization authorization for `ks:fiks`, a configured Maskinporten client, and a Fiks test organization/account. Exact requirements depend on the Fiks service and direction.
- [Altinn environments](https://docs.altinn.studio/en/technology/architecture/capabilities/devops/environments/): TT02 is an Altinn 3 test environment. This does not establish that the project uses Altinn 3 or TT02.
- [Altinn App API](https://docs.altinn.studio/en/api/apps/): the documented TT02 app API pattern is `https://{org}.apps.tt02.altinn.no/{org}/{appname}`; the app owner may configure APIs differently.
- [Altinn Events API](https://docs.altinn.studio/en/events/api/): Events API has a separate TT02 endpoint (`https://platform.tt02.altinn.no/events/api/v1`) and token requirements. Use it only if project configuration confirms Events API is the integration product.
- [Altinn authentication](https://docs.altinn.studio/en/api/scenarios/authentication/) and [system vendor setup](https://docs.altinn.studio/en/authorization/getting-started/system-vendor/): Maskinporten/system-user requirements, scopes, resources, organization rights, and certificates are service-owner controlled; do not infer a universal scope or credential from the platform name.
- [Altinn test deployment](https://docs.altinn.studio/en/altinn-studio/v8/reference/testing/deploy/) and [test authorization](https://docs.altinn.studio/en/altinn-studio/v8/reference/configuration/authorization/guidelines_authorization/test_authorization_application/): app deployment, test actors, and rights setup are app-owner/service-owner tasks. Test actor data such as Tenor is product- and scenario-dependent.
- [Maskinporten certificate authentication](https://docs.digdir.no/docs/Maskinporten/maskinporten_virksomhetssertifikat): test and production credential setup are distinct; private keys/secrets must remain outside BirkNext evidence.

No universal Altinn test endpoint, scope, test organization, synthetic identity, or submission API can be prescribed until the project’s actual Altinn product and service registration are known. Fiks I/O and Altinn are distinct products; Fiks test’s connection to Altinn does not prove that this project routes Altinn through Fiks.

## Practical test plan

### Preconditions

1. Identify the system of record and owner for each hop. Confirm whether Altinn, Fiks I/O, both, or another channel is used; obtain the approved test/QA flow diagram and message type/channel mapping.
2. Obtain test-environment onboarding from the relevant service owner and, if Fiks is involved, KS. Configure approved test organizations/users, rights/scopes, certificates, and endpoints through the owning platforms. Keep credentials in the approved secret store, never in BirkNext.
3. Select the exact XSD and transitive schemas from the project snapshot. Confirm the channel/message type and version mapping. Prepare synthetic approved test data; never use a real child’s identifiers.
4. Confirm correlation identifiers and read-only evidence sources for each checkpoint before sending. Agree on retention and cleanup for transport queues, parked messages, logs, and test records.

### Layered execution and evidence

Run each checkpoint independently; an upstream accepted/HTTP-success response is not downstream processing proof.

| Layer | Action | Evidence to record | Status if evidence is unavailable |
|---|---|---|---|
| Contract | Validate synthetic XML locally against the selected XSD; separately check business rules | schema version, validation result, redacted payload hash | Not assessed |
| External submission | Submit only through approved test UI/API/client | environment, request/message ID, response, timestamp | Not authorized/unavailable |
| Transport delivery | Check the provider’s delivery/status mechanism | provider receipt and transport message ID | Not observed |
| Receiver | Check Meldingsmottaket ingress/validation evidence | receiver timestamp, correlation/message ID, schema/business outcome | Not observed |
| Identity | Check Person lookup using approved test identity | resolved/unresolved result and internal reference only | Not assessed |
| Domain processing | Check Skoletjenesten acceptance | domain result and internal reference | Not observed |
| Persistence | Verify through approved read-only API/query | persisted record ID/state; no FNR/DUF/BiRK identifier | Not assessed |

Record `Observed`, `NotObserved`, `NotAuthorized`, `Unavailable`, `TimedOut`, `NotApplicable`, or `NotAssessed`. Use Passed/Failed only for an explicit expected assertion. Report the last observed hop and the next unobserved hop; do not claim a failure solely from absent telemetry.

### Scenarios

- Valid synthetic message: verify every layer from local schema validation through persistence separately.
- Invalid XSD and malformed XML: distinct payloads; observe which system rejects them. Do not presume rejection location.
- Schema-valid but business-invalid report: verify business validation independently.
- Unknown test identity: confirm the approved unresolved/park/reject outcome and no unintended domain persistence.
- Person service unavailable and destination unavailable: execute only through an approved fault-injection/mock mechanism; observe retry schedule, bounded exhaustion, parking/final state, and no duplicate persistence.
- Duplicate: submit the same supported idempotency/business key twice; observe actual behavior. Delivery/message IDs may change on resend.
- Unsupported contract version and multiple message types: use only registered test versions/channels; check routing and explicit unsupported behavior.
- Authentication and authorization: test invalid credentials separately from an authenticated principal lacking rights, using a dedicated test client.
- Parking/replay: identify owner, reason, retention, operator, and replay controls first; replay once after correcting the cause and verify idempotent outcome.
- Correlation/privacy: follow a synthetic message across every hop using documented IDs; do not assume message ID, correlation ID, trace ID, business reference, or internal child reference are interchangeable. Inspect DTOs, persistence, logs, errors, parking, and revision evidence for prohibited identifiers.
- Correction/resubmission and period boundaries: test only after domain semantics and expected replacement/overlap rules are confirmed.

### Automation boundary

BirkNext can analyze snapshot XSD evidence, show source/config/runtime provenance separately, maintain a checklist, and collect manually supplied checkpoint evidence. Automated external submission is deferred: the actual API/product, test tenant, authorized client, credentials, payload contract, endpoint, and downstream probes are absent. If later implemented, use a provider adapter with a backend-enforced non-production allowlist, approved synthetic payload/template, secret references only, bounded async polling, explicit cancellation, and per-hop checkpoint results. Never accept arbitrary production endpoints or raw real-person payloads.

## Current readiness and unresolved project facts

Readiness is **Needs configuration / not assessed**, not failed. The workspace does not provide the project’s external product/API, test endpoint, service registration/scopes, test account, synthetic identity source, message channel, XSD, receiver logs/status, Person probe, Skoletjenesten read-only verification, or cross-hop correlation contract. Obtain those from project/service owners before producing a runnable project-specific test guide. BirkNext has not run a live Altinn/Fiks test.
