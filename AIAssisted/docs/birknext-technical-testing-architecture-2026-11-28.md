# Technical Testing: arkitektur (2026-11-28)

## 1. Prinsipp

**Én felles kildeanalyse og sporbarhet, egne versjonerte regler, to arbeidsflater og én felles testhistorikk.**

```
Felles kildeanalyse og sporbarhet
(Markdown · frontend · REST · GraphQL · backend · integrasjoner · testkode)
        │   ScenarioDiscoveryResult (én inventar, kildebaserte identiteter tss-…)
        ▼
Egne, versjonerte klassifiseringsregler
E2ERules (e2e-rules/1) · TestWorkClassifier (test-work/1)   — shared/TestWorkClassification.cs
        │
   ┌────┴──────────────────┐
   ▼                       ▼
Manual Testing           Technical Testing
forretningsflyt,         tekniske flyter, kontrakter,
manuell forberedelse     automatisering, observasjoner
   └────┬──────────────────┘
        ▼
Felles testhistorikk og relasjoner
(test_plan_drafts: planer, testtilfeller, revisjoner, godkjenning, kjøringer, kildeversjoner)
```

**Kildeteknologi avgjør ikke hvem som tester.** Et GraphQL-kall kan inngå i en manuell E2E-prosess, og en frontendsjekk kan være rent teknisk. Reglene leser bare det produsentene allerede har registrert:

- kapabilitet
- kandidattype
- evidenstype
- kontrakttype
- en persons eksplisitte valg (Manuell, Teknisk eller Begge)

Tittel og filnavn brukes aldri.

## 2. Ansvar og eierskap

| Domene | Eier | Felles eller per arbeidsflate |
|---|---|---|
| Kildeanalyse, scenarioforslag, identiteter | `TestScenarioDiscoveryEngine` | Felles |
| E2E-status og arbeidskategori | `E2ERules`, `TestWorkClassifier` (shared, rene funksjoner) | Felles regler, ulik standardvisning |
| Testplaner og testtilfeller | `TestPlanSuggestionService` (`test_plan_drafts`) | **Én plan-entitet.** Ingen teknisk kopi. Testtilfellets `TestKind` sier Manuell eller Teknisk. |
| Kjøringer i planer | `ManualTestRuns` (`occ:`-hendelser) | Felles |
| Kjøringer i spesialmoduler (API QR, IQR, Critical E2E, ytelse) | Spesialmodulene, per målmiljø | Technical Testing («Verify & investigate») |
| Integrasjonsreiser og suiter | Integration Testing | Technical Testing, spesialområde. Ikke en konkurrerende plan- eller E2E-modul. |

## 3. Klassifisering (`test-work/1`)

Kategorier:

- `ManualBusiness`, `ManualE2E`
- `TechnicalApi`, `TechnicalGraphQl`, `TechnicalIntegration`, `TechnicalE2E`
- `Hybrid`
- `TechnicalOther` (frontendkvalitet, ytelse). Denne er en utvidelse av listen i oppgaven, slik at ingen teknisk kandidat feilplasseres.
- `NeedsClassification`

**Overflate** (`TechnicalSurface`) leses fra evidensen:

- `contract-operation` med `OpenApi` gir REST.
- `GraphQlSchema` eller `GraphQlOperations` gir GraphQL.
- Integrasjonsreise eller integrasjonstype gir meldinger.
- `ui-source` gir brukergrensesnitt.

**Standardvisning:**

- Manual viser `ManualBusiness`, `ManualE2E` og `Hybrid`.
- Technical viser alt teknisk, `Hybrid` og `NeedsClassification`.
- Hybride flyter vises i **begge**.

## 4. E2E-regler (`e2e-rules/1`)

| Status | Når (evidens) |
|---|---|
| **Bekreftet E2E** | Deklarert integrasjonsreise (komponentkjede gjennom to eller flere systemer), eller en guidescenario-flyt der stegene går gjennom mer enn ett system. |
| **E2E-kandidat** | En testspesifikasjon som erklærer E2E (TEST-E-…), en brukerflyt funnet i UI-kilden, eller en eksisterende eller utviklerskrevet test gjennom meldinger eller flere komponenter. Grensen er erklært eller antydet, ikke bevist. |
| **Ikke E2E** | Én operasjon, én side eller ett akseptansescenario uten oppgitt systemgrense. |
| **Må avklares** | En produsent reglene ikke kjenner. |

**Utførelse:**

- En persons valg (Manuell, Teknisk eller Begge) vinner alltid.
- Uten valg gjelder dette:
  - En E2E-flyt med både manuell og teknisk arbeidsflate er **Hybrid**.
  - Ellers gjelder primær arbeidsflate.
  - En testspesifikasjon er teknisk, eller hybrid når den er E2E.

**Hver vurdering har begrunnelser** (`Reasons`) og regelversjon. Det gjør dem forklarbare og testbare.

Den generelle Spec Kit-parseren er **ikke** endret.

## 5. Teknisk testoversikt

Tallene viser kildebevis og forberedelse, med eksakte poster bak hvert tall.

| Tall | Betyr | Klikk |
|---|---|---|
| Tekniske E2E-flyter | Bekreftet og kandidat, teknisk eller hybrid | Finn og analyser `?work=e2e` (nøyaktig de kandidatene) |
| REST/API, GraphQL, Integrasjon | Etter overflate | `?work=api`, `graphql`, `integration` |
| Med automatisert test | En test i kilden som peker på **selve** scenarioet. **Ikke kjørt.** | `?work=automated` |
| Må klassifiseres | Ukjent produsent | `?work=clarify` |
| Tekniske testtilfeller, Godkjent, Påvirket av kildeendring | Lagrede testtilfeller som er tekniske | Planlegg og klargjør med nøyaktig de tilfellene (samme utvalgskontrakt som Testoversikt) |

**Kjørt, bestått, feilet og blokkert** vises der de er registrert: i planenes kjøring og i «Verify & investigate». De utledes aldri av kildebevis.

## 6. Scope

- Manual Testing støtter prosjekt, modul, spesifikasjon og dokument, med eksakte identiteter (V12).
- Teknisk testoversikt bruker i denne første versjonen **prosjektets** siste scenarioforslag. Scope-kombinasjoner for de tekniske tallene er ikke laget (se implementasjonsdokumentet).
- E2E-flyter på tvers av moduler trenger en eksplisitt regel for medlemskap i scope. Forslag: en flyt hører til en modul når minst ett av stegene eller komponentene ligger der. Ikke implementert.

## 7. Manuell og teknisk forberedelse

| | Manual Testing | Technical Testing |
|---|---|---|
| E2E | Brukerrettede E2E-flyter | API-, GraphQL- og integrasjons-E2E |
| Scenarioer | Akseptansescenarioer | Tekniske testscenarioer |
| Forutsetninger | Forutsetninger og testdata | Kontrakter, miljø og avhengigheter |
| Steg | Manuelle teststeg | Kall, meldinger og tekniske kontrollpunkter |
| Forventet resultat | Forventet forretningsresultat | Forventet respons og nedstrøms resultat |

Samme flyt kan ha en manuell og en teknisk testvariant i samme plan. Varianten skilles på testtilfellets `TestKind`, ikke ved å kopiere planen.

Tekniske felt (mål, inngangsoperasjon, autentisering, nedstrøms forventning, observerbarhet, feil og retry) finnes allerede delvis i kandidatenes maler (`Template`: operation, input, expected-response, schema, errors). Egne felt på tekniske testtilfeller er neste steg.

## 8. V2 (2026-11-28): presedens, populasjoner, scope, testtilfeller og utførelse

### 8.1 Tre uavhengige vurderinger (`e2e-rules/2`, `test-work/2`)

| Vurdering | Verdier | Grunnlag |
|---|---|---|
| **Hva** testes (`TestSubject`) | Forretnings-E2E, teknisk E2E, REST/API, GraphQL, integrasjon, forretningsfunksjon, annen teknisk, ukjent | Evidensen: kapabilitet, kandidattype og kontrakttype. Teknologien alene avgjør aldri E2E. |
| **Hvordan** (`Proposed`, `Decided`, `Execution`) | Manuell, teknisk eller begge | Reglene foreslår. Personens valg avgjør. Et avvik vises (`Deviates`) og angres aldri. |
| **Hvor sikkert** (`E2EStatus` + `E2EEvidence`) | Bekreftet, kandidat, ikke E2E eller må avklares | Bekreftet krever **startpunkt, kryssing av systemgrense, observerbart utfall og kildekobling**, med en *bevist* kjede (deklarert reise eller guideflyt gjennom flere systemer). Mangler noe, blir det kandidat med de manglende punktene navngitt. |

**Presedens:**

1. Kildebevis
2. Deterministiske regler
3. Personens metodevalg
4. Revisjon (hvem og når, `OverviewMethodDecision`)
5. Ny vurdering når kilden endres

Et metodevalg gjør **aldri** en kandidat til bekreftet E2E. En API- eller GraphQL-kontrakt alene, en frontendkomponent alene eller en kodetest alene er aldri E2E eller en kjøring.

### 8.2 Personens valg (Manuell, Teknisk eller Begge)

- **Lagring:** StoryGrouping-dokumentet per spesifikasjon og import (`TestKinds`), med kandidat-ID som nøkkel. Det har hvem og når, og er versjonert med dokumentets revisjon.
- **Leses nå av begge arbeidsflatene:** Testoversikt-svaret har `MethodDecisions` med kildeidentiteten fra prosjektets gjeldende scenarioforslag.
- **Eldre valg:** et valg uten identitet, der kandidaten ikke lenger finnes, blir ikke koblet til noe.
- **Begrensning (åpen):** valgene følger ikke med til en ny import. De er lagret per import med kandidat-ID. Se implementasjonsdokumentet.

### 8.3 Populasjonskontrakter

| Arbeidsflate | Før V2 | Fra V2 |
|---|---|---|
| **Manual Testoversikt** | Alle scenarioer i lagrede spesifikasjoner og alle testtilfeller i aktive planer | Det samme, men uten rent teknisk arbeid (`O.ForManual`): testtilfeller med type «Teknisk», scenarioer valgt «Teknisk» (og deres testtilfeller uten egen type), og dokumenterte modultester uten erklært E2E. «Begge», hybride flyter og uklassifiserte eldre poster blir. Plankort viser planens egne totaler (planer er felles). |
| **Teknisk testoversikt** | (V1) prosjektets tekniske kandidater og testtilfeller | Tekniske og hybride kandidater, samt testtilfeller med type «Teknisk» eller uten type med teknisk eller hybrid scenario. Testtilfeller med type «Manuell» aldri. |

**Fem ting skilles alltid:**

- unike flyter (én per kildeidentitet)
- scenarioforslag
- forberedte testtilfeller
- planlagte forekomster
- registrerte resultater

Ett fysisk testtilfelle telles én gang per tall.

### 8.4 Scope

Teknisk testoversikt og Finn og analyser bruker **samme velger og samme scope-kontrakt** som Testoversikt (`SpecificationScopePicker`, `TestScopeReader`, `O.Scope`).

**Kandidatenes kildefiler:**

- spesifikasjonen til brukerhistorien
- stidelen av hver evidensreferanse

Modulen bestemmes av den kanoniske modulregelen (`ModuleStructure`).

**Medlemskap:**

| Nivå | En kandidat er med når … |
|---|---|
| Prosjekt | Alltid |
| Modul | Minst én av filene ligger i modulen. **En flyt på tvers av moduler er med i hver modul den berører**, telles én gang per scope og merkes «på tvers av moduler». Den eies aldri av én modul. |
| Spesifikasjon | En fil i pakken (og i modulen når den er valgt) |
| Dokument | Nøyaktig den filen |

Et ugyldig eller utdatert valg gir en tom liste og en melding, aldri en bredere populasjon.

**Testtilfeller i scope:** samme utsnitt som Testoversikt (`O.Of`).

### 8.5 Teknisk testtilfelle

Et teknisk testtilfelle er et vanlig testtilfelle i den felles planen. Detaljene lagres i `TemplateValues` under `tech.*` (`shared/TechnicalCaseTemplates.cs`, `technical-case/1`). Det gir samme lagring, revisjon og historikk som andre felt.

- **Felt:** testmål, system, startpunkt, rute eller operasjon, autentisering, miljø, testdata, inndata, avhengigheter, respons, tilstand etterpå, hendelser og meldinger, lagring, observerbarhet, korrelasjon, opprydding, feil og retry, kildebevis.
- **Maler** per type (REST, GraphQL, integrasjon, teknisk E2E, hybrid, annen) bestemmer hva som vises først. Resten ligger under «Avanserte tekniske detaljer». Ingen felt er påkrevd.
- **Manuelle testtilfeller ser aldri feltene.**
- **Hemmeligheter:** en verdi som ser ut som et passord, token eller en nøkkel, avvises av serveren (400 `secret-value`) og varsles i skjemaet.

### 8.6 Utførelse per testtilfelle

Samme hendelsesmodell som før (`ScenarioExecutionEvent`, `occ:`-poster), med valgfrie felt:

- `CaseId`
- `Environment`
- `PlanRevision`
- `Expected`, `Actual`
- `Evidence`

**Status per testtilfelle** er siste hendelse for testtilfellet selv eller for hele forekomsten.

**Status per forekomst** og planens sammendrag er uendret: siste hendelse av begge typer. Statusmodellen er den eksisterende (Ikke kjørt, Bestått, Feilet, Blokkert). «Pågår» og «Ikke aktuelt» finnes ikke i modellen og er ikke lagt til.

Ingenting kjøres av BirkNext. «Bestått» utledes aldri av kildebevis.

### 8.7 Integration Testing

Deklarerte reiser er **bekreftet teknisk E2E** i den felles regelmotoren. Det finnes ingen egen E2E-definisjon. Knutepunktet lenker til:

- Finn og analyser (bekreftede flyter)
- Teknisk testoversikt
- Testplaner

Diagnostikk, topologi, suiter og IQR er uendret.
