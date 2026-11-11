# BirkNext — Skolenærvær evidence matrix (independent source oracle, 2026-11-11)

This is the independent oracle the BirkNext results were compared with. It was built **from the real source packages only**, read-only. Nothing in a package was built, run or modified, and nothing was taken from BirkNext's own output. The claims are labelled per row:

- **V**: verified in source, with file:line.
- **T**: a test in the package asserts it. Tests were **not executed**.
- **S**: in specs or docs only.
- **I**: inferred.
- **NV**: not verifiable from these packages.

**Sources** (in `C:\Users\ajaan\Downloads`; `C:\Downloads` does not exist, see the system audit §1). Each package was copied and SHA-256 hashed before reading; the originals were not modified. Paths below are relative to the package root; `[M2LB (7)]` names the package.

| Package | SHA-256 (prefix) | Content |
|---|---|---|
| `M2LB (7).zip` | c7bb9f12 | English-named generation: MeldingsutvekslerReceiver, SkoleService, SkoleAdapter, PersonService, TjenesteService |
| `Tiltak 2 - Sikkert digitalt økosystem (1).zip` (shown as `Tiltak 2 (1)`) | 1293b192 | MU (Buf.Meldingsutveksler) |
| `M2LB (6).zip` | a306ee47 | Norwegian-named Skole (code); MM and SkoleAdapter specs only |
| `M2LB (5).zip` | 0af7dcf7 | Norwegian-named Skole and Meldingsutvekslermottak (code); SkoleAdapter specs only |
| `M2LB (3).zip` | bc780b6b | English-named MeldingsutvekslerReceiver, SkoleService, SkoleAdapter (code) |
| `M2LB (4).zip` | 04903137 | Skole, MM and SkoleAdapter specs only |

**Verification of the oracle:**
- The main session re-checked the highest-impact claims directly in source:
  - the intake routes (`/reports` vs `/rapporter`);
  - MU polling (`Polling:Enabled true`, `Schedule 24:00:00` in MU `appsettings.json`);
  - the positive-only receipt policy (`MessageTypeRegistration.cs:58-63`);
  - the MM registered XSD versions;
  - the MU schema-tool versions (no v2.0.1);
  - the Parquet.Net reference;
  - the period-closed topic `skole.perioder` (publish rules in `WolverineSetup.cs` / `WolverineOppsett.cs`).
- One correction to section 2 below: SkoleService **also consumes Person events** on the Service Bus topics `person.barn` and `person.person` (`[M2LB (7)] SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/WolverineSetup.cs:56-57`; `[M2LB (5)] Skole/src/M2LB.Skole.Infrastructure/Messaging/WolverineOppsett.cs:53-54`). This is in addition to the HTTP lookup.
- Deployment order and the deployed generation **cannot** be derived from these packages. The ZIP names and import order were not used for that.

---

# Oracle: Skolenærvær chain, first half (Altinn → MU → MM → SkoleService)

Read-only source audit. Paths are relative to the extracted package root. Nothing was built or run.

Labels: **V** = verified in source (file:line) · **T** = a test asserts it (cited) · **I** = inferred from code/docs · **NV** = not verifiable from these sources.

Package map (from directory listing):

| Pkg | Zip | MM folder | MM state |
|---|---|---|---|
| [M2LB (7)] | M2LB (7) | `MeldingsutvekslerReceiver` (English names) | full source + tests |
| [Tiltak 2 (1)] | Tiltak 2 – Sikkert digitalt økosystem (1) | — (this is MU: `Buf.Meldingsutveksler.*`) | full source + tests |
| [M2LB (6)] | M2LB (6) | `Meldingsutvekslermottak` | **specs only** (13 files, no `src/`) |
| [M2LB (5)] | M2LB (5) | `Meldingsutvekslermottak` (Norwegian names) | full source + tests |
| [M2LB (3)] | M2LB (3) | `MeldingsutvekslerReceiver` (English names) | full source + tests, ~identical to [M2LB (7)] |
| [M2LB (4)] | M2LB (4) | `Meldingsutvekslermottak` | **specs only** (13 files, no `src/`) |

---

## 1. Altinn → MU ([Tiltak 2 (1)])

### 1.1 How MU learns of an Altinn instance: webhook and polling, both

| # | Claim | Label | Evidence |
|---|---|---|---|
| 1.1a | MU exposes one `POST` endpoint per configured subscription; the route is the path of `Subscriptions[].Endpoint` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service/src/Altinn/Service/AltinnHooksEndpoints.cs:15-22` |
| 1.1b | Dev subscription endpoint `https://buf-meldings-utveksler.azurewebsites.net/api/altinn/webhook`, SourceFilter `https://buf.apps.tt02.altinn.no/buf/skolenaervaer`, TypeFilter `app.instance.process.completed`, Consumer `/org/buf` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service/appsettings.Development.json:30-41`; defaults `appsettings.json:39-44` |
| 1.1c | The CloudEvent `platform.events.validatesubscription` is answered 200 without processing | V | `AltinnHooksEndpoints.cs:29-34` |
| 1.1d | The event's `source` must match the SourceFilter at a path boundary, otherwise 403 | V/T | `AltinnHooksEndpoints.cs:36-51, 63-71`; tests `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service.Tests/src/Altinn/AltinnHooksEndpointsTests.cs:165-212` |
| 1.1e | Processing is serialized by a `SemaphoreSlim(1,1)`, then `AltinnService.WebhookTriggered(payload)` runs | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service/src/Altinn/Service/AltinnHookService.cs:18, 38, 58-62` (file lines; the class starts at line 1) |
| 1.1f | At startup MU checks for the Events subscription and creates it if it is missing (`AutoSubscribeEnabled`): `GET/POST /events/api/v1/subscriptions` | V | `AltinnHookService.cs:72-159`; `src/Altinn/Api/IAltinnEventApi.cs:10-15`; hosted service `AltinnModuleRegistration.cs:40` |
| 1.1g | The webhook `source` (`https://buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{partyId}/{guid}`) is split into partyId and guid; then `GET /storage/api/v1/instances/{partyId}/{guid}` | V | `src/Altinn/Service/AltinnService.cs:325-326, 355-365`; `src/Altinn/Api/IAltinnStorageApi.cs:16-20` |
| 1.1h | **Polling also runs.** `AltinnPollingService` (BackgroundService) calls `HentNyeMeldinger` once at startup, then every `Altinn:Polling:Schedule` (default `24:00:00`, `Enabled: true`) | V/T | `src/Altinn/Service/AltinnPollingService.cs:16-31`; `appsettings.json:25-28`; tests `Service.Tests/src/Altinn/AltinnPollingServiceTests.cs:75-93` |
| 1.1i | Polling query: `GET /storage/api/v1/instances?appId=<StorageAppId>&process.ended=gt:<today-LookbackDays>&process.isComplete=true&excludeConfirmedBy=buf` (lookback default 100 days) | V | `AltinnService.cs:51-52`; `IAltinnStorageApi.cs:8-14`; `appsettings.json:24` |
| 1.1j | Auth to Altinn: Maskinporten JWT grant (company certificate thumbprint) exchanged at `GET /authentication/api/v1/exchange/maskinporten`; token cached (TTL 30 s, buffer 5 s) | V | `AltinnModuleRegistration.cs:45-73`; `src/Altinn/Api/IAltinnTokenApiService.cs:12-13`; `appsettings.json:29-37` |

### 1.2 Retrieval, app id and data types

| # | Claim | Label | Evidence |
|---|---|---|---|
| 1.2a | Altinn app id `buf/skolenaervaer` (TT02: Platform `https://platform.tt02.altinn.no`, apps `https://buf.apps.tt02.altinn.no`). Base `appsettings.json` leaves it empty | V | `appsettings.Development.json:11-14`; `appsettings.json:20-23` |
| 1.2b | The MU schema catalogue links the type `barnevern.tiltak.skolenaervaer` to `"altinnApp": "skolenaervaer"` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.TestData/TestData/xsds/v3/bufdir.xsd.katalog.json:25-28` |
| 1.2c | **No Altinn `dataType` filter.** Only the instance's data elements whose content type contains `xml` are fetched; the PDF and anything else are skipped | V | `AltinnService.cs:123-124, 391-394` |
| 1.2d | Each XML element is fetched from `instanceData.SelfLinks.Platform`, trimmed to start at `storage/…`, via `GET /{**instanceDataUri}` | V | `AltinnService.cs:133-141, 162, 333-353`; `IAltinnStorageApi.cs:22-27` |
| 1.2e | **Attachments are not forwarded.** `OutboundMessage` is built with `MainFile` only; `Attachments` stays empty | V | `AltinnService.cs:256-265` |
| 1.2f | MU parses `Meldingshode`; unparsable → skipped (telemetry only). A duplicate `Meldingshode/Id` → skipped | V | `AltinnService.cs:185-219` |
| 1.2g | **MU XSD-validates Altinn XML** before forwarding (schema chosen by namespace); failure → status `ValidertMotXsdFeilet`, not forwarded | V/T | `AltinnService.cs:223-246`; `src/Skjemaverktoy/XmlValidatorClient.cs:12-14`; tests `Service.Tests/src/Altinn/AltinnXsdValidationTests.cs:183-210, 257-298` |
| 1.2h | After all of an instance's XML elements are handled (also when validation fails), MU confirms the download to Altinn: `POST {BufdirAppsUrl}/buf/{app}/instances/{instanceId}/complete` | V/T | `AltinnService.cs:144`; `src/Altinn/Service/AltinnAppService.cs:43-49`; `src/Altinn/Api/IAltinnAppApi.cs:23-27`; tests `AltinnXsdValidationTests.cs:175-181, 195` |
| 1.2i | Altinn-side namespaces MU has XSDs for: `…skolenaervaer.v2.0.0`, `.v2.0.0.flat`, `.v2.0.0.altinn`, `.v0.8.2.flat`, `.v0.8.2.altinn` (v3 catalogue). **No v2.0.1** | V | `TestData/xsds/v3/bufdir.xsd.oversikt.json:5-41`; files in `TestData/xsds/v3/xsd/` |
| 1.2j | Which namespace the live Altinn app actually puts in `MeldingstypeNmsp` | NV | No Altinn app source or real sample in these packages; [M2LB (7)] MM research lists it as an open assumption (`[M2LB (7)]/MeldingsutvekslerReceiver/specs/001-meldingsutveksler-receiver/research.md:263-269`) |

### 1.3 What is stored, and encryption

| # | Claim | Label | Evidence |
|---|---|---|---|
| 1.3a | SQL (EF Core) tables: `Fagsystem`, `Melding`, `OppfolgingAvMelding`, `Kommunikasjonspart`, `Adresseboka` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service.DbContext/MeldingsutvekslerDbContext.cs:8-13` |
| 1.3b | `Melding` row: `MeldingId` (Guid = `Meldingshode/Id`), `Status`, `MeldingstypeNmsp`, `Meldingstype`, `SendtTidspunkt`, `Avsender/Mottaker`, `AvsendersRef`, `ProcessedDate` | V | `Service.DbContext/Models/Melding.cs:6-36` |
| 1.3c | Status flow for Altinn: history row added → `ValidertMotXsd` (or `ValidertMotXsdFeilet`=40) → `LagtIInnboks` (4) after the Service Bus send | V/T | `AltinnService.cs:221, 232, 248, 267`; enum `Models/Enums/Status.cs:5-26`; test `AltinnXsdValidationTests.cs:191` |
| 1.3d | Blob: the XML is uploaded **encrypted** to the resolved channel's `OutboundFromSystem.BlobContainerName`, blob name `{AltinnDataId}.xml` (not the MU message id) | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.BusIntegration/Service/ServiceBusSendService.cs:26-28, 166-188`; `AltinnService.cs:258-262` |
| 1.3e | Encryption: random AES-256 key + IV per file; AES key wrapped with the channel's RSA public key (OAEP-SHA256); wrapped key and IV in blob metadata `birkKey` and `iv` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Common/Encryption/EncryptionService.cs:8-27`; `Common/Storage/BlobStorageService.cs:39-66` |
| 1.3f | RSA keys per channel: Azure Key Vault secrets (`PrivateKeySecretName`/`PublicKeySecretName`, PEM) or local PEM files | V | `Common/Encryption/Services/AzureEncryptionKeysService.cs:24-47`; `appsettings.json:63-68`; `appsettings.LocalDocker.json:2-32` |
| 1.3g | The AES mode is the .NET `Aes.Create()` default (CBC/PKCS7), not set explicitly | I | `EncryptionService.cs:15-19` |
| 1.3h | MM's spec notes MU does not delete Altinn content (lookup by MU id, stored under the Altinn data id) | I (consistent with 1.3d) | `[M2LB (7)]/MeldingsutvekslerReceiver/specs/001-meldingsutveksler-receiver/spec.md:371-372` |

---

## 2. MU → MM transport

| # | Claim | Label | Evidence |
|---|---|---|---|
| 2a | Azure Service Bus **queues** (not topics or subscriptions), via Wolverine: `asb://queue/{PublishToQueue}` | V | `ServiceBusSendService.cs:144-157`; `BusIntegration/ApplicationBuilderExtensions.cs:74-93, 172-179` |
| 2b | Channel routing: the message namespace is matched to `Channels[].Domains` (equal, or prefix followed by `.`). MU sends to the channel's `OutboundFromSystem.QueueName` and listens on `InboundToSystem.QueueName` | V | `Common/Configuration/MessageChannelResolver.cs:20, 33-64`; `ServiceBusSendService.cs:52`; `ApplicationBuilderExtensions.cs:92` |
| 2c | MU dev config: channel "Tiltak Admin", domain `https://bufdir.no/barnevern.tiltak`, MU→fagsystem queue `tiltak_admin` / container `tiltak-admin`, fagsystem→MU `tiltak_admin_mu` / `tiltak-admin-mu`. No M2LB channel in MU's checked-in config | V | `appsettings.Development.json:66-85` |
| 2d | MM config (the mirror): domain `https://bufdir.no/barnevern.tiltak.skolenaervaer`, `InboundToSystem` queue/container `<channel>`, `OutboundFromSystem` `<channel>_mu` / `<channel>-mu`. Locally the channel is `m2lb_skolenaervaer` (containers `m2lb-skolenaervaer`, `m2lb-skolenaervaer-mu`) | V | `[M2LB (7)]/MeldingsutvekslerReceiver/src/M2LB.MeldingsutvekslerReceiver.Host/appsettings.json:46-65`; `[M2LB (7)]/Aspire/Aspire/AppHost.cs:48-55` |
| 2e | The deployed M2LB channel name and queue names | NV | placeholders `<channel>` only |
| 2f | **Reference message, payload not inline.** Envelope `ServiceBusEnvelope{ BufdirMelding | AckMelding | PingPongMelding | AltinnPrefillMelding | AltinnPrefillResponseMelding, PublishToQueue }`; `BufdirMelding = ServiceBusMessage{ MainFilePath, Attachments[]{FileName, FilePath}, MessageOrigin (FiksIo|Altinn), MessageTypeNamespace }` | V | `Common/KoModels/ServiceBusEnvelope.cs:6-26`; `KoModels/MessageTypes/ServiceBusMessage.cs:5-23`; `KoModels/MeldingOrigin.cs:3-7` |
| 2g | MM fetches the payload **from blob storage** (MU's account, `MessagingChannels:BlobUrl`), not over HTTP to MU: `InboundToSystem.BlobContainerName` + `GetAndDecryptFileAsync(MainFilePath)` | V | `[M2LB (7)]/MeldingsutvekslerReceiver/src/M2LB.MeldingsutvekslerReceiver.Meldingsutveksler/MuSeam.cs:24-29`; `[Tiltak 2 (1)]/…/Common/Storage/BlobStorageService.cs:103-148` |
| 2h | Decryption with M2LB's channel private key from MU's Key Vault (`AzureEncryptionKeyConfiguration:KeyVaultName = <mu-keyvault>`); fetch errors map to `MissingContent`, `MissingEncryptionData`, `DecryptionFailed` (retryable); blob service errors are infrastructure (held) | V | `[M2LB (7)]/…/Host/appsettings.json:57-69`; `MuSeam.cs:31-54`; `Core/Handling/MessageHandler.cs:89-106` |
| 2i | MM reaches MU through the FagKlient NuGet (`Buf.Meldingsutveksler.BusIntegration/Common/FagKlient`), `isFagKlient: true` | V | `[M2LB (7)]/…/Meldingsutveksler/MuRegistration.cs:51-54`; `…Meldingsutveksler.csproj:24-26` |
| 2j | Correlation: the envelope has **no message id**. The MU message id is `Meldingshode/Id` inside the decrypted XML; MM logs it as `CorrelationId`, sends it as `X-Correlation-Id`, and puts it in the body as `meldingId`. The queue `MessageId` is kept separately as `QueueMessageId` | V | `MessageHandler.cs:118-123, 161, 224-226`; `[M2LB (7)]/…/Skolenaervaer/SkoleServiceClient.cs:61, 69`; `Meldingsutveksler/InboundMessageHandler.cs:33` |

---

## 3. MM processing ([M2LB (7)] reference; [M2LB (5)] differences in §5)

### 3.1 Pipeline and XML validation

| # | Claim | Label | Evidence |
|---|---|---|---|
| 3.1a | Steps: registry lookup by namespace prefix → pause check → fetch and decrypt → parse (DTD prohibited, at most 10M characters) → protocol-message check → exact-version check → XSD validate and non-finite check → map → POST to owner → classify → receipt | V | `[M2LB (7)]/…/Core/Handling/MessageHandler.cs:77-174` |
| 3.1b | Registered namespace ([M2LB (7)]): `https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1.flat`, Version `2.0.1`, schema key `skolenaervaer-v2.0.1-flat-m2lb`. Prefix `https://bufdir.no/barnevern.tiltak.skolenaervaer` | V/T | `[M2LB (7)]/…/Host/appsettings.json:14-23`; tests `tests/…Unit/Registry/MessageTypeRegistryTests.cs:15, 42-43` |
| 3.1c | XSD file: `bufdir.barnevern.tiltak.skolenaervaer.v2.0.1.flat.m2lb.xsd` (embedded). It is the published `…v2.0.1.flat.xsd` with the `TypeFravaer` enumeration (1,2,3,4,5,9) replaced by `maxLength 16` | V/T | `src/M2LB.MeldingsutvekslerReceiver.Skolenaervaer/SkolenaervaerMessageType.cs:14-20`; diff of the two files (`Skjema/…flat.xsd:342-347` vs `…flat.m2lb.xsd:342`); `Skjema/PROVENANCE.md`; test `Unit/Skolenaervaer/SkolenaervaerSchemaTests.cs` |
| 3.1d | XSD targetNamespace `https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1.flat`, `version="2.0.1"`, root `Skolenaervaer`; self-contained | V | `Skjema/bufdir.barnevern.tiltak.skolenaervaer.v2.0.1.flat.xsd:2, 351` |
| 3.1e | Any other version or representation (e.g. `.v2.0.0.flat`, `.v2.0.0.altinn`) → `UnsupportedVersion` rejection; never validated against another schema | V/T | `MessageHandler.cs:133-139`; `Core/Registry/MessageTypeRegistry.cs:21-22`; `NamespaceVersion.cs:17-30`; test `Unit/Registry/NamespaceVersionTests.cs:31-32` |
| 3.1f | Validation uses `System.Xml.Schema` (`XDocument.Validate`), not MU's SkjemaVerktoy; the root must end up `Valid` | V | `MessageHandler.cs:191-208`; research `specs/…/research.md:250-256` |

### 3.2 XML → JSON mapping ([M2LB (7)], v2.0.1 flat)

Mapper `[M2LB (7)]/…/Skolenaervaer/SkolenaervaerMapper.cs:14-67`; DTO `SkolenaervaerReport.cs:11-49` (camelCase Web defaults, nulls written, strict numbers). All XML elements are in the document's namespace `ns`.

| XML (v2.0.1 flat) | JSON property | Transformation | Evidence | Test |
|---|---|---|---|---|
| `Meldingshode/Id` | `meldingId` | text | Mapper:22; DTO:12 | T `Unit/Skolenaervaer/SkolenaervaerMapperTests.cs:12` |
| `Meldingshode/SendtTidspunkt` | `sendtTidspunkt` | text, unchanged | Mapper:23; DTO:13 | T `Integration/US1/DeliverValidMessageTests.cs:42` |
| (registered version) | `skjemaversjon` | `"2.0.1"` from config, not from the XML | Mapper:24; DTO:14 | T `SkolenaervaerMapperTests.cs:18` |
| `Klient/Identifikator/Fodselsnummer` | `barn: {type:"Foedselsnummer", verdi}` | first child of Identifikator | Mapper:25, 36-42; DTO:15, 26 | T `SkolenaervaerMapperTests.cs:36-37` |
| `Klient/Identifikator/DUFnummer` | `barn: {type:"DufNummer", verdi}` | — | Mapper:40 | T `SkolenaervaerMapperTests.cs:43-46` |
| (no Fodselsnummer/DUFnummer, e.g. `PseudonymId`) | `barn: null` | — | Mapper:41 | I |
| `Meldingshode/Avsender/Organisasjon/Organisasjonsnummer` | `rapportoer: {type:"Organisasjon", identitet}` | — | Mapper:26, 44-47; DTO:16, 31 | T `SkolenaervaerMapperTests.cs:61` |
| `Meldingshode/Avsender/Person/Fodselsnummer` | `rapportoer: {type:"Person", identitet}` | — | Mapper:48 | T `SkolenaervaerMapperTests.cs:70-73` |
| `Periode/StartDato`, `Periode/SluttDato` | `periode.startDato`, `periode.sluttDato` | text | Mapper:27; DTO:17, 36 | T `SkolenaervaerMapperTests.cs:83` |
| `AntallTimerEtterPlanen` (**root child** in v2.0.1) | `antallTimerEtterPlanen` | double, JSON number | Mapper:28, 62-63; DTO:18 | T `SkolenaervaerMapperTests.cs:97-98` |
| `AntallTimerFaktiskNaervaer` (root child) | `antallTimerFaktiskNaervaer` | double | Mapper:29; DTO:19 | T `SkolenaervaerMapperTests.cs:99` |
| `FravaerRegistrering/Fravaer/TypeFravaer` | `fravaer[].kode` | text (`01` kept) | Mapper:30-31; DTO:20, 38 | T `DeliverValidMessageTests.cs:52` |
| `FravaerRegistrering/Fravaer/TimerFravaer` | `fravaer[].antallTimer` | double | Mapper:31 | T `SkolenaervaerMapperTests.cs:100, 109` |
| `Meldingshode/OppfolgingAvMelding/MeldingsForbindelse` | `meldingsforbindelse.type` | text; the whole object is null when absent | Mapper:33, 52-58; DTO:21, 40 | I |
| `…/OppfolgingAvMelding/StartMeldingId` | `meldingsforbindelse.startMeldingId` | — | Mapper:57 | I |
| `…/OppfolgingAvMelding/MeldingId` | `meldingsforbindelse.meldingId` | optional | Mapper:58 | I |
| Dropped: `PseudonymId`, `KontaktInfoAvsender`, sender name, `Mottaker`, `FagsystemAvsender`, `AvsendersRef`, `Domene`, `MeldingstypeNmsp`, `MottakersRef` | — | never read | Mapper:9-10 | — |

v2.0.1 vs v2.0.0 structure: in v2.0.1 the hours and `FravaerRegistrering` sit directly under `SkolenaervaerType` and are `xs:nonNegativeInteger` (`[M2LB (7)]/…/Skjema/…v2.0.1.flat.xsd:299, 324-332`). In v2.0.0 they are inside `SkolenaervaerRegistrering` and are `xs:double` (`[Tiltak 2 (1)]/…/xsd/…v2.0.0.flat.xsd:302, 329-340`). **V**

### 3.3 Outbound call to SkoleService

| # | Claim | Label | Evidence |
|---|---|---|---|
| 3.3a | **`POST /api/skole/v1/skolenaervaer/reports`** ([M2LB (7)], [M2LB (3)]). Readiness `GET /health/ready` | V/T | `[M2LB (7)]/…/Host/appsettings.json:25-30`; `SkoleServiceClient.cs:69, 89`; test `Integration/Infrastructure/ReceiverHost.cs:31`; server `[M2LB (7)]/SkoleService/src/M2LB.SkoleService.Api/Rest/ReportIntake/ReportIntakeEndpoint.cs:17` |
| 3.3b | Body `application/json` = `SkolenaervaerReport` (see 3.2) | V | `SkoleServiceClient.cs:65-66` |
| 3.3c | Header `X-Correlation-Id: <Meldingshode/Id>` | V/T | `SkoleServiceClient.cs:34, 46, 61`; `ReceiverHost.cs:189` |
| 3.3d | Auth (deployed): Entra app token via `IDownstreamApi.CallApiForAppAsync`, user-assigned managed identity, app role `Skolenaervaer.Submit` ([M2LB (7)]). Local and tests: no token (`RequestAppToken: false`) | V | `SkolenaervaerRegistration.cs:39-51`; `SkoleServiceClient.cs:21-36`; `Host/appsettings.json:117-126`; server role `[M2LB (7)]/SkoleService/src/M2LB.SkoleService.Api/Auth/Authentication.cs:16` |
| 3.3e | Resilience: attempt timeout 10 s, total 30 s, 1 retry on connection failure, 5xx or 408, circuit breaker | V | `SkolenaervaerRegistration.cs:26-37` |
| 3.3f | Response classification: 200 with `outcome:"Received"` → Complete; 422 with a registered `reasonCode` → Rejected (complete); 409 → temporary, does not count toward pause; 408/429/5xx → temporary, counts; anything else → retryable `InvalidOwnerResponse` | V | `Core/Handling/ResponseClassifier.cs:30-42`; permanent codes `Host/appsettings.json:35-42` |
| 3.3g | Contract versions: intake OpenAPI `info.version 1.0.0`, server base `/api/skole/v1`. "v2.0.0 / v2.0.1" are **XSD and `skjemaversjon` versions**, not API versions | V | `[M2LB (7)]/SkoleService/specs/001-skoleservice/contracts/rapportinntak.openapi.yaml:4, 11-16, 69-79` |

---

## 4. Acknowledgment MM → MU (and to Altinn)

| # | Claim | Label | Evidence |
|---|---|---|---|
| 4a | MM sends an **application receipt** (`Kvittering`, ns `https://bufdir.no/Applikasjonskvittering.v2`) as an ordinary MU message: `{receiptId}.xml` encrypted into the outbound container, envelope on the outbound queue (`<channel>_mu`), `MessageTypeNamespace` = the **original** message's namespace | V | `[M2LB (7)]/…/Meldingsutveksler/ReceiptSender.cs:12-35`; `Core/Receipt/ReceiptBuilder.cs:18, 54-72` |
| 4b | Receipt content: `OppfolgingAvMelding{MeldingsForbindelse=ApplikasjonsKvittering, StartMeldingId, MeldingId=original id}`, `Mottaker` = original sender, no `Resultat`; `Informasjon` = reason code only when `Negative` is on. Receipt id = UUIDv5(original id + ":ApplikasjonsKvittering") | V | `ReceiptBuilder.cs:13-14, 31-36, 54-72` |
| 4c | Policy [M2LB (7)]: `Positive: true, Negative: false`, so a **rejected message also gets the positive receipt** (no reason code) | V | `Host/appsettings.json:31-34`; `MessageHandler.cs:238-244` |
| 4d | A failed receipt send is alerted and never rethrown; the message still completes | V/T | `MessageHandler.cs:251-260`; test `Integration/US4/ReceiptTests.cs:13` |
| 4e | MU side: an Applikasjonskvittering is XSD-validated, needs `OppfolgingAvMelding`, sets the **original** `Melding` to `HentetAvFagsystem` (6), deletes the outbound files, forwards the receipt to **Fiks IO** (to `Mottaker.Organisasjon.Organisasjonsnummer`), then sends `AckMelding{MeldingId=receipt id, AckOnMeldingstype=original ns}` back to the channel and sets `Signalert` | V | `[Tiltak 2 (1)]/Buf.Meldingsutveksler.Service/src/ServiceHandler/InboundMessageHandler.cs:41-46, 97-104, 138-186` |
| 4f | For a person sender, `Mottaker.Organisasjon!` is null, so the Fiks IO forward fails after `HentetAvFagsystem` is set; the MM spec records this as a known gap | I (code) + V (doc) | `InboundMessageHandler.cs:142-161`; `[M2LB (7)]/…/specs/…/spec.md:690` |
| 4g | MM handles MU's `AckMelding` as `Protocol AckMelding` (recorded, completed, never forwarded) | V/T | `[M2LB (7)]/…/Meldingsutveksler/AckMessageHandler.cs:16-27`; `MessageHandler.cs:71-75`; test `ReceiptTests.cs:46` |
| 4h | **There is no MM → Altinn acknowledgment.** Altinn is told only by MU's `…/complete` call at download time, before and independent of MM (§1.2h) | V | `AltinnService.cs:144`; `AltinnAppService.cs:43-49` |
| 4i | MU's own ack handler (fagsystem → MU AckMelding) only logs | V | `[Tiltak 2 (1)]/…/Service/src/ServiceHandler/AckMessageHandler.cs:8-12` |

---

## 5. MM differences across [M2LB (7)] / [M2LB (5)] / [M2LB (3)] ([M2LB (6)], [M2LB (4)]: specs only)

| Aspect | [M2LB (7)] (M2LB 7) | [M2LB (3)] (M2LB 3) | [M2LB (5)] (M2LB 5) | [M2LB (6)] (M2LB 6) / [M2LB (4)] (M2LB 4) |
|---|---|---|---|---|
| Folder / project | `MeldingsutvekslerReceiver` / `M2LB.MeldingsutvekslerReceiver.*` | same | `Meldingsutvekslermottak` / `M2LB.Meldingsutvekslermottak.*` (Norwegian identifiers: `Meldingsbehandler`, `Kvitteringsbygger`, …) | `Meldingsutvekslermottak`, **no src** (specs, plan, tasks only) |
| Registered ns | `…skolenaervaer.v2.0.1.flat` (`Host/appsettings.json:19`) | same | `…skolenaervaer.v2.0.0.flat` (`[M2LB (5)]/…/Host/appsettings.json:19`) | specs reference `.v2.0.0.flat` |
| XSD | `…v2.0.1.flat(.m2lb).xsd`; hours under the root, `nonNegativeInteger` | same | `…v2.0.0.flat(.m2lb).xsd`; the source copy is **byte-identical** to MU's `[Tiltak 2 (1)]/…/xsd/…v2.0.0.flat.xsd` (cmp) | — |
| Mapper hours path | `root/AntallTimer*` (Mapper:28-30) | same | `root/SkolenaervaerRegistrering/AntallTimer*` (`[M2LB (5)]/…/SkolenaervaerMapper.cs:19, 29-31`) | — |
| Intake route | `/api/skole/v1/skolenaervaer/reports` | same | **`/api/skole/v1/skolenaervaer/rapporter`** (`[M2LB (5)]/…/Host/appsettings.json:28`) | spec `contracts/skoletjenesten-mapping.md:5`: `/rapporter` |
| Owner name | `SkoleService` / API `SkoleServiceApi` | same | `Skoletjenesten` / `SkoletjenestenApi` | — |
| JSON period fields | `startDato`/`sluttDato` (explicit `JsonPropertyName`) | same | `startTidspunkt`/`sluttTidspunkt` (record `Periode(StartTidspunkt, SluttTidspunkt)`, camelCase) `[M2LB (5)]/…/SkolenaervaerRapport.cs:35` | — |
| Permanent reason codes | English (`MISSING_IDENTIFIER`, …) | same | Norwegian (`MANGLER_IDENTIFIKATOR`, …) `[M2LB (5)]/…/appsettings.json:35-42` | — |
| App role | `Skolenaervaer.Submit` | per CHANGELOG same | `Skole.Rapportinntak` (`[M2LB (5)]/…/SkoletjenestenKlient.cs:22-23`) | — |
| Dead-letter container / account | `dead-letters`, `stm2lbmumottak<env>nwe001` | same | `mu-dodbrev`, `stm2lbmottak<env>nwe001` | — |
| FagKlient version | `0.2.0` ("published to Smidig") `Directory.Build.props:8` | `0.2.0` but comment says "patched … branch build … MuLokal" `:9` | `FagKlientVersjon 2.0.0`, "patched … MuLokal" `[M2LB (5)]/…/Directory.Build.props:9` | fagklient-endringer.md: [M2LB (6)] "Planned", [M2LB (4)] "Implemented on PR branch 2026-10-12" |
| [M2LB (7)] vs [M2LB (3)] code delta | — | only the meter name (`…Receiver.Intake` vs `…Receiver`), package versions (M2LB.Common 3.1.1 vs 2.3.0, Blobs 12.30.1 vs 12.29.2), extra `TelemetryNamesTests.cs` in [M2LB (7)] | — | [M2LB (6)] vs [M2LB (4)]: only `fagklient-endringer.md`, `research.md`, `tasks.md` differ |

The deployment order cannot be inferred from these sources (**NV**), and none is asserted here.

---

## 6. [Tiltak 2 (1)] MU components and protocol

| Component | Role | Evidence |
|---|---|---|
| `Buf.Meldingsutveksler.Service` | server: Altinn webhook and polling, Fiks IO, history DB, validation, routing | `[Tiltak 2 (1)]/README.md:12`; `src/Altinn/*`, `src/FiksIO/*` |
| `Buf.Meldingsutveksler.Service.DbContext` | EF Core model and migrations (`20250926_Init` … `20260227_AddresseToAdresse`) | `Service.DbContext/Migrations/` |
| `Buf.Meldingsutveksler.BusIntegration` | Wolverine + Azure Service Bus: queue listeners, `ServiceBusSendService`, `DeadLetterHandler`, retry options | `BusIntegration/*`; `README.md:16` |
| `Buf.Meldingsutveksler.Common` | envelope models, RSA/AES encryption, blob storage, channel resolver, `MuException` | `Common/*` |
| `Buf.Meldingsutveksler.FagKlient` | NuGet for fagsystems: `AddFagKlient` → `SetupMeldingsutvekslerKlient`, no-op prefill handlers | `FagKlient/FagKlientRegistration.cs:13-20` |
| `Buf.Meldingsutveksler.SkjemaVerktoy` (v1.1.0) | XSD register and validator, kodelister, tekster, `Meldingsprotokoll` models | `SkjemaVerktoy/*`; csproj `<Version>1.1.0` |
| `Buf.Meldingsutveksler.TestData` | XSD, kodeliste and tekst catalogue (v3) | `TestData/TestData/xsds/v3/*` |
| Demo.* (DebugPanel, FagsystemKlientEksempel, KommuneKlient) | examples and tools | dirs |

**Meldingsprotokoll** (`SkjemaVerktoy/Meldingsprotokoll`): `Meldingshode` (Id, MeldingstypeNmsp, FagsystemAvsender, SendtTidspunkt, Avsender/Mottaker as Organisasjon|Person, OppfolgingAvMelding{MeldingsForbindelse, StartMeldingId, MeldingId}), `Applikasjonskvittering/Kvittering`, `Saksbehandlerkvittering/Kvittering`, `Feilmelding`. `MeldingsForbindelseType` = Ny, Duplikat, Oppdatering, Tillegg, Trukket, Slettet, ApplikasjonsKvittering, SaksbehandlerKvittering, Svar, Feil (`Enums/MeldingsForbindelseType.cs:5-38`). **V**

**Meldingstyper**

- Protocol namespaces in code (`Models/MeldingsprotokollNamespaces.cs:6-54`): Applikasjonskvittering, BarnevernGenerelt, BarnevernHenvisning{Familierad, Fosterhjem, Hjelpetiltak, Institusjon, SenterForeldreBarn, TverrfagligHelsekartlegging}, BarnevernHenvisning, FeilMelding, Generelt, Melding, Saksbehandlerkvittering, Vedlegg, TestMelding, TrukketMelding. Skolenærvær is **not** among them; it is routed by channel domain. **V**
- v3 catalogue (`TestData/xsds/v3/bufdir.xsd.katalog.json:10-318`): barnevern.tiltak.skoleopplysninger, **barnevern.tiltak.skolenaervaer (altinnApp `skolenaervaer`)**, the 6 barnevern.henvisning.*, applikasjonskvittering, feilmelding, saksbehandlerkvittering, testmelding, melding.trukket, vedleggmelding, plus libraries. **V**
- MU→fagsystem inbound handling accepts only Applikasjonskvittering, Saksbehandlerkvittering and Feilmelding (`Service/src/ServiceHandler/InboundMessageHandler.cs:41-46`). **V**
- Fiks IO types: ping, pong, feilmelding, applikasjonskvittering, saksbehandlerkvittering, henvisning (`src/FiksIO/Utils/FiksIoMeldingTyper.cs:5-10`). **V**
- Envelope kinds: BufdirMelding, AckMelding, PingPongMelding, AltinnPrefillMelding, AltinnPrefillResponseMelding (`ServiceBusEnvelope.cs:8-25`). **V**

---

## 7. Checks against the prior claim set

Prior: *"Altinn→MU via webhook then XML retrieval; MU→MM Service Bus reference + encrypted payload retrieval; MM validates XML, maps to JSON, POSTs /api/skole/v1/skolenaervaer/rapporter (HTTP JSON); MM→MU application acknowledgment"*

| Prior element | Verdict | Detail |
|---|---|---|
| Altinn→MU via webhook then XML retrieval | **Confirmed, incomplete** | Webhook (Events subscription `app.instance.process.completed`) + Storage GET of XML data, **plus** a 24-hour polling fallback that runs at startup. Only XML data is fetched; attachments and PDF are dropped. MU XSD-validates before forwarding ([Tiltak 2 (1)] code), contradicting MM spec's "MU does not validate Altinn payloads today" (`[M2LB (7)]/…/spec.md:23`, written against MU `513c016`) |
| MU→MM Service Bus reference + encrypted payload retrieval | **Confirmed** | Queue (not topic) envelope with `MainFilePath`. MM reads MU's **blob storage** directly (not HTTP to MU) and decrypts with RSA-OAEP-SHA256-wrapped AES-256 via FagKlient |
| MM validates XML, maps to JSON | **Confirmed** | Against M2LB's relaxed embedded XSD (v2.0.1 flat in [M2LB (7)]/[M2LB (3)], v2.0.0 flat in [M2LB (5)]) |
| POST `/api/skole/v1/skolenaervaer/rapporter` | **Package-dependent** | Only [M2LB (5)] (and the [M2LB (6)]/[M2LB (4)] specs) use `/rapporter`. **[M2LB (7)] and [M2LB (3)] use `/api/skole/v1/skolenaervaer/reports`**, as do [M2LB (7)]'s and [M2LB (3)]'s SkoleService. The JSON shape also differs (`startDato` vs `startTidspunkt`) |
| MM→MU application acknowledgment | **Confirmed, qualified** | An Applikasjonskvittering.v2 sent as an ordinary MU message, routed by the original namespace. Positive-only by policy; rejected messages also get the positive one. MU then marks the original `HentetAvFagsystem`, tries a Fiks IO forward (fails for person senders), and replies with `AckMelding`. MM never acknowledges to Altinn; Altinn gets MU's `/complete` at download time |

### Further risks found (not in the prior set)

1. **Version or representation mismatch between MU and MM: I, high risk.** MU forwards with `MessageTypeNamespace = Meldingshode/MeldingstypeNmsp` (`AltinnService.cs:264`). MU knows only v2.0.0 / v0.8.2 (`.flat` / `.altinn`), with no v2.0.1. [M2LB (7)]/[M2LB (3)] MM accepts only `.v2.0.1.flat`. Anything else is completed as `UnsupportedVersion` (with a positive receipt) and not delivered. [M2LB (7)] research lists this as an unverified assumption (`research.md:263-269`, `:544`).
2. **FagKlient API drift: V.** [M2LB (7)]/[M2LB (5)]/[M2LB (3)] MM implement `OnMessageReceived(…, InboundMessageContext, …)`, `ListenToPrefill` and `Receiver*Exception`, none of which exist in [Tiltak 2 (1)]'s FagKlient source (`[Tiltak 2 (1)]/…/Common/KlientHandlers/IInboundMessageHandler.cs:5-8`; grep finds no `InboundMessageContext` in [Tiltak 2 (1)]). [Tiltak 2 (1)] is therefore not the FagKlient build that MM compiles against ([Tiltak 2 (1)] packages are `1.0.0`; MM pins `0.2.0` in [M2LB (7)]/[M2LB (3)] and `2.0.0` in [M2LB (5)]).
3. **MU channel config: V/NV.** MU's dev `Tiltak Admin` domain `https://bufdir.no/barnevern.tiltak` would also capture skolenærvær. Whether a separate M2LB channel (`…barnevern.tiltak.skolenaervaer`) exists in deployed MU config cannot be verified (NV). The resolver picks the most specific prefix ("sorted by domain length so the most specific prefix wins", `MessageChannelResolver.cs:6-8`), so if both channels are configured, skolenærvær goes to the M2LB channel. (V, from the class doc comment)


---

# Oracle: Skolenærvær second half (SkoleService / Skole, Person, Tjeneste, Utdanning, Period lock, Bufdata)

Read-only source audit. Root = the extracted package root. All paths are relative to that root. Nothing was built or run.

Labels: **V** = verified in source (file:line) · **T** = a test asserts it (test cited; tests were not run) · **S** = only in specs/docs · **I** = inferred · **NV** = not verifiable from these packages.

## 0. What each package contains (the caller's package descriptions were partly wrong)

| Pkg | Zip | Intake service | SkoleAdapter | MU receiver | Person / Tjeneste provider of SkoleService's dependency routes | Aspire skole wiring |
|---|---|---|---|---|---|---|
| [M2LB (7)] | M2LB (7) | `SkoleService/` code + tests (English names) | code + tests | `MeldingsutvekslerReceiver/` code | **Implemented**: `PersonService` + `TjenesteService` | yes (skole + eventhubs-skole + delivery containers) |
| [M2LB (3)] | M2LB (3) | `SkoleService/` code + tests (English names, Norwegian dependency wire) | code + tests | `MeldingsutvekslerReceiver/` code | **Not implemented**: `Person/`, `Tjeneste/` have no matching routes (grep found none) | yes (same as [M2LB (7)]) |
| [M2LB (5)] | M2LB (5) | `Skole/` code + tests (Norwegian names) | **specs only** (11 files) | `Meldingsutvekslermottak/` code (96 .cs) | Not implemented | Skole + delivery containers, no eventhubs-skole |
| [M2LB (6)] | M2LB (6) | `Skole/` code + tests, the same as [M2LB (5)] except for user-secrets lines in Program.cs (`diff -rq` shows only that file) | **specs only** | `Meldingsutvekslermottak/` **specs only** (0 .cs) | Not implemented | Skole + delivery containers |
| [M2LB (4)] | M2LB (4) | `Skole/` **specs only** (18 files; tasks.md 0 done / 159 open) | specs only | specs only (0 .cs) | Not implemented | no skole in AppHost |

Notes:
- The caller described [M2LB (6)] as having "no SkoleAdapter project" and [M2LB (4)] as having "no Skole". In fact [M2LB (6)] has SkoleAdapter specs but no adapter code, and [M2LB (4)] has the `Skole/` folder with specs only.
- Tasks: [M2LB (7)] and [M2LB (3)] SkoleService have 173 done and 2 open. [M2LB (6)] and [M2LB (5)] Skole have 90 done and 69 open. The [M2LB (7)] and [M2LB (3)] SkoleAdapter have 98 done and 7 open; the [M2LB (6)] SkoleAdapter has 2 done and 91 open. (Counts from `^- \[[xX ]\]` in each tasks.md.)
- Project renames, all **V** from the directory listings: `Skole`→`SkoleService` and `Meldingsutvekslermottak`→`MeldingsutvekslerReceiver` (in [M2LB (3)] and [M2LB (7)]); `Person`→`PersonService`, `Tjeneste`→`TjenesteService`, `Hendelse`→`HendelseService`, `Autorisasjon`→`AutorisasjonService` and `Revisjon`→`RevisjonService` (in [M2LB (7)] only). The spec folder `001-skoletjenesten` became `001-skoleservice`. The code identifiers also went from Norwegian to English (for example `Rapportinntaksbehandler`→`ReportIntakeHandler`).
- [M2LB (7)] and [M2LB (3)] SkoleService differ mainly in the dependency wire names (section 2) and in a few messaging and contract files. Only [M2LB (7)] has `RejectUnhandledMessage.cs`, `SkoleOperations.cs`, `EventEnvelopes.cs`, the unit `Contracts/` tests and `Telemetry/` tests.

---

## 1. Report intake (Skolenærvær)

### 1a. [M2LB (7)] / [M2LB (3)] SkoleService

| Claim | Label | Evidence |
|---|---|---|
| Route `POST /api/skole/v1/skolenaervaer/reports` | V | `[M2LB (7)]/SkoleService/src/M2LB.SkoleService.Api/Rest/ReportIntake/ReportIntakeEndpoint.cs:17` ([M2LB (3)] identical apart from line 18) |
| Auth: app role `Skolenaervaer.Submit` | V | `…Api/Auth/Authentication.cs:16,38`; endpoint `.RequireAuthorization(Authentication.ReportIntake)` `ReportIntakeEndpoint.cs:28` |
| `X-Correlation-Id` header required and must be a UUID (the MU message id), else 400 | V / T | `ReportIntakeEndpoint.cs:21-22`; T `tests/M2LB.SkoleService.Integration/ReportIntake/ReportIntakeContractTests.cs:174 X_Correlation_Id_is_required` |
| JSON DTO (wire names pinned to the MU/Altinn XSD): `meldingId`(uuid), `sendtTidspunkt`, `skjemaversjon`, `barn{type,verdi}`(nullable), `rapportoer{type,identitet}`, `periode{startDato,sluttDato}`, `antallTimerEtterPlanen`, `antallTimerFaktiskNaervaer`, `fravaer[{kode,antallTimer}]`, `meldingsforbindelse{type,startMeldingId,meldingId?}`(nullable) | V | `…Rest/ReportIntake/ReportRequest.cs:16-77` |
| Strict JSON (`additionalProperties:false`), maxLengths (skjemaversjon 16, barn.verdi 12, rapportoer.identitet 11, kode 16), finite numbers, `xs:dateTime` for the send time and `xs:date` for the period dates. A breach is `ContractBreach` (400) | V / T | `ReportRequest.cs:83-98`; T `ReportIntakeContractTests.cs:96 A_contract_violation_answers_400`, `:140 A_value_one_character_over_its_maxLength_answers_400_and_at_maxLength_200` |
| Schema version **2.0.1**. The absence-code list is keyed by version: `"2.0.1": ["1","2","3","4","5","9"]` | V | `…Api/appsettings.json:18-20`; T fixture `tests/…Integration/ReportIntake/ReportIntakeHelper.cs:19` |
| v2.0.1 replaced v2.0.0 on 2026-10-14 (DEC-126); period fields changed from `startTidspunkt/sluttTidspunkt` to `startDato/sluttDato` | S | `[M2LB (7)]/SkoleService/autodoc/CHANGELOG.md:115-118`; `specs/001-skoleservice/spec.md:1016-1017`; `.specify/memory/constitution.md:32-35` |
| Spec OpenAPI: `contracts/rapportinntak.openapi.yaml` (info.version 1.0.0, server `/api/skole/v1`, path `/skolenaervaer/reports`, responses 200/400/401/403/409/422/503) | S | `[M2LB (7)]/SkoleService/specs/001-skoleservice/contracts/rapportinntak.openapi.yaml:4,12,16,32-54`. Generated copy: `[M2LB (7)]/SkoleService/autodoc/openapi.yaml:56` |
| Validity rules, in this order: `MISSING_IDENTIFIER`, `START_DATE_AFTER_END_DATE`, `INVALID_HOURS`, `OUTSIDE_VALIDITY_WINDOW`, `NOT_REPORTING_PERIOD`, `OUTSIDE_VALIDITY_WINDOW` (period not open), `UNEXPECTED_MESSAGE_RELATION`. `UNKNOWN_BARN` is created only at lock | V / T | `…Domain/Skolenaervaer/ValidityCheck.cs:31-60`; `ReasonCode.cs:12-21`. T `tests/M2LB.SkoleService.Unit/Skolenaervaer/ValidityCheckTests.cs` (e.g. `:77 START_DATE_AFTER_END_DATE_wins_over_INVALID_HOURS_when_both_apply`, `:150 NOT_REPORTING_PERIOD_for_july`, `:179 Rules_are_checked_in_the_published_order`) |
| The enum lists `INVALID_HOURS` before `START_DATE_AFTER_END_DATE`, but the checks run in the opposite order. A test pins the check order, so the code is intentional; the enum order is only cosmetic | V / I | `ReasonCode.cs:14-16` vs `ValidityCheck.cs:34-38` |
| Responses: 200 `{"outcome":"Received"}`; 422 problem+json with `reasonCode` and `correlationId`; 400 contract violation; 409 on a unique or concurrency collision (SQL 2601/2627); 503 on DB, Key Vault, timeout or Autorisasjon failure | V / T | `ReportIntakeHandler.cs:157-159`; `ErrorHandling/ProblemDetailsHandler.cs:43-44,73-87`; T `ReportIntakeContractTests.cs:31,48`, `ReportIntakeTransactionTests.cs:21` |
| Idempotency: a known `meldingId` replays its stored outcome byte for byte (`message_intake` table, PK on message id); concurrent duplicates give 409 or the replay | V / T | `ReportIntakeHandler.cs:44-46`; endpoint doc `ReportIntakeEndpoint.cs:10-11`; T `ReportIntakeIdempotencyTests.cs:28,43,60`; `ConcurrencyTests.cs:65` |
| Correlation: `correlationId` is stored on `message_intake` and on outbox events (`EventEnvelope.Create(..., correlationId)`) | V | `ReportIntakeHandler.cs:86-87,135-137,147` |
| Message chains (meldingsforbindelse): Trukket/Slettet close the chain (withdraw or delete the reports and publish an event, delete held rows); Tillegg is acknowledged with no effect; Oppdatering and Duplikat create a new version or report | V / T | `ReportIntakeHandler.cs:93-103,124-141`; T `CorrectionAndChainTests.cs:34-211` (Scenario_1…9) |
| Persistence: one SQL database `SkoleDb` with two EF contexts (`SkolenaervaerDbContext` schema `skolenaervaer`, `UtdanningDbContext` schema `utdanning`) | V | `Program.cs:93-96` |
| Skolenærvær tables: `report`, `report_version`, `fravaer_line`, `rejection`, `message_intake`, `message_chain`, `held_report`, `reporting_period`, `lock_postponement`, `dekning_basis(_barn)`, `dekning_summary(_rejection)`, `delivery`, `delivery_row`, `delivery_file_outbox`, `job_lock`, `processed_event`, `sweep_request` | V | `…Infrastructure/Persistence/Skolenaervaer/Migrations/20261020073439_Initial.cs` (e.g. `message_intake` :202, `report` :255, `reporting_period` :280, `report_version` :313, `held_report` :149, `delivery` :88, `delivery_row` :124, `delivery_file_outbox` :106) |
| The intake transaction holds the period row (`HOLDLOCK, ROWLOCK` on `skolenaervaer.reporting_period`) so the lock waits for in-flight intakes | V / T | `ReportIntakeHandler.cs:69-73`; T `Locking/LockRerunTests.cs:150 The_claim_waits_for_an_intake_transaction_holding_the_period_row_and_its_report_is_frozen` |
| Child not resolved, or Person unavailable: the report is **held** (encrypted with Key Vault in `held_report`) and answered exactly like an accepted report. It is swept on Person events and every hour | V / T | `ReportIntakeHandler.cs:52-67,106-111`; T `Held/HeldReportTests.cs:42,61,102` |
| Published event: `SkolenaervaerReportChanged` to Service Bus topic `skole.rapporter` (also `skole.perioder`, `skole.utdanning`, queues `leselogg` and `operasjonsregistrering`) | V | `…Infrastructure/Messaging/WolverineSetup.cs:50-54` ([M2LB (7)]); [M2LB (3)] `:49-53` |
| Caller is MeldingsutvekslerReceiver, `IntakePath: /api/skole/v1/skolenaervaer/reports` | V | `[M2LB (7)]/MeldingsutvekslerReceiver/src/M2LB.MeldingsutvekslerReceiver.Host/appsettings.json:28` ([M2LB (3)] same) |

### 1b. [M2LB (6)] / [M2LB (5)] Skole (older generation)

| Claim | Label | Evidence |
|---|---|---|
| Route `POST /api/skole/v1/skolenaervaer/rapporter` (not `/reports`) | V | `[M2LB (6)]/Skole/src/M2LB.Skole.Api/Rest/Rapportinntak/RapportinntakEndepunkt.cs:17` ([M2LB (5)] identical) |
| App role `Skole.Rapportinntak` (Utdanning: `Skole.Utdanningsinnmating`) | V | `[M2LB (6)]/Skole/src/M2LB.Skole.Api/Auth/Autentisering.cs:16-17` |
| Schema version **2.0.0**; period as `startTidspunkt/sluttTidspunkt` (xs:dateTime) | V | `[M2LB (6)]/Skole/src/M2LB.Skole.Api/appsettings.json:19`; `RapportForespoersel.cs:37-41` (PeriodeDto); handler `Rapportinntaksbehandler.cs:34` |
| Response 200 `{"utfall":"Mottatt"}`; reason codes in Norwegian (`MANGLER_IDENTIFIKATOR`, `UGYLDIG_TIMEVERDI`, …, `UKJENT_BARN`) | V / T | `Rapportinntaksbehandler.cs:105-107`; `Domain/Skolenaervaer/Aarsakskode.cs:9-15`; T `[M2LB (6)]/Skole/tests/M2LB.Skole.Integration/Rapportinntak/RapportinntakKontraktTests.cs:31 A_valid_report_answers_200_with_exactly_utfall_Mottatt` |
| Interim behaviour: an unresolved child, Person unavailable, or Trukket/Slettet/Tillegg answers **503 with nothing written**. There is no hold and no chain handling | V | `Rapportinntaksbehandler.cs:18-22,64-70` |
| Tables: `rapporteringsperiode`, `jobblaas`, `prosessert_hendelse`, `avvisning`, `meldingsmottak`, `rapport`, `rapportversjon`, `fravaerslinje`. There are no delivery, held or dekning tables | V | `…Persistence/Skolenaervaer/Migrations/20261012135232_Grunnlag.cs:46`; `20261012142608_Rapportinntak.cs:15,35,54` |
| The MU receiver in [M2LB (5)] calls `/rapporter` with schema 2.0.0 | V | `[M2LB (5)]/Meldingsutvekslermottak/src/M2LB.Meldingsutvekslermottak.Host/appsettings.json:28` |

---

## 2. Person lookup

| Claim | Label | Evidence |
|---|---|---|
| SkoleService calls Person **over HTTP** through `IDownstreamApi` `PersonApi` with an app token and a total timeout (`PersonApi:Timeout`, default 3 s) | V | `[M2LB (7)]/…Infrastructure/Person/PersonLookupClient.cs:18-26,102-114`; `appsettings.json` `PersonApi` section (`Timeout 00:00:03`) |
| Intake lookup: `POST api/person/v1/internal/barn/lookup-identifier`, body `{type:"Foedselsnummer"|"DufNummer", value}` | V | `PersonLookupClient.cs:30-31,144-149` |
| Response `{barnRegistreringId, birkId, birthDate, provisionalBirthDate, sikkerhetsnivaa}`. 404 means NotFound. A body without `birkId` counts as Unavailable; one without `sikkerhetsnivaa` counts as WithoutClassification and the report is held | V | `PersonLookupClient.cs:36-48,151-160` |
| Lock/batch: `POST api/person/v1/internal/barn/get` `{barnRegistreringIds:[≤1000]}` returns `{barn:[…], notFound:[…]}` | V | `PersonLookupClient.cs:80-100,162-164` |
| Utdanning linking: `POST api/person/v1/internal/barn/lookup-ekstern-id` `{eksternId}`. 409 means Ambiguous | V | `PersonLookupClient.cs:52-78`; used at `…Infrastructure/Utdanning/UtdanningIntake.cs:40`, `UtdanningLinker.cs:109` |
| The lookup is done **outside** the DB transaction and only for submissions | V | `ReportIntakeHandler.cs:52-56` |
| The [M2LB (7)] provider exists and matches: `PersonService` maps `/api/person/v1/internal/barn` + `/lookup-identifier`, `/get`, `/lookup-ekstern-id`, app role `Barn.Lookup`; request `IdentifierLookupRequest(Type, Value)` | V | `[M2LB (7)]/PersonService/src/M2LB.PersonService.Api/Rest/Internal/BarnMachineLookupEndpoints.cs:17,25-42,103-111` |
| **[M2LB (3)]** client uses Norwegian routes (`api/person/v1/internt/barn/oppslag-identifikator`, `…/oppslag-eksternid`, `…/hent`), the body field `verdi`, and the response fields `foedselsdato/usikkerFoedselsdato/ikkeFunnet` | V | `[M2LB (3)]/SkoleService/src/M2LB.SkoleService.Infrastructure/Person/PersonLookupClient.cs:30-31,54,86,154-155,164` |
| **[M2LB (6)]/[M2LB (5)]** client uses the same Norwegian routes (`…/internt/barn/oppslag-identifikator`, `oppslag-eksternid`, `hent`) | V | `[M2LB (6)]/Skole/src/M2LB.Skole.Infrastructure/Person/PersonoppslagKlient.cs:29,50,74` |
| In [M2LB (6)], [M2LB (5)], [M2LB (3)] and [M2LB (4)] **no Person project implements these routes**. The contract is "proposed" (spec `contracts/avhengigheter/person-maskinoppslag.md`), and only WireMock or the local stub stands in | V (absence by grep) / S | grep of `[M2LB (3)]/Person/src`, `[M2LB (6)]/Person/src`, `[M2LB (5)]/Person/src`, `[M2LB (4)]/Person/src` found no match; T fixtures `[M2LB (3)]/…/Fixtures/PersonWireMock.cs:44-62`, `[M2LB (6)]/…/Fixtures/PersonWireMock.cs:36-52` |
| Local Stub mode (`PersonLookup:Mode=Stub`, `local/person-stub.json`) | V | `[M2LB (7)]/…/Api/Program.cs:99-105`; `PersonLookupStub.cs` |
| SkoleService also **consumes Person events** (Service Bus topics `person.barn` and `person.person`) to trigger sweeps and links | V / T | `WolverineSetup.cs:56-57`; T `HeldReportTests.cs:102,172`, `UnlinkedUtdanningRecordTests.cs:52` |

---

## 3. Tjeneste (placements per period)

| Claim | Label | Evidence |
|---|---|---|
| Client `PlacementSourceClient`: `POST api/tjeneste/v1/internal/placements/period`, body `{fromDate,toDate,barnRegistreringIds,continuation}`, paged through `continuation`. The response is `{placements:[{barnRegistreringId,category,fromDate,toDate}], continuation}` | V | `[M2LB (7)]/…Infrastructure/Tjeneste/PlacementSources.cs:14-52`; `Domain/Shared/Ports/IPlacementSource.cs:7-35` |
| Category enum pinned: Fosterhjem / PrivatInstitusjon / StatligInstitusjon / Other / **Unknown** ("BiRK does not supply it; every placement is this until it does") | V / T | `IPlacementSource.cs:15-28`; T `tests/M2LB.SkoleService.Unit/Shared/PlacementCategoryWireTests.cs:23,30,34` ([M2LB (7)] only) |
| **Tjeneste is NOT called by the report intake.** It is used only by `BasisCollector`, for the expected population and coverage at **period lock** and for the GraphQL follow-up lists | V | `grep IPlacementSource` shows consumers only in `Infrastructure/Dekning/BasisCollector.cs:34,55`; callers `Locking/PeriodLocker.cs:67-69`, `FollowUp/FollowUpService.cs:38` |
| Tjeneste unavailable during the lock: nothing is committed and the lock retries | V / T | `BasisCollector.cs:53-60`; T `Locking/LockRerunTests.cs:134 Tjeneste_unavailable_during_gather_commits_nothing_and_the_lock_completes_once_it_recovers` |
| The [M2LB (7)] provider exists: `TjenesteService` maps `POST /api/tjeneste/v1/internal/placements/period` (policy `SkoleServiceAuthorization`), with matching pinned wire names | V | `[M2LB (7)]/TjenesteService/src/M2LB.TjenesteService.Api/Program.cs:208-209`; `Internal/PlacementsContracts.cs:12-45` |
| [M2LB (3)] client: `api/tjeneste/v1/internt/plasseringer/periode` with `fraDato/tilDato/barnRegistreringIder/fortsettelse` → `plasseringer` | V | `[M2LB (3)]/…/Tjeneste/PlacementSources.cs:28,32,51-52` |
| [M2LB (6)]/[M2LB (5)]: `PlasseringskildeKlient` (`api/tjeneste/v1/internt/plasseringer/periode`) is registered in DI but **has no consumer**, because [M2LB (6)]/[M2LB (5)] have no lock or dekning code | V | `[M2LB (6)]/Skole/src/M2LB.Skole.Infrastructure/Tjeneste/Plasseringskilder.cs:32`; `Program.cs:89-95`; grep `IPlasseringskilde` finds only DI and the Wolverine service-location line `WolverineOppsett.cs:65` |
| [M2LB (6)], [M2LB (5)], [M2LB (3)] and [M2LB (4)] Tjeneste projects implement no such route | V (absence by grep) | grep of `p{3,4,5,6}/Tjeneste/src` |

---

## 4. Utdanning: BiRK → CDC → Event Hub → SkoleAdapter → SkoleService

| Claim | Label | Evidence |
|---|---|---|
| Utdanning **is a domain inside SkoleService** (`Domain/Utdanning`, separate `UtdanningDbContext`, schema `utdanning`). Architecture tests enforce the boundary | V / T | `[M2LB (7)]/SkoleService/src/M2LB.SkoleService.Domain/Utdanning/*`; `Program.cs:96`; T `tests/M2LB.SkoleService.Unit/Architecture/ArchitectureTests.cs:31,44,51,58` |
| Utdanning tables: `change_intake`, `utdanningsforhold(_version)`, `manglende_skoletilbud(_version)`, `unlinked_utdanning_record`, `processed_event` | V | `…Persistence/Utdanning/Migrations/20261020073449_Initial.cs:18,35,89,113` |
| SkoleService ingest endpoints (app role `Utdanning.Write`, `Idempotency-Key` header required, at most 200 characters): `POST/PUT/DELETE /api/skole/v1/utdanning/utdanningsforhold[/{recordId}]`, the same for `/manglende-skoletilbud`, `GET /record-ids`, `GET /code-lists`. Every write answers 200 `{"outcome":"Received"}`; 422 reasons are `MISSING_RECORD_ID`, `MISSING_BARN_REFERENCE`, `MISSING_FROM_DATE` and `INVALID_DATE` | V / T | `…Api/Rest/Utdanning/UtdanningEndpoints.cs:22-81,91-93`; `UtdanningRequests.cs:41-44`; `Auth/Authentication.cs:17`; T `Integration/Utdanning/UtdanningIngestContractTests.cs:31-222` |
| [M2LB (6)]/[M2LB (5)] equivalent: `/api/skole/v1/utdanning/…/{postId}`, `/postidentiteter`, `/kodelister`, response `utfall:"Mottatt"` | V / T | `[M2LB (6)]/Skole/src/M2LB.Skole.Api/Rest/Utdanning/UtdanningEndepunkter.cs:22-75`; T `[M2LB (6)]/…/Utdanning/UtdanningInnmatingKontraktTests.cs:168` |
| An unknown child (barnEksternId not found in Person) is held encrypted as an unlinked record, then retried hourly or on a Person event | V / T | `Jobs/UnlinkedUtdanningRecordJob.cs:11-20`; T `UnlinkedUtdanningRecordTests.cs:39,52`, `UnlinkedUtdanningRecordJobTests.cs:42` |
| SkoleAdapter CDC tables: `dbo.Utdanning` (PK `UtdanningPK`, child `BarnFK`, `Fra`/`Til` dates, codes `UtdanningTypeFk`, `utdanningSkoleTypeFk`) and `dbo.ManglendeSkoletilbud` (PK `ManglendeSkoletilbudPK`, `BarnFk`, `GyldigFraDato/GyldigTilDato` DateTime, code `ManglendeSkoletilbudÅrsakTypeFk`) | V | `[M2LB (7)]/SkoleAdapter/src/M2LB.SkoleAdapter.Worker/appsettings.json` `Tables` section (same in [M2LB (3)]) |
| Event Hub names: dev/local `m2lb-cdc-dev.BirkM2LB.dbo.Utdanning` and `…dbo.ManglendeSkoletilbud`; QA `m2lb-cdc-qa.Birk.dbo.Utdanning` and `…ManglendeSkoletilbud` | V / S | `SkoleAdapter.Worker/appsettings.Development.json:3-4`, `appsettings.Qa.json:3-4`, `appsettings.Local.json:3-4`; spec `specs/001-skoleadapter/contracts/cdc-kilde.md:11-12` (the same lines in [M2LB (7)], [M2LB (6)], [M2LB (3)] and [M2LB (4)]) |
| Adapter → SkoleService: `SkoleServiceClient` with `BasePath=/api/skole/v1/utdanning`, headers `Idempotency-Key` (hash of the change id) and `X-Correlation-Id`, an app token through `IDownstreamApi` `SkoleServiceApi`; 200 reads `outcome`, 422 reads `reasonCode` | V | `[M2LB (7)]/SkoleAdapter/src/M2LB.SkoleAdapter.Infrastructure/SkoleService/SkoleServiceClient.cs:61-86`; `EventHubs/ChangeHandler.cs:117` |
| Mapping: create→POST, update→PUT, delete or tombstone→DELETE `{path}/{recordId}`. `recordId` = UUID v5 of `"<table>:<pk>"`. Request bodies are `UtdanningsforholdRequest(recordId?, barnEksternId, fromDate, toDate, utdanningType, skoleType)` and `ManglendeSkoletilbudRequest(..., reason)`. Code translation is configured but the mapping tables are **empty** (`CodeTranslation: {}`), so unmapped codes are sent as `unknown` | V / T | `Domain/Translation/RequestBuilder.cs:18-19,44-45`; `Domain/Identity/RecordIdentity.cs:7-8`; `Translation/Requests.cs:11-24`; `Requests.cs:36`; `Worker/appsettings.json` `CodeTranslation`; T `Integration/Forwarding/CreateAndChangeTests.cs:12`, `DeletionTests.cs:14`, `Unit/Translation/RequestWireNamesTests.cs:32-56` ([M2LB (7)] only) |
| SkoleService code lists contain only `unknown` (version `0.1.0-draft`) | V | `[M2LB (7)]/SkoleService/src/M2LB.SkoleService.Api/appsettings.json` `Utdanning:CodeLists` |
| Type mismatch: the adapter sends `barnEksternId` as `long?` while SkoleService binds `int?`. A value above int.MaxValue would fail JSON binding (400) | V / I | `Requests.cs:13,21` vs `UtdanningRequests.cs:13,27` |
| **Upstream CDC capture is not live in these snapshots**: the CDC order for the two tables is a draft and "Ingenting her er sendt" (nothing sent); tasks T081, T082 and T095 are open; Aspire uses a hand-written config "until Terraform captures the two tables (gate R1)"; CdcReplay routes them as `AwaitingCaptureOwnerByTable` | S / V | `[M2LB (7)]/SkoleAdapter/docs/cdc-bestilling.md:3-6`; `[M2LB (7)]/SkoleAdapter/specs/001-skoleadapter/tasks.md:251-252,382`; `[M2LB (7)]/Aspire/Aspire/AppHost.cs:34-38`; `[M2LB (7)]/CdcReplay/src/M2LB.CdcReplay/EventHubRouter.cs:57-61` |
| [M2LB (6)], [M2LB (5)] and [M2LB (4)]: SkoleAdapter is **specs only**. In [M2LB (6)] and [M2LB (5)] SkoleService's Utdanning REST ingest exists, but no adapter code feeds it | V | file listings ([M2LB (6)], [M2LB (5)], [M2LB (4)] `SkoleAdapter/` = 11 spec files) |

---

## 5. Period lock and Bufdata delivery

| Claim | Label | Evidence |
|---|---|---|
| Monthly periods, lock day 11 at 06:00 Oslo time; July is not a reporting month; the first period is 2026-10 | V / T | `[M2LB (7)]/…/Api/appsettings.json` `Skolenaervaer:Calendar` (`LockDay 11`, `Postponement 06:00:00`, `NonReportingMonths [7]`); validation `Program.cs:71-75`; T [M2LB (6)] `Unit/Skolenaervaer/RapporteringskalenderTests.cs:15 Lock_for_october_2026_is_06_00_Oslo_on_11_november_in_CET`; [M2LB (7)] `LockRerunTests.cs:103 July_is_never_locked` |
| States Open → Locking → Locked → Delivered | V / T | `[M2LB (7)]/…/Domain/Skolenaervaer/PeriodLock.cs:3-9`; T `Unit/Skolenaervaer/PeriodLockTests.cs:14` |
| **Scheduled jobs ([M2LB (7)]/[M2LB (3)])**: `PeriodCreatorJob` (daily), `LockSchedulerJob` (every minute; claims Open→Locking and sends the durable `RunPeriodLock`), `DeliveryFileOutboxJob` (every minute), `HeldReportSweepJob`, `UnlinkedUtdanningRecordJob`, `AlertAssessmentJob` (5 min), `RetentionJob` (daily). Each runs on one replica through leased `job_lock` | V | `Program.cs:180-186`; `Jobs/LockSchedulerJob.cs:21-27,53-59,67-79`; `Delivery/DeliveryFileOutboxJob.cs:24-25,46`; `Jobs/PeriodCreatorJob.cs:19-20`; `Jobs/LeasedJob.cs:9-33` |
| `PeriodLocker`: gathers outside a transaction (unlinked Utdanning links, held reports, Tjeneste placements, Person batch), then commits once: freezes population, coverage and dataset rows, publishes each report as `Locked`, marks the period Locked and queues one outbox row per delivery file | V / T | `Locking/PeriodLocker.cs:23-41,52-70,171-191`; `Locking/RunPeriodLockHandler.cs:17-26`; T `Locking/PeriodLockEndToEndTests.cs:49,62,88,98` |
| Operations: `PUT /api/skole/v1/operations/periods/{period}/lock-time` (postpone the lock, Autorisasjon `PostponeLock`), `GET /api/skole/v1/operations/periods`; local only: `POST /api/skole/v1/local/periods/{period}/lock-now` | V / T | `Rest/Operations/OperationsEndpoints.cs:21,23,69,109`; `Rest/Local/LocalEndpoints.cs:16`; T `Operations/LockPostponementTests.cs:40-134` |
| **Parquet delivery is IMPLEMENTED in [M2LB (7)] and [M2LB (3)]**: Parquet.Net pinned `[6.1.0]`; `DatasetParquetWriter` + `DekningParquetWriter`; blob containers `skolenaervaer-rapporter` and `skolenaervaer-dekning`; path `period=YYYY-MM/data.parquet`; `_manifest.json` written last; write-once (`If-None-Match:*`) with a SHA-256 comparison; then the delivery is Published, the period Delivered, and `SkolenaervaerPeriodClosed` goes through the outbox to topic `skole.perioder` | V / T | `M2LB.SkoleService.Infrastructure.csproj:29`; `Delivery/ParquetLibrary.cs:11`; `Delivery/DeliveryStorage.cs:13-20`; `Delivery/ManifestWriter.cs:17-23`; `Delivery/DeliveryExport.cs:34-44,85-100,114-115`; `WolverineSetup.cs:50`; T `Unit/Delivery/ParquetDeterminismTests.cs:66-194`, `ParquetLibraryTests.cs:12`, `Integration/Locking/PeriodLockEndToEndTests.cs:129,166,190`, `LockRerunTests.cs:51,74`, `LockRecoveryTests.cs:47` |
| Storage: `Delivery:Storage:BlobEndpoint` (Azurite connection string locally, otherwise endpoint + managed identity). It is **empty** in `appsettings.json`, so the deployed value comes from the environment | V / NV | `Program.cs:133-142`; `appsettings.json` `Delivery:Storage:BlobEndpoint ""`. Whether any environment has a delivery account configured: NV |
| A Bufdata-side consumer (reading the containers or the `SkolenaervaerPeriodClosed` event) is not in these packages | NV | — |
| **[M2LB (6)]/[M2LB (5)]**: the `Periodelaas` domain (`Aapen/Laaser/Laast/Levert`) and `PeriodeoppretterJobb` exist. There is **no** lock scheduler, PeriodLocker, delivery code or Parquet package; the lock and delivery tasks are open | V / S | `[M2LB (6)]/Skole/src/M2LB.Skole.Domain/Skolenaervaer/Periodelaas.cs:3-9`; `Program.cs:136-138`; grep `Parquet|Leveranse` in `[M2LB (6)]/Skole/src` = none; tasks `[M2LB (6)]/Skole/specs/001-skoletjenesten/tasks.md:519-563` (T103–T110 `[ ]`) |
| [M2LB (6)], [M2LB (5)] and [M2LB (4)] specify the same delivery (containers, `periode=YYYY-MM/data.parquet`, `_manifest.json` last) | S | `[M2LB (6)]/Skole/specs/001-skoletjenesten/contracts/bufdata-leveranse.md:14-17,21,55,73-81` ([M2LB (4)] identical) |
| [M2LB (6)]/[M2LB (5)] Aspire already declares the two delivery containers (they are not used by code) | V | `[M2LB (6)]/Aspire/Aspire/AppHost.cs:40-41`; `[M2LB (5)]/Aspire/Aspire/AppHost.cs:40-41` |

---

## 6. Per-package capability matrix

| Capability | [M2LB (7)] | [M2LB (3)] | [M2LB (5)] | [M2LB (6)] | [M2LB (4)] |
|---|---|---|---|---|---|
| Intake route | `/reports` V | `/reports` V | `/rapporter` V | `/rapporter` V | `/rapporter` S |
| Schema version | 2.0.1 V | 2.0.1 V | 2.0.0 V | 2.0.0 V | spec only |
| App role | `Skolenaervaer.Submit` | same | `Skole.Rapportinntak` | same | S |
| Hold / chains (Trukket, Slettet, Tillegg) | V | V | 503 interim V | 503 interim V | S |
| Person client routes | `internal/barn/lookup-identifier`, `get`, `lookup-ekstern-id` | `internt/barn/oppslag-identifikator`, `hent`, `oppslag-eksternid` | Norwegian (as [M2LB (3)]) | Norwegian | S |
| Person provider implements them | **yes** (PersonService) | no | no | no | no |
| Tjeneste client used | at lock / follow-up V | same V | registered, unused V | registered, unused V | S |
| Tjeneste provider route | **yes** (TjenesteService) | no | no | no | no |
| Utdanning REST ingest | V | V | V | V | S |
| SkoleAdapter code | V | V | specs | specs | specs |
| Period lock execution | V | V | domain + period creator only | same | S |
| Parquet + manifest delivery | **V** | **V** | not implemented (S) | not implemented (S) | S |
| GraphQL follow-up / dekning stats | V | V | — | — | S |
| MU receiver code | V | V | V | specs | specs |

---

## 7. Tests (names; not run)

- **[M2LB (7)] SkoleService** (59 test files, 396 `[Fact]/[Theory]`):
  - Intake: `ReportIntakeContractTests` (31–182), `ReportIntakeIdempotencyTests` (28–60), `ConcurrencyTests` (29, 65), `CorrectionAndChainTests` (34–211), `GradertBarnIntakeTests` (42–90), `ReportIntakePrivacyTests` (35–56), `ReportIntakeTransactionTests:21`.
  - Held: `HeldReportTests` (42–172), `HeldAtLockTests` (26, 51).
  - Lock: `PeriodLockEndToEndTests` (49–212), `LockRerunTests` (35–255), `LockRecoveryTests` (27–74), `GraderingAtLockTests` (27–85), `LockPostponementTests` (40–134).
  - Utdanning: `UtdanningIngestContractTests` (31–222), `UnlinkedUtdanningRecordTests`, `UnlinkedUtdanningRecordJobTests`, `UtdanningBasisTests`, `RecordIdsTests`.
  - Contract schemas: `Integration/Contracts/ContractSchemaTests` (48–152).
  - Unit tests: `ValidityCheckTests`, `PeriodLockTests`, `ParquetDeterminismTests`, `ParquetLibraryTests`, `ArchitectureTests`, `PlacementCategoryWireTests` ([M2LB (7)] only), `Unit/Contracts/*` ([M2LB (7)] only: `PinnedWireNamesTests`, `MessageIdentityTests`, …).
  - WireMock fixtures pin the Person and Tjeneste routes: `Fixtures/PersonWireMock.cs:44,50,62`, `TjenesteWireMock.cs:23`.
- **[M2LB (3)] SkoleService**: 51 files and 358 facts, with the same suites minus `Unit/Contracts`, `Unit/Telemetry`, `PlacementCategoryWireTests`, `PersonEventRejectionTests` and `ReadLogMessageTypeTests`. Its WireMock fixtures use the Norwegian routes (`[M2LB (3)]/…/Fixtures/PersonWireMock.cs:44-62`, `TjenesteWireMock.cs:23`).
- **[M2LB (6)]/[M2LB (5)] Skole**: 136 facts:
  - Intake: `RapportinntakKontraktTests`, `RapportinntakIdempotensTests`, `RapportinntakPersonvernTests`, `RapportinntakTransaksjonTests`.
  - Utdanning: `UtdanningInnmatingKontraktTests`, `UkobletUtdanningspost*Tests`, `UtdanningsgrunnlagTests`, `PostidentiteterTests`.
  - Jobs: `PeriodeoppretterJobbTests`, `JobblaasTests`, `HelseTests`.
  - Unit: `GyldighetskontrollTests`, `KvalitetskontrollTests`, `RapporteringskalenderTests`, `RapportTests`, `UtdanningsendringTests`, `ArkitekturTests`, `FeltkrypteringTests`, `ProblemdetaljerTests`.
  - There are **no lock or delivery tests**.
- **[M2LB (7)] SkoleAdapter**: 150 facts:
  - Integration: `Forwarding/CreateAndChangeTests` (12–99), `DeletionTests` (14–62), `BarnReferenceTests` (13–48), `Quickstart/RealSkoleTests:23`, plus `Leadership`, `Reconciliation`, `Resumption`, `Privacy` and `ErrorHandling` suites.
  - Unit: `Debezium`, `Identity`, `Translation` (incl. `RequestWireNamesTests`, [M2LB (7)] only), `Classification`, `EventHubs`.
- **[M2LB (3)] SkoleAdapter**: 141 facts; it lacks `RequestWireNamesTests` and `TelemetryNamesTests`.
- **[M2LB (6)], [M2LB (5)], [M2LB (4)] SkoleAdapter**: no tests (specs only).
- **Load test**: `[M2LB (7)]/SkoleService/tests/load/rapportinntak.js` (exists, not inspected in depth).

---

## 8. Prior claim set checked

Prior claim: "SkoleService receives HTTP JSON report; consults Person (identity lookup) and Tjeneste (placement/period lookup); Utdanning populated from BiRK via CDC/Event Hub → SkoleAdapter; Period Lock monthly closing then Bufdata Parquet + manifest delivery (Parquet not implemented in reviewed snapshot)".

| Part | Verdict |
|---|---|
| Receives an HTTP JSON report | **Confirmed** (V). The route differs by package: `/reports` in [M2LB (7)]/[M2LB (3)], `/rapporter` in [M2LB (6)]/[M2LB (5)]/[M2LB (4)]. |
| Consults Person (identity lookup) | **Confirmed** (V), with nuances: the lookup runs outside the transaction and only for submissions; an unavailable Person **holds** the report in [M2LB (7)]/[M2LB (3)] but returns **503** in [M2LB (6)]/[M2LB (5)]. Person is also called in batch at lock and by external id for Utdanning. Only [M2LB (7)] has a real Person provider for the route; in [M2LB (6)]–[M2LB (4)] it is a proposed contract. |
| Consults Tjeneste for placements/periods | **Imprecise.** Tjeneste is **not** consulted at intake. It is used only at period lock and for the GraphQL follow-up lists, to compute expected coverage (V). In [M2LB (6)]/[M2LB (5)] the client is registered but has no consumer. Only [M2LB (7)] has the provider route. |
| Utdanning from BiRK via CDC/Event Hub → SkoleAdapter | **Code path confirmed** in [M2LB (7)]/[M2LB (3)] (V), and Utdanning is a domain inside SkoleService. But the upstream CDC capture of `dbo.Utdanning` and `dbo.ManglendeSkoletilbud` is **not ordered or live** (draft order, open gate R1), and the code mappings are empty, so everything is sent as `unknown` (S/V). In [M2LB (6)], [M2LB (5)] and [M2LB (4)] the adapter is specs only. |
| Period Lock monthly closing | **Confirmed in [M2LB (7)]/[M2LB (3)]** (V/T): lock day 11 at 06:00 Oslo time, July skipped. In [M2LB (6)]/[M2LB (5)] only the domain enum and the period creator exist (no lock execution). |
| "Parquet not implemented in reviewed snapshot" | **Contradicted for [M2LB (7)] and [M2LB (3)]**: Parquet.Net 6.1.0, two containers, `data.parquet` plus `_manifest.json` written last, SHA-256 write-once and the `SkolenaervaerPeriodClosed` event are implemented and tested. It is true only for [M2LB (6)], [M2LB (5)] and [M2LB (4)]. |
