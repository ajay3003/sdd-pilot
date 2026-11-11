# Skolenærvær — system reference (from the real source packages, 2026-11-11)

This is a reference for testers. It describes how Skolenærvær works, **as implemented in the six imported source packages**, and where BirkNext represents each part.

- **Evidence:** every statement is backed by `../audits/birknext-skolenaervaer-evidence-matrix.md` (file:line).
- **Runtime:** nothing here is runtime-verified. Which code generation runs in any environment is **not known**.
- **In BirkNext:** the tester-facing explanation of the same system is the System Understanding guide (`/system-understanding`, Norwegian).

## 1. Two M2LB source generations

| | Norwegian-named generation | English-named generation |
|---|---|---|
| Packages | `M2LB (5)` (MM and Skole code), `M2LB (6)` (Skole code only), `M2LB (4)` (specs only) | `M2LB (7)`, `M2LB (3)` |
| MM project | `Meldingsutvekslermottak` | `MeldingsutvekslerReceiver` |
| Intake service | `Skole` | `SkoleService` |
| Report XSD / `skjemaversjon` | v2.0.0 flat | v2.0.1 flat (hours at the root, `xs:nonNegativeInteger`) |
| Intake route | `POST /api/skole/v1/skolenaervaer/rapporter` | `POST /api/skole/v1/skolenaervaer/reports` |
| Success answer | `{"utfall":"Mottatt"}` | `{"outcome":"Received"}` |
| App role | `Skole.Rapportinntak` | `Skolenaervaer.Submit` |
| Person unavailable / unknown child | 503, nothing written | report held, encrypted (`held_report`) |
| Period lock execution, Parquet delivery | not implemented (rows prepared only) | implemented and tested |
| Period-closed event | `SkolenaervaersperiodeLukket` (declared, not published) | `SkolenaervaerPeriodClosed` (published) |

MU (`Tiltak 2 - Sikkert digitalt økosystem (1)`) is one product. Its schema tool ships skolenærvær v0.8.1, v0.8.2 and v2.0.0 (flat and altinn), but **no v2.0.1**.

## 2. Main flow (monthly report)

| Step | What happens | Transport / format | BirkNext representation |
|---|---|---|---|
| Altinn → MU | MU learns of a completed instance in two ways: by webhook (`/api/altinn/webhook`, event `app.instance.process.completed`, source `…/buf/skolenaervaer`) and by polling (on by default, every 24 h, `excludeConfirmedBy=buf`). It fetches the XML data elements only, validates them against the XSD, stores them encrypted (AES-256, key wrapped with the channel's RSA key) in the channel's blob container, then calls Altinn `/complete` | HTTPS; XML | Skolenærvær Testing (Rapportmottak); System Understanding edge `e-altinn-mu` |
| MU → MM | A Service Bus **queue** message carries a *reference* (`MainFilePath`, namespace, origin), never the payload. MM reads the blob directly and decrypts it with its Key Vault key. Correlation is `Meldingshode/Id` | Service Bus (Wolverine); encrypted XML in Blob | `e-mu-mm`; Integration Topology `mu-mm` |
| MM | Validates against the one registered flat XSD, maps XML → JSON (mapping table in evidence matrix §3.2), and POSTs to SkoleService with an Entra app token, using `X-Correlation-Id` = `Meldingshode/Id` | HTTP; JSON | `e-mm-skole`; Message Verification |
| MM → MU receipt | An Applikasjonskvittering is sent as an ordinary MU message on `<channel>_mu`. The policy is **positive only**: rejected reports also get a positive receipt. MU marks the message `HentetAvFagsystem` and replies `AckMelding`. Nothing goes from MM to Altinn | Service Bus | `e-mm-mu-ack` |
| SkoleService intake | Strict JSON; idempotent per `meldingId`; handles chains (Trukket, Slettet, Tillegg) | — | System Understanding rules |

**Version risk.** The English-named MM accepts only `…skolenaervaer.v2.0.1.flat`. Any other namespace is completed as `UnsupportedVersion` with a positive receipt, and the report is never delivered. MU knows v2.0.0 at most. The namespace the Altinn app actually sends is not in these sources. This is recorded as Conflicting (SYS-1).

## 3. Side flows

| Flow | Implementation | Status |
|---|---|---|
| **Person** (identity) | HTTP lookup outside the transaction (3 s budget; `…/barn/lookup-identifier`); Person events consumed on the Service Bus topics `person.barn` and `person.person` | Only `M2LB (7)`'s PersonService implements the English lookup routes |
| **Tjeneste** (placements) | `POST api/tjeneste/v1/internal/placements/period`, called by `BasisCollector` **at period lock** and for the follow-up lists, never at intake | Implemented in `M2LB (7)`; the client is registered but unused in the Norwegian generation |
| **Utdanning** (education) | BiRK → CDC → Event Hub (`m2lb-cdc-<env>.<db>.dbo.Utdanning` / `ManglendeSkoletilbud`) → SkoleAdapter → `/api/skole/v1/utdanning/…` (role `Utdanning.Write`, `Idempotency-Key`); Utdanning is a domain inside SkoleService (own schema) | SkoleAdapter code exists in `M2LB (7)` and `M2LB (3)` only. The CDC capture is not live (the order is a draft; CdcReplay shows AwaitingCapture). Code translations are empty |
| **Period lock → Bufdata** | Lock day 11 at 06:00 Oslo time, July skipped; Open → Locking → Locked → Delivered. Write-once Parquet `period=YYYY-MM/data.parquet` in `skolenaervaer-rapporter` / `skolenaervaer-dekning`, `_manifest.json` last, then `SkolenaervaerPeriodClosed` on **`skole.perioder`** | English-named generation only. `Delivery:Storage:BlobEndpoint` is empty in source. No Bufdata consumer exists in any package |

Other Service Bus topics SkoleService publishes: `skole.rapporter` (report changed) and `skole.utdanning`. In BirkNext, the Period-closed integration identity is `skole.perioder` plus both event names (fixed in this task, BN-1/BN-2).

## 4. What BirkNext can and cannot show

- **Can show:**
  - Source recognition by component or project (MU in `Tiltak 2 (1)`; four M2LB candidates, never chosen automatically).
  - Contract evidence per snapshot (OpenAPI and XSD, by generation).
  - Messaging channels and producers or consumers (Source Analysis).
  - The curated guide.
  - Readiness and prerequisites per journey.
- **Cannot show:**
  - Runtime state of MU, MM, SkoleService or Bufdata. No reader exists; runs stay not observed unless evidence is recorded.
  - Which generation is deployed.
  - Whether Bufdata receives anything.
