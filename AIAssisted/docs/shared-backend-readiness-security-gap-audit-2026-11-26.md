# BirkNext â€“ revisjon av sikkerhet og beredskap for felles backend

**Dato:** 2026-11-26  
**Branch:** 008-traceability-first  
**Beslutning:** **NO-GO for delt drift i dagens TesterNative/API-konfigurasjon.**  
**Metode:** Statisk kildekode- og konfigurasjonsrevisjon. Ingen applikasjon, database, nettverkstjeneste, AVD-miljÃ¸ eller Azure DevOps-instans ble startet eller kontaktet.  
**Status:** **CONFIRMED** = direkte kildebelegg; **INFERRED** = fÃ¸lger av bekreftet kode; **NOT VERIFIED** = krever runtime-/IT-/produktavklaring.

## 01. Sammendrag

BirkNext er **ikke klar for felles drift**. PostgreSQL og Npgsql tÃ¥ler flere klienttilkoblinger som databaseprodukt, og testplan-draft har en god start pÃ¥ optimistisk concurrency. Det er ikke nok til Ã¥ gjÃ¸re appen flerbrukersikker.

Den aktive API-en har en sentral sikkerhetsgrense for localhost: /api og /graphql krever autentisert sesjon/bearer-token, loopback Host og lokal socket peer, og appen stanser oppstart dersom bind-adressen ikke er loopback. Middleware-kommentaren sier eksplisitt at Ã©n lokal bruker er stÃ¸ttet, og at prosjektbasert autorisasjon mangler. Dette reduserer dagens nettverkseksponering, men er ikke en felles identitets- eller tilgangsmodell.

NÃ¥r middleware-gaten fjernes/endres for delt drift, vil mange controller-actions fortsatt bare ha den generelle IntegrationConfigurationWritePolicy/ActiveEventExecutePolicy, eller ingen action-policy. Disse policyene er capabilities for lokale kjÃ¸re-/konfigurasjonsfunksjoner, ikke roller som Reader, Tester, Reviewer eller ProjectAdmin. Ingen konsistent server-side project membership/resource authorization ble funnet. En innlogget bruker kan derfor ikke i dag begrenses til sine prosjekter/operasjoner. Dette er primÃ¦rblokkeren.

Viktige Ã¸vrige funn:

- **Plan concurrency er delvis klar:** TestPlanDraftRecord.Revision er EF concurrency token; service krever basisrevisjon for viktige writes og returnerer 409 uten lagring ved stale update. Ikke alle dataomrÃ¥der deler dette mÃ¸nsteret.
- **Frontend konflikthÃ¥ndtering er ujevn:** planservice gir lesbar 409-feil, men ikke generell merge/diff/reapply. Ikke alle planendepunkter krever basisrevisjon. Alle separate domener er ikke verifisert for silent overwrite.
- **Godkjenning er ikke sikker som flerbrukerattest:** case review event ligger i planinnhold/revisjoner og bÃ¦rer actor tekst, men actor er User.Identity.Name eller "Local user (this PC)"; det er ikke pÃ¥vist dedikert test-lead-policy, immutable content-hash binding eller universell invalidasjon etter alle relevante endringer.
- **Kildegrunnlag er ikke automatisk felles:** snapshotmetadata finnes i DB, mens Git source settings/checkout og andre managed stores er under backendens App_Data/BIRKNEXT_APP_DATA_DIR. I TesterNative peker dette til per-user app-data; separat AVD-instans fÃ¥r ikke disse filene bare ved delt DB.
- **TesterNative starter lokalt:** launcher starter egen PostgreSQL, API og frontend. Ingen Shared Mode/remote API URL-setting ble funnet. En fremtidig delt modus mÃ¥ ikke falle tilbake til lokal database.
- **Admin-/fil-/integrasjonsflate er bred:** 102 controller-filer i API-prosjektet, metoder for testdata reset, backup/restore, source config, credentials, transfer og systemsettings. Flere routes har bare lokale capability policies of ingen action policy. Central middleware auth beskytter dagens lokale runtime, maar levert geen project- of gebruikersscheiding.

**Anbefaling:** Behold TesterNative Local Mode for offline/individuelt arbeid. Bygg Shared Mode som eksplisitt frontend â†’ Ã©n sentralt driftet BirkNext API â†’ sentral PostgreSQL pÃ¥ stÃ¸ttet serverlagring, pluss delt kilde-/artefaktlagring. FÃ¸r API blir eksternt tilgjengelig: Entra/OIDC-identitet, server-side prosjekt- og ressursautorisasjon pÃ¥ hver route/GraphQL field, hemmelighetsisolasjon, revisjonsbundet godkjenning, sentral kildeplassering, kontrollert migrering og driftssikker backup/monitorering. **Ikke aktiver felles drift nÃ¥.**

## 02. Omfang og begrensninger

GjennomgÃ¥tt: forrige audit, API Program.cs/middleware/controllers/services, EF contexts/schema/migrations, launcher/hosts, frontend endpoint services og utvalgte tests/docs. Kun statisk inspeksjon. Ingen live konfigurasjon/credentials, database, nettverk, build/package runtime, AVD policy eller IT ressurser kontrollert. Ingen security probing eller scenarier mot en kjÃ¸rende service. Controllerroute-inventar er utledet fra nÃ¥vÃ¦rende route-attributter og MapControllers; GraphQL er separat. Dynamiske routes, proxy routes og runtime feature-gating er NOT VERIFIED.

Repoet hadde eksisterende ikke-committede endringer og untracked filer. De ble ikke endret av denne auditen. Kun denne rapporten er ny.

## 03. Verifisering av forrige audit

| Tidligere funn | NÃ¥vÃ¦rende kodekontroll |
|---|---|
| PostgreSQL/Npgsql, ikke SQLite | **CONFIRMED.** BirkNext.Api.csproj Npgsql provider; Program.cs UseNpgsql; DatabaseConnection. Ingen aktiv SQLite-provider funnet. |
| TesterNative DB per bruker | **CONFIRMED.** DataLayout.Default bruker %LOCALAPPDATA%\BirkNext\<profile>, Database er database/postgres-data; launcher starter lokal server. |
| Generisk ekstern DB-connection string | **CONFIRMED med begrensning.** DatabaseConnection.GetConnectionString aksepterer ConnectionStrings:Default eller POSTGRES_*; TesterNative overskriver til loopback. |
| Lokal backend/API per bruker | **CONFIRMED.** TesterLauncher.StartAndRunAsync starter BackendProcess og FrontendHost; PostgresManager starter lokal PostgreSQL. |
| API network binding forbudt | **CONFIRMED.** Program.cs kaller LoopbackBinding.Violations og thrower ved non-loopback URL; middleware sjekker peer og forwarding headers. |
| Auth kreves for API lokalt | **CONFIRMED, presisering:** LocalRequestGuardMiddleware krever gyldig LocalSession-cookie eller bearer for /api og /graphql. Controller AllowAnonymous-attributt omgÃ¥r ikke middleware, bortsett fra egne bootstrap/extension flows. |
| Ingen prosjektbasert authorization | **CONFIRMED.** Guard-kommentar sier at alle autentiserte lokale callers ser alle prosjekter; ingen generell project membership/role enforcement funnet. |
| Plan revision/409 | **CONFIRMED bare for TestPlanDraft paths.** EF Revision concurrency token; WriteAsync mapper DbUpdateConcurrencyException til 409. Ikke alle API writes er slik beskyttet. |
| Reviewer identity/approval integrity | **Presisert:** review events finnes, men Actor er navn eller lokal placeholder. Generic config-write policy, ikke Test Lead authorization; content hash binding ikke dokumentert. |
| Sources/files per user | **CONFIRMED for Git settings/app-data.** Hosts peker til per-user app-data/git-sources. All source bytes/files for every provider er ikke verifisert delt. |

## 04. NÃ¥vÃ¦rende arkitektur (bekreftet)

```text
Tester A / Windows profile A                Tester B / Windows profile B
Browser                                    Browser
 -> local FrontendHost                      -> separate local FrontendHost
 -> local BackendProcess                     -> separate local BackendProcess
 -> local PostgreSQL 127.0.0.1                -> separate PostgreSQL 127.0.0.1
 -> %LOCALAPPDATA%...app-data                -> separate app-data / git-sources
```

TesterLauncher.RunAsync tar SingleInstance-mutex per data root. StartAndRunAsync oppretter egne porter/credentials, starter PostgreSQL og backend, venter pÃ¥ health og starter frontend; avslutning stopper prosessene launcheren eier. Hosts.BackendProcess.Environment setter ConnectionStrings__Default, ASPNETCORE_URLS, BIRKNEXT_APP_DATA_DIR og GitSources__SettingsDirectory eksplisitt. Lokal DB kan ikke hoppes over i dette launch-path; ingen external API URL finnes her.

## 05. Anbefalt mÃ¥larkitektur (ikke implementert)

```text
AVD Tester A -- OIDC/TLS --+
AVD Tester B -- OIDC/TLS --+--> sentral BirkNext API --> PostgreSQL (server storage)
AVD Tester C -- OIDC/TLS --+           |              --> shared source/artifact store
AVD Test Lead -- OIDC/TLS -+           +--> audit/metrics/logs
```

Klientbrowser bruker API over TLS/reverse proxy; den fÃ¥r aldri DB credentials. Backend avgjÃ¸r brukeridentitet, prosjektmedlemskap og tillatelser per ressurs/handling. PostgreSQL datakatalog forblir pÃ¥ stÃ¸ttet serverdisk. Designet krever kode- og IT/security-endringer; det finnes ikke i TesterNative nÃ¥.

## 06. Backend API-inventar og sikkerhetsstatus

### Sentrale gates

Program.cs registrerer UseAuthentication â†’ LocalRequestGuardMiddleware â†’ UseAuthorization â†’ MapControllers; MapGraphQL registrerer /graphql. Ingen FallbackPolicy funnet. Middleware gjelder /api og /graphql bortsett fra OPTIONS, Browser Companion extension routes og local-session bootstrap, som har egne kontroller. For normale kall kreves loopback Host, lokal socket peer, ingen proxy headers og LocalSession-cookie eller validert JWT bearer. Cookie unsafe methods krever frontend Origin og custom header (CSRF). Derfor betyr manglende [Authorize] ikke anonym tilgang i dagens runtime; det betyr ingen ekstra per-action policy. JWT authentication innebÃ¦rer heller ikke prosjektrolle.

Policies i kode: IntegrationConfigurationWritePolicy, ActiveEventExecutePolicy, SystemConfigurationPermissions.Policy, WorkspaceTransferPermissions.Policy. Dette er capabilities/lokal trusted execution, ikke roller som Reader/Tester/Lead/Admin eller resource membership.

### Controller- og route-surface

API-prosjektet har 102 controller-filer, konvensjonelt registrert med MapControllers. Tabellen grupperer alle routable controllers, route-prefixer og antall HTTP method-attributter. Full action route suffix og method ligger i navngitt fil. Per-action authorization varierer; class-level policy er ikke hele svaret. For komplett assessment mÃ¥ hver action kobles til resource query og owner check. Denne auditen bekrefter surface og sentral gate, men semantisk kodegjennomgang av hundrevis av action bodies er ikke uttÃ¸mmende; dette er en eksplisitt release blocker/backlog item, ikke en pÃ¥stand om en verifisert exploit.

| Controller | Route-prefix | Actions: method count | Class-level policy |
|---|---|---|---|
| ActiveEventsController.cs | api/active-events | GET 6, POST 2 | ActiveEventExecute; fire GET AllowAnonymous, middleware gjelder fortsatt |
| AdminController.cs | api/admin | GET 7, POST 4 | none |
| AdoTestPlanExportController.cs | api/test-plan-suggestions/ado-export | GET 2, PUT 1, POST 3 | none; utvalgte actions policy |
| AdoTestPlansController.cs | api/azure-devops/test-plans | GET 5, POST 8, PUT 1 | none; per-action policy varierer |
| AiCodeReviewController.cs | api/ai-code-review; api/code-risk-review | GET 3, POST 1 | none |
| AltinnEnvironmentController.cs | api/target-environments/altinn | GET 1, POST 5, PUT 1, DELETE 2 | IntegrationConfigurationWrite |
| AltinnTestingController.cs | api/altinn-testing | GET 4, POST 7 | IntegrationConfigurationWrite; callbacks/reads varierer |
| AnalysisPageModelController.cs | api/analysis-page-model | GET 5 | none |
| ApiQualityController.cs | api/api-quality | POST 6, GET 2 | none |
| ApplicationMessagingController.cs | api/integrations/application-messaging | GET 2, POST 1, PUT 1, DELETE 1 | none |
| AuthenticatedBrowserSessionController.cs | api/frontend-quality/auth-session | POST 3, GET 1 | none; session-specific checks |
| AuthenticatedReviewController.cs | api/authenticated-review | POST 1 | none |
| AzureEnvironmentController.cs | api/azure-environment | GET 7, POST 5 | none |
| BlobStorageController.cs | api/blob-storage | GET 6, POST 2, DELETE 1 | none; observation mutations policy |
| BrowserAuthenticationController.cs | api/browser-authentication | GET 2, PUT 2, POST 1, DELETE 1 | none; selected mutations policy |
| BrowserAutomationDiagnosticController.cs | api/browser-automation-diagnostic | POST 1 | none |
| BrowserCompanionController.cs | api/browser-companion; api/browser-companion/extension | POST 9 | none; extension own checks |
| CanonicalInterpretationController.cs | api/canonical/{scope} | GET 22, POST 4 | none |
| CodeTraceabilityEvidenceController.cs | api/code-traceability | GET 1 | none |
| ContractEvidenceController.cs | api/contract-evidence | GET 4 | none |
| ContractTestingController.cs | api/contract-testing | GET 17, POST 22, PUT 8, DELETE 5 | IntegrationConfigurationWrite; selected action variation |
| CriticalE2EController.cs | api/critical-e2e | GET 3, POST 15, DELETE 1 | none |
| DeclaredIntegrationJourneysController.cs | api/integration-journeys/declared | GET 4, POST 3, PUT 1, DELETE 1 | none at class; inspect actions |
| DeliveryAzureDevOpsController.cs | api/delivery-reporting/azure-devops | GET 4, POST 4, PUT 1, DELETE 1 | none |
| DeliveryEvidenceController.cs | api/delivery-evidence | GET 2 | none |
| DeliveryReportingController.cs | api/delivery-reporting | GET 10, POST 4, PATCH 1, DELETE 3 | none |
| DependencyReviewController.cs | api/dependency-review | GET 9, POST 6 | none |
| EndpointDiscoveryHistoryController.cs | api/endpoint-discovery | GET 1, POST 1 | none |
| ExecutionAccessController.cs | api/execution-access | GET 1 | none |
| ExecutionEvidenceController.cs | api/execution-evidence | GET 13, POST 5, PUT 2, DELETE 2 | ActiveEventExecute |
| ExecutionExplorerController.cs | api/execution-explorer | GET 5 | none |
| ExternalIntegrationsController.cs | api/target-environments/external-integrations; api/setup-library | GET 9, PUT 2, POST 12, DELETE 1 | IntegrationConfigurationWrite |
| FrontendAccessibilityController.cs | api/frontend-accessibility | POST 2 | none |
| FrontendAuthenticatedApiSurfaceController.cs | api/frontend-quality | POST 1 | none |
| FrontendBrowserRuntimeController.cs | api/frontend-runtime | POST 2 | none |
| FrontendLighthouseController.cs | api/frontend-lighthouse | POST 2 | none |
| FrontendQualityEnginesController.cs | api/frontend-quality-engines | GET 2, POST 1 | none |
| FunctionalQualityScopeController.cs | api/delivery-reporting/projects/{projectId}/scope | GET 4, POST 5, PUT 2, DELETE 2 | none |
| GeneratedDocumentationController.cs | api/system-diagnostics/generated-documentation | POST 2 | none |
| GitSourcesController.cs | api/git-sources | GET 5, POST 7, PUT 1, DELETE 1 | none |
| GraphQlSchemaArtifactsController.cs | api/graphql-schema-artifacts | GET 1, PUT 1, DELETE 1 | none |
| HeadlessAuthDiagnosticController.cs | api/headless-auth-diagnostic | POST 2 | none |
| ImpactAnalysisRunsController.cs | api/impact-analysis/runs | GET 3, POST 1 | none |
| ImplementationGapsController.cs | api/implementation-gaps | GET 2, POST 2 | IntegrationConfigurationWrite |
| ImplementationTraceabilityController.cs | api/implementation-traceability | GET 1, POST 2 | none |
| IntegrationEvidenceBindingsController.cs | api/integration-review/evidence-bindings | GET 2, PUT 1 | IntegrationConfigurationWrite |
| IntegrationJourneysController.cs | api/integration-journeys | GET 10, POST 2 | ActiveEventExecute; anonymous attributes on some actions do not bypass guard |
| IntegrationMessageFlowController.cs | api/integration-message-flow | GET 2, PUT 1 | IntegrationConfigurationWrite |
| IntegrationsController.cs | api/integrations; api/integration-review | GET 8, POST 7, PUT 4, DELETE 2 | IntegrationConfigurationWrite |
| IntegrationSuitesController.cs | api/integration-suites | GET 9, POST 9 | IntegrationConfigurationWrite |
| IntegrationTopologyController.cs | api/integration-topology | GET 1, POST 4 | IntegrationConfigurationWrite |
| IqrSourceEvidenceController.cs | api/integration-review/source; api/source-analysis | GET 8, POST 1 | none |
| LibraryPageModelController.cs | api/library-page-model | GET 2 | none |
| LocalHttpsProxyController.cs | api/local-https-proxy | GET 1, POST 13 | none |
| LocalSessionController.cs | api/local-session | POST 1, DELETE 1 plus status/bootstrap | own launch/session checks |
| ManagedEdgeCdpController.cs | api/managed-edge | POST 7 | none |
| ManualReviewController.cs | api/manual-reviews | GET 1, POST 1 | none |
| ManualTestRunsController.cs | api/manual-test-runs | GET 7, POST 6, PUT 1 | IntegrationConfigurationWrite |
| MarkdownDiagnosticsController.cs | api/system-diagnostics/markdown | POST 2 | none |
| MessageObservationsController.cs | api/message-observations | GET 2, POST 1, DELETE 1 | IntegrationConfigurationWrite |
| ParquetController.cs | api/parquet | GET 9, POST 13, DELETE 2 | IntegrationConfigurationWrite |
| PerformanceTestsController.cs | api/performance-tests | GET 17, POST 17, PUT 3, DELETE 2 | ActiveEventExecute |
| PipelineReviewController.cs | api/pipeline-review | GET 4 | none |
| ProjectActivityController.cs | api/project-activity | GET 1, POST 1 | none |
| ProjectCompatibilityController.cs | api/system-diagnostics/project-compatibility | POST 1 | none |
| ProjectDocumentsController.cs | api/project-documents | GET 2, PUT 1 | none |
| ProjectImportController.cs | api/project-import | GET 1, POST 3, DELETE 1 | none |
| QualityReviewPageModelController.cs | api/quality-review-page-model | GET 4 | none |
| RecommendedWorkflowController.cs | api/recommended-workflow | POST 7 | none |
| ReviewPageModelController.cs | api/review-page-model | GET 5 | none |
| RuntimeAuthenticationController.cs | api/runtime-authentication | GET 1, POST 1 | none |
| SampleProjectsController.cs | api/sample-projects | GET 4 | none |
| ScimProvisioningController.cs | api/integrations/scim | GET 3, POST 2 | none at class |
| ScopeRegistryController.cs | api/scopes | GET 8, POST 6, PUT 1 | SystemConfiguration |
| SecurityClassificationController.cs | api/security-classification | GET 4, PUT 1, DELETE 1, POST 3 | none |
| SetupProfilesController.cs | api/setup-profiles/{packId} | GET 2, POST 2 | IntegrationConfigurationWrite |
| SnapshotsController.cs | api/snapshots | GET 6, POST 1, PUT 2 | none |
| SourceChangeImpactController.cs | api/impact-analysis/source-change | GET 3, POST 1 | none |
| SourceContextController.cs | api/source-context | GET 2, POST 2 | SystemConfiguration on selected action |
| SourceIntegrationsController.cs | api/integrations/source-integrations | GET 1, POST 1 | none |
| SourceRepositoryAssociationsController.cs | api/source-repository-associations | GET 1, POST 2 | none |
| SpecificationCatalogController.cs | api/specification-catalog/{projectKey} | GET 1, POST 1 | none |
| StoryGroupingController.cs | api/test-plan-suggestions/story-grouping | GET 1, POST 1 | IntegrationConfigurationWrite |
| SystemInfoController.cs | api/system | GET 2 | none |
| SystemUnderstandingController.cs | api/system-understanding | GET 5, POST 8, PUT 1 | IntegrationConfigurationWrite on selected actions |
| TargetEnvironmentDetectionController.cs | api/frontend-target | POST 3 | none |
| TaskAlignmentRunsController.cs | api/task-alignment/runs | GET 3, POST 1 | none |
| TechnicalHistoryController.cs | api/technical-history | GET 5, POST 5 | none |
| TechnologyCoverageController.cs | api/technology-coverage | GET 1 | none |
| TestCoverageReviewController.cs | api/test-coverage-review | GET 4, POST 2 | none |
| TestDataController.cs | api/test-data | GET 15, POST 21, PUT 2, DELETE 3 | none |
| TestEvidenceController.cs | api/test-evidence | GET 2, POST 1 | none |
| TestFixtureController.cs | root fixture routes | GET 3 | fixture surface; deployment status NOT VERIFIED |
| TestPackSourceGuidanceController.cs | api/integration-journeys/packs/{packId}/source-guidance | POST 1 | IntegrationConfigurationWrite |
| TestPlanSuggestionsController.cs | api/test-plan-suggestions | GET 19, POST 18, PUT 2 | IntegrationConfigurationWrite on most writes; reads vary |
| TranslationSettingsController.cs | api/translation-settings | GET 3, PUT 1, POST 4 | SystemConfiguration on selected actions |
| WasmPerformanceController.cs | api/wasm-performance | POST 1 | none |
| WasmSecurityController.cs | api/wasm-security | POST 1 | none |
| WorkspacePersistenceController.cs | api/workspace-persistence | GET 6, POST 8, DELETE 1 | none; user scoping must be checked per method |
| WorkspaceTransferController.cs | api/workspace-transfer; partial api/packages | GET/POST and package actions | backup/restore class policy; partial package actions require individual review |

Action-level routes and per-method attributes are in these controller files. The table is the complete controller surface index, but not a claim that each action bodyâ€™s object scoping was exhaustively audited. Any shared release needs generated action-by-action inventory including route, verb, resource scope, principal, permission, input validation, concurrency and audit. This remains a release requirement.

GraphQL is mapped at /graphql, development tooling disabled outside Development; central middleware applies to request. No RequireAuthorization on GraphQL mapping was seen. Resolver-level project authorization NOT VERIFIED. MapFallbackToFile serves SPA, not API. No separate general health route mapping found in Program.cs.

## 07. Authentication

Program.cs registers JWT bearer with Authentication:Entra:Authority/Audience, HTTPS metadata and MapInboundClaims=false, plus LocalSessionAuthenticationHandler. LocalSessionService issues a random token after a launch code exchange; token hashes remain in memory, idle lifetime 12h, absolute 7 days, rotation revokes sessions. Local cookie is HttpOnly/SameSite Strict; restart clears in-memory sessions. Guard validates bearer if supplied (invalid bearer is rejected rather than replaced by ambient cookie), otherwise local cookie.

Local identity is the constant local-user per backend, not Windows account identity and not distinguishable between simultaneous testers on one shared instance. Entra bearer may supply a stable principal if configured, but project membership is absent. TestPlanSuggestionsController.Actor uses authenticated name or Local user placeholder. Services often accept actor strings; not every source is verified server-derived. Do not trust caller-supplied CreatedBy/ApprovedBy. No Windows integrated authentication found. Authentication required for normal API request is confirmed; multi-user identity model is not ready.

## 08. Authorization

Existing policies protect selected operations: active-event execution, integration configuration, system configuration, workspace transfer. They are not a central project RBAC system. No role admin/membership model for Reader, Tester, Test Lead, Project Admin, System Admin found.

AdminController methods have no class policy; some methods call config access checks, but reset/system operations need explicit authorization review. ADO routes have a mix of action policy and unannotated methods. TestPlanSuggestions writes generally use IntegrationConfigurationWritePolicy, not distinct tester/reviewer role. GitSources, SourceContext, Snapshots, ProjectImport, SpecificationCatalog and TestData include mutable actions with no class-level policy. These still require local authentication in current deployment. If network guard is relaxed, they do not inherit project permissions automatically.

Minimum: default-deny shared resource routes; explicit operation policy plus project authorization; principalâ†’project membershipâ†’action permissionâ†’object relationship. Apply to GraphQL too. Anonymous exceptions allowlisted.

## 09. Object authorization / IDOR and project isolation

Plan service loads rows by plan ID, then applies domain consistency checks (project key/discovery). That is not user membership. GUIDs make guessing harder but are not authorization. Snapshot, testdata, workspace, source and transfer routes use IDs; no universal resource authorization filter was found. Workspace UserId scoping exists for some workspace persistence, not all app data.

No exploit was attempted. Confirmed design gap: no project/resource policy across all application routes. Current middleware comment says local authenticated user sees every project. In shared mode this is a BOLA/IDOR risk unless every lookup is scoped to callerâ€™s membership. Exact route-level exploitable cross-project dataflow is NOT VERIFIED in broad static sweep; avoid claiming a specific exploit without targeted method review.

## 10. User and project isolation

Records include project/application/scope/snapshot identifiers; some workspaces have UserId. No consistent authenticated partition key across all contexts, caches, file stores and exports. Plan project key is stored in draft content/record metadata, not tied to caller ACL. Frontend LastDiscoveryCache keys project/snapshot, but all backend cache key isolation was not exhaustively verified. Project isolation is NOT READY.

## 11. Concurrent editing matrix

| Entity/write | Current mechanism | Lost update / atomicity | UX / recommendation |
|---|---|---|---|
| TestPlanDraft save/review/add/restore/lifecycle | Revision EF concurrency token; BaseRevision; 409 on EF concurrency error | Same-row stale update rejected, no merge; plan JSON and revision row saved together via SaveChangesAsync | Good baseline; audit all writes; preserve draft and show diff/reapply. |
| Plan create | Guid, CreationRequestId, process-local dictionary and database lookup | dictionary not cross-process; initial plan+revision one SaveChanges | DB unique/idempotency constraint for multiple API instances. |
| Immutable plan snapshots | unique draft+sequence and fingerprint indexes | duplicate snapshot constraints protect selected writes | Good scoped protection. |
| Cases/suites/steps inside draft | serialized whole ContentJson; plan revision | no row-level merge; hierarchy saved as aggregate | Validate references, case-level conflict workflow if needed. |
| Manual test runs | Revision concurrency token, unique one-in-progress index | partial concurrency protection | Verify all paths and 409 UX. |
| Story grouping/technical findings/some records | Revision/Version tokens on selected entities | mixed | inventory every mutable entity. |
| Scope registry | version token/unique constraints | partial domain protection | role-level approval + stale UX. |
| TestData/source relations/classification/templates/settings | no universal primitive verified | per-operation behavior NOT VERIFIED | token/ETag or serialized command/DB constraints. |
| Source imports/canonical snapshots | IDs/fingerprints/unique indexes in places | no universal import transaction/lock shown | stagingâ†’validateâ†’atomic publish per project/snapshot. |
| ADO mappings/transfers | uniqueness in selected mappings; external side effects | cannot atomically transact DB and ADO | idempotency key + durable reconciliation state. |
| JSON settings/files | individual stores and file replacement in some paths | no global concurrency | shared admin settings versioned; secrets in vault. |

EF SaveChanges transactions do not make separate API calls/files/ADO one transaction. Migration advisory lock only protects schema migration.

## 12. Frontend conflict UX

Plan service maps DbUpdateConcurrencyException to 409, tells user another person changed it, reload and that save did not happen. Controller Run returns service status. Frontend contains plan revision diff helpers and tests, but static inspection did not prove all mutations retain unsaved edits, show remote diff and permit safe reapply. No automatic merge guarantee. **PARTIALLY READY / NOT VERIFIED across routes.** Acceptance should state: â€œTesttilfellet er endret av en annen bruker. Dine endringer er ikke lagret.â€ Retain local draft, show differences, allow manual reapply to latest basis; never retry a whole stale object automatically.

## 13. Approval integrity

ReviewAsync appends TestCaseReviewEvent(state, actor, rationale, timestamp) into plan content and saves next plan revision. Accepted review checks blockers, unresolved conflicts and SourceChanged. Refresh can downgrade accepted to UnderReview when source changes. Useful process state, but not strong multi-user attestation:

- actor is name/local placeholder, not immutable principal subject;
- same IntegrationConfigurationWrite policy for editing and reviewing; no TestLead project role found;
- approval event not shown bound to fingerprint of title/preconditions/test data/steps/expected/source/test level;
- invalidation after all manual edit paths not proven;
- restore creates a new revision but approval validity must be re-evaluated;
- no self-approval policy found; product decision needed.

Minimum: immutable approval record keyed to exact plan/case revision + content hash, authenticated reviewer subject, project Lead permission, stale on any covered-field change/restore/source update, no caller actor fields, audit withdrawal/reapproval.

## 14. Audit trail

CreatedBy/UpdatedBy/SavedBy/ApprovedBy fields occur across records. Quality varies. Test plan review has actor/time/reason, but actor may be local placeholder. ADO transfer local initiator identity needs verification. Import initiator not universal. CorrelationIdMiddleware and rolling Serilog help troubleshooting; not tamper-resistant audit. Central events should include subject ID, project/resource, operation, result, revision before/after, reason, source snapshot/transfer ID and correlation ID; redact secrets and control log retention/access.

## 15. Source snapshots and files

| Artifact | Evidence/storage | Cross-user finding |
|---|---|---|
| Git source config/checkout | Hosts sets GitSources__SettingsDirectory under app-data/git-sources; app-data is per TesterNative root | Confirmed not shared between installations by default. |
| Source snapshots/evidence | SnapshotRegistryDbContext, AppDbContext, IqrSourceEvidenceController, ReviewSourceEvidenceProvider persist IDs/hashes/evidence | Metadata can be central; underlying source bytes/provider roots need verification. |
| Specification catalog | DB model stores archive hash and documents/entries/evidence JSON | Some content DB-backed; raw archive/temp input retention varies. |
| Manual run attachments | ManualRunAttachmentRecord stores content bytea plus project/run/AddedBy | DB-shared in a shared backend, still needs authorization. |
| Workspace packages/backups | WorkspaceTransfer and ManagedFileStores use server filesystem | DB backup alone may omit files. |
| Offline translation assets | package path/environment based | central deployment or client distribution needed. |
| Logs/settings/temp/cache | AppDataLocation, data.AppData and logs/temp | per backend instance; not replicated. |

No confirmed persisted absolute Tester A Windows path was found in the sampled plan/source metadata; a complete code sweep of every path field was not done. Exact universal M2LB raw ZIP/extracted content location is NOT VERIFIED. Current TesterNative import belongs to Aâ€™s local backend and app-data; B has a separate backend/storage, so cannot automatically access the exact same source evidence. â€œDB sharedâ€ alone is insufficient.

## 16. Source import concurrency

Snapshot IDs/fingerprints and discovery basis are preserved; plan creation rejects historical discovery and source refresh can mark source changed. No global per-project import lock or complete atomic boundary across DB + filesystem was verified. Unique fingerprints protect selected records only. Staging, partial visibility, concurrent v1/v2 activation, cache invalidation and failure recovery are NOT VERIFIED for all imports. Recommend archive staging/hash validation, immutable snapshot record, transactional metadata+active pointer, publish only when complete; cache keys include project/source/snapshot/version.

## 17. TesterNative Local/Shared feasibility

Local Mode exists and should remain offline: bundled PostgreSQL, backend, frontend, app-data. Shared Mode does not: launcher prepares local DB and starts backend and frontend, frontend API points to local backend, session code flow expects launcher. No remote endpoint config, auth redirect, shared outage UX or no-local-fallback logic found. Future Shared Mode should select trusted remote HTTPS config, skip local DB/backend entirely, use OIDC and show explicit outage; never silently create local divergent plans. Two explicit modes are feasible but require launcher/frontend/auth changes.

## 18. Backend remote configuration

Generic ASPNETCORE_URLS/Kestrel could express broad binding, but startup rejects non-loopback. CORS origin is configurable. No shared TLS/reverse proxy profile or forwarded-header trust is enabled; guard rejects proxy headers. FrontendHost maps local ports; remote API config not found. Therefore bind capability is not supported remote deployment. TLS may be terminated at enterprise proxy only after explicit proxy trust, secure cookies, external origin and CSRF review. Retry/offline behavior for remote endpoint does not exist.

## 19. PostgreSQL readiness

Npgsql and configurable connection string are present; seven EF contexts use PostgreSQL. Program startup runs DatabaseMigrationLock.MigrateAsync on seven contexts. PostgreSQL advisory lock serializes migrations but is not migration ownership, rollback, version compatibility or deployment orchestration. Npgsql pooling enabled in local connection; central pool size/timeout not set. TesterNative uses non-superuser role; central least-privilege provisioning needed. Full connection string may permit SSL options, but configured central TLS validation not verified. PostgreSQL is viable central DB over client/server protocol; datadir stays on server-supported storage.

## 20. Database migration risks

Startup auto-runs MigrateAsync. Every backend build that starts may apply schema; advisory lock avoids migration race only. Local launcher pre-upgrade backup cannot be assumed in central profile. Mixed app versions/schema compatibility and rollback not solved. Recommend controlled single migration identity/job, verified pre-backup, expand/contract, compatibility matrix, staged rollout and health gate. No migrations executed.

## 21. Secrets and credentials

- Local PostgreSQL credentials random and DPAPI CurrentUser protected; not usable as central credential model.
- Delivery Reporting PAT described in Program comments as Data Protection encrypted in App_Data. ADO/Git/Azure providers use separate stores. Per-user versus shared ownership is not consistently proven; no secret values accessed.
- Git auth settings under app-data; central source credentials require delegated identity or managed vault.
- Shared Data Protection key ring, token storage, logout/revocation and replica behavior not verified.
- Existing log redaction covers selected callback/password patterns; comprehensive secret leakage audit NOT VERIFIED.

Shared model: user tokens keyed to validated subject/tenant; project service credentials in approved vault with explicit ACL; never return secret from GET; audit use without value; centrally protect Data Protection keys.

## 22. File access security

Selected package extraction code includes path handling and CreateNew writes; WorkspacePackageService uses two-phase import. This does not prove all file routes safe. ProjectImport, GitSources, test-data, Parquet, blob, backup download and source retrieval need route-to-path review. Backup filename containment, traversal prevention, archive size/type bounds and temp cleanup were not dynamically tested. Central service needs isolated service-account ACL roots, normalized paths, archive slip tests, project-scoped authorization, retention and backup linkage.

## 23. Azure DevOps multi-user safety

AdoTestPlansController, AdoTestPlanExportController and AdoTestPlanTransferService implement settings, preview, transfer, reconcile, results. Selected writes use IntegrationConfigurationWritePolicy; controller has mixed actions. This is local capability, not project role/test lead authorization. Remote ADO effects and DB mapping/history are separate steps; cannot share one transaction. Some mapping unique constraints and transfer revision/history exist; universal retry idempotency/serialization is not verified. Shared personal PAT could expose one userâ€™s permissions to another. Use delegated user credentials or project-scoped service identity, role authorization, destination ownership validation, idempotency and durable reconciliation. No live ADO calls.

## 24. AVD and infrastructure

| Component | Current | Shared requirement |
|---|---|---|
| API host | tester process/session | IT-managed persistent service |
| PostgreSQL | bundled local per user | central PostgreSQL on supported local/server storage |
| Network | localhost | AVD to internal TLS endpoint, DNS/firewall |
| Identity | local launch code; optional Entra JWT | Entra/OIDC groups/claims and lifecycle |
| Source | per-user Git config/cache | shared artifact/source store |
| Operations | local backup primitives | off-host backup, restore drill, monitoring/log ownership |
| Client rights | package includes binaries; enterprise control may block | no server/admin install for testers; IT allowlisting |

AVD pooling, FSLogix, profile persistence, cert issuer, firewall, PostgreSQL service availability NOT VERIFIED. Do not run central service inside tester session. Docker/Podman is not inherently required; approved VM/service/database platform suffices subject to IT.

## 25. Logging and monitoring

CorrelationIdMiddleware and rolling Serilog logs exist; callback query redaction and launcher password redaction exist. No complete per-action audit for approvals/imports/permission denials/ADO operation proven. Central health/metrics/alerts/SIEM are not established. Add subject/resource/action/revision and result without sensitive payloads; monitor authz denials, import completion, migration, connection pool, storage, transfer state.

## 26. Backup and recovery

FullBackupService uses PostgreSQL exported repeatable-read snapshot plus configured managed file stores, encrypted .bnbackup, migration heads and file consistency retry/warning. TesterNative local launcher stores local/manual/pre-upgrade backups. This is not evidence of central schedule, off-host retention, PITR, key custody, restore drills or agreed RPO/RTO. PostgreSQL-only backup insufficient when source/git/artifact/settings are external files. Restore rolls back revisions after backup time. Central operations must schedule, encrypt, retain, include all stores and Data Protection keys, and verify restore.

## 27. Prioriterte funn

| ID | Severity / finding | Evidence, impact | Mitigation / status |
|---|---|---|---|
| SB-01 | **CRITICAL blocker: no project/resource authorization** | LocalRequestGuard comment says every authenticated caller sees every project; no uniform resource policy. Dangerous if shared guard relaxed. | Default-deny authorization on controllers/services/GraphQL; cross-project tests. Confirmed design gap; no exploit run. |
| SB-02 | **CRITICAL blocker: remote API unsupported** | LoopbackBinding throws for non-loopback; middleware rejects remote peers/proxy. | Purpose-built Shared profile after SB-01, TLS/proxy/CSRF review. Confirmed. |
| SB-03 | **HIGH: one local identity** | LocalSessionAuthenticationHandler principal is local-user; plan actor fallback. Cannot attribute testers. | Entra subject/tenant principal. Confirmed. |
| SB-04 | **HIGH: no reviewer/admin role separation** | Plan review uses config-write policy; no test-lead membership role. | Project roles, exact approval policy. Confirmed for plan route. |
| SB-05 | **HIGH: source/filesystem per instance** | GitSources directory under app-data. DB sharing misses source files/config. | Shared immutable source store. Confirmed for Git; other providers partial. |
| SB-06 | **HIGH: broad unscoped controller surface** | 102 controllers, mutable/admin/source/test-data routes, many lack class policy; middleware only local auth boundary. | Route action resource matrix and default-deny. Exact body impact partly not verified. |
| SB-07 | **HIGH: secrets not multi-user scoped** | DPAPI CurrentUser and App_Data stores; PAT encrypted but ownership unclear. | Vault/delegated tokens, subject/project ACL. Confirmed design gap. |
| SB-08 | **MEDIUM: conflict UX incomplete** | service 409 exists; universal UI retain/diff/reapply not verified. | Preserve draft and safe reconciliation. |
| SB-09 | **MEDIUM: approval not hash-bound** | plan event has actor/time, no demonstrated content fingerprint binding. | Immutable revision/hash approval and invalidation. |
| SB-10 | **MEDIUM: import not universally atomic** | multiple DB/file stores; full global lock/staging not proven. | Stage/publish and recovery. NOT VERIFIED for all flows. |
| SB-11 | **MEDIUM: startup migration ownership** | Program migrates seven contexts; advisory lock only serializes. | Central migration job/backup/compatibility. Confirmed. |
| SB-12 | **LOW/INFO: AVD profile persistence** | LocalAppData path; AVD retention external. | IT confirms FSLogix/profile rules. NOT VERIFIED. |

Severity is for a future shared deployment if its boundary is relaxed, not claim that current loopback install is remotely exploitable.

## 28. Beredskapsmatrise

| Capability | Status | Evidence / minimum change | Owner |
|---|---|---|---|
| Central PostgreSQL | PARTIALLY READY | Npgsql/connection override; define TLS/pool/least privilege | Dev + IT |
| Central backend | NOT READY | non-loopback deliberately rejected; shared host profile | Dev + IT |
| Remote client | NOT READY | no external API URL; explicit shared endpoint | Dev |
| TLS | NOT VERIFIED | no shared TLS setup; internal cert/proxy | IT + Security |
| Authentication | PARTIALLY READY | JWT exists, local user model; enforce Entra shared profile | Identity + Dev |
| Project authorization | NOT READY | no membership/roles; default-deny ACL | Dev + Product |
| Object authorization | NOT READY | no common owner-scoped resource filter | Dev |
| Server roles | NOT READY | capability policies only; create role map | Security + Test Lead |
| Approval binding | NOT READY | no immutable hash/lead auth | Dev + Test Lead |
| Plan concurrency | PARTIALLY READY | plan revision 409 only; cover every write | Dev |
| Case/suite concurrency | PARTIALLY READY | embedded plan aggregate; other entities mixed | Dev |
| Import concurrency | NOT VERIFIED | no global staging/publish proof | Dev |
| Shared source storage | NOT READY | per-user git-sources | Dev + IT |
| Snapshot provenance | PARTIALLY READY | IDs/fingerprints exist; project boundary/pointer atomicity | Dev |
| Secret isolation | NOT READY | mixed local DPAPI/App_Data | Security + IT |
| ADO authorization | NOT READY | generic policy/shared credential risk | Dev + Security |
| Migrations | PARTIALLY READY | advisory lock and startup Migrate | Dev + IT |
| Backup/restore | PARTIALLY READY | encrypted backup includes managed stores; central schedule absent | IT + Dev |
| Logs/monitoring | PARTIALLY READY | correlation/rolling logs; audit/central metrics absent | IT + Dev |
| AVD package | PARTIALLY READY | local mode shipped; Shared mode absent | Dev + IT |
| Local mode preservation | READY TODAY | current offline launcher | Dev regression acceptance |
| Shared fail-closed | NOT READY | no shared mode/outage behavior | Dev |

## 29. Prioritert backlog (design only)

### Phase 0 â€“ sikkerhetsblokkere

| Priority | Item / affected components | Acceptance / suggested test | Owner / omitted risk |
|---|---|---|---|
| P0-1 | Stable enterprise principal: Program, guard, frontend/session | unauthenticated denied; subject+tenant recorded; expiry/logout tested | Identity+Dev; no attribution |
| P0-2 | Default-deny project/resource authorization: all controllers/services/GraphQL | table-driven cross-project list/get/write/export deny | Dev+Test Lead; data disclosure |
| P0-3 | Roles and admin/review split: plan, Admin, source, ADO, reset | tester cannot lead-approve/delete/reset; project lead can approve assigned project only | Dev+Security; unauthorized actions |
| P0-4 | Secret ownership: Git/ADO/Azure/Data Protection | A cannot retrieve/use B token; project service credential scoped; redaction tests | Security+IT; credential disclosure |

### Phase 1 â€“ integrity

| Priority | Item / affected code | Acceptance / test | Owner / risk |
|---|---|---|---|
| P1-1 | concurrency inventory across mutable records | same-base concurrent writes yield one save/one 409, no lost write | Dev |
| P1-2 | revision-bound approval | hash all covered fields; edit/restore/source change marks stale | Dev+Test Lead |
| P1-3 | principal-derived audit actor | stable subject and before/after revisions for every write | Dev |
| P1-4 | atomic aggregate writes | injected failures leave no partial plan/suite/import state | Dev |

### Phase 2 â€“ shared source

| Priority | Item | Acceptance / test | Owner / risk |
|---|---|---|---|
| P2-1 | shared source artifact model | A imports; B separate AVD reads identical snapshot hash | Dev+IT; missing evidence |
| P2-2 | atomic imports/cache keys | parallel versions yield complete immutable snapshots; no mixed cache | Dev |
| P2-3 | file access/retention | cross-project download denied; traversal/size/archive checks | Dev+IT |

### Phase 3 â€“ dual modes

| Priority | Item | Acceptance / test | Owner / risk |
|---|---|---|---|
| P3-1 | explicit Local/Shared startup graph in TesterLauncher/Hosts/FrontendHost | Local starts local DB; Shared starts no local DB/API | Dev |
| P3-2 | remote auth/config/outage UI | validated TLS, OIDC, outage error, never local fallback | Dev+Security; split data/token leak |
| P3-3 | Local regression | offline workflow and existing data paths unchanged | Dev |

### Phase 4 â€“ operations

| Priority | Item | Acceptance | Owner / risk |
|---|---|---|---|
| P4-1 | central migration release process | single owner, verified pre-backup, compatibility/rollback runbook | Dev+IT |
| P4-2 | PostgreSQL operations | TLS, least privilege, pool sizing, patch/HA monitoring | IT |
| P4-3 | backup/DR | scheduled off-host backup of DB+files+keys; restore meets RPO/RTO | IT+Dev |
| P4-4 | observability | authz/import/migration/DB alerts; redaction checks | IT+Dev |

### Phase 5 â€“ acceptance

Isolated CI databases and fake remote providers only: authentication, cross-project BOLA, stale-edit retention, approval race/invalidation, concurrent source imports, cross-session snapshot read, secret isolation, ADO authorization/idempotency/partial failure, file boundary, migration/restore, local offline, shared outage no fallback. None run in this audit.

## 30. Acceptance test design

Use principals A/B/Lead and projects X/Y in disposable environment. Verify unauthenticated denial for API and GraphQL; role matrix; IDs from Y denied to X; same-revision concurrent plan updates result one success and one explicit conflict with loser draft retained; approval becomes stale for title/precondition/data/step/expected/source/test-level mutation and restore; two source imports create complete snapshots and second AVD reads same hash; A cannot read B tokens; ADO retries do not duplicate remote cases; archive/path traversal and project file boundary; backup restores source stores and database references; Local Mode remains offline; Shared Mode outage shows clear error and never starts local DB.

## 31. Architecture choice

| Option | Assessment |
|---|---|
| A. Local TesterNative per tester | Supported now, offline and isolated; no collaboration. Preserve. |
| B. Multiple local backends to central PostgreSQL | Generic connection config can be external, but **not recommended**: no user/project ACL, local source stores, shared secret ownership, file access, migration ownership. |
| C. One central backend + PostgreSQL | Preferred shared architecture after P0-P4; one authorization boundary and shared stores. Not ready. |
| D. Dual-mode TesterNative | Preferred product shape: explicit Local and Shared. Requires launcher/frontend/auth config changes; Shared skips DB/API local and fails closed. |

PostgreSQL does not isolate application users or files. Do not activate B/C by only changing a connection string or relaxing loopback.

## 32. Ã…pne spÃ¸rsmÃ¥l til IT

AVD pooled/personal? FSLogix and LocalAppData lifecycle? Approved internal host OS/service model? PostgreSQL platform/version/HA/TLS? DNS/certificate/firewall from AVD? Entra app/claims/groups/conditional access? Secret vault and Data Protection key custody? Shared source Git/artifact storage and ACL? Backup RPO/RTO/key escrow/restore owner? SIEM/log retention? Application allowlisting/signing? None verified from repository.

## 33. Ã…pne spÃ¸rsmÃ¥l til Test Lead / produkteier

Which teams/projects can see what? Who can create/edit/review/approve/archive/delete/restore/export? Is self-approval permitted? Which fields invalidate approval? Must approval target immutable revision? Plan-level or case-level conflicts? Retention for case history? Source import activation/review? ADO delegated vs project-shared credential? Audit retention/RPO/RTO? Owner of templates/global settings?

## 34. Evidence appendix

| Area | Exact source evidence |
|---|---|
| API pipeline/routing | AIAssisted/backend/BirkNext.Api/Program.cs: AddAuthentication/AddAuthorization, LoopbackBinding, middleware ordering, MapControllers, MapGraphQL, startup migrations. |
| Central guard | Services/LocalSecurity/LocalRequestGuardMiddleware.cs: InvokeAsync, session/bootstrap exceptions, IsLocalPeer, Host/proxy, bearer/cookie, CSRF. |
| Auth identity | LocalSessionAuthenticationHandler.cs: local-user principal; LocalSessionService.cs: code/session issue, lifetimes, in-memory storage, revoke/rotation. |
| Bind policy | Services/LocalSecurity/LoopbackBinding.cs: Violations, PortOnlyUrls; Program.cs startup exception. |
| TesterNative | launcher/TesterLauncher.cs RunAsync/StartAndRunAsync; Layout.cs DataLayout; SingleInstance.cs; PostgresManager.cs. |
| Child API/frontend | launcher/Hosts.cs BackendProcess.Environment/Start and FrontendHost: DB/appdata/Git paths/local URLs. |
| DB provider | Configuration/DatabaseConnection.cs GetConnectionString; BirkNext.Api.csproj; Program.cs UseNpgsql registrations. |
| Migrations | Data/DatabaseMigrationLock.cs MigrateAsync; Program.cs startup migration scope. |
| Plan routes | Controllers/TestPlanSuggestionsController.cs route api/test-plan-suggestions, Actor, method-level policies, Run. |
| Plan concurrency/review | Services/TestPlanning/TestPlanSuggestionService.cs SaveAsync, ReviewAsync, AddManualCaseAsync, WriteAsync, RestoreRevisionAsync; Data/AppDbContext.cs TestPlanDraftRecord Revision token and revision unique index. |
| Plan UI | frontend Services/PlanRevisionDiff*, PlanPreparePresentation.cs, Pages/ManualTesting.razor; existing PlanRevisionDiffTests.cs (source read only). |
| Other concurrency | Data/AppDbContext.cs tokens/indexes for selected manual-run, story-grouping, technical-finding and other records. |
| Source storage | Services/AppDataLocation.cs; launcher Hosts.cs Git path; Controllers/GitSourcesController.cs, ProjectImportController.cs, SnapshotsController.cs, SpecificationCatalogController.cs; Services/SourceAnalysis/ReviewSourceEvidenceProvider.cs. |
| Secrets | launcher Support.cs DatabaseCredentials Save/Load DPAPI; Program.cs PAT storage comment; ADO/Git/Azure providers. No values inspected. |
| ADO | Controllers/AdoTestPlansController.cs and AdoTestPlanExportController.cs; Services/TestPlanning/AdoTestPlanTransferService.cs and AdoTestPlanSettingsService.cs; AppDbContext ADO mapping indexes. |
| Files/backup | Services/WorkspaceTransfer/FullBackupService.cs CreateAsync; ManagedFileStores.cs; WorkspaceTransferController*.cs; launcher/BirkNext.Updater/PackageInspector.cs. |
| Logging | Program.cs Serilog; Middleware/CorrelationIdMiddleware.cs; launcher Support.cs LauncherLog.Redact. |
| Endpoint inventory | backend/BirkNext.Api/Controllers (102 files); Program.cs MapControllers/MapGraphQL. Per action route and attributes in listed controller files in Â§06. |

Line numbers can vary with uncommitted branch state; class/method names and route literals are the reproducible references. Tie implementation follow-up to an exact commit/build. No secrets, database contents or live endpoint data were copied into this report.
### Action-level route register

Generated by read-only parsing of each controller source. Each line lists HTTP verb, fully combined attribute route, controller/action, method-level policy (or none). The central guard described above still applies unless a route is one of its explicit exceptions. Input validation, project ownership and persistence behavior require action-body review; this register is not a security certification.

| Verb | Route | Handler | Method policy attribute |
|---|---|---|---|
| GET | api/active-events/environments/{environmentId}/trust | ActiveEventsController.cs:Trust | none |
| GET | api/active-events/providers | ActiveEventsController.cs:Providers | none |
| GET | api/active-events/scenarios | ActiveEventsController.cs:Scenarios | none |
| GET | api/active-events/readiness | ActiveEventsController.cs:Readiness | none |
| POST | api/active-events/runs | ActiveEventsController.cs:Start | none |
| POST | api/active-events/runs/{runId:guid}/cancel | ActiveEventsController.cs:Cancel | none |
| GET | api/active-events/runs | ActiveEventsController.cs:History | none |
| GET | api/active-events/runs/{runId:guid} | ActiveEventsController.cs:Get | none |
| GET | api/admin/system-settings | AdminController.cs:GetSystemSettings | none |
| GET | api/admin/feature-visibility | AdminController.cs:GetFeatureVisibility | none |
| GET | api/admin/editable-settings | AdminController.cs:GetEditableSettings | none |
| GET | api/admin/configuration-access | AdminController.cs:GetConfigurationAccess | none |
| GET | api/admin/configuration-changes | AdminController.cs:GetConfigurationChanges | none |
| POST | api/admin/system-settings | AdminController.cs:SaveSystemSettings | none |
| POST | api/admin/reset-local-database | AdminController.cs:ResetLocalDatabase | none |
| GET | api/admin/data-reset/plan | AdminController.cs:DataResetPlan | none |
| POST | api/admin/data-reset | AdminController.cs:DataReset | none |
| POST | api/admin/environment-diagnostics | AdminController.cs:RunEnvironmentDiagnostics | none |
| GET | api/admin/configuration-health | AdminController.cs:GetConfigurationHealth | none |
| GET | api/test-plan-suggestions/ado-export/profile | AdoTestPlanExportController.cs:Profile | none |
| PUT | api/test-plan-suggestions/ado-export/profile | AdoTestPlanExportController.cs:SaveProfile | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/ado-export/preview | AdoTestPlanExportController.cs:Preview | none |
| POST | api/test-plan-suggestions/ado-export/file | AdoTestPlanExportController.cs:File | none |
| POST | api/test-plan-suggestions/ado-export/report | AdoTestPlanExportController.cs:Report | none |
| GET | api/test-plan-suggestions/ado-export/history | AdoTestPlanExportController.cs:History | none |
| GET | api/azure-devops/test-plans/settings | AdoTestPlansController.cs:Settings | none |
| GET | api/azure-devops/test-plans/connections/{id:guid}/projects | AdoTestPlansController.cs:Projects | none |
| POST | api/azure-devops/test-plans/connections/{id:guid}/check | AdoTestPlansController.cs:Check | none |
| GET | api/azure-devops/test-plans/plans | AdoTestPlansController.cs:Plans | none |
| GET | api/azure-devops/test-plans/suites | AdoTestPlansController.cs:Suites | none |
| POST | api/azure-devops/test-plans/plans | AdoTestPlansController.cs:CreatePlan | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/azure-devops/test-plans/suites | AdoTestPlansController.cs:CreateSuite | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/azure-devops/test-plans/pending/{id:guid}/verify | AdoTestPlansController.cs:Verify | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/azure-devops/test-plans/default | AdoTestPlansController.cs:SaveDefault | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/azure-devops/test-plans/transfer/preview | AdoTestPlansController.cs:Preview | none |
| POST | api/azure-devops/test-plans/transfer | AdoTestPlansController.cs:Transfer | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/azure-devops/test-plans/transfer/reconcile | AdoTestPlansController.cs:Reconcile | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/azure-devops/test-plans/results | AdoTestPlansController.cs:Results | none |
| POST | api/azure-devops/test-plans/results/fetch | AdoTestPlansController.cs:FetchResults | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/ai-code-review/sources | AiCodeReviewController.cs:Sources | none |
| POST | api/ai-code-review/runs | AiCodeReviewController.cs:Run | none |
| GET | api/ai-code-review/runs | AiCodeReviewController.cs:History | none |
| GET | api/ai-code-review/runs/{runId:guid} | AiCodeReviewController.cs:Get | none |
| GET | api/target-environments/altinn | AltinnEnvironmentController.cs:Get | none |
| POST | api/target-environments/altinn/detect | AltinnEnvironmentController.cs:Detect | none |
| PUT | api/target-environments/altinn/settings | AltinnEnvironmentController.cs:Save | none |
| POST | api/target-environments/altinn/observations | AltinnEnvironmentController.cs:Observe | none |
| POST | api/target-environments/altinn/browser-observation | AltinnEnvironmentController.cs:ObserveBrowser | none |
| POST | api/target-environments/altinn/session/secret | AltinnEnvironmentController.cs:AddSecret | none |
| DELETE | api/target-environments/altinn/session | AltinnEnvironmentController.cs:ClearSession | none |
| POST | api/target-environments/altinn/session/identities | AltinnEnvironmentController.cs:AddIdentity | none |
| DELETE | api/target-environments/altinn/session/identities | AltinnEnvironmentController.cs:ClearIdentities | none |
| GET | api/altinn-testing/contract | AltinnTestingController.cs:Contract | none |
| POST | api/altinn-testing/contract/public-metadata | AltinnTestingController.cs:DiscoverPublic | none |
| POST | api/altinn-testing/contract/accept-changes | AltinnTestingController.cs:AcceptChanges | none |
| POST | api/altinn-testing/contract/import-openapi | AltinnTestingController.cs:ImportOpenApi | none |
| GET | api/altinn-testing/configuration | AltinnTestingController.cs:Configuration | none |
| POST | api/altinn-testing/contract/source-analysis | AltinnTestingController.cs:UseSourceAnalysis | none |
| GET | api/altinn-testing/sign-in/status | AltinnTestingController.cs:Status | none |
| POST | api/altinn-testing/sign-in/start | AltinnTestingController.cs:Start | none |
| POST | api/altinn-testing/sign-in/sign-out | AltinnTestingController.cs:SignOutTester | none |
| GET | api/altinn-testing/sign-in/callback | AltinnTestingController.cs:Callback | none |
| POST | api/altinn-testing/sign-in/callback | AltinnTestingController.cs:CallbackFormPost | none |
| GET | api/analysis-page-model/spec-drift | AnalysisPageModelController.cs:GetSpecDriftModel | none |
| GET | api/analysis-page-model/impact-analysis | AnalysisPageModelController.cs:GetImpactAnalysisModel | none |
| GET | api/analysis-page-model/requirements-traceability | AnalysisPageModelController.cs:GetRequirementsTraceabilityModel | none |
| GET | api/analysis-page-model/implementation-review | AnalysisPageModelController.cs:GetImplementationReviewModel | none |
| GET | api/analysis-page-model/implementation-traceability | AnalysisPageModelController.cs:GetImplementationTraceabilityModel | none |
| POST | api/api-quality/review | ApiQualityController.cs:Review | none |
| POST | api/api-quality/fuzzing/plan | ApiQualityController.cs:FuzzingPlan | none |
| POST | api/api-quality/fuzzing/runs | ApiQualityController.cs:StartFuzzing | none |
| GET | api/api-quality/fuzzing/runs/{runId} | ApiQualityController.cs:FuzzingRun | none |
| POST | api/api-quality/fuzzing/runs/{runId}/cancel | ApiQualityController.cs:CancelFuzzing | none |
| GET | api/api-quality/security-execution | ApiQualityController.cs:SecurityExecution | none |
| POST | api/api-quality/authorization/runs | ApiQualityController.cs:RunAuthorization | none |
| POST | api/api-quality/analyze | ApiQualityController.cs:Analyze | none |
| GET | api/integrations/application-messaging | ApplicationMessagingController.cs:Get | none |
| GET | api/integrations/application-messaging/source-scope | ApplicationMessagingController.cs:SourceScope | none |
| POST | api/integrations/application-messaging/source-scope | ApplicationMessagingController.cs:UseSourceScope | none |
| PUT | api/integrations/application-messaging/{applicationId}/binding | ApplicationMessagingController.cs:Bind | none |
| DELETE | api/integrations/application-messaging | ApplicationMessagingController.cs:Delete | none |
| POST | api/frontend-quality/auth-session/start | AuthenticatedBrowserSessionController.cs:Start | none |
| GET | api/frontend-quality/auth-session/{sessionId} | AuthenticatedBrowserSessionController.cs:Status | none |
| POST | api/frontend-quality/auth-session/{sessionId}/authenticate | AuthenticatedBrowserSessionController.cs:Authenticate | none |
| POST | api/frontend-quality/auth-session/{sessionId}/cancel | AuthenticatedBrowserSessionController.cs:Cancel | none |
| POST | api/authenticated-review/capabilities | AuthenticatedReviewController.cs:Capabilities | none |
| GET | api/azure-environment/status | AzureEnvironmentController.cs:Status | none |
| POST | api/azure-environment/sign-in/interactive | AzureEnvironmentController.cs:SignInInteractive | none |
| POST | api/azure-environment/sign-in/device-code | AzureEnvironmentController.cs:SignInDeviceCode | none |
| POST | api/azure-environment/refresh | AzureEnvironmentController.cs:Refresh | none |
| POST | api/azure-environment/sign-out | AzureEnvironmentController.cs:SignOut | none |
| GET | api/azure-environment/subscriptions | AzureEnvironmentController.cs:Subscriptions | none |
| POST | api/azure-environment/analyze | AzureEnvironmentController.cs:Analyze | none |
| GET | api/azure-environment/snapshots | AzureEnvironmentController.cs:Snapshots | none |
| GET | api/azure-environment/snapshots/{id:guid} | AzureEnvironmentController.cs:Snapshot | none |
| GET | api/azure-environment/comparison | AzureEnvironmentController.cs:Comparison | none |
| GET | api/azure-environment/lookup | AzureEnvironmentController.cs:Lookup | none |
| GET | api/azure-environment/target-suggestions | AzureEnvironmentController.cs:TargetSuggestions | none |
| GET | api/blob-storage/stores | BlobStorageController.cs:Stores | none |
| GET | api/blob-storage/stores/{storeId}/containers | BlobStorageController.cs:Containers | none |
| GET | api/blob-storage/stores/{storeId}/containers/{container}/blobs | BlobStorageController.cs:List | none |
| GET | api/blob-storage/stores/{storeId}/containers/{container}/properties | BlobStorageController.cs:Properties | none |
| GET | api/blob-storage/stores/{storeId}/containers/{container}/preview | BlobStorageController.cs:Preview | none |
| POST | api/blob-storage/validate | BlobStorageController.cs:Validate | none |
| POST | api/blob-storage/observations | BlobStorageController.cs:Save | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/blob-storage/observations | BlobStorageController.cs:Observations | none |
| DELETE | api/blob-storage/observations/{id:guid} | BlobStorageController.cs:DeleteObservation | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/browser-authentication/unattended | BrowserAuthenticationController.cs:Get | none |
| GET | api/browser-authentication/automated-execution | BrowserAuthenticationController.cs:AutomatedExecution | none |
| PUT | api/browser-authentication/unattended | BrowserAuthenticationController.cs:Save | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/browser-authentication/unattended/enabled | BrowserAuthenticationController.cs:SetEnabled | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/browser-authentication/unattended/validate | BrowserAuthenticationController.cs:Validate | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/browser-authentication/unattended | BrowserAuthenticationController.cs:Clear | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/browser-automation-diagnostic/run | BrowserAutomationDiagnosticController.cs:Run | none |
| POST | api/browser-companion/pairing/start | BrowserCompanionController.cs:StartPairing | none |
| POST | api/browser-companion/status | BrowserCompanionController.cs:Status | none |
| POST | api/browser-companion/unpair | BrowserCompanionController.cs:Unpair | none |
| POST | api/browser-companion/dedicated | BrowserCompanionController.cs:Dedicated | none |
| POST | api/browser-companion/pair | BrowserCompanionController.cs:Pair | none |
| POST | api/browser-companion/validate | BrowserCompanionController.cs:Validate | none |
| POST | api/browser-companion/heartbeat | BrowserCompanionController.cs:Heartbeat | none |
| POST | api/browser-companion/command-result | BrowserCompanionController.cs:CommandResult | none |
| POST | api/browser-companion/evidence | BrowserCompanionController.cs:Evidence | none |
| POST | api/canonical/{scope}/interpret | CanonicalInterpretationController.cs:Interpret | none |
| GET | api/canonical/{scope}/summary | CanonicalInterpretationController.cs:Summary | none |
| GET | api/canonical/{scope}/review | CanonicalInterpretationController.cs:Review | none |
| GET | api/canonical/{scope}/trace/summary | CanonicalInterpretationController.cs:TraceSummary | none |
| GET | api/canonical/{scope}/governance | CanonicalInterpretationController.cs:Governance | none |
| GET | api/canonical/{scope}/release-readiness | CanonicalInterpretationController.cs:ReleaseReadiness | none |
| POST | api/canonical/{scope}/release-readiness/reports | CanonicalInterpretationController.cs:FreezeRelease | none |
| GET | api/canonical/{scope}/release-readiness/reports | CanonicalInterpretationController.cs:ReleaseReports | none |
| GET | api/canonical/{scope}/release-readiness/reports/{id:guid} | CanonicalInterpretationController.cs:ReleaseReport | none |
| POST | api/canonical/{scope}/release-readiness/reports/{id:guid}/sign-off | CanonicalInterpretationController.cs:SignOffRelease | none |
| GET | api/canonical/{scope}/trace/features | CanonicalInterpretationController.cs:TraceFeatures | none |
| GET | api/canonical/{scope}/trace/features/{featureId:guid} | CanonicalInterpretationController.cs:TraceFeature | none |
| GET | api/canonical/{scope}/impact | CanonicalInterpretationController.cs:Impact | none |
| GET | api/canonical/{scope}/snapshots | CanonicalInterpretationController.cs:Snapshots | none |
| GET | api/canonical/{scope}/snapshots/{id:guid}/relations | CanonicalInterpretationController.cs:SnapshotRelations | none |
| GET | api/canonical/{scope}/snapshots/compare | CanonicalInterpretationController.cs:Compare | none |
| GET | api/canonical/{scope}/entities | CanonicalInterpretationController.cs:Entities | none |
| GET | api/canonical/{scope}/entities/{id:guid} | CanonicalInterpretationController.cs:Entity | none |
| GET | api/canonical/{scope}/entities/{id:guid}/chain | CanonicalInterpretationController.cs:Chain | none |
| GET | api/canonical/{scope}/relations | CanonicalInterpretationController.cs:Relations | none |
| GET | api/canonical/{scope}/aliases/resolve | CanonicalInterpretationController.cs:ResolveAlias | none |
| GET | api/canonical/{scope}/operations | CanonicalInterpretationController.cs:Operations | none |
| GET | api/canonical/{scope}/decisions | CanonicalInterpretationController.cs:Decisions | none |
| POST | api/canonical/{scope}/decisions | CanonicalInterpretationController.cs:Decide | none |
| GET | api/canonical/{scope}/identities/requirements | CanonicalInterpretationController.cs:RequirementIdentities | none |
| GET | api/canonical/{scope}/identities/resolve | CanonicalInterpretationController.cs:ResolveIdentities | none |
| GET | api/code-traceability/links | CodeTraceabilityEvidenceController.cs:Links | none |
| GET | api/contract-evidence/providers | ContractEvidenceController.cs:Providers | none |
| GET | api/contract-evidence/{snapshotId:guid} | ContractEvidenceController.cs:Get | none |
| GET | api/contract-evidence/{snapshotId:guid}/constraints | ContractEvidenceController.cs:Constraints | none |
| GET | api/contract-evidence/compare | ContractEvidenceController.cs:Compare | none |
| POST | api/contract-testing/contracts | ContractTestingController.cs:Import | none |
| GET | api/contract-testing/contracts | ContractTestingController.cs:List | none |
| GET | api/contract-testing/projects | ContractTestingController.cs:Projects | none |
| GET | api/contract-testing/source-contracts | ContractTestingController.cs:SourceContracts | none |
| POST | api/contract-testing/contracts/identity-decisions | ContractTestingController.cs:DecideIdentity | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/contract-testing/contracts/{key}/identity | ContractTestingController.cs:IdentityHistory | none |
| PUT | api/contract-testing/contracts/{key}/project | ContractTestingController.cs:AssignProject | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/contracts/from-source | ContractTestingController.cs:ImportFromSource | none |
| POST | api/contract-testing/contracts/from-source/bulk | ContractTestingController.cs:ImportFromSourceBulk | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/bindings/preview | ContractTestingController.cs:PreviewBindings | none |
| POST | api/contract-testing/bindings/apply | ContractTestingController.cs:ApplyBindings | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/projects/{projectKey}/coverage | ContractTestingController.cs:ProjectCoverage | none |
| GET | api/contract-testing/contracts/{key} | ContractTestingController.cs:Get | none |
| GET | api/contract-testing/contracts/{key}/coverage | ContractTestingController.cs:Coverage | none |
| GET | api/contract-testing/contracts/{key}/retention | ContractTestingController.cs:Retention | none |
| PUT | api/contract-testing/contracts/{key}/lifecycle | ContractTestingController.cs:SetLifecycle | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/contract-testing/contracts/{key}/versions/{version:int}/hidden | ContractTestingController.cs:SetHidden | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/contract-testing/contracts/{key}/versions/{version:int} | ContractTestingController.cs:DeleteVersion | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/contract-testing/contracts/{key} | ContractTestingController.cs:DeleteContract | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/contract-testing/contracts/{key}/diff | ContractTestingController.cs:Diff | none |
| POST | api/contract-testing/contracts/{key}/tests | ContractTestingController.cs:Generate | none |
| POST | api/contract-testing/contracts/{key}/test-data | ContractTestingController.cs:GenerateTestData | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/contracts/{key}/tests/negatives | ContractTestingController.cs:ProposeNegatives | none |
| POST | api/contract-testing/contracts/{key}/tests/query | ContractTestingController.cs:Tests | none |
| POST | api/contract-testing/tests/{id:guid}/query | ContractTestingController.cs:Test | none |
| GET | api/contract-testing/tests/{id:guid}/datasets | ContractTestingController.cs:Datasets | none |
| PUT | api/contract-testing/tests/{id:guid}/binding | ContractTestingController.cs:Bind | none |
| POST | api/contract-testing/tests/{id:guid}/regenerate | ContractTestingController.cs:Regenerate | none |
| PUT | api/contract-testing/tests/{id:guid} | ContractTestingController.cs:Edit | none |
| POST | api/contract-testing/tests/{id:guid}/duplicate | ContractTestingController.cs:Duplicate | none |
| GET | api/contract-testing/tests/{id:guid}/revisions | ContractTestingController.cs:Revisions | none |
| GET | api/contract-testing/tests/{id:guid}/revisions/{revision:int} | ContractTestingController.cs:Revision | none |
| GET | api/contract-testing/tests/{id:guid}/compare | ContractTestingController.cs:CompareRevisions | none |
| GET | api/contract-testing/tests/{id:guid}/migration | ContractTestingController.cs:Migration | none |
| POST | api/contract-testing/tests/{id:guid}/migration | ContractTestingController.cs:ApplyMigration | none |
| POST | api/contract-testing/tests/{id:guid}/revert | ContractTestingController.cs:Revert | none |
| DELETE | api/contract-testing/tests/{id:guid} | ContractTestingController.cs:Delete | none |
| GET | api/contract-testing/contracts/{key}/binding | ContractTestingController.cs:Binding | none |
| PUT | api/contract-testing/contracts/{key}/binding | ContractTestingController.cs:SaveBinding | none |
| GET | api/contract-testing/credentials/{environmentId} | ContractTestingController.cs:Credential | none |
| PUT | api/contract-testing/credentials | ContractTestingController.cs:SaveCredential | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/contract-testing/credentials/{environmentId} | ContractTestingController.cs:DeleteCredential | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/contracts/{key}/chain-suggestions/query | ContractTestingController.cs:ChainSuggestions | none |
| POST | api/contract-testing/contracts/{key}/chain-suggestions/{id}/accept | ContractTestingController.cs:AcceptChainSuggestion | none |
| POST | api/contract-testing/contracts/{key}/chain-suggestions/{id}/reject | ContractTestingController.cs:RejectChainSuggestion | none |
| GET | api/contract-testing/chains | ContractTestingController.cs:Chains | none |
| GET | api/contract-testing/chains/{id:guid} | ContractTestingController.cs:Chain | none |
| POST | api/contract-testing/chains | ContractTestingController.cs:CreateChain | none |
| PUT | api/contract-testing/chains/{id:guid} | ContractTestingController.cs:SaveChain | none |
| DELETE | api/contract-testing/chains/{id:guid} | ContractTestingController.cs:DeleteChain | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/contract-testing/chains/{id:guid}/runs | ContractTestingController.cs:RunChain | BirkNextPermissions.ActiveEventExecutePolicy |
| POST | api/contract-testing/runs | ContractTestingController.cs:Run | BirkNextPermissions.ActiveEventExecutePolicy |
| POST | api/critical-e2e/overview | CriticalE2EController.cs:Overview | none |
| GET | api/critical-e2e/flows | CriticalE2EController.cs:Flows | none |
| POST | api/critical-e2e/flows | CriticalE2EController.cs:SaveFlow | none |
| POST | api/critical-e2e/modules/register | CriticalE2EController.cs:RegisterModule | none |
| POST | api/critical-e2e/modules/unregister | CriticalE2EController.cs:UnregisterModule | none |
| GET | api/critical-e2e/flows/{flowId}/versions | CriticalE2EController.cs:FlowVersions | none |
| DELETE | api/critical-e2e/flows/{flowId} | CriticalE2EController.cs:DeleteFlow | none |
| POST | api/critical-e2e/history/preview | CriticalE2EController.cs:HistoryPreview | none |
| POST | api/critical-e2e/history/archive | CriticalE2EController.cs:ArchiveRuns | none |
| POST | api/critical-e2e/history/restore | CriticalE2EController.cs:RestoreRuns | none |
| POST | api/critical-e2e/history/clear | CriticalE2EController.cs:ClearHistory | none |
| POST | api/critical-e2e/run | CriticalE2EController.cs:RunAsync | none |
| POST | api/critical-e2e/pick-element | CriticalE2EController.cs:PickElementAsync | none |
| GET | api/critical-e2e/suggestions/snapshots | CriticalE2EController.cs:SuggestionSnapshotsAsync | none |
| POST | api/critical-e2e/suggestions/generate | CriticalE2EController.cs:GenerateSuggestionsAsync | none |
| POST | api/critical-e2e/suggestions/verify | CriticalE2EController.cs:VerifySuggestionAsync | none |
| POST | api/critical-e2e/suggestions/decide | CriticalE2EController.cs:DecideSuggestionAsync | none |
| POST | api/critical-e2e/suggestions/manual-review | CriticalE2EController.cs:ManualReviewSuggestionAsync | none |
| POST | api/critical-e2e/suggestions/approve | CriticalE2EController.cs:ApproveSuggestionAsync | none |
| GET | api/integration-journeys/declared/{projectKey}/requirements | DeclaredIntegrationJourneysController.cs:RequirementLinks | none |
| POST | api/integration-journeys/declared/{projectKey}/{journeyId}/requirements | DeclaredIntegrationJourneysController.cs:LinkRequirement | none |
| POST | api/integration-journeys/declared/{projectKey}/{journeyId}/requirements/{linkId:guid}/status | DeclaredIntegrationJourneysController.cs:RequirementStatus | none |
| GET | api/integration-journeys/declared | DeclaredIntegrationJourneysController.cs:List | none |
| GET | api/integration-journeys/declared/migration-report | DeclaredIntegrationJourneysController.cs:MigrationReport | none |
| GET | api/integration-journeys/declared/{projectKey}/{journeyId}/revisions | DeclaredIntegrationJourneysController.cs:Revisions | none |
| POST | api/integration-journeys/declared | DeclaredIntegrationJourneysController.cs:Declare | none |
| PUT | api/integration-journeys/declared/{projectKey}/{journeyId} | DeclaredIntegrationJourneysController.cs:Revise | none |
| DELETE | api/integration-journeys/declared/{projectKey}/{journeyId} | DeclaredIntegrationJourneysController.cs:Remove | none |
| GET | api/delivery-reporting/azure-devops/connections | DeliveryAzureDevOpsController.cs:Connections | none |
| POST | api/delivery-reporting/azure-devops/connections | DeliveryAzureDevOpsController.cs:Create | none |
| PUT | api/delivery-reporting/azure-devops/connections/{id:guid} | DeliveryAzureDevOpsController.cs:Update | none |
| DELETE | api/delivery-reporting/azure-devops/connections/{id:guid} | DeliveryAzureDevOpsController.cs:Delete | none |
| POST | api/delivery-reporting/azure-devops/connections/{id:guid}/validate | DeliveryAzureDevOpsController.cs:Validate | none |
| GET | api/delivery-reporting/azure-devops/connections/{id:guid}/projects | DeliveryAzureDevOpsController.cs:Projects | none |
| GET | api/delivery-reporting/azure-devops/connections/{id:guid}/repositories | DeliveryAzureDevOpsController.cs:Repositories | none |
| GET | api/delivery-reporting/azure-devops/connections/{id:guid}/branches | DeliveryAzureDevOpsController.cs:Branches | none |
| POST | api/delivery-reporting/azure-devops/resolve | DeliveryAzureDevOpsController.cs:Resolve | none |
| POST | api/delivery-reporting/azure-devops/preview | DeliveryAzureDevOpsController.cs:Preview | none |
| GET | api/delivery-evidence/providers | DeliveryEvidenceController.cs:Providers | none |
| GET | api/delivery-evidence/{snapshotId:guid} | DeliveryEvidenceController.cs:Get | none |
| GET | api/delivery-reporting/projects | DeliveryReportingController.cs:Projects | none |
| POST | api/delivery-reporting/projects | DeliveryReportingController.cs:CreateProject | none |
| POST | api/delivery-reporting/imports/preview | DeliveryReportingController.cs:Preview | none |
| POST | api/delivery-reporting/imports/{stagingId:guid}/commit | DeliveryReportingController.cs:Commit | none |
| DELETE | api/delivery-reporting/imports/{stagingId:guid} | DeliveryReportingController.cs:Discard | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/snapshots | DeliveryReportingController.cs:Snapshots | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/snapshots/{snapshotId:guid} | DeliveryReportingController.cs:Snapshot | none |
| PATCH | api/delivery-reporting/projects/{projectId:guid}/snapshots/{snapshotId:guid} | DeliveryReportingController.cs:UpdateSnapshot | none |
| DELETE | api/delivery-reporting/projects/{projectId:guid}/snapshots/{snapshotId:guid} | DeliveryReportingController.cs:DeleteSnapshot | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/snapshots/{snapshotId:guid}/baseline-suggestion | DeliveryReportingController.cs:SuggestBaseline | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/comparison-preview | DeliveryReportingController.cs:ComparisonPreview | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/reports | DeliveryReportingController.cs:Reports | none |
| POST | api/delivery-reporting/reports | DeliveryReportingController.cs:Generate | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/scope | DeliveryReportingController.cs:Scope | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/reports/compare | DeliveryReportingController.cs:CompareReports | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/reports/{reportId:guid} | DeliveryReportingController.cs:Report | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/reports/{reportId:guid}/export | DeliveryReportingController.cs:Export | none |
| DELETE | api/delivery-reporting/projects/{projectId:guid}/reports/{reportId:guid} | DeliveryReportingController.cs:DeleteReport | none |
| GET | api/dependency-review/source-scope | DependencyReviewController.cs:SourceScope | none |
| POST | api/dependency-review/source-runs | DependencyReviewController.cs:RunSource | none |
| GET | api/dependency-review/sbom-candidates | DependencyReviewController.cs:SbomCandidates | none |
| GET | api/dependency-review/providers | DependencyReviewController.cs:Providers | none |
| GET | api/dependency-review/internal-packages | DependencyReviewController.cs:InternalPackages | none |
| GET | api/dependency-review/runs | DependencyReviewController.cs:History | none |
| GET | api/dependency-review/runs/{runId:guid} | DependencyReviewController.cs:Get | none |
| POST | api/dependency-review/simulate | DependencyReviewController.cs:Simulate | none |
| GET | api/dependency-review/inventories | DependencyReviewController.cs:Inventories | none |
| POST | api/dependency-review/inventories/import | DependencyReviewController.cs:Import | none |
| POST | api/dependency-review/inventories/deployed | DependencyReviewController.cs:CaptureDeployed | none |
| POST | api/dependency-review/health | DependencyReviewController.cs:RunHealth | none |
| GET | api/dependency-review/health | DependencyReviewController.cs:HealthHistory | none |
| GET | api/dependency-review/health/{runId:guid} | DependencyReviewController.cs:GetHealth | none |
| POST | api/dependency-review/health/{runId:guid}/refresh | DependencyReviewController.cs:RefreshHealth | none |
| GET | api/endpoint-discovery/history | EndpointDiscoveryHistoryController.cs:History | none |
| POST | api/endpoint-discovery/baseline | EndpointDiscoveryHistoryController.cs:Baseline | none |
| GET | api/execution-access | ExecutionAccessController.cs:Get | none |
| POST | api/execution-evidence/runs | ExecutionEvidenceController.cs:Start | BirkNextPermissions.ActiveEventExecutePolicy |
| POST | api/execution-evidence/runs/{runId:guid}/cancel | ExecutionEvidenceController.cs:Cancel | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/execution-evidence/runs | ExecutionEvidenceController.cs:List | none |
| GET | api/execution-evidence/runs/{runId:guid} | ExecutionEvidenceController.cs:Get | none |
| GET | api/execution-evidence/runs/{runId:guid}/manifest | ExecutionEvidenceController.cs:Manifest | none |
| GET | api/execution-evidence/runs/{runId:guid}/applicability | ExecutionEvidenceController.cs:Applicability | none |
| GET | api/execution-evidence/runs/{runId:guid}/calls/{callId:guid} | ExecutionEvidenceController.cs:Call | none |
| GET | api/execution-evidence/compare | ExecutionEvidenceController.cs:Compare | none |
| GET | api/execution-evidence/runs/{runId:guid}/export | ExecutionEvidenceController.cs:Export | none |
| POST | api/execution-evidence/runs/export | ExecutionEvidenceController.cs:ExportMany | none |
| DELETE | api/execution-evidence/runs/{runId:guid} | ExecutionEvidenceController.cs:Delete | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/execution-evidence/runs/delete | ExecutionEvidenceController.cs:DeleteMany | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/execution-evidence/deletion-preview | ExecutionEvidenceController.cs:DeletionPreview | none |
| PUT | api/execution-evidence/runs/{runId:guid}/protection | ExecutionEvidenceController.cs:Protect | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/execution-evidence/runs/{runId:guid}/calls/{callId:guid}/bodies | ExecutionEvidenceController.cs:RemoveBodies | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/execution-evidence/runs/{runId:guid}/integrity | ExecutionEvidenceController.cs:Integrity | none |
| GET | api/execution-evidence/storage | ExecutionEvidenceController.cs:Storage | none |
| GET | api/execution-evidence/retention | ExecutionEvidenceController.cs:Retention | none |
| PUT | api/execution-evidence/retention | ExecutionEvidenceController.cs:SaveRetention | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/execution-evidence/retention/preview | ExecutionEvidenceController.cs:RetentionPreview | none |
| POST | api/execution-evidence/retention/apply | ExecutionEvidenceController.cs:ApplyRetention | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/execution-evidence/audit | ExecutionEvidenceController.cs:Audit | none |
| GET | api/execution-explorer/overview | ExecutionExplorerController.cs:Overview | none |
| GET | api/execution-explorer/runs | ExecutionExplorerController.cs:Runs | none |
| GET | api/execution-explorer/runs/{source}/{id} | ExecutionExplorerController.cs:Run | none |
| GET | api/execution-explorer/compare | ExecutionExplorerController.cs:Compare | none |
| GET | api/execution-explorer/runs/{source}/{id}/report | ExecutionExplorerController.cs:Report | none |
| GET | api/target-environments/external-integrations/providers | ExternalIntegrationsController.cs:Providers | none |
| GET | api/target-environments/external-integrations | ExternalIntegrationsController.cs:List | none |
| GET | api/target-environments/external-integrations/{id}/test-data | ExternalIntegrationsController.cs:TestData | none |
| PUT | api/target-environments/external-integrations/{id}/test-data | ExternalIntegrationsController.cs:BindTestData | none |
| POST | api/target-environments/external-integrations/detect | ExternalIntegrationsController.cs:Detect | none |
| GET | api/target-environments/external-integrations/{id} | ExternalIntegrationsController.cs:Get | none |
| POST | api/target-environments/external-integrations/{id}/detect | ExternalIntegrationsController.cs:DetectOne | none |
| POST | api/target-environments/external-integrations | ExternalIntegrationsController.cs:Create | none |
| PUT | api/target-environments/external-integrations/{id} | ExternalIntegrationsController.cs:Update | none |
| DELETE | api/target-environments/external-integrations/{id} | ExternalIntegrationsController.cs:Delete | none |
| POST | api/target-environments/external-integrations/{id}/session-secret | ExternalIntegrationsController.cs:AddSecret | none |
| GET | api/target-environments/external-integrations/setup/export | ExternalIntegrationsController.cs:Export | none |
| POST | api/target-environments/external-integrations/setup/preflight | ExternalIntegrationsController.cs:Preflight | none |
| POST | api/target-environments/external-integrations/setup/apply | ExternalIntegrationsController.cs:Apply | none |
| GET | api/target-environments/external-integrations | ExternalIntegrationsController.cs:List | none |
| GET | api/target-environments/external-integrations/{id:guid} | ExternalIntegrationsController.cs:Get | none |
| POST | api/target-environments/external-integrations | ExternalIntegrationsController.cs:Save | none |
| POST | api/target-environments/external-integrations/{id:guid}/versions | ExternalIntegrationsController.cs:NewVersion | none |
| POST | api/target-environments/external-integrations/{id:guid}/duplicate | ExternalIntegrationsController.cs:Duplicate | none |
| POST | api/target-environments/external-integrations/{id:guid}/archive | ExternalIntegrationsController.cs:Archive | none |
| GET | api/target-environments/external-integrations/{id:guid}/export | ExternalIntegrationsController.cs:Export | none |
| GET | api/target-environments/external-integrations/{id:guid}/compare | ExternalIntegrationsController.cs:Compare | none |
| POST | api/target-environments/external-integrations/{id:guid}/preflight | ExternalIntegrationsController.cs:Preflight | none |
| POST | api/target-environments/external-integrations/{id:guid}/apply | ExternalIntegrationsController.cs:Apply | none |
| POST | api/frontend-accessibility/review | FrontendAccessibilityController.cs:Review | none |
| POST | api/frontend-accessibility/readiness | FrontendAccessibilityController.cs:Readiness | none |
| POST | api/frontend-quality/authenticated-api-surface | FrontendAuthenticatedApiSurfaceController.cs:Probe | none |
| POST | api/frontend-runtime/review | FrontendBrowserRuntimeController.cs:Review | none |
| POST | api/frontend-runtime/readiness | FrontendBrowserRuntimeController.cs:CheckReadiness | none |
| POST | api/frontend-lighthouse/review | FrontendLighthouseController.cs:Review | none |
| POST | api/frontend-lighthouse/readiness | FrontendLighthouseController.cs:Readiness | none |
| POST | review | FrontendPassiveSecurityController.cs:Review | none |
| POST | readiness | FrontendPassiveSecurityController.cs:Readiness | none |
| GET | api/frontend-quality-engines/status | FrontendQualityEnginesController.cs:GetStatus | none |
| POST | api/frontend-quality-engines/status | FrontendQualityEnginesController.cs:PostStatus | none |
| GET | api/frontend-quality-engines/readiness/{engineId} | FrontendQualityEnginesController.cs:GetReadiness | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/scope/available | FunctionalQualityScopeController.cs:Available | none |
| POST | api/delivery-reporting/projects/{projectId:guid}/scope/resolve | FunctionalQualityScopeController.cs:Resolve | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/scope/items | FunctionalQualityScopeController.cs:Items | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/scope/items/detail | FunctionalQualityScopeController.cs:Detail | none |
| POST | api/delivery-reporting/projects/{projectId:guid}/scope/scopes | FunctionalQualityScopeController.cs:Create | none |
| PUT | api/delivery-reporting/projects/{projectId:guid}/scope/scopes/{scopeId:guid} | FunctionalQualityScopeController.cs:Update | none |
| POST | api/delivery-reporting/projects/{projectId:guid}/scope/scopes/{scopeId:guid}/clone | FunctionalQualityScopeController.cs:Clone | none |
| DELETE | api/delivery-reporting/projects/{projectId:guid}/scope/scopes/{scopeId:guid} | FunctionalQualityScopeController.cs:Delete | none |
| POST | api/delivery-reporting/projects/{projectId:guid}/scope/scopes/{scopeId:guid}/references | FunctionalQualityScopeController.cs:Reference | none |
| DELETE | api/delivery-reporting/projects/{projectId:guid}/scope/scopes/{scopeId:guid}/references | FunctionalQualityScopeController.cs:RemoveReference | none |
| PUT | api/delivery-reporting/projects/{projectId:guid}/scope/assessments | FunctionalQualityScopeController.cs:Assess | none |
| POST | api/delivery-reporting/projects/{projectId:guid}/scope/snapshots | FunctionalQualityScopeController.cs:Freeze | none |
| GET | api/delivery-reporting/projects/{projectId:guid}/scope/snapshots | FunctionalQualityScopeController.cs:Snapshots | none |
| POST | api/system-diagnostics/generated-documentation/run | GeneratedDocumentationController.cs:Run | none |
| POST | api/system-diagnostics/generated-documentation/structure | GeneratedDocumentationController.cs:Structure | none |
| GET | api/git-sources/status | GitSourcesController.cs:Status | none |
| POST | api/git-sources/scan | GitSourcesController.cs:Scan | none |
| POST | api/git-sources/inspect | GitSourcesController.cs:Inspect | none |
| POST | api/git-sources/repositories | GitSourcesController.cs:Add | none |
| GET | api/git-sources/links | GitSourcesController.cs:Links | none |
| PUT | api/git-sources/links/{linkId} | GitSourcesController.cs:Update | none |
| DELETE | api/git-sources/links/{linkId} | GitSourcesController.cs:Unlink | none |
| POST | api/git-sources/links/{linkId}/fetch | GitSourcesController.cs:Fetch | none |
| GET | api/git-sources/links/{linkId}/available-branches | GitSourcesController.cs:AvailableBranches | none |
| GET | api/git-sources/links/{linkId}/branches | GitSourcesController.cs:Branches | none |
| POST | api/git-sources/links/{linkId}/check | GitSourcesController.cs:Check | none |
| GET | api/git-sources/links/{linkId}/matches | GitSourcesController.cs:Matches | none |
| POST | api/git-sources/links/{linkId}/association | GitSourcesController.cs:Decide | none |
| POST | api/git-sources/import | GitSourcesController.cs:Import | none |
| GET | api/graphql-schema-artifacts | GraphQlSchemaArtifactsController.cs:List | none |
| PUT | api/graphql-schema-artifacts | GraphQlSchemaArtifactsController.cs:Save | none |
| DELETE | api/graphql-schema-artifacts | GraphQlSchemaArtifactsController.cs:Delete | none |
| POST | api/headless-auth-diagnostic/prerequisite | HeadlessAuthDiagnosticController.cs:Prerequisite | none |
| POST | api/headless-auth-diagnostic/run | HeadlessAuthDiagnosticController.cs:Run | none |
| GET | api/impact-analysis/runs/requirements | ImpactAnalysisRunsController.cs:Requirements | none |
| POST | api/impact-analysis/runs | ImpactAnalysisRunsController.cs:Run | none |
| GET | api/impact-analysis/runs/history | ImpactAnalysisRunsController.cs:History | none |
| GET | api/impact-analysis/runs/history/{runId:guid} | ImpactAnalysisRunsController.cs:HistoryItem | none |
| GET | api/implementation-gaps | ImplementationGapsController.cs:List | none |
| GET | api/implementation-gaps/{id:guid} | ImplementationGapsController.cs:Get | none |
| POST | api/implementation-gaps/{id:guid}/reevaluate | ImplementationGapsController.cs:Reevaluate | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/implementation-gaps/{id:guid}/reviews | ImplementationGapsController.cs:Review | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/implementation-traceability/status | ImplementationTraceabilityController.cs:GetStatus | none |
| POST | api/implementation-traceability/fetch | ImplementationTraceabilityController.cs:Fetch | none |
| POST | api/implementation-traceability/test-connection | ImplementationTraceabilityController.cs:TestConnection | none |
| GET | api/integration-review/evidence-bindings/{integrationId}/configuration-values | IntegrationEvidenceBindingsController.cs:ConfigurationValues | none |
| GET | api/integration-review/evidence-bindings | IntegrationEvidenceBindingsController.cs:List | none |
| PUT | api/integration-review/evidence-bindings | IntegrationEvidenceBindingsController.cs:Set | none |
| GET | api/integration-journeys/packs | IntegrationJourneysController.cs:Packs | none |
| GET | api/integration-journeys/packs/{packId}/architecture-rules | IntegrationJourneysController.cs:Rules | none |
| GET | api/integration-journeys/packs/{packId}/source-guidance | IntegrationJourneysController.cs:SourceGuidance | none |
| GET | api/integration-journeys/source-setup | IntegrationJourneysController.cs:SourceSetup | none |
| GET | api/integration-journeys/integration-templates | IntegrationJourneysController.cs:IntegrationTemplates | none |
| GET | api/integration-journeys/test-packs | IntegrationJourneysController.cs:TestPacks | none |
| GET | api/integration-journeys/catalog | IntegrationJourneysController.cs:Catalog | none |
| POST | api/integration-journeys/runs | IntegrationJourneysController.cs:Start | none |
| POST | api/integration-journeys/runs/validate | IntegrationJourneysController.cs:Validate | none |
| GET | api/integration-journeys/runs | IntegrationJourneysController.cs:History | none |
| GET | api/integration-journeys/runs/{runId:guid} | IntegrationJourneysController.cs:Get | none |
| GET | api/integration-journeys/runs/{runId:guid}/payload-flow | IntegrationJourneysController.cs:PayloadFlow | none |
| GET | api/integration-message-flow/{environmentId}/source-contracts/{snapshotId:guid} | IntegrationMessageFlowController.cs:SourceContracts | none |
| GET | api/integration-message-flow/{environmentId} | IntegrationMessageFlowController.cs:Get | none |
| PUT | api/integration-message-flow/{environmentId} | IntegrationMessageFlowController.cs:Save | none |
| POST | api/integrations/{id}/mapping-evidence | IntegrationsController.cs:CheckMapping | none |
| POST | api/integrations/platforms/{id}/servicebus-evidence | IntegrationsController.cs:CheckServiceBus | none |
| GET | api/integrations | IntegrationsController.cs:Get | none |
| GET | api/integrations/terraform-comparison | IntegrationsController.cs:TerraformComparison | none |
| POST | api/integrations/templates/{templateId}/apply | IntegrationsController.cs:ApplyTemplate | none |
| POST | api/integrations | IntegrationsController.cs:Create | none |
| PUT | api/integrations/{id} | IntegrationsController.cs:Update | none |
| POST | api/integrations/{id}/enabled | IntegrationsController.cs:SetEnabled | none |
| DELETE | api/integrations/{id} | IntegrationsController.cs:Delete | none |
| PUT | api/integrations/platforms/{id} | IntegrationsController.cs:UpdatePlatform | none |
| GET | api/integrations/contracts | IntegrationsController.cs:Contracts | none |
| PUT | api/integrations/contracts | IntegrationsController.cs:SaveContract | none |
| DELETE | api/integrations/contracts | IntegrationsController.cs:DeleteContract | none |
| GET | api/integrations/{id}/contract-discovery | IntegrationsController.cs:ContractDiscovery | none |
| PUT | api/integrations/contracts/source-selection | IntegrationsController.cs:SelectSourceContract | none |
| POST | api/integrations/import-legacy | IntegrationsController.cs:ImportLegacy | none |
| GET | api/integrations/contract-relationship | IntegrationsController.cs:ContractRelationship | none |
| GET | api/integrations/readiness | IntegrationsController.cs:Readiness | none |
| POST | api/integrations/run | IntegrationsController.cs:Run | none |
| GET | api/integrations/runs | IntegrationsController.cs:History | none |
| GET | api/integrations/runs/{runId:guid} | IntegrationsController.cs:Run | none |
| GET | api/integration-suites/scopes | IntegrationSuitesController.cs:Scopes | none |
| GET | api/integration-suites | IntegrationSuitesController.cs:List | none |
| GET | api/integration-suites/{id:guid} | IntegrationSuitesController.cs:Get | none |
| POST | api/integration-suites | IntegrationSuitesController.cs:Generate | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/integration-suites/{id:guid}/reviews | IntegrationSuitesController.cs:Review | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/integration-suites/{id:guid}/specification-links | IntegrationSuitesController.cs:SpecificationLink | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/integration-suites/composite/discovery | IntegrationSuitesController.cs:Discover | none |
| GET | api/integration-suites/composite/contexts | IntegrationSuitesController.cs:Contexts | none |
| GET | api/integration-suites/composite/contexts/revisions | IntegrationSuitesController.cs:ContextRevisions | none |
| GET | api/integration-suites/composite/contexts/{id:guid} | IntegrationSuitesController.cs:Context | none |
| POST | api/integration-suites/composite/contexts | IntegrationSuitesController.cs:SaveContext | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/integration-suites/{id:guid}/plan/approve | IntegrationSuitesController.cs:ApprovePlan | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/integration-suites/{id:guid}/composite-runs/preflight | IntegrationSuitesController.cs:Preflight | none |
| POST | api/integration-suites/{id:guid}/composite-runs | IntegrationSuitesController.cs:RunComposite | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/integration-suites/{id:guid}/composite-runs/{runId:guid}/retest-plan | IntegrationSuitesController.cs:RetestPlan | none |
| POST | api/integration-suites/{id:guid}/composite-runs/retest | IntegrationSuitesController.cs:RetestStage | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/integration-suites/{id:guid}/composite-runs/{runId:guid} | IntegrationSuitesController.cs:CompositeRun | none |
| POST | api/integration-suites/{id:guid}/runs | IntegrationSuitesController.cs:Execute | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/integration-topology/{environmentId} | IntegrationTopologyController.cs:Get | none |
| POST | api/integration-topology/{environmentId} | IntegrationTopologyController.cs:WithObservations | none |
| POST | api/integration-topology/{environmentId}/discover | IntegrationTopologyController.cs:Discover | none |
| POST | api/integration-topology/{environmentId}/profiles/{profileId}/coverage | IntegrationTopologyController.cs:Coverage | none |
| POST | api/integration-topology/{environmentId}/profiles/{profileId}/paths | IntegrationTopologyController.cs:Paths | none |
| GET | api/integration-review/source/schema-index | IqrSourceEvidenceController.cs:SchemaIndex | none |
| GET | api/integration-review/source/{snapshotId:guid}/schemas | IqrSourceEvidenceController.cs:Schemas | none |
| GET | api/integration-review/source/environment-values | IqrSourceEvidenceController.cs:EnvironmentValues | none |
| GET | api/integration-review/source/scope | IqrSourceEvidenceController.cs:Scope | none |
| GET | api/integration-review/source/infrastructure-suggestions | IqrSourceEvidenceController.cs:InfrastructureSuggestions | none |
| GET | api/integration-review/source | IqrSourceEvidenceController.cs:List | none |
| GET | api/integration-review/source/dependency-sources | IqrSourceEvidenceController.cs:DependencySources | none |
| GET | api/integration-review/source/{snapshotId:guid} | IqrSourceEvidenceController.cs:Get | none |
| GET | api/library-page-model/qa-artifact-library | LibraryPageModelController.cs:GetQAArtifactLibraryModel | none |
| GET | api/library-page-model/sample-projects | LibraryPageModelController.cs:GetSampleProjectsModel | none |
| GET | api/local-https-proxy/runtime | LocalHttpsProxyController.cs:Runtime | none |
| POST | api/local-https-proxy/compatibility | LocalHttpsProxyController.cs:Compatibility | none |
| POST | api/local-https-proxy/start | LocalHttpsProxyController.cs:Start | none |
| POST | api/local-https-proxy/status | LocalHttpsProxyController.cs:Status | none |
| POST | api/local-https-proxy/observed-cookie-attributes | LocalHttpsProxyController.cs:ObservedCookieAttributes | none |
| POST | api/local-https-proxy/stop | LocalHttpsProxyController.cs:Stop | none |
| POST | api/local-https-proxy/attended-session | LocalHttpsProxyController.cs:AttendedSession | none |
| POST | api/local-https-proxy/attended-session/end | LocalHttpsProxyController.cs:EndAttendedSession | none |
| POST | api/local-https-proxy/certificate/install | LocalHttpsProxyController.cs:InstallCertificate | none |
| POST | api/local-https-proxy/certificate/remove | LocalHttpsProxyController.cs:RemoveCertificate | none |
| POST | api/local-https-proxy/launch-edge | LocalHttpsProxyController.cs:LaunchEdge | none |
| POST | api/local-https-proxy/edge/restart | LocalHttpsProxyController.cs:RestartEdge | none |
| POST | api/local-https-proxy/execute/rest | LocalHttpsProxyController.cs:ExecuteRest | none |
| POST | api/local-https-proxy/execute/graphql | LocalHttpsProxyController.cs:ExecuteGraphQl | none |
| DELETE | api/local-session | LocalSessionController.cs:Disconnect | none |
| POST | api/local-session/rotate | LocalSessionController.cs:Rotate | none |
| POST | api/managed-edge/preflight | ManagedEdgeCdpController.cs:Preflight | none |
| POST | api/managed-edge/launch | ManagedEdgeCdpController.cs:Launch | none |
| POST | api/managed-edge/connect | ManagedEdgeCdpController.cs:Connect | none |
| POST | api/managed-edge/status | ManagedEdgeCdpController.cs:Status | none |
| POST | api/managed-edge/verify | ManagedEdgeCdpController.cs:Verify | none |
| POST | api/managed-edge/fetch | ManagedEdgeCdpController.cs:Fetch | none |
| POST | api/managed-edge/disconnect | ManagedEdgeCdpController.cs:Disconnect | none |
| GET | api/manual-reviews | ManualReviewController.cs:List | none |
| POST | api/manual-reviews | ManualReviewController.cs:Record | none |
| GET | api/manual-test-runs/availability | ManualTestRunsController.cs:Availability | none |
| GET | api/manual-test-runs | ManualTestRunsController.cs:List | none |
| GET | api/manual-test-runs/page | ManualTestRunsController.cs:Page | none |
| GET | api/manual-test-runs/{id:guid} | ManualTestRunsController.cs:Get | none |
| POST | api/manual-test-runs | ManualTestRunsController.cs:Start | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/manual-test-runs/{id:guid} | ManualTestRunsController.cs:Save | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/manual-test-runs/{id:guid}/complete | ManualTestRunsController.cs:Complete | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/manual-test-runs/{id:guid}/abandon | ManualTestRunsController.cs:Abandon | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/manual-test-runs/defects | ManualTestRunsController.cs:Defects | none |
| POST | api/manual-test-runs/{id:guid}/defects/{defectId} | ManualTestRunsController.cs:UpdateDefect | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/manual-test-runs/{id:guid}/attachments | ManualTestRunsController.cs:Attach | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/manual-test-runs/{id:guid}/attachments/{attachmentId:guid} | ManualTestRunsController.cs:Attachment | none |
| GET | api/manual-test-runs/{id:guid}/attachments | ManualTestRunsController.cs:Attachments | none |
| POST | api/manual-test-runs/{id:guid}/attachments/{attachmentId:guid}/withdraw | ManualTestRunsController.cs:Withdraw | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-diagnostics/markdown/content-integrity/run | MarkdownDiagnosticsController.cs:ContentIntegrity | none |
| POST | api/system-diagnostics/markdown/explorer-coverage/run | MarkdownDiagnosticsController.cs:ExplorerCoverage | none |
| POST | api/message-observations | MessageObservationsController.cs:Import | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/message-observations | MessageObservationsController.cs:List | none |
| GET | api/message-observations/{id:guid} | MessageObservationsController.cs:Get | none |
| DELETE | api/message-observations/{id:guid} | MessageObservationsController.cs:Delete | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/parquet/sessions | ParquetController.cs:Open | none |
| GET | api/parquet/sessions | ParquetController.cs:Sessions | none |
| GET | api/parquet/sessions/{id:guid} | ParquetController.cs:Overview | none |
| POST | api/parquet/sessions/{id:guid}/rows | ParquetController.cs:Rows | none |
| POST | api/parquet/sessions/{id:guid}/profile | ParquetController.cs:Profile | none |
| POST | api/parquet/sessions/{id:guid}/validate | ParquetController.cs:Validate | none |
| POST | api/parquet/sessions/{id:guid}/manifest | ParquetController.cs:Manifest | none |
| GET | api/parquet/sessions/{id:guid}/contracts | ParquetController.cs:Discover | none |
| GET | api/parquet/contracts | ParquetController.cs:Contracts | none |
| POST | api/parquet/sessions/{id:guid}/association | ParquetController.cs:Associate | none |
| POST | api/parquet/sessions/{id:guid}/rules | ParquetController.cs:Generate | none |
| POST | api/parquet/sessions/{id:guid}/review | ParquetController.cs:Review | none |
| POST | api/parquet/compare | ParquetController.cs:Compare | none |
| POST | api/parquet/baseline | ParquetController.cs:Baseline | none |
| GET | api/parquet/rule-sets | ParquetController.cs:RuleSets | none |
| POST | api/parquet/sessions/{id:guid}/rule-sets | ParquetController.cs:Approve | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/parquet/sessions/{id:guid} | ParquetController.cs:Close | none |
| POST | api/parquet/evidence | ParquetController.cs:Save | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/parquet/evidence | ParquetController.cs:List | none |
| GET | api/parquet/evidence/{id:guid} | ParquetController.cs:Get | none |
| GET | api/parquet/evidence/{id:guid}/export | ParquetController.cs:Export | none |
| GET | api/parquet/evidence/{id:guid}/report | ParquetController.cs:Report | none |
| POST | api/parquet/evidence/{id:guid}/approve-baseline | ParquetController.cs:ApproveBaseline | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| DELETE | api/parquet/evidence/{id:guid} | ParquetController.cs:DeleteEvidence | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/performance-tests/local-execution | PerformanceTestsController.cs:LocalExecution | none |
| PUT | api/performance-tests/local-execution/ca-bundle | PerformanceTestsController.cs:ConfigureCaBundle | none |
| PUT | api/performance-tests/local-execution/tls-repair-mode | PerformanceTestsController.cs:SetTlsRepairMode | none |
| DELETE | api/performance-tests/local-execution/ca-bundle | PerformanceTestsController.cs:RemoveCaBundle | none |
| GET | api/performance-tests/resource-targets | PerformanceTestsController.cs:ResourceTargets | none |
| GET | api/performance-tests/resource-providers | PerformanceTestsController.cs:ResourceProviders | none |
| GET | api/performance-tests | PerformanceTestsController.cs:Overview | none |
| GET | api/performance-tests/providers | PerformanceTestsController.cs:Providers | none |
| POST | api/performance-tests/definitions | PerformanceTestsController.cs:Create | none |
| PUT | api/performance-tests/definitions/{id} | PerformanceTestsController.cs:Update | none |
| DELETE | api/performance-tests/definitions/{id} | PerformanceTestsController.cs:Delete | none |
| POST | api/performance-tests/data-profiles | PerformanceTestsController.cs:SaveDataProfile | none |
| GET | api/performance-tests/definitions/{id}/readiness | PerformanceTestsController.cs:Readiness | none |
| POST | api/performance-tests/definitions/{id}/network-check | PerformanceTestsController.cs:NetworkCheck | none |
| POST | api/performance-tests/definitions/{id}/readiness-check | PerformanceTestsController.cs:ReadinessCheck | none |
| POST | api/performance-tests/trial | PerformanceTestsController.cs:Trial | none |
| GET | api/performance-tests/scenario-sources | PerformanceTestsController.cs:ScenarioSources | none |
| GET | api/performance-tests/scenario-contracts | PerformanceTestsController.cs:ScenarioContracts | none |
| POST | api/performance-tests/setup-profile/preview | PerformanceTestsController.cs:PreviewSetup | none |
| POST | api/performance-tests/setup-profile/apply | PerformanceTestsController.cs:ApplySetup | none |
| GET | api/performance-tests/setup-profile/export | PerformanceTestsController.cs:ExportSetup | none |
| GET | api/performance-tests/setup-profile/example | PerformanceTestsController.cs:SetupExample | none |
| POST | api/performance-tests/providers/{providerId}/prepare | PerformanceTestsController.cs:Prepare | none |
| POST | api/performance-tests/maintenance/cleanup | PerformanceTestsController.cs:Cleanup | none |
| POST | api/performance-tests/runs | PerformanceTestsController.cs:Run | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/performance-tests/runs | PerformanceTestsController.cs:Runs | none |
| GET | api/performance-tests/runs/{runId:guid} | PerformanceTestsController.cs:GetRun | none |
| POST | api/performance-tests/runs/{runId:guid}/cancel | PerformanceTestsController.cs:Cancel | BirkNextPermissions.ActiveEventExecutePolicy |
| GET | api/performance-tests/baselines | PerformanceTestsController.cs:Baselines | none |
| POST | api/performance-tests/baselines | PerformanceTestsController.cs:Promote | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/performance-tests/baselines/{baselineId}/archive | PerformanceTestsController.cs:Archive | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/performance-tests/baselines/proposals | PerformanceTestsController.cs:Propose | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/performance-tests/baselines/{baselineId}/approve | PerformanceTestsController.cs:Approve | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/performance-tests/baselines/{baselineId}/reject | PerformanceTestsController.cs:Reject | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/performance-tests/runs/{runId:guid}/delete | PerformanceTestsController.cs:DeleteRun | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/performance-tests/regression | PerformanceTestsController.cs:Regression | none |
| GET | api/performance-tests/regression/export | PerformanceTestsController.cs:RegressionExport | none |
| GET | api/performance-tests/trends | PerformanceTestsController.cs:Trends | none |
| GET | api/performance-tests/compare | PerformanceTestsController.cs:Compare | none |
| GET | api/pipeline-review/sources | PipelineReviewController.cs:Sources | none |
| GET | api/pipeline-review | PipelineReviewController.cs:Review | none |
| GET | api/pipeline-review/probe | PipelineReviewController.cs:Probe | none |
| GET | api/pipeline-review/compare | PipelineReviewController.cs:Compare | none |
| GET | api/project-activity | ProjectActivityController.cs:List | none |
| POST | api/project-activity | ProjectActivityController.cs:Record | none |
| POST | api/system-diagnostics/project-compatibility/run | ProjectCompatibilityController.cs:Run | none |
| GET | api/project-documents/{kind} | ProjectDocumentsController.cs:GetDocument | none |
| PUT | api/project-documents/{kind} | ProjectDocumentsController.cs:UpsertDocument | none |
| GET | api/project-documents | ProjectDocumentsController.cs:GetSummary | none |
| POST | api/project-import/preview | ProjectImportController.cs:Preview | none |
| GET | api/project-import/{stagingId:guid}/application | ProjectImportController.cs:ApplicationOptions | none |
| POST | api/project-import/{stagingId:guid}/application | ProjectImportController.cs:ChooseApplication | none |
| POST | api/project-import/{stagingId:guid}/commit | ProjectImportController.cs:Commit | none |
| DELETE | api/project-import/{stagingId:guid} | ProjectImportController.cs:Discard | none |
| GET | api/quality-review-page-model/quality-review | QualityReviewPageModelController.cs:GetQualityReviewModel | none |
| GET | api/quality-review-page-model/api-quality-review | QualityReviewPageModelController.cs:GetApiQualityReviewModel | none |
| GET | api/quality-review-page-model/frontend-quality-review | QualityReviewPageModelController.cs:GetFrontendQualityReviewModel | none |
| GET | api/quality-review-page-model/integration-quality-review | QualityReviewPageModelController.cs:GetIntegrationQualityReviewModel | none |
| POST | api/recommended-workflow/build-steps | RecommendedWorkflowController.cs:BuildSteps | none |
| POST | api/recommended-workflow/mark-in-progress | RecommendedWorkflowController.cs:MarkInProgress | none |
| POST | api/recommended-workflow/mark-reviewed | RecommendedWorkflowController.cs:MarkReviewed | none |
| POST | api/recommended-workflow/approve | RecommendedWorkflowController.cs:Approve | none |
| POST | api/recommended-workflow/reject | RecommendedWorkflowController.cs:Reject | none |
| POST | api/recommended-workflow/invalidate-approvals | RecommendedWorkflowController.cs:InvalidateApprovals | none |
| POST | api/recommended-workflow/readiness | RecommendedWorkflowController.cs:GetReadiness | none |
| GET | api/review-page-model/dashboard | ReviewPageModelController.cs:GetDashboardModel | none |
| GET | api/review-page-model/constitution-explorer | ReviewPageModelController.cs:GetConstitutionExplorerModel | none |
| GET | api/review-page-model/data-model-explorer | ReviewPageModelController.cs:GetDataModelExplorerModel | none |
| GET | api/review-page-model/plan-explorer | ReviewPageModelController.cs:GetPlanExplorerModel | none |
| GET | api/review-page-model/task-explorer | ReviewPageModelController.cs:GetTaskExplorerModel | none |
| GET | api/runtime-authentication/catalog | RuntimeAuthenticationController.cs:Catalog | none |
| POST | api/runtime-authentication/resolve | RuntimeAuthenticationController.cs:Resolve | none |
| GET | api/sample-projects | SampleProjectsController.cs:GetProjects | none |
| GET | api/sample-projects/meta | SampleProjectsController.cs:GetMeta | none |
| GET | api/sample-projects/{slug}/file | SampleProjectsController.cs:GetFile | none |
| GET | api/sample-projects/{slug}/documents | SampleProjectsController.cs:GetDocuments | none |
| GET | api/integrations/scim | ScimProvisioningController.cs:Overview | none |
| GET | api/integrations/scim/source-scope | ScimProvisioningController.cs:SourceScope | none |
| POST | api/integrations/scim/source-scope | ScimProvisioningController.cs:UseSourceScope | none |
| POST | api/integrations/scim/{platformId}/checks | ScimProvisioningController.cs:RunSafeChecks | none |
| GET | api/integrations/scim/checks/{runId:guid} | ScimProvisioningController.cs:GetRun | none |
| GET | api/scopes | ScopeRegistryController.cs:List | none |
| GET | api/scopes/{scopeId:guid} | ScopeRegistryController.cs:Get | none |
| POST | api/scopes | ScopeRegistryController.cs:Create | none |
| PUT | api/scopes/{scopeId:guid} | ScopeRegistryController.cs:Save | none |
| POST | api/scopes/{scopeId:guid}/approve | ScopeRegistryController.cs:Approve | SystemConfigurationPermissions.Policy |
| POST | api/scopes/{scopeId:guid}/archive | ScopeRegistryController.cs:Archive | SystemConfigurationPermissions.Policy |
| GET | api/scopes/{scopeId:guid}/compare | ScopeRegistryController.cs:Compare | none |
| GET | api/scopes/membership | ScopeRegistryController.cs:Membership | none |
| POST | api/scopes/verifying-scenarios | ScopeRegistryController.cs:VerifyingScenarios | none |
| POST | api/scopes/{scopeId:guid}/submit | ScopeRegistryController.cs:Submit | none |
| GET | api/scopes/{scopeId:guid}/check | ScopeRegistryController.cs:Check | none |
| POST | api/scopes/check-members | ScopeRegistryController.cs:CheckMembers | none |
| GET | api/scopes/catalog | ScopeRegistryController.cs:Catalog | none |
| GET | api/scopes/systems | ScopeRegistryController.cs:Systems | none |
| GET | api/scopes/selection | ScopeRegistryController.cs:Selection | none |
| GET | api/security-classification | SecurityClassificationController.cs:Overview | none |
| GET | api/security-classification/source-scope | SecurityClassificationController.cs:SourceScope | none |
| PUT | api/security-classification/context | SecurityClassificationController.cs:SaveContext | none |
| DELETE | api/security-classification/context | SecurityClassificationController.cs:ClearContext | none |
| POST | api/security-classification/runs | SecurityClassificationController.cs:Run | none |
| POST | api/security-classification/static-reviews | SecurityClassificationController.cs:RunStatic | none |
| POST | api/security-classification/runtime-verifications | SecurityClassificationController.cs:RunRuntime | none |
| GET | api/security-classification/consumers | SecurityClassificationController.cs:Consumers | none |
| GET | api/security-classification/runs/{runId:guid} | SecurityClassificationController.cs:GetRun | none |
| POST | api/setup-profiles/{packId}/preview | SetupProfilesController.cs:Preview | none |
| POST | api/setup-profiles/{packId}/apply | SetupProfilesController.cs:Apply | none |
| GET | api/setup-profiles/{packId}/export | SetupProfilesController.cs:Export | none |
| GET | api/setup-profiles/{packId}/example | SetupProfilesController.cs:Example | none |
| GET | api/snapshots/source-analysis/{id:guid}/references | SnapshotsController.cs:SourceAnalysisReferences | none |
| GET | api/snapshots | SnapshotsController.cs:List | none |
| POST | api/snapshots/sync | SnapshotsController.cs:Sync | none |
| GET | api/snapshots/{type}/{id} | SnapshotsController.cs:Get | none |
| GET | api/snapshots/{type}/{id}/lineage | SnapshotsController.cs:Lineage | none |
| GET | api/snapshots/pointers | SnapshotsController.cs:Pointers | none |
| PUT | api/snapshots/pointers | SnapshotsController.cs:SetPointer | none |
| PUT | api/snapshots/{type}/{id}/retention | SnapshotsController.cs:SetRetention | none |
| GET | api/snapshots/{type}/{id}/deletion-check | SnapshotsController.cs:DeletionCheck | none |
| GET | api/impact-analysis/source-change/snapshots | SourceChangeImpactController.cs:Snapshots | none |
| POST | api/impact-analysis/source-change | SourceChangeImpactController.cs:Analyze | none |
| GET | api/impact-analysis/source-change/history | SourceChangeImpactController.cs:History | none |
| GET | api/impact-analysis/source-change/history/{runId:guid} | SourceChangeImpactController.cs:HistoryItem | none |
| GET | api/source-context | SourceContextController.cs:Get | none |
| GET | api/source-context/history | SourceContextController.cs:History | none |
| POST | api/source-context/baseline | SourceContextController.cs:SelectBaseline | SystemConfigurationPermissions.Policy |
| POST | api/source-context/declared-branch | SourceContextController.cs:DeclareBranch | SystemConfigurationPermissions.Policy |
| GET | api/integrations/source-integrations | SourceIntegrationsController.cs:Get | none |
| POST | api/integrations/source-integrations/discover | SourceIntegrationsController.cs:Discover | none |
| GET | api/source-repository-associations | SourceRepositoryAssociationsController.cs:List | none |
| POST | api/source-repository-associations | SourceRepositoryAssociationsController.cs:Declare | none |
| POST | api/source-repository-associations/{id:guid}/revoke | SourceRepositoryAssociationsController.cs:Revoke | none |
| GET | api/specification-catalog/{projectKey} | SpecificationCatalogController.cs:Current | none |
| POST | api/specification-catalog/{projectKey}/check | SpecificationCatalogController.cs:Check | none |
| GET | api/test-plan-suggestions/story-grouping | StoryGroupingController.cs:Get | none |
| POST | api/test-plan-suggestions/story-grouping | StoryGroupingController.cs:Apply | BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/system/build-info | SystemInfoController.cs:BuildInfo | none |
| GET | api/system/runtime | SystemInfoController.cs:Runtime | none |
| GET | api/system-understanding/guides | SystemUnderstandingController.cs:List | none |
| GET | api/system-understanding/guides/{guideKey} | SystemUnderstandingController.cs:Get | none |
| GET | api/system-understanding/guides/{guideKey}/revisions/{revision:int} | SystemUnderstandingController.cs:Revision | none |
| GET | api/system-understanding/guides/{guideKey}/candidates | SystemUnderstandingController.cs:Candidates | none |
| POST | api/system-understanding/guides/{guideKey}/candidates/{candidateId:guid}/draft | SystemUnderstandingController.cs:CandidateDraft | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/candidates/{candidateId:guid}/decisions | SystemUnderstandingController.cs:Decide | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/candidates/{candidateId:guid}/apply | SystemUnderstandingController.cs:Apply | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/system-understanding/guides/{guideKey}/generation | SystemUnderstandingController.cs:Generation | none |
| POST | api/system-understanding/guides/{guideKey}/generation/{jobId:guid}/retry | SystemUnderstandingController.cs:Retry | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/system-understanding/guides/{guideKey}/draft | SystemUnderstandingController.cs:SaveDraft | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/revisions/{revision:int}/submit | SystemUnderstandingController.cs:Submit | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/revisions/{revision:int}/return-to-draft | SystemUnderstandingController.cs:ReturnToDraft | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/revisions/{revision:int}/approve | SystemUnderstandingController.cs:Approve | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/system-understanding/guides/{guideKey}/revisions/{revision:int}/publish | SystemUnderstandingController.cs:Publish | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/frontend-target/detect | TargetEnvironmentDetectionController.cs:DetectConfiguration | none |
| POST | api/frontend-target/reachability | TargetEnvironmentDetectionController.cs:ProbeReachability | none |
| POST | api/frontend-target/continue-in-browser | TargetEnvironmentDetectionController.cs:ContinueDetectionInBrowser | none |
| POST | api/task-alignment/runs | TaskAlignmentRunsController.cs:Save | none |
| GET | api/task-alignment/runs/latest | TaskAlignmentRunsController.cs:Latest | none |
| GET | api/task-alignment/runs | TaskAlignmentRunsController.cs:List | none |
| GET | api/task-alignment/runs/{id:guid} | TaskAlignmentRunsController.cs:Get | none |
| POST | api/technical-history/api-review-runs | TechnicalHistoryController.cs:StoreRun | none |
| GET | api/technical-history/api-review-runs | TechnicalHistoryController.cs:Runs | none |
| GET | api/technical-history/api-review-runs/{id:guid} | TechnicalHistoryController.cs:Report | none |
| POST | api/technical-history/review-runs | TechnicalHistoryController.cs:StoreReviewRun | none |
| GET | api/technical-history/review-runs | TechnicalHistoryController.cs:ReviewRuns | none |
| GET | api/technical-history/review-runs/{id:guid}/report | TechnicalHistoryController.cs:ReviewRunReport | none |
| POST | api/technical-history/findings/stage | TechnicalHistoryController.cs:TrackStage | none |
| GET | api/technical-history/findings | TechnicalHistoryController.cs:Findings | none |
| POST | api/technical-history/findings | TechnicalHistoryController.cs:Track | none |
| POST | api/technical-history/findings/{id:guid} | TechnicalHistoryController.cs:Update | none |
| GET | api/technology-coverage | TechnologyCoverageController.cs:Get | none |
| GET | api/test-coverage-review/sources | TestCoverageReviewController.cs:Sources | none |
| POST | api/test-coverage-review/runs | TestCoverageReviewController.cs:Run | none |
| GET | api/test-coverage-review/runs | TestCoverageReviewController.cs:History | none |
| GET | api/test-coverage-review/runs/{runId:guid} | TestCoverageReviewController.cs:Get | none |
| GET | api/test-coverage-review/decisions | TestCoverageReviewController.cs:Decisions | none |
| POST | api/test-coverage-review/decisions | TestCoverageReviewController.cs:Decide | none |
| GET | api/test-data | TestDataController.cs:Catalog | none |
| GET | api/test-data/assets/{id} | TestDataController.cs:Asset | none |
| GET | api/test-data/built-in-defaults | TestDataController.cs:BuiltInDefaults | none |
| POST | api/test-data/built-in-defaults/restore | TestDataController.cs:RestoreBuiltInDefaults | none |
| GET | api/test-data/candidates | TestDataController.cs:Candidates | none |
| POST | api/test-data/observed-value/match | TestDataController.cs:MatchObservedValue | none |
| POST | api/test-data/registrations | TestDataController.cs:Register | none |
| POST | api/test-data/xml-fixtures/bind | TestDataController.cs:BindXmlFixture | none |
| POST | api/test-data/canonical-fixtures | TestDataController.cs:SaveCanonicalFixture | none |
| POST | api/test-data/assets/{id}/approve | TestDataController.cs:Approve | none |
| POST | api/test-data/assets/{id}/rename | TestDataController.cs:Rename | none |
| GET | api/test-data/identities/{reference} | TestDataController.cs:Identity | none |
| POST | api/test-data/assets/{id}/validate | TestDataController.cs:Validate | none |
| POST | api/test-data/consumer-references/migrate | TestDataController.cs:MigrateReferences | none |
| GET | api/test-data/schema-associations | TestDataController.cs:Associations | none |
| POST | api/test-data/schema-associations | TestDataController.cs:Associate | none |
| POST | api/test-data/assets/{id}/deprecate | TestDataController.cs:Deprecate | none |
| GET | api/test-data/review-flags | TestDataController.cs:ReviewFlags | none |
| GET | api/test-data/consumers | TestDataController.cs:Consumers | none |
| POST | api/test-data/requirements | TestDataController.cs:AddRequirement | none |
| DELETE | api/test-data/requirements/{id} | TestDataController.cs:RemoveRequirement | none |
| POST | api/test-data/matches | TestDataController.cs:Matches | none |
| POST | api/test-data/usages | TestDataController.cs:Bind | none |
| DELETE | api/test-data/usages/{id} | TestDataController.cs:Unbind | none |
| GET | api/test-data/references | TestDataController.cs:References | none |
| GET | api/test-data/assets/{id}/edit | TestDataController.cs:EditView | none |
| PUT | api/test-data/assets/{id} | TestDataController.cs:Edit | none |
| POST | api/test-data/assets/{id}/restore-default | TestDataController.cs:RestoreDefault | none |
| DELETE | api/test-data/assets/{id} | TestDataController.cs:Delete | none |
| GET | api/test-data/assets/{id}/replacements | TestDataController.cs:Replacements | none |
| POST | api/test-data/assets/{id}/replace | TestDataController.cs:Replace | none |
| GET | api/test-data/reset/preview | TestDataController.cs:ResetPreview | none |
| POST | api/test-data/reset | TestDataController.cs:Reset | none |
| GET | api/test-data/resources | TestDataController.cs:Resources | none |
| GET | api/test-data/resources/{alias} | TestDataController.cs:Resource | none |
| POST | api/test-data/resources/validate | TestDataController.cs:ValidateResource | none |
| POST | api/test-data/resources | TestDataController.cs:CreateResource | none |
| PUT | api/test-data/resources/{alias} | TestDataController.cs:UpdateResource | none |
| POST | api/test-data/resources/{alias}/duplicate | TestDataController.cs:DuplicateResource | none |
| GET | api/test-data/resources/operations | TestDataController.cs:ResourceOperations | none |
| POST | api/test-data/resources/{alias}/readiness | TestDataController.cs:ResourceReadiness | none |
| GET | api/test-evidence/providers | TestEvidenceController.cs:Providers | none |
| GET | api/test-evidence/source-tests | TestEvidenceController.cs:SourceTests | none |
| POST | api/test-evidence/results/preview | TestEvidenceController.cs:Preview | none |
| GET | auth-required | TestFixtureController.cs:AuthRequired | none |
| GET | test-fixture/no-auth-required | TestFixtureController.cs:NoAuthRequired | none |
| GET | login | TestFixtureController.cs:Login | none |
| POST | api/integration-journeys/packs/{packId}/source-guidance/apply | TestPackSourceGuidanceController.cs:Apply | none |
| GET | api/test-plan-suggestions | TestPlanSuggestionsController.cs:Workspace | none |
| GET | api/test-plan-suggestions/summary | TestPlanSuggestionsController.cs:Summary | none |
| GET | api/test-plan-suggestions/drafts/{id:guid} | TestPlanSuggestionsController.cs:Draft | none |
| POST | api/test-plan-suggestions/drafts | TestPlanSuggestionsController.cs:Create | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| PUT | api/test-plan-suggestions/drafts/{id:guid} | TestPlanSuggestionsController.cs:Save | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/drafts/{id:guid}/revisions | TestPlanSuggestionsController.cs:Revisions | none |
| GET | api/test-plan-suggestions/drafts/{id:guid}/revisions/{revision:int} | TestPlanSuggestionsController.cs:Revision | none |
| POST | api/test-plan-suggestions/drafts/{id:guid}/restore | TestPlanSuggestionsController.cs:Restore | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/archive | TestPlanSuggestionsController.cs:Archive | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/unarchive | TestPlanSuggestionsController.cs:Unarchive | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/delete | TestPlanSuggestionsController.cs:Delete | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/review | TestPlanSuggestionsController.cs:Review | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/refresh | TestPlanSuggestionsController.cs:Refresh | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/drafts/{id:guid}/impact | TestPlanSuggestionsController.cs:Impact | none |
| POST | api/test-plan-suggestions/drafts/{id:guid}/resolve-conflict | TestPlanSuggestionsController.cs:ResolveConflict | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/snapshots | TestPlanSuggestionsController.cs:SaveSnapshot | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/snapshots/{id:guid} | TestPlanSuggestionsController.cs:Snapshot | none |
| GET | api/test-plan-suggestions/compare | TestPlanSuggestionsController.cs:Compare | none |
| GET | api/test-plan-suggestions/snapshots/{id:guid}/export | TestPlanSuggestionsController.cs:Export | none |
| GET | api/test-plan-suggestions/project | TestPlanSuggestionsController.cs:Project | none |
| POST | api/test-plan-suggestions/project/traceability | TestPlanSuggestionsController.cs:ProjectTraceability | none |
| POST | api/test-plan-suggestions/discover | TestPlanSuggestionsController.cs:Discover | none |
| POST | api/test-plan-suggestions/discover/changes | TestPlanSuggestionsController.cs:Changes | none |
| GET | api/test-plan-suggestions/project/work-queues | TestPlanSuggestionsController.cs:WorkQueues | none |
| GET | api/test-plan-suggestions/discover/last | TestPlanSuggestionsController.cs:LastDiscovery | none |
| POST | api/test-plan-suggestions/project-drafts | TestPlanSuggestionsController.cs:CreateProject | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/manual-cases | TestPlanSuggestionsController.cs:AddManualCase | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/drafts/{id:guid}/variants | TestPlanSuggestionsController.cs:Variants | none |
| GET | api/test-plan-suggestions/project/overview | TestPlanSuggestionsController.cs:Overview | none |
| GET | api/test-plan-suggestions/project/story-status | TestPlanSuggestionsController.cs:StoryStatus | none |
| GET | api/test-plan-suggestions/project/story-status/history | TestPlanSuggestionsController.cs:StoryStatusHistory | none |
| POST | api/test-plan-suggestions/project/story-status/confirm | TestPlanSuggestionsController.cs:ConfirmStory | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/dashboard-settings | TestPlanSuggestionsController.cs:DashboardSettings | none |
| PUT | api/test-plan-suggestions/dashboard-settings | TestPlanSuggestionsController.cs:SaveDashboardSettings | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/translation | TestPlanSuggestionsController.cs:Translation | none |
| POST | api/test-plan-suggestions/drafts/{id:guid}/translate | TestPlanSuggestionsController.cs:Translate | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/exclude-scenario | TestPlanSuggestionsController.cs:ExcludeScenario | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| POST | api/test-plan-suggestions/drafts/{id:guid}/translate-all | TestPlanSuggestionsController.cs:TranslateAll | BirkNextPermissions.IntegrationConfigurationWritePolicy |
| GET | api/test-plan-suggestions/requirement-coverage | TestPlanSuggestionsController.cs:RequirementCoverage | none |
| GET | api/translation-settings | TranslationSettingsController.cs:Get | none |
| PUT | api/translation-settings | TranslationSettingsController.cs:Save | SystemConfigurationPermissions.Policy |
| GET | api/translation-settings/offline | TranslationSettingsController.cs:Offline | none |
| GET | api/translation-settings/libretranslate | TranslationSettingsController.cs:LibreTranslate | none |
| POST | api/translation-settings/libretranslate/pull | TranslationSettingsController.cs:Pull | SystemConfigurationPermissions.Policy |
| POST | api/translation-settings/libretranslate/start | TranslationSettingsController.cs:Start | SystemConfigurationPermissions.Policy |
| POST | api/translation-settings/libretranslate/stop | TranslationSettingsController.cs:Stop | SystemConfigurationPermissions.Policy |
| POST | api/translation-settings/test | TranslationSettingsController.cs:Test | SystemConfigurationPermissions.Policy |
| POST | api/wasm-performance/discover-assets | WasmPerformanceController.cs:DiscoverAssets | none |
| POST | api/wasm-security/scan | WasmSecurityController.cs:Scan | none |
| POST | api/workspace-persistence/save-current | WorkspacePersistenceController.cs:SaveCurrent | none |
| POST | api/workspace-persistence/save-as | WorkspacePersistenceController.cs:SaveAs | none |
| GET | api/workspace-persistence/load/{workspaceId} | WorkspacePersistenceController.cs:Load | none |
| GET | api/workspace-persistence/bootstrap/{workspaceId} | WorkspacePersistenceController.cs:Bootstrap | none |
| GET | api/workspace-persistence/documents/{workspaceId} | WorkspacePersistenceController.cs:Documents | none |
| POST | api/workspace-persistence/bodies/missing | WorkspacePersistenceController.cs:MissingBodies | none |
| POST | api/workspace-persistence/bodies | WorkspacePersistenceController.cs:StoreBodies | none |
| GET | api/workspace-persistence/list | WorkspacePersistenceController.cs:List | none |
| POST | api/workspace-persistence/rename/{workspaceId} | WorkspacePersistenceController.cs:Rename | none |
| POST | api/workspace-persistence/duplicate/{workspaceId} | WorkspacePersistenceController.cs:Duplicate | none |
| DELETE | api/workspace-persistence/delete/{workspaceId} | WorkspacePersistenceController.cs:Delete | none |
| POST | api/workspace-persistence/auto-save | WorkspacePersistenceController.cs:AutoSave | none |
| GET | api/workspace-persistence/current-state | WorkspacePersistenceController.cs:GetCurrentState | none |
| GET | api/workspace-persistence/export/{workspaceId} | WorkspacePersistenceController.cs:Export | none |
| POST | api/workspace-persistence/import | WorkspacePersistenceController.cs:Import | none |
| GET | api/workspace-transfer/backups/status | WorkspaceTransferController.cs:Status | none |
| GET | api/workspace-transfer/backups | WorkspaceTransferController.cs:ListBackups | none |
| POST | api/workspace-transfer/backups | WorkspaceTransferController.cs:CreateBackup | none |
| POST | api/workspace-transfer/backups/{fileName}/inspect | WorkspaceTransferController.cs:InspectBackup | none |
| GET | api/workspace-transfer/backups/{fileName}/download | WorkspaceTransferController.cs:DownloadBackup | none |
| GET | api/workspace-transfer/restores | WorkspaceTransferController.cs:ListRestores | none |
| POST | api/workspace-transfer/restores | WorkspaceTransferController.cs:Restore | none |
| POST | packages/scopes | WorkspaceTransferController.Packages.cs:Scopes | none |
| POST | packages/export/preview | WorkspaceTransferController.Packages.cs:PreviewExport | none |
| POST | packages/export | WorkspaceTransferController.Packages.cs:Export | none |
| GET | packages/exports/{fileName}/download | WorkspaceTransferController.Packages.cs:DownloadPackage | none |
| POST | packages/import | WorkspaceTransferController.Packages.cs:StagePackage | none |
| POST | packages/import/{operationId:guid}/preview | WorkspaceTransferController.Packages.cs:PreviewImport | none |
| POST | packages/import/{operationId:guid}/apply | WorkspaceTransferController.Packages.cs:ApplyImport | none |
| GET | packages/import/{operationId:guid}/report | WorkspaceTransferController.Packages.cs:ImportReport | none |
| GET | operations | WorkspaceTransferController.Packages.cs:Operations | none |

The current uncommitted changes to Program.cs and TestPlanSuggestionsController.cs were checked by diff before completion. The action-level register already includes the three added story-status routes; Program.cs adds scoped UserStoryStatusService registration. These unrelated working-tree changes were not modified. The controller actor/policy described above still lacks project role authorization.

