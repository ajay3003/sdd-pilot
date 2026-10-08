# Generated documentation evidence

Generated documentation is documentation that a tool, build step, script or agent workflow writes from the code — for example per-module
`autodoc/` or `generated-docs/` folders with `README.md`, `architecture.md`, `dataflow.md`, `data-model.md`, `message-bus.md`, `openapi.yaml`,
`schema.graphql`, `blazor-routes.md` and `CHANGELOG.md`. BirkNext treats it as **its own evidence type**:

| Evidence | Examples | Owner in BirkNext |
|---|---|---|
| Authored documentation | Constitution, Specification, Plan, Tasks, Data Model | Shared Artifact Repository and the explorers |
| Generated documentation | `autodoc/`-style folders, generated contracts | Source Analysis → `IqrSourceSnapshot.GeneratedDocumentation` |
| Source evidence | Code, configuration, contracts, pipelines, IaC | Source Analysis (Architecture, Database, evidence domains) |
| Runtime evidence | Observed endpoints, Azure inventory, test runs | Runtime reviews |

Rules: generated docs ≠ source ≠ specification ≠ constitution ≠ runtime evidence. Source is stronger implementation evidence than generated docs.
A stale generated document does not make the implementation wrong; a specification/generated mismatch does not make the specification wrong; a task
marked done is not verified by documentation; detected drift is not a failed requirement. Every discrepancy is **"Potential drift – requires review"**.

## Where it is computed

Generated-documentation evidence is analysed **once at upload**, inside `IqrSourceStore.AnalyzeValidatedAsync`, over the same in-memory workspace and the
models Source Analysis already built (Architecture facts, Database, Contracts, Configuration). Nothing is re-read from the archive later.

- `IqrSourceArchiveReader` captures, in the same bounded pass, Markdown documents and documentation-workflow files (`.claude/`, `.cursor/` …, scripts
  named for documentation) as `Workspace.DocumentationCandidates` (2,000 files, 1 MB each, 24 MB total, never more than the remaining expanded-read
  allowance — documentation reads can never reject an archive) and every entry's own time as `Workspace.EntryModified`. Both are memory-only.
- `Services/SourceAnalysis/GeneratedDocumentation/`:
  - `GeneratedDocumentationDetector` — workflow/generator files, generated folders, origin (Generated / Authored / Unknown), document kind.
  - `DocumentStructure`, `DocumentKeyReader`, `SourceRouteReader`, `TechnologyVocabulary` — deterministic structure and key readers.
  - `GeneratedDocumentationAnalyzer` — module scoping, source keys, freshness, expected outputs, generated-vs-source drift, Spec-Kit contract drift.
  - `AuthoredDocumentationDrift` — request-time comparison with the selected authored artifacts.
  - `GeneratedDocumentationDiagnosticService` + `POST api/system-diagnostics/generated-documentation/run` — snapshot binding, project isolation, cache.
- Contracts: `shared/GeneratedDocumentationContracts.cs` (namespace `BirkNext.GeneratedDocumentation`).

Only paths, content fingerprints, line counts, declared dates and bounded structured keys are stored. No document text, configuration value or secret is
stored; keys pass the Source Analysis redaction.

## Detection and provenance

- **Workflow files**: agent skill/command definitions, hook registrations (`settings.json` with `hooks`), hook scripts, documentation-tool
  configuration (`mkdocs.yml`, `docfx.json`, `typedoc.json`, `Doxyfile` …), build targets (`package.json` docs scripts, csproj documentation targets) and
  documentation scripts. A workflow **declares** a folder when an output verb precedes the folder on the same line or an "Output" heading names it;
  a mention alone is a **reference** (the workflow may only read that folder). Declared output files are the `<folder>/<file>` names it states.
- **Generated folders**: directory conventions (`autodoc`, `generated-docs`, `docs/generated`, `build/docs` … — configurable through
  `GeneratedDocumentationOptions`) and folders a workflow declares (an explicit repository path must match exactly; a convention name matches every
  folder of that name).
- **Origin**: Spec-Kit feature folders (`specs/<feature>/…`), `.specify/` and constitutions are **Authored** and never generated documentation, even
  when a workflow writes them. Agent context files (`CLAUDE.md`, `AGENTS.md` …) are workflow configuration, not documentation. A file in a generated
  folder that the detected workflow does not declare has **Unknown** origin and is listed as an additional document (never a failure). A single file
  with an explicit generated marker in its header is generated with unknown provenance.
- **Provenance**: `GeneratorType` is AgentWorkflow / BuildGenerator / DocumentationTool / Script / Unknown. `GeneratorName` is set only when the
  workflow names itself (e.g. skill front matter). Confidence reuses the Source Analysis evidence states: Confirmed (declared by a named workflow),
  StronglySupported (declared, unnamed), Inferred (convention or marker only). Without a declaring workflow the label is
  **"Likely generated / Unknown provenance"** — never a fabricated generator.
- **Kind**: from file name, format and content (OpenAPI root keys, SDL, mermaid diagram types, dated changelog headings, entity/route/topic tables).

## Module scope and freshness

A module is the folder a generated folder documents (its parent, skipping container folders such as `docs/` or `build/`; a root-level output such as
`docs/architecture/` documents the whole repository). Source of module A never dates documents of module B.

Freshness per document (`Current`, `Stale` shown as **Potentially stale**, `Unknown`, `NotEnoughEvidence`, `NotApplicable`):

1. A source commit/fingerprint the document declares, compared with the snapshot commit when that is known (strongest).
2. Generation time: a date the document declares (`Last updated: …`, `x-last-updated`, `# Last updated`), else a trustworthy archive time.
3. Relevant dated source evidence of the same module: EF-style migration identifiers (data-model documents only) and archive times — **only when the
   archive times vary**. A downloaded/exported archive stamps every entry with one time; then archive times are never used.
4. Changelog and version metadata (latest changelog version vs `<Version>` in the module, "Unreleased" entries) are **supporting signals only**.

Stale requires dated source evidence newer than the generation date. No generation date → Unknown; no trustworthy dated source evidence → Not enough
evidence. The current machine time is never used. Changelogs are history records (Not applicable).

**Expected outputs** come only from workflow declarations, and count as missing only when the module shows the capability the document describes
(HTTP API, GraphQL, messaging code, data model, UI routes). Extra documents are reported, never failures.

## Deterministic comparisons (DocumentationDrift)

All comparisons use structured keys; no prose similarity, no AI, no embeddings.

| Family | Generated side | Source side | Notes |
|---|---|---|---|
| HTTP routes | OpenAPI operations, Method/Path tables, inline `GET /x` spans | Minimal-API facts, controller/endpoint attributes, health/GraphQL mappings | Segment-suffix matching tolerates group prefixes; "documented but not found" may be a library route |
| API contract / GraphQL schema | Generated contract (parsed by the Contracts analyzer) | A non-generated contract of the same type in the module (e.g. a committed schema snapshot) | `SourceEvidenceDiff.ContractChanges`; Equivalent / Potentially drifted / Unable to compare |
| UI routes | Route/page tables | — | Unable to compare: component files are not read |
| Messaging resources | Resource columns and property/value tables | Resolved channel names, messaging facts, configuration entity names, literal reads | Documented ≠ declared ≠ observed at runtime |
| Data model | Entity headings and entity/table columns | Database analysis tables/entities of the module | Names merged per table; index/key objects ignored |
| Configuration keys | Key columns with hierarchical keys | Configuration entries and literal key reads | Values are never compared or shown |
| Architecture components | Dotted project identifiers under the module's naming root | Source projects, deployable components | Repository-level overviews are checked only for what they state |

A structurally invalid generated contract (parser error, malformed JSON) is the only **Fail**. Semantic disagreement is at most **Needs review**.
One logical mismatch is one candidate even when several generated documents state it (several evidence references).

## Authored vs generated (request time)

The diagnostic receives the Constitution, Specification, Plan and Tasks the workspace selected (same role authority as Requirements Traceability) and
compares them **only** with the snapshot of the same imported project (shared Project Import identity). Artifacts are scoped to a module by their path.

- **SpecificationDrift**: explicit `METHOD /path` literals and Spec-Kit *Key Entities* bullets the generated documentation of the scope lacks; Spec-Kit
  contract files (`specs/<feature>/contracts/`) vs generated contracts (operations and field types). The generated document never supersedes the
  specification; source presence is attached as separate evidence ("source stronger", nothing is overwritten).
- **ConstitutionDrift**: only explicit rules that govern a vocabulary technology directly ("MUST NOT use X", "X is prohibited", "MUST use X" vs a
  documented competing technology of the same exclusive category).
- **PlanDrift**: Spec-Kit Technical Context fields (Storage, Primary Dependencies, Language/Version, Target Platform, Project Type, Testing).
- **DeliveryDrift**: a task marked complete that names a route, entity or resource the generated documentation describes while the module's source
  evidence does not — "Task completion not independently verified" (never "incomplete"). Implementation Review remains the task-result owner.

## Snapshot binding, isolation and cache

- Every result names its exact snapshot. A historical snapshot is evaluated with its own evidence; one analyzed before this feature reports
  "Not available for this snapshot" and is never replaced by current source.
- A project import's diagnostic uses that import's snapshot only; another project's snapshot is refused; authored artifacts are never compared with a
  snapshot of a different project (or a snapshot without shared import provenance).
- Results are cached per snapshot id, generated-evidence fingerprints, authored fingerprints and analyzer version (10 minutes, in memory).

## UI

- **System Settings → Developer → Generated Documentation Health** (`?section=generated-docs-health`): description, Run diagnostics, snapshot binding,
  summary counts (no score), generator/workflow evidence, filters (All, Fresh, Potentially stale, Unknown freshness, Missing expected docs, Source
  discrepancies, Cross-artifact drift), module details and per-candidate evidence panels (left side, right side, difference, snapshot/artifact
  version, why review is needed). Empty state: "No generated documentation detected in the current source snapshot."
- **Source Analysis → Overview → Source evidence**: a compact Generated documentation row with a link to the diagnostic.
- Generated documentation never feeds the Specification, Constitution, Plan, Task or Data Model explorers, Document Quality Review, FQR/AQR/IQR or
  Environment Analysis. A generated OpenAPI/GraphQL file remains contract evidence in the Contracts domain (as before), never a Specification.

## Limitations

- Freshness of non-data-model documents needs varied archive times or a declared source commit; exported archives usually give "Not enough evidence".
- Route evidence covers minimal APIs, controller and endpoint attributes; route groups, conventions and library-provided endpoints can surface as
  "documented but not found" candidates.
- Code-first GraphQL schemas are not reconstructed; a GraphQL comparison needs a committed schema in the module.
- Expected outputs follow workflow declarations; conditional prose ("skip if no X") is not interpreted — the module capability check stands in for it.
- No discrepancy found does not mean the documentation is fully synchronized: only extractable structured keys are compared.
