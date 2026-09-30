**Generic source-derived database architecture — implementation and validation, 2026-09-30**

Database analysis is available at `/source-analysis`. It uses the existing bounded in-memory ZIP reader and insert-only source snapshot store. It never opens a connection to a database described by the uploaded source. The stored model, diagram and comparisons describe source assertions, not deployed schema or runtime drift.

The implementation is a conservative V1. Unsupported mappings stay unresolved. In particular, a table/entity assertion count is not a verified physical table count, and a database candidate count is not a physical database count.

| Required item | Result |
|---|---|
| A. Starting HEAD | `953cf4ed45ceb7ec1f0a698daa0c2d9ca3c3ad7a` |
| B. Branch | `008-traceability-first` |
| C. Initial status | Clean; `git status --short` produced no entries in BirkNext. The session's parent directory is not a Git repository. |
| D. Reused components | `IqrSourceArchiveReader.Workspace`, archive fingerprinting and bounds, `IqrSourceAnalyzer`, `IqrSourceStore`, `IqrSourceSnapshotRecord.EvidenceJson`, source history, existing HTTP client and HTML/JSON download helpers. The generic endpoint shares the existing upload validation and store; no separate archive persistence pipeline was introduced. |
| E. Graph technology | Native SVG with a local JS interop module. Existing Blazor/JS interop is sufficient; no existing graph or SQL parser package was found. Deterministic grid layout, spatial interactions and reduced card detail avoid a large graph dependency. |
| F. Dependencies | No new package dependencies. Existing Roslyn parses C# syntax. The DDL subset uses a bounded tokenizer and structured parser, not a regex-only parser. |
| G. License | No third-party graph library. Installed Roslyn 4.5.0 package metadata declares MIT. Existing test dependency license terms remain unchanged. |
| H. Generic model | `DatabaseArchitectureSnapshot`, `DatabaseModel`, `SchemaModel`, `TableModel`, `ColumnModel`, `KeyModel`, `RelationshipModel`, `IndexModel`, `ConstraintModel`, evidence assertions and conflicts. Defaults, expressions and index filters have nullable model fields but are deliberately not populated by V1. |
| I. Evidence states | Confirmed, Inferred, ObservedFromMigration, ObservedFromDDL, Unresolved. Conflict is preserved separately, with both assertions and source locations. No percentage confidence is invented. |
| J. Abstraction | `IDatabaseSchemaExtractor`: name/version, capability check, cancellable `AnalyzeAsync`, neutral extraction result. Separate EF Core, migration and SQL DDL implementations. |
| K. EF coverage | Direct DbContext subclasses, primary constructors through existing normalization, DbSet and inline Entity registrations, matching configuration classes, ToTable, default/explicit schema, Table/Column/Key/Required/length annotations, ordered keys/FKs, alternate keys, Property, column names/types, required state, lengths/precision, generated-on-add, computed-column presence, indexes/uniqueness, dependent-side HasOne FK chains and delete behavior. Convention evidence remains inferred. |
| L. Migration coverage | Create/Drop/Rename table, Add/Alter/Drop/Rename column, Add/Drop FK, Create/Drop index, Add/Drop PK; CreateTable columns and constraints. Designer DbContext attributes establish context ownership. Up operations are replayed in source-path order; this is not deployment history. Generation is marked only when an explicit marker exists; otherwise it remains unknown. |
| M. DDL coverage | Basic SQL Server/PostgreSQL CREATE TABLE, ALTER TABLE ADD, inline/table PK/FK/REFERENCES, UNIQUE, nullability, CREATE INDEX and unique indexes. Quoted identifiers, comments and string literal bodies are tokenized separately. Type modifiers are preserved. |
| N. Unsupported constructs | Inherited/custom context bases, full semantic type binding, configuration-only models without resolved registration, principal-side HasMany FK binding, full owned/join/table-splitting models, model snapshot BuildModel assertions, migration branching/custom SQL/data operations, full dialect grammar, general ALTER/DROP DDL replay, views/procedures/functions. These must not be interpreted as absent schema or database defects. |
| O. Merge behavior | Explicit context ownership metadata is used when available. Without it, a migration group attaches only if exactly one source-project context exists. Ambiguous contexts and independent DDL groups stay separate. Matching explicit table identities merge evidence; inferred entity candidates are not silently promoted to physical tables. |
| P. Conflicts | Explicit column nullability/type/length/precision assertions, table-name assertions and key assertions retain source/value pairs. Explicit facts supersede convention display values, while convention evidence remains available. Contradictory explicit assertions produce conflict records and NeedsReview. This is not a complete semantic reconciliation engine. |
| Q. Persistence/history | Database JSON is inserted with its exact source snapshot, fingerprint, extraction timestamp, analyzer version and extractor versions in the existing source record. Analyzer version 2; EF syntax 1, migration syntax 2, DDL tokens 1. Historical records are not updated. Reanalysis creates a new source snapshot. No automatic cache/reuse was added to the existing insert-only behavior. |
| R. Overview | Candidate databases, schema groups, table/entity assertions, columns, relationships by strength, indexes, unresolved evidence, conflicts, providers and limitations. |
| S. Diagram | Zoom, pan, drag, mouse/keyboard table selection, fit/reset/focus, expand selected/all, reduced columns, related focus, filters and layout saving. Dynamic SVG, not a static image. |
| T. Cards | Name, evidence marker, all extracted PK/FK columns and up to three other columns by default. Selected/all expansion and PK/FK-only mode. |
| U. Edges | Cardinality and state labels; solid confirmed, dashed inferred, dotted migration/DDL. Unresolved targets remain in the structural views rather than drawing a fabricated edge. |
| V. Search | Table, entity and column names only; no generic code-search results are mixed into the diagram. |
| W. Filters | Database candidate, schema, source project, Confirmed only, Include inferred, Hide isolated, relationship visibility, PK/FK-only. |
| X. Related | Direct, two levels, all reachable; extracted relationships only. |
| Y. Layout | Explicit Save; browser-local coordinates keyed by exact source snapshot ID, fingerprint, database selection and default diagram view. Reload restoration tested. No cross-snapshot or cross-device reuse. |
| Z. Table details | Logical/physical name, schema, source project, context, provider, safe connection key, columns, keys, relationships, indexes, constraints, evidence and review items. |
| AA. Column details | Known CLR/store fields, nullable assertions and their states, PK/FK, identity/generated/computed state, length and precision; source evidence. Secret-bearing defaults/expressions are not shown. |
| AB. Evidence | Exact snapshot/fingerprint, relative project/path, symbol, line range, extractor, assertion type, explanation and state. No source bodies or connection values are retained. |
| AC. Changes | Previous source analysis in the same source-owner scope is compared with the selected analysis. Requires two analyses. |
| AD. Diff semantics | Source schema change: table/column/key/FK/index additions, removals and changes. No migration deployment or runtime drift claim. Removed objects are not inserted into the current model. |
| AE. Export | Escaped HTML report, generic JSON model, current visible SVG layout with legend. No raster export or diagram-diff ghost nodes. |
| AF. Secrets | Configuration values excluded by the existing reader. Only GetConnectionString keys may be recorded. Defaults, SQL expressions and literal bodies are excluded. A `SECRET_DB_123` fixture verifies exclusion from serialized results and persisted history. |
| AG. Accessibility | Tables/Relationships give equivalent structural access. Keyboard table selection/details, search, controls, fit/reset/focus. Scoped Axe check on the pilot workspace: zero violations; not a claim of whole-application WCAG compliance. |
| AH. Responsive | 1440/1100/768/390 checked. Controls wrap and details stack; no horizontal page overflow in the diagram smoke. |
| AI. Large schemas | 10/50/100/200 node smoke checks passed. Approximately 15 ms for 200 reduced-detail nodes; approximately 24 ms with 199 synthetic edges in the separate pilot check. These are browser render sanity measurements, not load-test guarantees. |
| AJ. M2LB fingerprint | `c850a1b2813bbf6e2a9eba1f311d63ff0332eacf35764cd6b767489ac22dc41e`, local `M2LB (2).zip`, 4,734,868 bytes. |
| AK. Providers | SQL Server source evidence in all seven contexts and the DDL group. Technologies: EF Core, SQL Server, SQL DDL. PostgreSQL is covered by the generic DDL fixture, not asserted for this pilot. |
| AL. DbContexts | AutorisasjonsDbContext, HendelseDbContext, BirkAdapterDbContext, PersonDbContext, AdapterDbContext, BirkStagingDbContext, TjenesteDbContext. |
| AM. Candidates | Eight: seven context candidates and one independent DDL source group. This does not establish eight physical databases. |
| AN. Schemas | Known names: BiRKAdapter, birk_staging, tjeneste, wolverine. Unknown schema groups remain unknown; no automatic dbo/public default. |
| AO. Tables | 70 table/entity assertions after migration ownership reconciliation; not a verified physical-table total. |
| AP. Relationships | 42 assertions, including unresolved targets; 32 have resolvable graph endpoints. |
| AQ. Indexes | 129 source index assertions; not a verified deployed index count. |
| AR. Inferred items | 11 table candidates and one relationship. Other column/property convention evidence remains attached to its facts. |
| AS. Conflicts | Zero explicit conflicts detected by the supported fact checks. This does not prove the absence of conflicts outside extractor coverage. |
| AT. Unresolved | 56 evidence entries. They include unsupported operations/mappings and unresolved or ambiguous symbols/targets; not 56 database defects. |
| AU. Example trace | BarnIAndrelinjeBarnevern.PersonId → Person.PersonId: HasForeignKey in `Person/src/M2LB.Person.Infrastructure/Persistence/PersonDbContext.cs`, lines 126–128; OnDelete through line 129; separate migration FK evidence in `Person/src/M2LB.Person.Infrastructure/Migrations/20260306124732_InitialCreate.cs`, lines 182–187. No historical Person.Id assumption was used. |
| AV. Generic pilot | InventoryContext fixture: two tables, one explicit N:1 relationship, zero unresolved/conflicts, provider unknown. Generic browser fixture separately verifies two tables and one relationship without pilot names. |
| AW. Focused backend | 48 passed, 0 failed, 0 skipped: 19 database fixture cases and 29 existing source cases. |
| AX. Focused frontend | 7 passed, 0 failed, 0 skipped: four database component tests and three existing source UI tests. Additional generic browser smoke passed. |
| AY. Source regression | Initial baseline: 29/0/0. Broader source/path run: 60/0/0 before final fixture additions; final full backend includes the source cases. |
| AZ. IQR | 381 passed, 0 failed, 0 skipped (`Integrations`/`IntegrationQuality` filter). |
| BA. AQR | 154/0/0 (`ApiReview`/`ApiQuality`). |
| BB. FQR | 53/0/0 (`FrontendQuality`). |
| BC. Dependency Review | 100/0/0. |
| BD. Active CDC | 65/0/0. |
| BE. Critical E2E | 131/0/0. |
| BF. Security Classification | 47/0/0. |
| BG. Full backend | Final restored-infrastructure run: 3,169 passed, 0 failed, 6 skipped (3,175 total). Earlier unrestricted semantic run: 3,166/0/6. An intervening run before the final namespace fixture had 3,106 passed, 62 failed, 6 skipped because PostgreSQL was unavailable. Starting the existing Podman VM and existing test PostgreSQL container resolved those failures. An initial sandboxed run also failed infrastructure checks (3,064 passed, 98 failed, 6 skipped); its unrestricted rerun passed. |
| BH. Full frontend | Unit suite: 4,446 passed, 0 failed, 0 skipped. Normal unrestricted browser run: 9 passed, 8 failed, 0 skipped. A mixed prestarted run: 2 passed, 15 failed. Starting-HEAD full browser run: 2 passed, 15 failed. The eight normal-run failures all reproduce in the baseline full suite; prestarted-only maintenance tests pass 2/0/0 separately. Do not claim the legacy browser suite is green. |
| BI. Debug | Final backend and frontend solution builds succeeded, zero errors. |
| BJ. Release | Final backend and frontend solution builds succeeded, zero errors; existing warnings remain. |
| BK. Timing | Small archive read plus extraction: approximately 0.36 s. Final M2LB read plus extraction: 28.9 s; approximately 22–31 s across development runs. Final real API upload/history/browser pilot: 33.2 s, 70 cards and 32 resolvable edges, zero browser errors, three immutable source-history records. Source snapshot `9514ed07-5aef-490a-bdf0-c88284dd8fda`, database snapshot `534519bb-c404-4e6f-b8be-3ea9242fd220`. No source-project database was contacted. |
| BL. Files | Listed below; only this feature, its tests/tooling and this report are included. |
| BM. Commit | The final response records the resulting commit hash. |
| BN. Diff check | `git diff --check` and `git diff --cached --check` passed, including all 21 staged files. |
| BO. Final status | Recorded in the final response after commit. |
| BP. Remaining gaps | See N; also no live metadata, full migration branch graph, cross-device layout storage, automatic cache reuse, manual arbitrary snapshot pair selection, path-between-tables UI, diagram diff/ghost nodes, or data classification. Extraction remains explicitly partial where mappings cannot be resolved. |

The final pilot groups are source-derived:

| Candidate | Tables/entities | Relationships | Index assertions |
|---|---:|---:|---:|
| AutorisasjonsDbContext | 12 | 10 | 46 |
| HendelseDbContext | 23 | 19 | 23 |
| BirkAdapterDbContext | 3 | 0 | 0 |
| PersonDbContext | 11 | 9 | 40 |
| AdapterDbContext | 1 | 0 | 6 |
| BirkStagingDbContext | 6 | 0 | 2 |
| TjenesteDbContext | 6 | 3 | 12 |
| DDL group, Tjeneste/.pipeline | 8 | 1 | 0 |

The 23 Hendelse assertions include entity candidates whose physical mapping is not established separately from migration declarations. Unproven source identities are not silently merged. Source paths, providers and names above are pilot evidence only; the extractor and graph core contain no M2LB-specific branches or constants.

Changed implementation files:

- `AIAssisted/shared/DatabaseArchitectureContracts.cs`
- `AIAssisted/shared/IqrSourceContracts.cs`
- `AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj`
- `AIAssisted/backend/BirkNext.Api/Controllers/IqrSourceEvidenceController.cs`
- `AIAssisted/backend/BirkNext.Api/Services/DatabaseArchitecture/DatabaseExtraction.cs`
- `AIAssisted/backend/BirkNext.Api/Services/DatabaseArchitecture/DatabaseArchitectureAnalyzer.cs`
- `AIAssisted/backend/BirkNext.Api/Services/DatabaseArchitecture/MigrationSchemaExtractor.cs`
- `AIAssisted/backend/BirkNext.Api/Services/DatabaseArchitecture/SqlDdlSchemaExtractor.cs`
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs`
- `AIAssisted/backend/BirkNext.Api/Services/Integrations/SourceEvidence/IqrSourceStore.cs`
- `AIAssisted/backend/BirkNext.Api.Tests/Services/DatabaseArchitectureTests.cs`
- `AIAssisted/frontend/BirkNext.Web/BirkNext.Web.csproj`
- `AIAssisted/frontend/BirkNext.Web/Layout/NavMenu.razor`
- `AIAssisted/frontend/BirkNext.Web/Services/IntegrationCatalogApiService.cs`
- `AIAssisted/frontend/BirkNext.Web/Pages/SourceAnalysis.razor`
- `AIAssisted/frontend/BirkNext.Web/Components/DatabaseWorkspace.razor`
- `AIAssisted/frontend/BirkNext.Web/Components/DatabaseWorkspace.razor.css`
- `AIAssisted/frontend/BirkNext.Web/wwwroot/js/databaseDiagram.js`
- `AIAssisted/frontend/BirkNext.Web.Tests/Pages/DatabaseWorkspaceTests.cs`
- `AIAssisted/frontend/BirkNext.Web.PlaywrightTests/database-diagram-smoke.cjs`
- `docs/database-architecture-validation.md`

The reusable browser smoke takes the frontend origin as its first argument and an optional JSON result path as its second. It uses the existing Playwright Node tooling; set NODE_PATH to your existing module location if needed. It mocks a generic source snapshot, not the source upload/storage pipeline. The separate local M2LB pilot exercised the actual upload/store/history path and reopened the persisted diagram.
