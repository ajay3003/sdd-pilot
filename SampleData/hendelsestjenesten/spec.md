# Hendelsestjenesten — Feature Specification

**Feature**: Hendelsestjenesten (Hendelsesdokumentasjon for barn i barnevernet)
**Version**: 1.0
**Date**: 2026-04-24
**Status**: Draft

---

## Overview

Hendelsestjenesten gir saksbehandlere i barnevernet en felles, pålitelig kilde for å se dokumenterte hendelser knyttet til barn under omsorg. Tjenesten tar imot hendelsesdata fra eksisterende systemer, lagrer disse med full historikk, og gjør dem tilgjengelig via et strukturert grensesnitt med streng tilgangskontroll.

Behovet springer ut av at barnevernet er underlagt strenge lovkrav til dokumentasjon og sporbarhet. Hendelser som tvangsbruk, rømming, uteblivelse og bortføring må registreres nøyaktig, bevares uten mulighet for sletting, og deles kontrollert med relevante fagpersoner.

---

## Clarifications

### Session 2026-04-24

- Q: Hva er tilgjengelighetsmålet for Hendelsestjenesten i produksjon? → A: 99,9 % oppetid (~9 timer nedetid per år)
- Q: Hva er forventet oppførsel ved midlertidig utilgjengelig meldingsplattform (Service Bus)? → A: Outbox-kø — hendelsen lagres alltid; publisering retryes til det lykkes; operatørvarsling ved langvarig blokkering
- Q: Hva er lovpålagt minimumsgrense for oppbevaring av hendelsesdata? → A: Minimum 10 år; sletting kun ved eksplisitt lovhjemmel og etter avtale med behandlingsansvarlig
- Q: Hva er forventet belastningsskala for tjenesten? → A: Opp til 2 000 samtidige brukere; ubegrenset antall hendelser per barn
- Q: Hva skjer dersom en hendelse aldri får barnidentitet koblet til seg? → A: Hendelsen bevares uknyttet i systemet; operatørvarsling utløses etter definert ventetid (f.eks. 30 dager)

### Session 2026-04-27

- Q: Hva er det korrekte navnet på tjenesten som leverer barnidentitet asynkront? → A: Persontjenesten
- Q: Hva er kravet til kryptering av data i hvile? → A: Transparent kryptering på lagringssjiktet (infrastrukturnivå)
- Q: Hvilken API-protokoll og versjonsstrategi skal brukes? → A: REST (HTTP/JSON) med URL-basert versjonering (f.eks. /api/v1/)
- Q: Hva er den kanoniske termen for rømning-hendelseskategorien — "rømming" eller "rømning"? → A: rømning (juridisk korrekt norsk term fra barnevernsloven)
- Q: Hva er krav til operasjonell observabilitet utover revisjonslogg? → A: Full stack — metriker (feilrate, latens, kødybde) + strukturert logging + distribuert tracing (OpenTelemetry)
- Q: Hva er den unike nøkkelen fra BiRK som brukes for idempotent mottak? → A: Ekstern BiRK-tildelt UUID/ID per hendelse; brukes som idempotenssnøkkel
- Q: Hvordan varsles Hendelsestjenesten om at et barn er registrert i Persontjenesten? → A: Persontjenesten publiserer "barn registrert"-hendelse til Service Bus; Hendelsestjenesten abonnerer og trigger kobling
- Q: Hva er kravet til rate limiting for lese-API-et? → A: Håndteres på API-gateway-nivå per klient/token — ikke applikasjonens ansvar
- Q: Hva er konflikthåndteringsstrategien ved samtidige innmatingsforsøk for samme kildeId? → A: Last-write-wins basert på kildesystemets tidsstempel fra BiRK; eldre tidsstempel forkastes
- Q: Hva er standard sidestørrelse og maksgrense for hendelseslisten? → A: Standard 25 per side, maksimum 100 per side
- Q: Hvilken autentiseringsmekanisme brukes for API-kallere? → A: OAuth2/OIDC Bearer-token validert mot plattformens identitetstjeneste for alle klienter
- Q: Hva er datastrukturen for Involvert-entiteten? → A: Alltid en referanse til plattformens identitetstjeneste — ingen ekstern person uten kjent plattform-ID
- Q: Hvilke felt er obligatoriske i en innkommende hendelse fra BiRK? → A: kildeId, hendelsestype, tidspunkt og barnIdentitet — hendelse uten barnIdentitet avvises (erstatter tidligere beslutning om asynkron kobling)
- Q: Hva inneholder meldingen som publiseres til andre fagsystemer (FR-08)? → A: Tynn notifikasjon: kildeId, barnIdentitet, hendelsestype, tidspunkt og lenke til ressurs — forbrukere henter detaljer via API
- Q: Validerer Hendelsestjenesten barnIdentitet mot Persontjenesten ved mottak? → A: Nei — validering er adapterens ansvar; Hendelsestjenesten lagrer uten oppslag mot Persontjenesten
- Q: Hva er canonical HendelsesType-navn — "Rømming" (konstitusjonen) eller "rømning" (juridisk norsk)? → A: "Rømming" — konstitusjonen er autoritativ; konsistens på tvers av alle M2LB-dokumenter
- Konstitusjonell justering (BiRK Hendelsesadapter-konstitusjon §1.3, §3.1): barnIdentitet er ikke obligatorisk ved mottak — adapter kan levere med BarnId=null; Hendelsestjenesten kobler asynkront via TjenesteOpprettet fra Tjenestemodul
- Konstitusjonell justering (§3.4): Involvert er fritekst (BiRK RegAv) i M01 — plattform-ID-krav utsettes til M02+
- Konstitusjonell justering (§3.1, Vedlegg A): korrekt avhengighet er Tjenestemodul (ikke Persontjenesten); Hendelsestjenesten abonnerer på TjenesteOpprettet
- Konstitusjonell justering (§3.1): BirkTiltakPK er teknisk felt på Hendelse når BarnId er null

---

## Problem Statement

Saksbehandlere mangler i dag en enhetlig oversikt over hendelser knyttet til det enkelte barn. Data ligger fragmentert i eldre systemer, uten standardisert tilgang eller sporbarhet for hvem som har sett hva. Dette gjør det vanskelig å:

- Få en samlet tidslinje over et barns hendelseshistorikk
- Sikre at kun autoriserte personer ser sensitive detaljer
- Etterleve lovpålagte krav om dokumentasjon og revisjonsspor

---

## Goals

1. Gi saksbehandlere en komplett og pålitelig hendelsestidslinje per barn
2. Håndheve rettighetsstyrt tilgang til sensitive hendelsesdetaljer
3. Sikre at alle hendelsesdata er uforanderlige og fullt sporbare
4. Gjøre hendelsesdata tilgjengelig for videre behandling i andre fagsystemer

---

## Non-Goals

- Tjenesten registrerer ikke hendelser direkte — det gjøres av integrerende systemer (fase 1)
- Tjenesten utfører ingen faglig vurdering av hendelsenes konsekvenser
- Tjenesten sender ikke varslinger til parter utenfor systemet
- Tjenesten administrerer ikke tiltak eller oppfølgingsplaner

---

## User Scenarios & Testing

### Scenario 1: Saksbehandler ser hendelsestidslinje for et barn

**Gitt** at saksbehandler har tilgang til barnet og rettigheten `Hendelse:HentHendelserForBarn`
**Når** saksbehandler åpner hendelsesvisningen for barnet
**Så** vises en liste over hendelser sortert med nyeste øverst, med type, dato og status

**Forventet resultat**: Hendelseslisten vises innen rimelig tid. Listen kan filtreres på hendelsestype og blades gjennom ved mange treff.

---

### Scenario 2: Saksbehandler åpner detaljert hendelse med tvangsdetaljer

**Gitt** at saksbehandler har tilgang til barnet og rettigheten `Hendelse:HentHendelse`
**Og** saksbehandler har rettigheten `Hendelse:SeInngrepDetalj`
**Når** saksbehandler velger en tvangshendelse fra listen
**Så** vises full detalj inkludert rettslig grunnlag, politiinvolvering og tvangsprotokollstatus

**Forventet resultat**: Alle detaljfelter for tvangsbruk er synlige. Dersom saksbehandler mangler `SeInngrepDetalj`-rettigheten, skjules disse feltene.

---

### Scenario 3: Saksbehandler åpner hendelse uten tilstrekkelige rettigheter

**Gitt** at saksbehandler mangler rettigheten `Hendelse:SeInvolverte`
**Når** saksbehandler åpner en hendelse som har registrerte involverte personer
**Så** vises hendelsens grunndata, men seksjonen for involverte er ikke synlig

**Forventet resultat**: Systemet returnerer hendelsen uten de beskyttede feltene. Ingen feilmelding vises for manglende rettighet — feltet er simpelthen ikke tilstede.

---

### Scenario 4: Hendelsesdata innmating fra BiRK

**Gitt** at Hendelsesadapteren mottar en ny eller endret hendelse fra BiRK via meldingsstrøm
**Når** adapteren sender inn hendelsesdataene
**Så** lagres hendelsen som ny versjon (eller registreres som uendret) og knyttes til rett barn og tjeneste

**Forventet resultat**: Samme data sendt inn to ganger fører ikke til ny versjon. Endrede data fører til at ny versjon opprettes og gammel versjon bevares. Hendelsen publiseres videre til andre fagsystemer.

---

### Scenario 5: Hendelse innmeldt uten kjent barn — automatisk kobling

**Gitt** at adapteren sender inn en hendelse der `barnIdentitet` ikke er kjent (BirkTiltakPK satt, BarnId = null)
**Når** Tjenestemodul publiserer `TjenesteOpprettet`-hendelse som matcher BirkTiltakPK
**Så** kobles hendelsen automatisk til riktig `barnIdentitet` og `tjenesteId` uten manuell behandling

**Forventet resultat**: Ingen hendelsesdata går tapt. Koblingen skjer automatisk. Etter kobling er `barnIdentitet` låst og kan ikke endres.

---

### Scenario 6: Historikk og versjonsvisning

**Gitt** at en hendelse har blitt endret og finnes i flere versjoner
**Når** saksbehandler åpner hendelsesdetaljen
**Så** er versjonhistorikken tilgjengelig og alle tidligere versjoner kan ses

**Forventet resultat**: Ingen versjon er slettet. Saksbehandler kan se hva som var registrert ved hvert endringstidspunkt.

---

## Functional Requirements

### FR-01: Motta og lagre hendelsesdata

- Systemet skal ta imot tvangsbruk (inngrep) og rømming/uteblivelse/bortføring fra integrerte systemer
- Innmating skal være idempotent: uendrede data fører ikke til ny versjon; idempotens bestemmes av ekstern BiRK-tildelt UUID (`kildeId`)
- Endret innhold fører alltid til at ny versjon opprettes og gammel versjon bevares
- Ved samtidige innmatingsforsøk for samme `kildeId` gjelder last-write-wins basert på kildesystemets tidsstempel; melding med eldre tidsstempel forkastes
- Obligatoriske felt ved mottak: `kildeId`, `hendelsestype`, `tidspunkt` — hendelse uten ett av disse avvises med valideringsfeil. `tidspunkt` tilsvarer `FraDato` (dato) og `FraTidspunkt` (klokkeslett, valgfritt) i datamodellen
- `barnIdentitet` er valgfritt ved mottak; dersom null, lagres `BirkTiltakPK` som teknisk kobling-nøkkel
- Dersom `barnIdentitet` er null og `BirkTiltakPK` ikke er oppgitt, avvises hendelsen med 422 Unprocessable Entity — det er ikke mulig å koble hendelsen på et senere tidspunkt
- Øvrige feltvalideringer håndheves ved mottak: gyldige referanseverdier for hjemmel, rømmingskategori og protokollstatus

### FR-02: Versjonert og uforanderlig historikk

- Ingen hendelsesversjon kan slettes
- Alle versjoner skal være tilgjengelig for visning
- Tidspunkt for opprettelse av hver versjon skal registreres

### FR-03: Asynkron kobling til barn

- Hendelse kan mottas med `barnIdentitet = null`; i så fall lagres `BirkTiltakPK` som teknisk felt
- Hendelsestjenesten abonnerer på `TjenesteOpprettet`-hendelse fra Tjenestemodul; ved mottak kobles ventende hendelser til riktig `barnIdentitet` og `tjenesteId` automatisk
- Etter kobling er `barnIdentitet` og tjenestetilknytning låst og kan ikke endres
- Hendelsen bevares uknyttet i systemet — slettes aldri automatisk som følge av manglende kobling
- Dersom `barnIdentitet` ikke ankommer innen 30 dager, utløses operatørvarsling

### FR-04: Hendelsestidslinje for saksbehandler

- Saksbehandler kan hente alle hendelser for ett barn
- Listen skal støtte filtrering på hendelsestype
- Listen skal støtte paginering ved mange treff; standard sidestørrelse er 25, maksimum er 100 per side
- Sortering skal være nyeste hendelse øverst som standard

### FR-05: Detaljvisning av hendelse

- Saksbehandler kan se full detalj for én hendelse, inkludert alle versjoner
- Feltene for involverte, tvangsdetaljer og rømmingsdetaljer vises kun for brukere med tilstrekkelig rettighet
- Manglende rettighet medfører at feltet utelates — ikke en feilsituasjon

### FR-06: Rettighetsstyring

- Fem separate operasjoner styrer tilgang (se Operasjoner)
- Alle API-kallere autentiseres via OAuth2/OIDC Bearer-token validert mot plattformens identitetstjeneste
- Tilgangskontroll delegeres til autorisasjonstjenesten — ingen lokal tilgangsstyring
- Alle leseoperasjoner som returnerer hendelsesdata for ett barn skal logges for revisjonsformål

### FR-07: Referansedata

- Systemet eier og eksponerer referansedataene: hendelsestyper, rettslige grunnlag (hjemmel), rømmingskategorier og tvangsprotokolstatuser
- Rettslige grunnlag har gyldighetsperiode (fra/til dato)
- Historiske hjemler skal kunne hentes for bakoverkompatibilitet

### FR-08: Publisering av hendelseshendelse til andre fagsystemer

- Når en hendelse lagres, publiseres en tynn notifikasjon til andre abonnenter med feltene: `kildeId`, `barnIdentitet`, `hendelsestype`, `tidspunkt` og URL-lenke til hendelsesressursen — forbrukere henter full detalj via API ved behov
- Hendelsen lagres alltid ved mottak; publisering til meldingsplattformen skjer via outbox-mekanisme
- Dersom meldingsplattformen er midlertidig utilgjengelig, retryes publisering kontinuerlig til det lykkes
- Operatørvarsling skal utløses dersom publisering er blokkert utover en definert terskel
- Ingen lagret hendelse skal forbli upublisert uten aktiv oppfølging

### FR-09: Helsestatusendepunkter

- Systemet eksponerer endepunkter for livsstatus (liveness) og klarthet (readiness)
- Brukes av infrastruktur for automatisk overvåking og restart

---

## Success Criteria

| Kriterium | Målverdi |
|-----------|----------|
| Saksbehandler kan hente hendelsestidslinje uten merkbar ventetid | Under 2 sekunder ved opp til 2 000 samtidige brukere (95. persentil) |
| Ingen hendelsesversjon kan slettes eller overskrives | 0 slettede versjoner etter system i produksjon |
| Hendelsesdata oppbevares i samsvar med lovkrav | Alle data tilgjengelig i minimum 10 år; sletting kun ved dokumentert lovhjemmel |
| Samme hendelsesdata innmeldt to ganger gir ingen ny versjon | 100 % idempotent mottak |
| Hendelse med ukjent barn kobles automatisk når TjenesteOpprettet ankommer | Alle ventende hendelser kobles innen 5 minutter etter at TjenesteOpprettet er publisert |
| Sensitive detaljer skjules for brukere uten rettighet | 0 uautoriserte felteksponeringer ved kontrollert test |
| Alle leseoperasjoner for hendelsesdata logges | 100 % revisjonsdekning for `HentHendelserForBarn` og `HentHendelse` |
| Publisering til andre fagsystemer skjer atomisk med lagring | Ingen hendelse lagret uten tilsvarende publisering |
| Tjenesten er tilgjengelig i produksjon | Minimum 99,9 % oppetid (~9 timer planlagt/uplanlagt nedetid per år) |

---

## Key Entities

| Entitet | Beskrivelse |
|---------|-------------|
| Hendelse | En dokumentert episode knyttet til ett barn; identifisert unikt av ekstern `kildeId` (BiRK-tildelt UUID). `BirkTiltakPK` lagres som teknisk felt når `barnIdentitet` ikke er kjent ved mottak |
| HendelsesVersjon | En uforanderlig øyeblikkstilstand av hendelsesdataene |
| HendelsesType | Klassifisering: Inngrep, Rømming, Uteblivelse, Bortføring |
| Involvert | Person involvert i hendelsen — plattform-ID i M02+; fritekst (BiRK `RegAv`) tillatt i M01 |
| InngrepDetalj | Tilleggsdata spesifikt for tvangshendelser (hjemmel, politiinvolvering, protokoll) |
| RømmingsDetalj | Tilleggsdata for rømming/uteblivelse/bortføring (varighet, politikontakt, kategori) |
| HjemmelType | Rettslig grunnlag med gyldighetsperiode |
| RømmingKategoriType | Underkategori for rømmingshendelser |

---

## Operasjoner (Tilgangsstyring)

| Operasjon | Omfang | Formål |
|-----------|--------|--------|
| `Hendelse:HentHendelserForBarn` | Barnespesifikk | Hente hendelsestidslinje |
| `Hendelse:HentHendelse` | Barnespesifikk | Se detaljert hendelse |
| `Hendelse:SeInvolverte` | Barnespesifikk | Se involverte personer |
| `Hendelse:SeInngrepDetalj` | Barnespesifikk | Se tvangsdetaljer |
| `Hendelse:SeRommingsDetalj` | Barnespesifikk | Se rømmings-/uteblivelsesdetaljer |

---

## Assumptions

1. **Fase 1 er skrivebeskyttet for brukere**: Kun integrerte systemer (via maskin-til-maskin) kan registrere hendelser. Saksbehandlere har kun lesetilgang.
2. **BiRK er eneste datakilde i fase 1**: All hendelsesdata kommer fra BiRK via adapteren. Direkte registrering fra institusjoner er fase 2.
3. **Autorisasjonstjenesten er alltid tilgjengelig**: Dersom autorisasjonstjenesten er utilgjengelig, nektes tilgang — ingen åpen fallback.
4. **Barnidentitet er unik og stabil**: Når `barnIdentitet` er koblet, endres den ikke. Hendelse kan lagres midlertidig uten `barnIdentitet` (BirkTiltakPK satt); kobling skjer automatisk via `TjenesteOpprettet` fra Tjenestemodul.
5. **Outbox-mønster sikrer leveringsgaranti**: Publisering til fagsystemer håndteres via outbox-mekanisme — hendelsen lagres alltid, og publisering retryes til det lykkes. Midlertidig utilgjengelighet hos meldingsplattformen medfører ikke tap av hendelse.
6. **Revisjonslogg er ikke søkbar av sluttbrukere**: Loggen er utelukkende for etterforsknings- og kontrollformål av systemadministratorer.
7. **Lovpålagt oppbevaringstid er minimum 10 år**: Hendelsesdata og tilhørende versjoner skal oppbevares i minimum 10 år. Sletting krever eksplisitt lovhjemmel (f.eks. barnevernsloven eller arkivloven) og skriftlig avtale med behandlingsansvarlig. Systemet skal aldri slette data autonomt.
8. **Skaleringsmål er 2 000 samtidige brukere**: Systemet skal dimensjoneres for opp til 2 000 samtidige saksbehandlere uten degradering av ytelse. Antall hendelser per barn er ubegrenset.
9. **Kryptering av data i hvile håndteres på infrastrukturnivå**: All lagret hendelsesdata skal krypteres transparent på lagringssjiktet (disk/database-nivå). Applikasjonen har ingen ansvar for felt-nivå kryptering.
10. **API-protokoll er REST/HTTP/JSON med URL-basert versjonering**: Alle eksterne endepunkter eksponeres som REST-API med JSON-payload og versjoneres via URL-prefix (f.eks. `/api/v1/`). Breaking changes introduseres alltid som ny versjon.
11. **Full observabilitets-stack kreves**: Tjenesten skal eksponere metriker (feilrate, latens, kødybde for outbox), strukturert logging og distribuert tracing via OpenTelemetry. Dette er krav i fase 1, ikke fase 2.
12. **Rate limiting håndteres på API-gateway-nivå**: Throttling per klient/token er ikke applikasjonens ansvar. Hendelsestjenesten forutsetter at API-gateway håndhever dette før trafikk når tjenesten.

---

## Dependencies

| Avhengighet | Type | Formål |
|-------------|------|--------|
| Autorisasjonstjenesten | Runtime | Tilgangskontroll for alle operasjoner |
| Tjenestemodul | Meldingsbasert (Service Bus) | Publiserer `TjenesteOpprettet`; Hendelsestjenesten abonnerer for asynkron barnkobling |
| BiRK / Hendelsesadapter | Inngående integrasjon | Datakilde for hendelser (fase 1) |
| Meldingsplattform (Service Bus) | Runtime | Publisering og konsumering av domenehendelser |

---

## Out of Scope

- Faglig vurdering av hendelsers alvorlighetsgrad
- Varsling til foresatte, advokater eller andre eksterne parter
- Tiltak, oppfølgingsplaner eller dokumenter knyttet til hendelsen
- Direkte registrering fra institusjonsansatte (fase 2)
- Søk på tvers av barn

---

## Open Questions

Ingen åpne spørsmål av kritisk betydning — alle vesentlige valg er dekket av eksisterende dokumentasjon og gjeldende plattformkonstitusjon.
