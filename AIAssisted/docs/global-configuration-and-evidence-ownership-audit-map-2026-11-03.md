# Global configuration and evidence ownership audit map — 2026-11-03

Phase 0 of the "configure once → analyze once → associate once → reuse everywhere" work, written **before** code changes. Read-only audit of
`AIAssisted/` at `02d91f0f` (branch `008-traceability-first`). Key claims were re-verified in code (marked ✔); the rest come from a traced sweep.
FE = `frontend/BirkNext.Web/`, BE = `backend/BirkNext.Api/`.

## A. Global configuration

| Concept | Canonical owner | Persisted | Consumers | Copied elsewhere? | Override / visible? | Refresh / invalidation | Can drift? | Migration |
|---|---|---|---|---|---|---|---|---|
| System Settings (feature visibility, logging, admin policy, FQ engine preferences) | BE `AdminService` | `appsettings*.json`; edits → `appsettings.Local.json` + `IConfigurationRoot.Reload()` | System Settings page, `FeatureVisibilityService`, FQ engine status | FE singleton caches flags | No | BE reload yes; FE `ApplyLocalFlags` fires **no event**, nav refreshes only on navigation; other tabs never | Yes (nav, other tabs) | Add change event |
| Azure DevOps, Anthropic, Performance limits | appsettings (read-only in UI) | appsettings | Delivery ADO, AI review, Performance | `PerformanceTestOptions.From` singleton read once | No | **Restart required** for performance options | Yes until restart | Document / IOptionsMonitor later |
| Target Environments (URL, env type/name, REST/GraphQL base, thresholds, features, engine selection, integrations, Project, Target Application) | FE `FrontendAnalysisSettingsService` | browser localStorage `birknext:frontend-analysis-settings` | Nearly every review via `FrontendAnalysisContextFactory` (deep clone into page `_context`) | Pages keep a clone for their lifetime; **Performance definitions copy `TargetOrigin`/`EnvironmentName`/`EnvironmentType` once** ✔ | Auth only (below) | `Changed` on save; subscribed by ProjectContextStrip, ProjectImport, ArtifactExplorerContext, RuntimeReviewSessionService, TechnologyCoverage — **not** AQR/FQR/IQR/Performance/Critical E2E; no cross-tab `storage` listener | Yes | Performance: resolve live, keep stored value as historical |
| Authentication (per environment) | profile `Authentication`; effective via `EffectiveAuthentication` (override > observed > detected) | localStorage + detection snapshots | Context factory, headless diagnostic, runtime auth selector | Context snapshot is data-minimised (no client id/tenant) — good | **Yes, visible** (only real Global/Override/Effective UI in the app) | via context | Low | — |
| Unattended login | BE `UnattendedLoginStore` | `App_Data/browser-authentication/unattended-login.json` + DataProtection credential | FQR/Performance/headless unattended paths | Own `LoginUrl`/`Provider`, **no environment id** | Not shown as override of the environment's auth | none | Yes (can contradict env auth) | Scope to environment / show provenance |
| Frontend testing capabilities / FQ engines | 4 layers: appsettings capabilities (L1), System Settings preference (L2), profile features/selection (L3), runtime companion/edge/proxy | appsettings + localStorage + runtime | FQR, settings engines tab, capabilities page | FQR default execution snapshot hard-codes L1 = true and fills L2 from profile toggles (suspected re-resolved by BE) | Partial "Disabled in System Settings" labels, no effective view | runtime `Changed` events | Medium | Effective-capability view |
| Performance engines | BE `PerformanceTestOptions` + `App_Data/performance-local-execution.json` | appsettings + App_Data | Performance | Each definition stores `ProviderId`/`RuntimeId` (default k6/Podman) | Per-definition, **not shown as override** | restart | Medium | Show as override |
| Connected systems / integrations | BE `IntegrationDefinitions`/`IntegrationEnvironmentStates`/`ExternalIntegrations`/`AltinnEnvironmentSettings` | DB | IQR, Topology, Skolenærvær | Legacy `profile.Integrations` still copied into every context; FE `birknext:integration-target-registry` | No | none | Yes (3 stores) | Retire legacy copy after migration |
| Target Application / Project Evidence | profile `Project`/`TargetApplication`, `ProjectEvidenceService` | localStorage | Dashboard, Workflow, TE project pane, IQR, Source Analysis | `PerformanceTestDefinition.ProjectId` | User choices beat inference (association) | `ProjectEvidence.Changed`, `ProjectApplicabilityState.Changed` | Low | — |
| In-session runtime review results | FE `RuntimeReviewSessionService` | memory | FQR/AQR/IQR/Dashboard | — | — | cleared on active-environment change (d118350b) but **not** on URL/auth change of the same environment | Medium | Fingerprint |
| AQR last report | FE `birknext:api-review` per profile | localStorage | AQR | `LastReport` restored by environment id only, never compared with current URL | — | none | Yes | Compare target identity |
| FQR history | `birknext:fqr-history:<profileId>` | localStorage | FQR | Immutable historical snapshots (documented) | — | — | No (historical) | — |
| Critical E2E | BE `App_Data/critical-e2e/*.json` | App_Data | E2E | Base URL from the live companion page (good); env name captured at page init | — | none | Low | — |
| Endpoint / Browser Discovery | FE `birknext:endpoint-discovery` + BE runs | both | AQR, Performance, FQR | Origins stored but reclassified against live approved origins | — | per visit | Low | — |

Feature page writing global config: FQR writes `FrontendReviewScope` into the profile (`FrontendQualityReview.razor:1168-1176`).

## B. Documents and work items (Markdown)

Token-level parsers are already shared: `SpecExplorerService.Parse` and `TaskExplorerService.Parse` (+ Plan/Constitution/DataModel analysis, `SampleArtifactClassifier`,
`ArtifactDocumentDiscovery`, `MarkdownTokenizer`) are compiled into **both** FE and BE (`BirkNext.Api.csproj:174-203`). Divergence is in the layers above:

| Concept | Owner(s) today | Persisted | Same-document rule differences (verified ✔ / traced) |
|---|---|---|---|
| Document type / role | `SampleArtifactClassifier` (front-matter > canonical filename > content > synonyms) for Import, Sample Projects, Explorers; Delivery: filename only under `<module>/specs/<feature>/`; Generated Docs: path regex; Source Analysis: `docs/specs/wiki` path | Import record roles; Delivery snapshot | Four rules overlap but disagree (e.g. `work/items.md` tasks detected by Import, ignored by Delivery; `specification.md` canonical for Import, not an Explorer pack file) |
| Tasks | `TaskExplorerService.Parse` | recomputed per ReviewContext; Delivery persists its own | ✔ Delivery `CheckboxRe` accepts `+` bullets, TaskExplorer does not; TaskExplorer counts bare `T001` lines, Delivery calls them untracked; task id padding differs (T001 / raw T1 in Drift / `T\d{2,4}` in TaskDelta / stripped in suggestions); backend `ReviewPageModelBuilder` counts every `- ` bullet as a task |
| Requirement references | `shared/RequirementReferenceParser` (FR-001) | — | ✔ TaskExplorer pads FR/SC to **2** digits (`FR-01`) so `FR-1` in tasks never matches spec `FR-001`; builders keep ids as written |
| Story ids / tags | SpecExplorer (two formats inside: `US1` and `US-001`), Delivery `StoryKey` (`US{n}`), SpecificationReviewProjection (`US-00n`) | — | `[US1-2]` accepted by TaskExplorer, Unresolved in Delivery; TaskSpecAlignment falls back to **position** (`[US3]` → 3rd story), Delivery never re-points |
| Removed stories, scenario specs | Delivery only | Delivery snapshot | Explorers count struck-through stories as live and "Scenario N" headings as user stories |
| Phases | TaskExplorer (any `##` containing "phase"/"fase N"), Delivery (`^(Phase|Fase) N`), Plan (Phase/Step/Group/Stage), ArtifactTraceability, ReviewPageModelBuilder (any line with "Phase") | — | Five rules |
| Work-item normalization | **Only** Delivery (`DeliveryWorkItemRelationships`: ExplicitStory, StoryPhase, FunctionalGroup, ScenarioGroup, SharedFoundational, RemovedStoryReference, UnresolvedReference) | Delivery DB, parser v4 | Traceability/Alignment/Explorers build their own models from raw trees |
| Decoding | `SourceTextDecoder` (UTF-8 → Windows-1252 fallback + mojibake flag) on the archive path (Import, Delivery, Source Analysis) | — | Browser single-file import and Sample Projects read UTF-8 only |
| Glossary | none (static `UserGuideContent.Glossary` only) | — | No `glossary.md` parser exists — governance gap (ADR-029 language gate not implemented) |
| ADR / README / checklists | ADR name + plan ADR ids; README/checklists Unclassified | — | Not parsed by any feature |
| Document counts | `ArtifactDocumentCount` = detected documents in supported roles (228 for M2LB); preview `Documents` = readable `.md` files up to 2000 (likely 561); `ProjectPackOverview` = role candidates; `ProjectEvidencePackages.DocumentCount` = 6 SDD roles | import record | Four counts, unlabelled differences |

Recommended canonical base: keep SpecExplorer/TaskExplorer as the single token parsers, route id normalisation through `RequirementReferenceParser`, and
lift Delivery's story-key/phase-key/removed-story/`Classify` into `shared/` as the one work-item model.

## C. Source evidence

Owner: `IqrSourceStore.AnalyzeValidatedAsync` builds one snapshot per upload, stored as `EvidenceJson`; archive bytes are not kept. Identity = snapshot
`Id` + `Archive.Sha256`; analyzer versions per sub-model. But ingestion is **not single-parse**: five zip readers run on the same bytes with different
filters (workspace reader, `DependencyReviewService.ReadArchive`, `DependencyInventory.UnsupportedManifests`, ApplicationMessaging, `ScimSourceReader`
for SCIM + Security Classification).

| Consumer | Reuses snapshot or re-parses | Count divergence risk | Stores snapshot id? | Payload |
|---|---|---|---|---|
| AQR REST | Reuses contracts for binding; engine parses only the runtime spec; two operation matchers | High | **No** | Downloads every full snapshot with OpenAPI contracts |
| AQR GraphQL | Ignores Source Analysis GraphQL schemas (binding filters to OpenAPI) | High | artifact hash | — |
| IQR | Reuses exact bound selection | Low | Yes | server loads up to 100 full snapshots |
| Integration Topology | Reuses, rebuilt per request; "latest" grouped by **archive file name** | High | No | server, full ×100 |
| Performance | Reuses contracts; drift by file name | Medium | Steps yes; **run records newest workspace snapshot, not the steps' snapshot** ✔ | server |
| Technology Coverage | **Newest snapshot only** (e.g. the last Terraform ZIP) | High with several packages | No | 1 snapshot |
| Code Risk / AI review | Reuses exact current/baseline | Low | Yes | server |
| Dependency Review | Reuses; evidence from a separate zip parser at upload | Medium | Yes | `dependency-sources` deserializes all |
| Pipeline Review | Reuses CI/CD, cached by snapshot + rules version | Low | Yes | server |
| Security Classification | Reuses evidence extracted by `ScimSourceReader` | Medium | Yes | server |
| Schema Studio / Test Data XSD | **Second XSD engine** (`XmlSchemaPackageAnalyzer`) vs Source Analysis `ContractAnalyzer`; artifact capped 100 files / 20 MB | High | Yes | light index + package (good) |
| Generated Docs Health | Reinterprets on read; loads 100 full snapshots even with an id | Medium | Yes | server |
| Documentation Drift | Copy of snapshot facts taken at import (not reinterpreted) | Medium | Yes | workspace |
| Source Analysis page | Summary list + detail by id (LRU 3) | — | — | correct split, but the list endpoint deserializes up to 50 full blobs |

"Latest" means four different things (per file name, per repository key, newest overall, explicit binding).

## D. Package roles / capabilities

| Classifier | Rule | Disagreement |
|---|---|---|
| `ApplicationContexts.RoleOf` | App > Supporting > **Infrastructure > Documentation** > GeneratedDocs > Contracts | ✔ vs `ClassificationOf`: App > Supporting > **Documentation > Infrastructure > Contracts > GeneratedDocs** — IaC + docs package is "Infrastructure evidence" in one, "Documentation source" in the other |
| `ApplicationContexts.Packages` | NotClassified until source setup lists the snapshot | `ProjectEvidencePackages.Of` assumes application source from `SourceState` → `IsApplicationCandidate` true while role NotClassified |
| Primary vs related | ApplicationContexts demotes a 2nd host to Related | `IsApplicationCandidate`, `SuggestedProject`, `ProjectSetupPlanner`, `TargetApplicationInference` still count it |
| `Satisfies` vs `Fits` | Satisfies lets application fill Supporting/Integration | Fits requires exact SupportingSource and rejects IntegrationSource |
| `FamilyOf`, `PackageCatalogService` | name suffixes / file-name patterns; 8 catalog role ids not mapped to `PackageEvidenceRole` | "never by name" broken |
| `ImportActivitySummary` | role frozen at import into the summary string | Dashboard grouping re-parses that string (stale) |
| Test-pack IaC vs topology IaC | Formats **or** Resources vs Format required | snapshot-level roles disagree |

## E. Top gaps to fix (ordered)

1. **Performance definitions run against a frozen copy of the target origin** while the UI says "Owned by the Target Environment" ✔ — resolve live from the
   Target Environment, keep the stored value only as historical provenance, show a mismatch.
2. **Performance run provenance records the newest workspace snapshot**, not the snapshot its steps came from ✔.
3. **One package classification**: `ClassificationOf` and `RoleOf` disagree ✔; derive both from one precedence.
4. **Task/requirement parsing rules differ** for the same file (`+` bullets ✔, FR/SC padding ✔, story tags, phases) — align TaskExplorer with
   `RequirementReferenceParser` and Delivery.
5. **Configuration change invalidation**: Feature Visibility has no change event; review pages hold deep-cloned contexts without listening;
   runtime results survive URL/auth edits of the same environment; AQR restores its last report by environment id only.
6. **Two XSD engines** (Source Analysis vs Schema Studio/Test Data).
7. **Five zip readers at ingestion** with different filters.
8. **Technology Coverage = newest snapshot only**; "latest" has four meanings.
9. **AQR does not use the contract owner** (downloads full snapshots, ignores Source Analysis GraphQL, stores no snapshot id).
10. Unattended login is installation-wide with its own provider/login URL; engine/provider per-definition choices not shown as overrides.
11. No glossary owner; README/checklist/ADR not parsed (governance gap, out of scope beyond reporting).
12. Summary list endpoint deserializes full blobs; several consumers load 100 full snapshots server-side.

## F. Implementation plan (scoped per the brief's "do not expand scope")

- **Commit 1 — configuration:** Performance target resolved live from the Target Environment (stored value = historical provenance, mismatch shown);
  Feature Visibility change event; runtime results scoped by environment fingerprint (URL + auth), AQR last report shown as current only for the same
  target; provenance labels where a feature shows a global value.
- **Commit 2 — documents:** TaskExplorer accepts `+` bullets and normalises FR/SC through `RequirementReferenceParser`; one story-key normaliser shared by
  Delivery and the explorers; labelled document-count semantics.
- **Commit 3 — source evidence:** one package classification precedence; Performance run provenance = the steps' snapshot; Technology Coverage labels
  its scope (newest snapshot) explicitly.
- Remaining items (XSD engine unification, single-pass ingestion, AQR contract owner, summary projection column, glossary owner) are larger
  migrations; reported here with their owners rather than rewritten in this pass.
