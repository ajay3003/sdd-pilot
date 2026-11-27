# BirkNext — OpenSpec migreringsrisiko og påvirkning på dagens løsning

**Dato:** 2026-11-27  
**Auditmodus:** Read-only statisk kildekode-, test- og dokumentasjonsanalyse. Ingen build, tester, importer, database, migrering eller eksterne tjenester ble kjørt.  
**Beslutning:** NO-GO for å aktivere OpenSpec som en kilde til gjeldende testgrunnlag før lifecycle, identity, KPI og Spec Kit-regresjoner er verifisert. Trygt å starte isolert design/fixture-arbeid etter avklaringer, men ikke persistent identity-migrering eller produksjonsaktivering.

## 1. Sammendrag

OpenSpec berører mer enn Markdown-parsing. I BirkNext går kildedokumenter gjennom klassifisering, katalog/evidens, semantic parsing, scenario discovery, candidate/source identity, testplanlagring, coverage/readiness, source-change checks og ADO-export. Flere tjenester konsumerer samme candidate- og identity-kontrakter. En endring i felles parser, module classification eller identity kan derfor påvirke eksisterende Spec Kit-prosjekter som M2LB selv om OpenSpec-importen ser riktig ut isolert.

Største konkrete integritetsrisiko er kjent i dagens kildekode/dokumentasjon: requirement-only discovery candidates bruker en v1 SourceIdentity basert på capability + CanonicalKey, uten spesifikasjonsfil. Proposal-dokumentet rapporterer 66 slike identiteter i M2LB brukt i mer enn ett dokument (opptil 39), men den påfølgende feilvirkningen i alle konsumenter er ikke fullt inventert. Candidate-ID kollisjoner scopes separat; de endrer ikke den lagrede SourceIdentity. Dette er latent risiko for coverage/planer ved krav uten scenario.

OpenSpec kan øke kollisjonsrommet: capability-scoped krav uten stabile FR-ID-er, flere aktive change-filer, rename/move og archive. Samtidig innføring av srcid/2 ville endre hvordan eksisterende cases/planer matcher, og er uttrykkelig et forslag som krever beslutning; persistent migrering er ikke godkjent.

**Anbefaling:** Alternativ C — behold den eksisterende Spec Kit-banen og introduser en egen OpenSpec-adapter over en formatnøytral, provenance-bevarende modell. Behandle current baseline, active proposal og archive som separate tilstander. Ikke rør lagrede identity-er eller historikk automatisk. Først gjennomfør isolerte parser-/reconciliation-fixtures og M2LB-regresjoner; deretter prosjektvis pilot.

## 2. Baseline og verifikasjonsgrenser

- **CONFIRMED:** Branch 008-traceability-first; HEAD 73b1327e; branch er tre commits foran upstream.
- **CONFIRMED:** Arbeidstreet er skittent med aktive samtidige endringer i Manual Testing/Data Quality, backend/frontend og tester. Ingen av disse ble endret. Bare denne rapporten ble opprettet i denne oppgaven.
- **CONFIRMED:** V10-dokumentasjon sier blant annet at requirement identity proposal krever beslutning og ikke er migrert. Den beskriver også flere V10 UX-punkter som åpne/not tested. Ikke anta at V11/V12 er implementert uten kodebevis.
- **CONFIRMED:** Den forrige OpenSpec-kompatibilitetsauditen ligger i AIAssisted/docs og ble lest som input; dette dokumentet analyserer konkrete blast-radius/identity-risikoer i tillegg.
- **UNKNOWN:** Runtime-resultat og aktuell M2LB-import ble ikke testet. All risikovurdering er kode-/dokumentbasert.
- **UNKNOWN:** Endelig OpenSpec schema/versjon for faktiske kundepakker. Forrige audit undersøkte offisiell spec-driven struktur v2.0.0; custom schemas kan avvike.

Statusetiketter i rapporten: CONFIRMED = direkte kilde-/test-/dokumentbelegg; LIKELY = sannsynlig ut fra faktisk dataflyt, men ikke reprodusert; POTENTIAL = mulig ved framtidig endring; UNKNOWN = mangler evidens.

## 3. Nåværende arkitektur og blast radius

```text
ZIP / repository snapshot
  -> ProjectImportService preview/commit
  -> ArtifactDocumentDiscovery / SampleArtifactClassifier
  -> SpecificationCatalogService.Read/CaptureAsync
  -> SpecExplorerService.Parse + BuildSemanticModel
  -> SpecificationEvidenceBuilder.BuildAll (server-held evidence)
  -> TestScenarioDiscoveryService + registered providers
  -> TestScenarioDiscoveryEngine (candidate ID, canonical key, SourceIdentity)
  -> TestPlanSuggestionService / TestScenarioDiscoveryEngine overview
  -> stored plans, revisions, cases, coverage/readiness, executions
  -> Traceability / Test Coverage / Testoversikt / source changes / ADO export
```

| Avhengighet | Faktisk oppførsel og antakelse | OpenSpec-/regresjonspåvirkning | Konsumenter / evidens |
|---|---|---|---|
| Import / snapshots | ZIP/workspace-dokumenter behandles av ProjectImport. Katalog capture mottar dokumentene i minnet ved import. | Feil kan oppstå etter at arkivet er lest: fil finnes, men semantikk kan være tom/feil. | ProjectImportService(.Application).cs, SpecificationCatalogService.CaptureAsync. |
| Klassifisering | Markdown klassifiseres ut fra navn/sti/innhold og WorkspaceArtifactType. `specs` er ikke alene formatbevis. | Endret path-regel kan endre hvilke gamle M2LB-filer som blir spesifikasjoner og dermed katalog-/KPI-populasjon. | SampleArtifactClassifier.cs, ArtifactDocumentDiscovery; tester SampleArtifactClassifierTests. |
| Modulgruppering | ModuleStructure har eksplisitte Spec Kit-root-regler `.specify/` og `specs/<feature>/`. | Capability-navn kan feiltolkes som produktmodul; endret grouping endrer filtrering og summer. | shared/ModuleStructure.cs; ManualTestingOverview ModuleFor. |
| Semantic parsing | Catalog og EvidenceBuilder bruker samme SpecExplorerService parser som browseren. Krav i katalogen krever ikke-blank ID og dedupliseres per fil/ID. | OpenSpec headings ohne FR-ID können aus persisted requirement catalog fallen; parseränderung wirkt auch Spec Kit. | SpecificationCatalogService.Read; SpecificationEvidenceBuilder.Build. |
| Scenario discovery | Providers liefern DiscoveredScenario; Engine normalisiert Kandidaten, Identität und Fingerprint. | Adapter muss diese Kontrakte korrekt füllen; falsche CanonicalKey/SourceIdentity wirkt weit downstream. | TestScenarioDiscoveryEngine.Finish, CandidateId, SourceIdentityOf; provider registry. |
| Plan/preparation | Preparation decisions und Type decisions werden u.a. nach normalisiertem Spezifikationspfad und SourceIdentity geladen. | Pfadverschiebung/Identity-Wechsel lässt Entscheidungen verschwinden oder kann fremde Entscheidung anwenden. | TestPlanSuggestionService.Overview.cs DecisionFor/Readiness; TestPlanSuggestionService. |
| Stored traceability | Case/plan/candidate-Verknüpfungen verweisen auf scenario/source identities; saved plan hält eigenen Inhalt und revision. | Identity-Migration kann Coverage, Plan-Diff und Quellstatus ändern, auch ohne Case-Text zu löschen. | PreparedCaseTraceability.cs; TestPlanSuggestionEngine; proposal identity §2. |
| Coverage/traceability | Requirements-Coverage und Traceability rechnen über kanonische Requirement-Modelle und explizite Links. | Incomplete mapping erzeugt false missing oder false covered; KPI denominator ändert sich. | RequirementCoverageService.cs; TraceabilityView.razor. |
| Source changes | Plan-Cases werden gegen ihren eigenen Source-/Snapshot-Kontext eingeordnet; Versions-/Änderungsregeln sind source-aware. | OpenSpec proposal/archive darf nicht als neue aktive Baseline interpretiert werden. | TestPlanSuggestionService.Overview.cs, .Changes.cs, SourceBaselineService. |
| Azure DevOps export | Export liest gespeicherten Plan und dessen Fälle/Zuordnungen. | Falsche Identität oder Status kann falsche externe Links/Transferinhalt ergeben. | AdoTestPlanExportService.cs, AdoTestPlanExporter.cs, AdoTestPlanTransferService.cs. |

Evidence-Stellen sind im Appendix mit Dateipfaden/Methoden. Shared services und bestehende source-format assumptions sind getrennt: Import/snapshot/catalog plumbing erweiterbar; parser, classifier, grouping, canonical identity und baseline semantics sind semantische Kanten.

## 4. Spec Kit-regresjonsrisiko

### Hva bør holdes bakoverkompatibelt

1. Eksisterende M2LB ZIP-/repository discovery og rolleklassifisering, inkludert rotspesifikasjoner og modulspesifikasjoner.
2. Nåværende SpecExplorerService-output for FR/US/AC, Given/When/Then, norsk/engelsk labels og eksisterende dokumentert TEST-E-kandidater.
3. `SpecificationCatalogService` server-held catalog/evidence behavior, existing document path/hash and snapshot semantics.
4. `TestScenarioDiscoveryEngine` candidate construction for existing capabilities, candidate ID/source identity for unchanged Spec Kit sources, and plan retrieval.
5. Existing TestPlanSuggestionService decisions, test plan membership, revisions, approvals/review states, executions, and ADO mappings.
6. Current ModuleStructure behavior for M2LB; capability is not automatically a module and file location alone must not infer E2E.

### Mulige regresjonsmekanismer

- **Parser i felles kode endret:** heading/regex-prioritering kan gjøre at Spec Kit-krav/scenarioer telles annerledes; samme browser/server parser må forbli konsistent.
- **Path classifier utvidet for `openspec/`:** for bred `specs`-regel kan klassifisere archive eller active change som canonical; for snever regel kan skjule rot-/modulspesifikasjoner.
- **Common model-normalisering:** hvis OpenSpec mangler IDs og adapter lager syntetiske ID-er, kan eksisterende consumers anta at de er source-authored IDs og bruke dem til links/export.
- **Identity generation:** ændrer candidate IDs/source IDs eller content fingerprints. Candidate ID stability explicitly excludes snapshot but includes project/capability/canonical key. Identity changes can break preparations, plan reconciliation and coverage.
- **Baseline source switch:** new parser could cause current source baseline to select active proposal instead of current spec, making existing cases Changed/Removed/Unavailable incorrectly.
- **Deduplication:** document-level same-ID rows may be intentional. Global de-dupe by title/ID could merge unrelated module requirements.
- **Re-import:** catalog revisions are content/fingerprint-based; changed classifier/parser evidence could create different catalogs for unchanged source or leave stale prior catalog semantics.

Regression gate: parse the same M2LB snapshot before/after and compare document set, semantic entities, IDs, paths, identities, candidate fingerprints, module grouping, KPI denominators, plan membership, readiness decisions, source status, approval and execution history. Any unexplained delta is NO-GO.

## 5. Identitet og dataintegritet

| Risiko | Feilmekanisme / berørte data | Sannsynlighet / impact | Deteksjon og mitigering | Rollback/containment |
|---|---|---|---|---|
| R1 v1 requirement-only collision (CONFIRMED latent) | CanonicalKey is `requirement:{module}|{id}` and SourceIdentity derives from capability+key, not file. Same FR-001 in documents with same/empty module shares identity. Proposal reports 66 M2LB identities across >1 docs, max 39. Case/plan link to A may count B covered. | Likelihood Medium; impact High for requirement-only coverage, plans and traceability. Existing observed impact marked possible, not reproduced as defect. | Per-document duplicate identity reconciliation; test case A does not cover B; retain explicit doc path in association. | Do not migrate stored v1. Roll back adapter/flag; keep old source values. |
| R2 OpenSpec requirement collision | Capability-scoped requirements with same names or synthesized IDs can be merged if key omits capability/path/change identity. | Likelihood Medium; impact High. | Collision matrix on repeated names, capabilities and files; no title-based merge. | Reject ambiguous candidates from canonical coverage, preserve raw source. |
| R3 srcid/2 compatibility break | New file-scoped IDs no longer equal v1 values consumed by stored cases, plan items, decisions/exports. Read-both resolution may be ambiguous without source path. | Likelihood High if migration attempted; impact High. | Dry-run over every persisted reference and exact 1/multiple/none resolution; compare old/new outputs. | Additive/read-only first; feature off restores old behavior per proposal, but persistent migration remains unapproved. |
| R4 cross-format false mapping | Text/title similarity maps recreated Spec Kit requirement to OpenSpec requirement despite semantic mismatch. | Likelihood Medium; impact High. | Human review with source evidence and explicit mapping rationale. | Leave separate identities; undo only explicit mapping records, never rewrite history. |
| R5 unstable rename/move | Path-derived identity changes after file/capability move; rename can split same concept or incorrectly merge new concept. | Medium; High. | OpenSpec RENAMED and move fixtures; identity migration preview. | Preserve old source record, mark successor relation separately. |
| R6 historical evidence reassignment | Importer writes new identity into existing plans/cases or approval/execution references. | Low absent implementation; impact Critical. | Snapshot old DB in isolated test, invariant counts and reference equality (not live user DB). | No automatic migration; immutable old references; reversible additive mapping only. |
| R7 false covered/readiness | Duplicate source IDs or active proposal pollute covered keys/denominator, yielding false readiness. | Medium; High. | Independent denominator reconciliation per source status and module; test B cannot cover A. | Disable OpenSpec contribution while retaining evidence in raw catalog. |
| R8 decision lookup drift | preparation/type decisions lookup by spec path and source identity; path/identity shifts make decisions disappear or attach to wrong item. | Medium; Medium/High. | Compare decision resolution before/after reimport and moved file. | Keep source path alias/version mapping explicit; no implicit path fallback. |

Confirmed finding: v1 identity structure and source-proposal evidence. Likelihood ratings for downstream impact are risk estimates, not reproduced production failures.

### identity/1 og srcid/2

`RequirementIdentity` in `CanonicalCorrelation.cs` is an implemented review model/ruleset `identity/1`: unscoped requirement IDs are correlated to feature-pack-scoped targets, with states Confirmed/Candidate/Ambiguous/Rejected/Unresolved. It is not equivalent to source-aware per-file identity. Proposal says only Confirmed identity/1 should override v2; other states remain unresolved/file-scoped. The separate srcid/2 proposal is not approved and not migrated. OpenSpec support must not smuggle that migration into parser work.

## 6. OpenSpec source lifecycle

If all Markdown documents become ordinary current specifications, likely outcomes include:

- Active ADDED rows counted as approved/current requirements.
- MODIFIED row counted alongside baseline old text, or replacing it without a recorded proposal state.
- REMOVED requirement remaining active because ordinary document import does not apply removal semantics, or disappearing without historical source link.
- RENAMED treated as a new requirement and old one removed, without successor mapping.
- Archived delta counted alongside current merged spec, causing duplicate denominator/coverage.
- Multiple active changes conflict without a deterministic effective baseline.
- Snapshot/version diff reports file text changes but not semantic proposal vs accepted baseline.

These are **LIKELY/POTENTIAL**, not runtime reproduced. Canonical source-state model should at minimum include `CurrentBaseline`, `ProposedChange`, `ArchivedChange`, explicit effective snapshot/revision, delta operation, capability key and relation to baseline requirement. Active changes may be visible in a separate impact/review view, but must not affect approved coverage/readiness until an authorized baseline transition is recorded. Archive is history, not additional current population.

## 7. Manual Testing impact

| Workflow | Current dependency and impact | Existing evidence/test coverage | Required adaptation and safeguard |
|---|---|---|---|
| A Testoversikt | Overview consumes discovery candidates, specification docs, cases, plans/revisions and source-state. Wrong identity/status changes rows, counts and source warning. | Overview service and V10 report; current tests are not OpenSpec fixtures. | Current/proposed/archive populations separate; reconcile all counts by source path and status. |
| B Finn og forstå | Discovery has a 2,000-item client window with server-side search; candidate identity/source identity used for selection. OpenSpec scenarios without stories must still be represented. | TestScenarioDiscoveryEngine.Search/Window/Pick; parser tests. | Test >window and ID-less requirement/scenario; explicit capability parent; preserve unsaved selection after import refresh. |
| C Planlegg og klargjør | Candidate type, requirement links, readiness decisions, steps/expected behavior and source evidence feed preparation. | TestPlanSuggestionService and preparation tests; no OpenSpec evidence. | Proposed changes shown as impact candidates, not current ready-to-test scenarios; no invented GWT or requirements. |
| D Testplaner | Saved cases/items, suite placement, revisions, reviews and plan identity use candidate/source links. New ID scheme may change membership/diff. | TestPlanSuggestionEngine and persistence tests; identity proposal describes affected consumers. | Verify plan/case equality across reimport; retain old source identity and pinned snapshot. |
| E Testgjennomføring | ManualTestRunService records execution by saved case/occurrence. Identity remap could make history appear under another source. | ManualTestRunService, execution tests. | Execution events immutable; source relation historical and version-pinned. |
| F Versjoner | Catalog snapshots/content hash and case source-state feed version display. OpenSpec proposal vs accepted state cannot be collapsed into latest file hash. | SpecificationCatalogService and source baseline services. | Separate source file revision from effective requirement baseline; show proposal status. |
| G Kildeendringer | Existing change detection can compare saved-case source to resolved source; OpenSpec delta intent is richer than text change. | TestScenarioDiscoveryService.Changes and source-change tests. | Compute impacts for delta operations; proposals do not mark accepted cases Changed/Removed as if implemented. |
| H ADO export | Export consumes saved plans/cases and external mapping; bad identity may alter export/duplicate detection. | AdoTestPlanExportService/Exporter and export tests. | Export source provenance and frozen case content; don't export proposal as current without explicit action. |

Scenario-without-User-Story and requirement-without-FR-ID: current parser/discovery model must be verified to preserve a direct requirement→scenario relation. Existing E2E `test-specification` provider can represent documented tests without invented story, but it is not evidence that arbitrary OpenSpec Scenario blocks are discovered.

Approvals: historical approval is on stored case/revision according to existing plan model, but source identity switch could change future source status. No OpenSpec-specific approval invalidation semantics were found. Do not rewrite old approvals; require new review where mapped content is materially changed.

## 8. KPI, project/module grouping and reconciliation

Metrics at risk include Scenarioforslag, Krav, scenarios with cases, test cases, approved, E2E, ready-to-test, P1-without-case, module/project counts, coverage %, readiness, source-change signals and planned scenario occurrences.

| Project state | Expected KPI behavior | Main failure to guard against |
|---|---|---|
| Spec Kit-only | Existing counts and grouping byte-/meaning-equivalent to baseline. | Regression from shared parser/classifier/identity change. |
| OpenSpec-only | Current baseline requirements/scenarios form denominator; IDs may be absent. | Omission because parser expects FR IDs; inventing IDs silently. |
| Mixed format | Count both distinct source evidence sets, with explicit provenance and no accidental merge. | Global duplicate-by-title/ID or duplicate current/proposed files. |
| Active changes | Separate proposal/impact counts, excluded from current readiness/coverage denominator. | Proposal appears approved/current. |
| Archived changes | Historical changes excluded from current denominator. | Archive adds duplicates after delta already merged. |
| Migrated requirements | Count according to explicit reviewed mapping and one effective baseline. Keep original references accessible. | Both old/new counted or old case falsely covers new requirement. |

Root-level vs module grouping: `ModuleStructure` assigns Spec Kit project-root specs to project-level and feature roots to module. OpenSpec capability name is not necessarily application module. Do not infer E2E from location; discovery labels/test type must come from source evidence or reviewed classification. Misgrouping affects filters, module table, project totals, Test Coverage, Traceability and P1 drill-down. M2LB project-level audit and proposal are representative but not a new import in this audit.

Independent Data Quality/reconciliation design is in `birknext-system-settings-data-quality-2026-11-27.md` and working-tree code. It should become a read-only oracle for discovered/classified/parsed/normalized counts, current/proposed/archive populations, identity collisions and dashboard denominator agreement. It is not yet treated as an approved or verified production control.

## 9. Impact på øvrige moduler

| Module | Impact | Evidence / reason |
|---|---|---|
| Specifications Explorer | HIGH | Direct SpecExplorerService parsing and document classification; IDs/headings/relationships visible. |
| Requirements Traceability | HIGH | CandidateId/requirement IDs and explicit test associations drive trace matrix and gaps. |
| Test Coverage | HIGH | SpecificationEvidenceBuilder and RequirementCoverageService use normalized requirements and source links. |
| Source Management | HIGH | Import, snapshot/catalog, source baseline and changed/removed state are origin. |
| Relationship Review | MEDIUM | CanonicalCorrelation/RequirementIdentity review and source evidence may receive new identities; avoid conflating mappings. |
| System Understanding | LOW/MEDIUM | Its guide/canonical correlation sources are separate, but requirement identity reconciliation may connect source facts. |
| Test Data | MEDIUM | Requirement/scenario links and snapshot references may be affected; actual data assets are separate. |
| Performance Review | LOW/UNKNOWN | No direct OpenSpec parser dependency confirmed; possible shared readiness/project source scoping. |
| Release Readiness | MEDIUM | Coverage/readiness inputs may be wrong if baseline or denominator polluted. |
| Delivery Overview/Reports | MEDIUM | Source snapshots and canonical requirements feed cross-artifact correlations; format parser is upstream. |
| Azure DevOps export | MEDIUM/HIGH | Saved plan and mappings may be affected by identity/status changes, although export transport is format-neutral. |

Classification reflects dependency risk, not claim that all listed modules fail.

## 10. Security, trust and operational risks

OpenSpec adds no inherent executable requirement, but expands the untrusted Markdown/path surface and introduces lifecycle metadata. Keep archive extraction path protections and file size limits. Do not fetch/execute project schemas or scripts during import. Treat custom schemas as data subject to allowlisted local parsing. Report parser errors with file/line, preserve raw content, bound document/count/regex work, and avoid silent truncation (catalog cap is 5,000 requirements; candidate response window is 2,000 while search reaches the remainder).

Source path/version metadata should be normalized and scoped by project/import. Duplicate files, case-insensitive paths, symlinks or archive path traversal are security/integrity review topics; this static audit does not assert a vulnerability in existing ZIP handling. Cross-application leakage was not established. Local/offline import remains possible if parser is local and no remote schema resolution is introduced.

## 11. Strategivalg

| Option | Complexity | Regression/identity risk | Data migration | Rollback | Vurdering |
|---|---|---|---|---|---|
| A Replace Spec Kit parser | Medium initially | Very high: shared M2LB semantics and identities change globally. | Likely/possible if semantics or IDs change. | Hard: old outputs/history can’t be reconstructed from new IDs alone. | NOT recommended. |
| B Add separate OpenSpec adapter only | Medium | Lower parser regression risk, but duplicated downstream model/lifecycle behavior likely. | None initially if read-only and distinct identities. | Good if routing is isolated. | Viable first increment, but needs shared normalized contract. |
| C Format-neutral model + Spec Kit/OpenSpec adapters | Medium/high | Best controlled blast radius if current Spec Kit adapter is characterized and preserved; identity policy still high risk. | None required for parsing rollout; mapping separate decision. | Good: disable OpenSpec adapter without modifying stored Spec Kit data. | Preferred. |

## 12. Anbefalt migreringssekvens

1. Pin baseline: capture representative M2LB fixture and expected file/entity/identity/KPI/plan results in isolated test storage.
2. Detect source format/status read-only; unknown/custom schemas produce diagnostics, never fallback to a false current interpretation.
3. Parse OpenSpec baseline only and compare output with human-reviewed expected records.
4. Validate collisions, missing IDs and stable identities; do not persist/relink.
5. Add proposed/archive delta semantics in separate read model and reconcile current/proposed/history counts.
6. Run Testoversikt, Finn og forstå, Planlegg, Testplaner, Versions, Source Changes and ADO browser workflows in isolated projects.
7. Pilot one non-production project with OpenSpec contribution opt-in and a rollback switch that disables its discovery while leaving original source and IDs intact.
8. Enable per application/project only after release gates pass. No automatic local Spec Kit→OpenSpec migration.

## 13. Risikoregister

| ID | Risiko og trigger | Severity / likelihood / confidence | Detectability | Mitigation / test | Containment / owner / gate |
|---|---|---|---|---|---|
| R01 | v1 requirement-only collision: same FR ID/module in several specs; one case counts for another. | P1 / Medium / CONFIRMED latent | Hard; counts may look plausible. | Duplicate identity audit; document A case must not cover B. | Disable new source mapping; Development + Test Lead; identity gate. |
| R02 | Active proposal read as current; readiness based on unapproved behavior. | P1 / Medium / LIKELY | Medium; compare status labels/KPI. | Baseline vs active fixtures; proposal excluded from current denominator. | Disable OpenSpec current contribution; Product Owner; lifecycle gate. |
| R03 | Archive double-counts merged current requirement. | P1 / Medium / LIKELY | Hard; dashboard totals inflated plausibly. | Archive fixture and independent reconciliation. | Filter archive from current; Development; KPI gate. |
| R04 | Parser/classifier common-code change regresses M2LB. | P1 / Medium / POTENTIAL | Medium with frozen expected output. | Spec Kit golden fixtures and before/after comparison. | Feature disable/revert adapter; Development; regression gate. |
| R05 | srcid/2 replaces v1 links or yields ambiguous read-time mapping. | P0 / Medium / CONFIRMED proposal-only risk | Hard; stored references silently resolve elsewhere. | Dry-run all uses, exact document scope, no writes until approval. | Keep v1; restore additive fields only; Architect/PO; identity gate. |
| R06 | Requirement rename/move changes ID; old case unlinked or false successor. | P1 / Medium / POTENTIAL | Hard without source diff. | Rename/move fixture + manual mapping review. | Preserve old identity and mark unresolved; Development/Test Lead. |
| R07 | Synthetic ID/dedupe merges equal titles across capability. | P1 / Medium / POTENTIAL | Medium via collision scan. | Include capability and source scope; never title-only merge. | Mark ambiguous/not coverage eligible; Development. |
| R08 | Multiple active changes conflict for same baseline requirement. | P1 / Medium / POTENTIAL | Medium. | Overlap diagnostics and explicit reviewer resolution. | Do not select effective merged baseline; Product Owner. |
| R09 | Scenario without US/FR omitted or orphaned. | P1 / Medium / LIKELY | Medium via file completeness counts. | Fixtures for direct requirement→scenario and E2E. | Preserve raw source, flag unsupported; Development/Test Lead. |
| R10 | Path grouping maps capability to app module or E2E. | P2 / Medium / POTENTIAL | Medium; compare expected groups. | Root/module/cross-module fixtures; source evidence determines E2E. | Revert OpenSpec grouping rule; Product Owner. |
| R11 | Source-version diff treats proposal as implemented or misses removal. | P1 / Medium / LIKELY | Hard; stale source status looks valid. | Delta lifecycle tests and case source-status assertions. | Keep previous effective baseline; Development. |
| R12 | Export uses wrong source identity/status after import. | P1 / Low/Medium / POTENTIAL | Medium via preview snapshot assertions. | ADO preview/export contract test with no live writes. | Disable export for unresolved mapping; Test Lead/ADO admin. |
| R13 | Parser silently ignores unsupported syntax or caps records. | P2 / Medium / POTENTIAL | Medium if diagnostics absent. | Per-file parse completeness and cap diagnostics. | Refuse readiness sign-off; Development. |
| R14 | Large/malformed package causes slow import/regex resource use. | P2 / Low/Medium / UNKNOWN | Requires isolated performance profiling. | Bounded fixtures and cancellation/resource tests. | Reject oversized package with safe diagnostic; Development/IT. |

Top five: R05 identity migration; R02 proposal/current confusion; R03 archive double-count; R01 existing v1 collision; R04 Spec Kit regression. Most likely behavior regressions are missing ID/parent relations and false source state. Hardest-to-detect are false coverage/readiness and plausible but inflated counts. Business decisions: lifecycle approval owner, active-change visibility, identity mapping authority, supported OpenSpec schema versions, and whether mapped OpenSpec baseline supersedes Spec Kit source.

## 14. Riskbasert test- og akseptansematrise (proposal, ikke kjørt)

| Test ID | Preconditions / steps | Expected outcome | Risk / level / priority |
|---|---|---|---|
| T01 | Frozen M2LB Spec Kit fixture; import/parse baseline and compare expected docs/FR/US/AC/GWT. | No changed entities, IDs, grouping or counts. | R04; integration; P0 |
| T02 | OpenSpec current specs with two capabilities, ID-less requirements and nested scenarios. | Exact reviewed requirement/scenario graph and provenance. | R09; unit+integration; P1 |
| T03 | Same FR-001 in two same-module/root Spec Kit docs; associate case to A. | Identity diagnostic; A does not mark B covered after supported fix. | R01; integration; P0 |
| T04 | One active change with ADDED/MODIFIED/REMOVED/RENAMED. | Proposal impact shown separately; current baseline unchanged. | R02/R11; unit+integration; P1 |
| T05 | Archive fixture where delta already merged in current spec. | Current KPI counts requirement once; archive historical. | R03; integration; P1 |
| T06 | Two active changes touch same requirement. | Conflict flagged; no implicit merge/current baseline. | R08; unit; P1 |
| T07 | Move/rename capability and requirement, with explicit and absent mapping. | Absent mapping leaves separate/unresolved; explicit mapping is auditable. | R06; unit+integration; P1 |
| T08 | Mixed Spec Kit/OpenSpec duplicate wording and IDs. | No auto-link; source format/path remain visible; counts reconciled. | R04/R07; integration; P1 |
| T09 | Reimport unchanged source, then changed source. | Stable identities for unchanged entities; new snapshot/evidence for changes; no stale catalog. | R11; integration; P1 |
| T10 | Existing saved plan, reviewed/approved case and execution record across new source import. | Membership, approval decision and execution history unchanged; source status explicit. | R05/R06; integration; P0 |
| T11 | Testoversikt KPI for Spec Kit-only/OpenSpec-only/mixed/active/archive/migrated. | Independent reference counts agree; no wrong denominator or scope. | R02/R03/R07; integration+browser; P0 |
| T12 | Finn og forstå >2000 candidates, filter by capability and ID-less requirement. | Server-side search finds all; no candidate identity loss. | R09; integration/browser; P1 |
| T13 | Planlegg and source-change workflow with proposal and removal. | Proposal is not current; historical case remains linked to original source. | R11; browser; P1 |
| T14 | ADO preview built from saved plan with unresolved source mapping. | Preview preserves case content; unresolved identity is visible/blocks unsafe transfer per policy. | R12; integration; P1 |
| T15 | Unsupported custom schema, duplicate files, malformed/large Markdown. | Actionable diagnostic; no silent source omission or unsafe resource exhaustion. | R13/R14; unit/perf; P2 |
| T16 | Disable OpenSpec adapter after isolated pilot. | Spec Kit workflow/data remain; OpenSpec records untouched and available for later re-enable. | rollback; integration; P0 |

Test levels: unit = parser/identity pure rules; integration = isolated source packages and disposable test store; browser = workflow/UI behavior; performance = isolated generated fixtures. Do not use the normal user database.

## 15. GO/NO-GO kriterier

**NO-GO**, hvis ett punkt gjelder:

- Spec Kit/M2LB entities, IDs, grouping, counts or behavior regress.
- Existing test-case/source links, plan membership, approvals or execution history change without explicit reviewed mapping.
- Proposals appear in current requirements/readiness or archives inflate counts.
- Identity collisions are auto-linked; unresolved identity is treated as covered.
- Unsupported source files are silently ignored or source baseline is ambiguous.
- OpenSpec deletion/rename cannot preserve historical evidence.
- KPI/reconciliation differs without an explained, approved population change.
- ADO preview cannot show which source revision produced case content.
- Rollback is not verified with isolated fixtures.

**GO krever:** pinned baseline; deterministic discovery/parser output; full file diagnostics; provenance per candidate; explicit current/proposed/archive semantics; identity collision checks; M2LB golden regression parity; plan/case/approval/execution invariants; independent KPI reconciliation; verified browser workflows; bounded performance; unsupported cases surfaced; tested adapter rollback.

Current gate state: **NO-GO for production use**; **PARTIAL for isolated implementation/design work**. No evidence supports enabling OpenSpec in current user workflows today.

## 16. Påkrevde beslutninger og åpne spørsmål

**Product Owner/Test Lead:** Which source is effective if a project contains both Spec Kit and OpenSpec? Who approves baseline activation? Should active proposals be visible to all testers? What is the policy for cases whose requirement is removed/renamed? Which module owns E2E classification?

**Architect/development:** Is identity/1 authoritative for source requirement scope, and what exactly persists v1 source identities in each consumer? Does the 66-collision M2LB figure remain valid against current HEAD? Which importer paths bypass SpecificationCatalog? What is the committed source baseline and migration mechanism?

**Release/operations:** Which OpenSpec version/custom schemas are in scope? How are pilot fixtures and rollback isolated? Are snapshot/database backups paired with source artifacts?

## 17. Evidensappendiks

Paths below are repository-relative; line numbers are from current working tree at audit time and may shift due to concurrent edits.

| File / method | Evidence relevance |
|---|---|
| AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs and ProjectImportService.Application.cs — PreviewAsync/commit | Workspace file flow and import entry. |
| AIAssisted/backend/BirkNext.Api/Services/Specifications/SpecificationCatalogService.cs:19,31-62 — Read, CaptureAsync | Markdown classification, parser invocation, path/hash/catalog revision. |
| AIAssisted/backend/BirkNext.Api/Services/Specifications/SpecificationEvidenceBuilder.cs:20,26,107-115 — Build, BuildAll | Shared semantic parser output mapped to coverage evidence and source artifacts. |
| AIAssisted/frontend/BirkNext.Web/Services/SpecExplorerService.cs:17,557,781,1393,1694 — SpecItemStartRe, BuildSemanticModel | Spec Kit-oriented IDs/story/scenario parsing and semantics. |
| AIAssisted/frontend/BirkNext.Web/Services/SampleProjects/SampleArtifactClassifier.cs:53,109,337-361 — Classify | Artifact roles, specs directory and canonical document heuristics. |
| AIAssisted/shared/ModuleStructure.cs:6,15,32-41,67-80 — StrongRoot, OverviewModule | Spec Kit-specific module/root assumptions. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/TestScenarioDiscoveryEngine.cs:117,127-134,221-237,723-741 — CandidateId, SourceIdentityOf, requirement loop, ScopeCollidingSpecificationKeys | Project candidate ID, canonical key and source identity generation; collision behavior. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionService.Overview.cs:44-52,89-101,137-143 — DecisionFor, Readiness, overview | Decision/readiness keyed by source identity/path; cases and plan/source state aggregated. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/PreparedCaseTraceability.cs | Saved-case requirement links and spec scenario identity. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/RequirementCoverageService.cs:34,40-95 — CoverageAsync | Canonical requirement coverage consumer. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/TestScenarioDiscoveryService.Changes.cs | Source baseline/change impact path. |
| AIAssisted/backend/BirkNext.Api/Services/TestPlanning/AdoTestPlanExportService.cs, AdoTestPlanExporter.cs | Export reads saved plans/cases and maps content. |
| AIAssisted/shared/CanonicalCorrelation.cs — RequirementIdentity, RuleSetVersion | Implemented reviewed identity/1 rules/states; distinct from srcid/2. |
| AIAssisted/docs/birknext-requirement-identity-migration-proposal-2026-11-27.md §§1-8 | Proposal states v1 requirement-only key lacks file, reports M2LB 66 cross-document source identities, lists affected consumers and says no migration. Proposal figures are documentary evidence, not reproduced in this audit. |
| AIAssisted/docs/birknext-v10-manual-testing-hardening-2026-11-27.md | V10 status/open items and no persistent identity migration. |
| AIAssisted/docs/birknext-openspec-compatibility-audit-2026-11-27.md | Prior OpenSpec format/adapter findings; this report adds downstream impact and risk register. |
| AIAssisted/docs/birknext-system-settings-data-quality-2026-11-27.md and AIAssisted/backend/BirkNext.Api/Services/DataQuality/* | Current uncommitted reconciliation/parser work; assessed as working tree, not shipped or approved. |
| AIAssisted/frontend/BirkNext.Web.Tests/Services/SpecExplorerServiceTests.cs; AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs | Existing test suites identified; no tests executed. OpenSpec coverage not verified. |

## 18. Endelig vurdering

- **Overall risk:** HØY for å legge OpenSpec direkte inn i gjeldende parser/identitetsbane; MODERAT og styrbar for read-only, separat adapter med current/proposed/archive-semantikk.
- **Trygt å starte:** isolert parserdesign og fixture-/reconciliation-testarbeid, etter beslutning om format/schema og baseline-eier.
- **Ikke trygt nå:** persistent srcid/2-migrering, automatisk cross-format mapping, eller bruk av OpenSpec proposals som gjeldende testgrunnlag.
- **Foretrukket arkitektur:** separat Spec Kit- og OpenSpec-adapter over formatnøytral provenance-modell; dagens Spec Kit-bane karakteriseres og låses med regresjonskrav.
- **Primære release gates:** identity preservation, lifecycle separation, M2LB parity, KPI reconciliation, plan/history invariants, visible unsupported diagnostics, rollback.
