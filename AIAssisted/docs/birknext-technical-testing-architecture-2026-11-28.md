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
