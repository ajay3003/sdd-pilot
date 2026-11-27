# BirkNext OpenSpec-kompatibilitets- og migreringsaudit

**Dato:** 2026-11-27  
**Omfang:** Statisk, skrivebeskyttet kildekode- og dokumentasjonsgjennomgang. Ingen runtime, import, bygging, tester, database eller eksterne systemer ble kjørt.

## 1. Sammendrag

BirkNext har en generell Markdown-import-/katalog-/evidenskjede, men semantikk og gruppering inneholder Spec Kit-orienterte forventninger: specs/<feature>, FR-/US-lignende ID-er, User Story og acceptance scenario-struktur. OpenSpec-filer kan derfor bli funnet som dokumenter uten at krav og scenarioer representeres korrekt.

OpenSpec skiller gjeldende spesifikasjoner (openspec/specs), aktive endringsforslag (openspec/changes) og arkivert endringshistorikk (changes/archive). BirkNext-koden som ble undersøkt har ingen påvist modell for disse tilstandene eller deltaene ADDED/MODIFIED/REMOVED/RENAMED. Aktivt forslag kan dermed potensielt blandes med gjeldende krav; arkiv kan gi dobbelttelling.

**Konklusjon: OpenSpec er ikke bekreftet støttet, og migrering er ikke trygg å automatisere i dag.** Behold Spec Kit-støtten. Nye OpenSpec-koblinger må gjennomgås eksplisitt av et menneske. Eksisterende testtilfeller, planer, godkjenninger og kjøringshistorikk skal beholde opprinnelig kildeevidens.

## 2. Baseline og begrensninger

- **CONFIRMED:** Branch 008-traceability-first, HEAD 73b1327e (V10 completion), tre commits foran upstream.
- **CONFIRMED:** Arbeidstreet var allerede skittent med samtidige Manual Testing/Data Quality-endringer. Disse ble ikke endret. Denne rapporten er den eneste filen opprettet for auditten.
- **CONFIRMED:** Repository-søk fant ingen OpenSpec-fixture eller OpenSpec-adapter.
- **UNKNOWN:** Konkret OpenSpec-versjon, lokale konvensjoner og custom schemas for fremtidige prosjekter.
- Ingen runtime-atferd er bekreftet; app/import ble ikke startet. Ucommittet Data Quality-kode omtales som arbeidskopi/forslag, ikke ferdig produksjonsfunksjon.

## 3. OpenSpec-format undersøkt

Offisiell OpenSpec-dokumentasjon og v2.0.0 release ble kontrollert 2026-11-27. Beskrivelsen gjelder innebygd spec-driven-flyt. Custom schemas/config kan endre struktur; ingen konkret OpenSpec-pakke ble undersøkt.

Standardstruktur: openspec/specs/<capability>/spec.md for gjeldende oppførsel; openspec/changes/<change-id>/{proposal.md,design.md,tasks.md,specs/<capability>/spec.md} for endring; openspec/changes/archive/<date>-<change-id>/ for arkivert endring.

Dokumentasjonen beskriver ADDED, MODIFIED, REMOVED og i v2 også RENAMED (FROM/TO). Arkivering anvender delta på baseline og flytter change-mappen til arkivet. Kilder: [v2.0.0 release](https://github.com/Fission-AI/OpenSpec/releases/tag/v2.0.0), [concepts](https://github.com/Fission-AI/OpenSpec/blob/main/docs/concepts.md), [writing specs](https://github.com/Fission-AI/OpenSpec/blob/main/docs/writing-specs.md), [FAQ](https://github.com/Fission-AI/OpenSpec/blob/main/docs/faq.md).

## 4. Nåværende BirkNext-kildearkitektur

| Komponent | Faktisk observert | OpenSpec-risiko |
|---|---|---|
| Import | ProjectImport behandler workspace-dokumenter og snapshot. Filimport er ikke semantisk støtte. | Ukjent struktur kan importeres uten korrekt kravmodell. |
| Klassifisering | SampleArtifactClassifier bruker filnavn, sti, mappe og innholdssignaler; specs er et generisk hint. | Skiller ikke current/active/archive. |
| Gruppering | ModuleStructure.StrongRoot beskriver uttrykkelig Spec Kit-mønstre som .specify/ og specs/<feature>/. | OpenSpec capability/change kan bli gruppert heuristisk som feature/modul. |
| Parser | SpecificationCatalogService og SpecificationEvidenceBuilder kaller SpecExplorerService.Parse/BuildSemanticModel. Semantikken bruker blant annet FR/NFR/SC/US/AC/REQ-ID-er, User Story, Scenario og BDD-markører. | OpenSpec Requirement-navn krever ikke FR-ID. Scenario kan gjenkjennes uten korrekt capability-/requirement-kobling. |
| Katalog/evidens | Dokumentsti, ID/tittel og content hash inngår i katalog-/snapshot-evidens. | Proveniens er nyttig, men gir ikke OpenSpec-livssyklussemantikk; ID-er kan være tvetydige. |
| Data Quality arbeidskopi | ReferenceSpecParser beskriver forenklet Spec Kit-orientert parsing. IdentityDryRun er forslag/tørrkjøringskode. | Ikke OpenSpec-støtte eller gjennomført migrering. |

## 5. Kompatibilitetsmatrise

| Evne | Status | Begrunnelse |
|---|---|---|
| ZIP/Markdown i generisk import | PARTIAL | Filhåndtering finnes; OpenSpec-semantikk ubevist. |
| Oppdage openspec/specs Markdown | PARTIAL | Generiske specs-hint finnes, OpenSpec-semantikk ikke påvist. |
| Kravoverskrift uten FR-ID | UNSUPPORTED / LIKELY | Ingen OpenSpec-parser funnet; endelig runtime-konklusjon krever fixture. |
| Nested Scenario + Given/When/Then | PARTIAL | BDD-mønstre finnes, parent-kobling til id-løst krav/capability er uverifisert. |
| ADDED/MODIFIED/REMOVED/RENAMED | UNSUPPORTED | Ingen deltaoperasjonsmodell funnet. |
| Current vs active vs archived | UNSUPPORTED | Ingen status-/effektiv-baselinemodell funnet. |
| Custom schema | UNKNOWN | Ingen OpenSpec-config/schema loader påvist. |
| Snapshot/hash provenance | SUPPORTED generisk | Kildeversjonering finnes, men tolker ikke OpenSpec-semantikk. |
| Eksisterende Spec Kit | SUPPORTED for forventede konvensjoner | Dagens parser/tester/arbeidsflyt er bygget rundt disse. OpenSpec bør supplere. |

## 6. Import, dashboard og Manual Testing

**CONFIRMED:** Markdown-dokumenter klassifiseres og katalogiserte spesifikasjoner går gjennom dagens parser. Ingen regel ble funnet som skiller OpenSpec current, active change eller archive.

**LIKELY / POTENTIAL P1:** Canonical OpenSpec-dokumenter kan vises generisk, mens id-løse krav utelates eller mister forelder. Active delta kan forveksles med gjeldende kildegrunnlag. Archive kan telles i tillegg til oppdatert baseline. Dette kan gi feil krav-/scenarioantall, coverage, readiness og kildeendringsvarsler. At filer importeres betyr ikke at de tolkes.

| Manual Testing-del | Vurdering | Alvorlighet |
|---|---|---|
| Testoversikt | Counts/coverage kan blande forslag med gjeldende baseline eller mangle krav. | P1 POTENTIAL |
| Finn og forstå | Markdown kan vises uten stabil krav-ID eller korrekt scenario-parent. | P1 POTENTIAL |
| Planlegg og klargjør | Active delta kan fremstå vedtatt; E2E uten User Story er ikke garantert funnet. | P1 POTENTIAL |
| Testplaner/gjennomføring | Bevar gammel source identity; remapping uten review kan bryte sporbarhet. | P0 hvis historikk overskrives; funksjon ikke bekreftet. |
| Versjoner/kildeendringer | Snapshot/hash finnes generisk; OpenSpec delta-diff mangler. | P1 |
| Azure DevOps export | OpenSpec identitet/status i mapping er ikke verifisert. | P1 POTENTIAL |

Anbefalt KPI: tell gjeldende krav fra baseline, proposals separat, archive kun historisk; coverage må knyttes til eksplisitt identity og snapshot. Ikke dedupliser bare med tittel/tekst.

## 7. Identitet og migrering

BirkNext bruker eksisterende krav-/snapshotmodeller og sti/evidens. Path-aware identity v2 (srcid/2) finnes i proposal/arbeidskopi, ikke som bekreftet produksjonsmigrering.

Risiko: like titler i ulike capabilities; rename/move; change ID er ikke krav-ID; MODIFIED kan endre innhold/scenario; REMOVED fjerner baselinekravet, men ikke historisk evidens; Spec Kit→OpenSpec kan gjenskape lik tekst med ny identity.

Trygg overgang krever bevart gammel identity/evidens og metadata for format, project, capability, path, snapshot/hash og baseline/change-status. Cross-format mapping skal ha preview, menneskelig godkjenning, begrunnelse og auditspor. Ingen automatisk relinking av planer, approvals eller execution history.

## 8. Data Quality

Arbeidskopiens ReferenceSpecParser dekker kjente Spec Kit-lignende former, ikke OpenSpec-livssyklus. Anbefalte read-only kontroller: format/schema-deteksjon; fildekning discovered→classified→parsed→normalized/rejected; separat baseline/proposal/archive; foreldreløse scenarioer og duplikate/tvetydige identity-er; MODIFIED uten baseline-mål; REMOVED/RENAMED uten match; flere active changes på samme capability; KPI-rekonsiliering; identity-mapping-rapport som ikke utfører koblinger.

## 9. Overgangsscenarier A–J

| Case | Nåværende gap/risiko | Anbefalt oppførsel |
|---|---|---|
| A: Spec Kit beholdes | Eksisterende formatforventning. | Regresjonstester; behold adapter. |
| B: bare OpenSpec | Semantisk parsing ikke bekreftet. | OpenSpec-adapter. |
| C: blandet format | Ingen format-routing/dedupe funnet. | Proveniens og format/status per krav. |
| D: krav gjenskapt uten mapping | Tittel/tekstlikhet kan gi feil link. | Behandle som ny identity inntil menneskelig review. |
| E: active MODIFIED | Kan forveksles med current. | Vis som forslag/impact. |
| F: change arkiveres | Archive kan dobbelttelles. | Lifecycle-aware snapshot; archive historisk. |
| G: rename/move | RENAMED/parser ikke funnet. | Bevar gammel identity; eksplisitt mapping. |
| H: scenario uten User Story | Parent-link uverifisert. | Modell støtter scenario direkte under requirement/capability. |
| I: flere aktive endringer | Konfliktmodell mangler. | Diagnostikk; ikke velg vilkårlig baseline. |
| J: custom/ustøttet syntax | Risiko for stille utelatelse. | Behold råfil og vis konkret unsupported-diagnostikk. |

## 10. Anbefalt adapterarkitektur

Gjenbruk generisk import, snapshot og evidens. Legg Spec Kit og OpenSpec parsing i separate adapters over felles normalized requirement/scenario-modell og SourceChangeSet (baseline/proposed/archive). Bevar råtekst, format, path, snapshot/hash, capability, change ID/status og parse-diagnostikk. Modellen må støtte krav uten FR-ID og scenario uten User Story uten å finne opp relasjoner.

Faser: (1) read-only discovery/parsing og fixtures; (2) normalized modell uten å endre Spec Kit-resultater; (3) delta- og baseline-semantikk; (4) menneskegodkjent identity mapping; (5) Manual Testing/KPI/Data Quality/export; (6) opt-in pilot etter regresjon.

## 11. Prioritert backlog

| Prioritet | Arbeid | Akseptansekriterium |
|---|---|---|
| P0 | Bevar Spec Kit identity og historikk. | Regresjon viser uendrede plan/case/revision/approval-koblinger; ingen auto-remap. |
| P1 | OpenSpec-deteksjon og baseline-parser. | Capability/krav/scenario/proveniens bevares; ukjent syntax rapporteres. |
| P1 | Change lifecycle/deltaer. | Current KPI utelater proposals/archive; deltaer diagnostiseres. |
| P1 | Identity mapping-vern. | Rename/move foreslås for review; ingen title/text auto-link. |
| P1 | Manual Testing og dashboard. | Current/proposal vises separat; gammel kildeevidens beholdes. |
| P1 | Data Quality reconciliation. | Fil-/krav-/scenario-dekning avstemmes uten dobbelttelling. |
| P2 | Custom schemas og skala. | Ustøttet schema feiler tydelig; store pakker testes. |

## 12. Foreslått teststrategi (ikke kjørt)

- Unit: Spec Kit-regresjon; OpenSpec baseline; id-løse requirements; nested scenarios/BDD; alle deltaer inkludert rename; duplikater og unsupported syntax.
- Integration med isolerte fixtures: ZIP discovery, current/active/archive, mixed format, reimport, snapshot/evidence, overlappende changes og diagnostics.
- Data reconciliation: forventede baseline/proposal/archive counts og requirement→scenario→case trace.
- Browser: skille current/proposals, vise source-change impact, beholde gammel kildeversjon på plan/case.
- Migrering: rename og Spec Kit→OpenSpec viser mappingforslag uten å utføre remapping.
- Skala: store pakker og deterministisk diagnostikk.

## 13. Spørsmål til utviklingsteamet

1. Hvilken tjeneste er autoritativ grense for source-format adapters?
2. Hvilke parsere utover SpecificationCatalog driver Testoversikt/planlegging?
3. Hvilke OpenSpec-versjoner og custom schemas skal støttes først?
4. Hvem godkjenner at en change blir effektiv baseline?
5. Skal aktive forslag være synlige for alle testere eller bare i review?
6. Hvordan skal identity proposal godkjennes før mappingfunksjon utvikles?

## 14. Evidensappendiks

Linjer gjelder arbeidskopien på auditdato og kan flytte seg ved samtidig arbeid; fil/metode er primærreferanse.

| Kilde | Relevant evidens |
|---|---|
| AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs og .Application.cs | Preview/commit, workspace-dokumenter, snapshot/katalogflyt; PreviewAsync og SpecificationCatalogAsync. |
| AIAssisted/backend/BirkNext.Api/Services/Specifications/SpecificationCatalogService.cs:19,35-42 | Klassifiserte Specification-stier sendes til dagens parser. |
| AIAssisted/backend/BirkNext.Api/Services/Specifications/SpecificationEvidenceBuilder.cs:20,26,113-115 | Semantisk parser brukes til kildeevidens. |
| AIAssisted/frontend/BirkNext.Web/Services/SpecExplorerService.cs:17,32,107-109,557,781,1393,1559,1583-1590,1694 | Item-ID regex, story/scenario/BDD parsing og semantic model. |
| AIAssisted/frontend/BirkNext.Web/Services/SampleProjects/SampleArtifactClassifier.cs:53,109,337-361 | Generisk specs-hint og dokumentheuristikk. |
| AIAssisted/shared/ModuleStructure.cs:6,15,32-41,65-80 | Eksplisitte Spec Kit-mønstre for modul-/featuregruppering. |
| AIAssisted/backend/BirkNext.Api/Services/DataQuality/ReferenceSpecParser.cs:10,17 | Ucommittet referanseparser for Spec Kit-lignende mønster. |
| AIAssisted/backend/BirkNext.Api/Services/DataQuality/IdentityDryRun.cs | Arbeidskopi av dry-run/forslag; ingen migrering utført. |
| AIAssisted/docs/birknext-requirement-identity-migration-proposal-2026-11-27.md | Proposal, ikke implementert identity v2. |
| AIAssisted/docs/birknext-system-settings-data-quality-2026-11-27.md | Arbeidsdokument, ikke bekreftet ferdig funksjon. |
| AIAssisted/frontend/BirkNext.Web.Tests/Services/SpecExplorerServiceTests.cs; AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs | Testområder funnet; ingen OpenSpec-test ble kjørt eller identifisert i fixturesøk. |

## 15. Endelig status

- **CONFIRMED:** Generisk Markdown/import/catalog/evidence finnes; parser og gruppering har Spec Kit-orienterte konvensjoner; OpenSpec-adapter, deltaoperasjoner og current/active/archive-modell ble ikke funnet.
- **LIKELY:** Enkelte OpenSpec Markdown-/BDD-elementer kan oppdages, mens kravidentitet/parent-relasjoner blir ufullstendige.
- **POTENTIAL:** Feil KPI, coverage, readiness og source-change varsler ved blanding av baseline, active changes og archive.
- **UNKNOWN:** Runtime-resultat mot konkret OpenSpec-prosjekt, custom schemas, ytelse og diagnostikk.
