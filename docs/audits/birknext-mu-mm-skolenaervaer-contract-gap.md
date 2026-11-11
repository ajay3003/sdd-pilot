# BirkNext: MU → MM Skolenærvær contract (integration gap), 2026-11-11

Branch `008-traceability-first`. Gap **MM-G1**, referenced from `birknext-multisource-e2e-gap-register.md` (MS-R2) and
`birknext-suggested-e2e-tests-gap-register.md`.

**Scope.** This is a static reading of the imported source archives in `AIAssisted/testdata/` (read only). It used a stdlib zip
reader into session scratch, not BirkNext.
- No MU or MM source was modified.
- No message was sent, and no queue, blob, Altinn, MU, MM or TT02 endpoint was contacted.
- No runtime configuration of any environment was read.

## 1. The finding under review

Task 4 (multi-source E2E) reported **MU → MM XML INCOMPATIBLE** (RS-07, gap MS-R2):
- MU's source knows Skolenærvær up to namespace `https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0(.flat)`;
- MM (M2LB `MeldingsutvekslerReceiver`) validates `…skolenaervaer.v2.0.1.flat`.

The namespaces really are disjoint. The open question was whether this is a true blocking incompatibility.

## 2. Evidence

### 2.1 Sending side: MU (`Tiltak 2 - Sikkert digitalt økosystem.zip`)

| Question | Evidence | Answer |
|---|---|---|
| Where does MU's v2.0.0 knowledge come from? | Every Skolenærvær XSD and catalogue sits under `Buf.Meldingsutveksler.TestData/TestData/xsds/…`, for example `xsds/v3/xsd/bufdir.barnevern.tiltak.skolenaervaer.v2.0.0.flat.xsd` (`version="2.0.0"`, SHA-256 `be6fe1975ee4…`) and `xsds/v3/bufdir.xsd.oversikt.json`, which lists v0.8.2, v2.0.0 and v2.0.0.flat/.altinn. The other occurrences are unit-test constants (`FagKlient.Tests/…/DeadLetterHandlerTests.cs:63`, `Service.Tests/…/InboundMessageHandlerTests.cs:123`). | **Test data, not a contract MU enforces.** |
| What schemas does MU validate against at runtime? | `Service/src/ServiceHandler/ServiceHandlerRegistration.cs:26-33`: `IReadOnlyFileSystem` = `SkjemaverktoyFileSystem` over the **blob container** `XsdKlientKonfigurasjon.ContainerName`. `Service/src/Skjemaverktoy/XmlValidatorClient.cs:21-46` looks the document namespace up in the catalogue loaded from that container and rejects an unknown namespace (`InvalidMeldingstype`). | The effective schema set is **runtime data** in a blob container. The source does not show which versions are deployed. |
| Does MU validate the Altinn Skolenærvær payload? | `Service/src/Altinn/Service/AltinnService.cs:164-225` (`HentBrukerUtfyllinger`): reads the XML (`ProcessFileStream`, 326-348: a plain `ReadToEndAsync`), parses only the `Meldingshode` (175), checks for duplicates, then `serviceBusSendService.SendMeldingAsync` with the **unchanged XML** and `MessageTypeNamespace = meldingshode.MeldingstypeNmsp` (215-223). `xmlValidatorClient` is called only on the fagsystem-inbound path (`ServiceHandler/InboundMessageHandler.cs:74`) and the Fiks IO path (`FiksIO/FiksIoService.cs:223`). | **No.** On the Altinn path MU is an opaque transport: no XSD validation, no transform. |
| Does MU transform or map between versions? | No XSLT and no mapping on the Altinn path. `XmlProcessingUtils.RemoveVersionFromNamespace` is used only to recognise protocol receipts (`InboundMessageHandler.cs:131,239`). | **No transform exists.** |
| How does MU route it? | `Common/Configuration/MessageChannelResolver.cs:28-64`: longest domain prefix wins; a match needs equality or `prefix + "."`. `Service/appsettings.Development.json:67-83`: channel "Tiltak Admin", domain `https://bufdir.no/barnevern.tiltak`, publish queue `tiltak_admin`. | Routing is **version-agnostic**: v2.0.0 and v2.0.1 go to the same channel. In MU's source configuration that channel is "Tiltak Admin", not an M2LB channel. |

### 2.2 Receiving side: MM (`M2LB.zip`, `MeldingsutvekslerReceiver`)

| Question | Evidence | Answer |
|---|---|---|
| Receiving contract | `src/M2LB.MeldingsutvekslerReceiver.Host/appsettings.json:15-24`: `NamespacePrefix` `https://bufdir.no/barnevern.tiltak.skolenaervaer`; one version, `Namespace` `…skolenaervaer.v2.0.1.flat`, `Version` 2.0.1, `Schema` `skolenaervaer-v2.0.1-flat-m2lb`. | v2.0.1.flat only. |
| Schemas | Embedded `src/M2LB.MeldingsutvekslerReceiver.Skolenaervaer/Skjema/bufdir.barnevern.tiltak.skolenaervaer.v2.0.1.flat.xsd` (`3d13b8daad43…`) and the relaxed `….v2.0.1.flat.m2lb.xsd` (`e9d031b7e562…`), whose only difference is that the six `TypeFravaerType` enumerations become `maxLength 16` (DEC-123). Root element `Fravaer` in both. | Validation uses the relaxed copy. |
| Version handling | `src/M2LB.MeldingsutvekslerReceiver.Core/Registry/MessageTypeRegistry.cs:21-22`: an **exact ordinal** namespace match. `Core/Handling/MessageHandler.cs:133-138`: no match leads to `RejectAsync(… Reason.UnsupportedVersion)`, a negative receipt, **before** any schema validation. 144: schema validation against the registered schema only. | Any v2.0.0(.flat) message is **rejected**. |
| Which namespace is used? | `MessageHandler.cs:80,134`: the envelope's `MessageTypeNamespace` (`ReceivedMessage.cs:24-25`), which MU copies from the `Meldingshode` (MU `AltinnService.cs:223`). | The sender's header decides. |
| Does runtime configuration change the effective schema? | The accepted version list is configuration (`MessageTypes:Skolenaervaer:Versions`). Accepting v2.0.0 would need a configuration entry **and** an embedded v2.0.0 schema, and the M2LB repository has no v2.0.0 schema since DEC-126 (`specs/001-meldingsutveksler-receiver/research.md:250-282`). | Only with a code change plus configuration, not by configuration alone. |
| MM's own view | `docs/mu-bestilling.md`: status "utkast (T117) … Ingenting her er sendt". §1 asks MU for a dedicated channel on `…barnevern.tiltak.skolenaervaer` (today Skolenærvær lands in "Tiltak Admin"). §7 asks MU to *confirm* that Altinn Skolenærvær arrives as `…v2.0.1.flat` and states "en v2.0.0-melding avvises som ukjent versjon". §8: MU has promised XSD validation of Altinn messages before production. `research.md:259-266`: "Assumption to verify with the MU team … no v2.0.1 sample message". | MM already treats both the sender version and the channel as **open dependencies on MU**. |

### 2.3 Structural difference between v2.0.0.flat and v2.0.1.flat

The namespace and the `version` attribute were normalised away before diffing. v2.0.1 still differs:
- the `SkolenaervaerRegistrering` wrapper is removed, and `AntallTimerEtterPlanen` / `AntallTimerFaktiskNaervaer` move up a level;
- `StartDato` / `SluttDato` change from `xs:dateTime` to `xs:date`;
- `TimerFravaer` and the hour counts change from `xs:double` to `xs:nonNegativeInteger`;
- a new `xs:length 11` facet is added;
- `Telefon`, `epost` and two `Navn` become optional;
- `minLength`/`maxLength` pairs become `length`.

A v2.0.0 document would **fail** v2.0.1 validation even with its namespace rewritten. This is a breaking version change, so a namespace alias cannot bridge it.

## 3. Conclusion

| Statement | Status |
|---|---|
| "MU's sending contract is v2.0.0, so MU → MM is incompatible" | **Not supported by the evidence.** On the Altinn path MU forwards the payload opaquely, without validation or transformation, and routes regardless of version. MU's v2.0.0 XSDs are test data. The XSD catalogue MU enforces on its other paths lives in a blob container and is not visible in source. |
| The real sending contract | The **Altinn app `buf/skolenaervaer`** data model, which sets `MeldingstypeNmsp` in the `Meldingshode`. Its source is **not imported**, so the effective sender version is **unknown**. |
| Receiving contract | MM accepts exactly `…skolenaervaer.v2.0.1.flat` and rejects any other version with a negative receipt. |
| Classification | **Static candidate, not a confirmed blocking incompatibility.** It **becomes blocking** if the Altinn app emits anything other than `…v2.0.1.flat`: every report would be rejected as UnsupportedVersion. The two versions differ structurally, and no transform exists in MU or MM. |
| Separate, evidence-backed routing dependency | MU's source configuration routes `barnevern.tiltak.*` to "Tiltak Admin" (`tiltak_admin`). MM's configuration expects a dedicated channel whose name is still a placeholder (`<channel>`). MM's order to MU is an unsent draft (T117). Until MU provisions the channel, Skolenærvær messages do not reach MM's queue in any configuration visible in source. This also depends on deployed MU configuration, which was not read. |
| Validation drift risk | If MU starts XSD-validating Altinn payloads (promised, DEC-99) with the **published** v2.0.1 XSD, `TypeFravaer` codes that MM's relaxed copy accepts would be stopped at MU (DEP-03). |

### Gap MM-G1

| Field | Value |
|---|---|
| ID | MM-G1 |
| Title | Skolenærvær sender version and MU channel unconfirmed for MU → MM |
| Status | OPEN (static candidate; blocking if confirmed) |
| Owner | Domain owner / MU team (sender version, channel), with the M2LB team (T117 order) |
| What closes it | 1. The Altinn app's data model or a real synthetic sample showing `MeldingstypeNmsp` = `…v2.0.1.flat`. 2. A dedicated MU channel on `…barnevern.tiltak.skolenaervaer`, with names in MM's configuration. 3. An agreement on which XSD MU validates against (published vs relaxed). |
| How BirkNext should present it | As a cross-source schema candidate with the transport marked as **opaque forwarding**: MU is not the schema owner on this path. The verdict stays INCOMPATIBLE-candidate until the sender's contract is imported or confirmed. This is recorded, not implemented here: the recovery task forbids new product scope, and the RS-07 behaviour (namespaces are disjoint) is factually correct as a static comparison. |
| Not done | No live traffic, no queue inspection, no change to MU or MM source. |
