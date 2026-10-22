# Source Analysis: shared source-evidence domains

Source Analysis owns the **source evidence**. Each review owns its own **interpretation**.

```
upload (POST api/source-analysis/snapshots)
  → IqrSourceArchiveReader: bounded in-memory workspace (nothing written to disk)
  → Architecture → Database → Observability
  → SourceEvidenceAnalyzer: Infrastructure · CI/CD · Configuration · Contracts → cross-domain links
  → security configuration evidence, Classification, Dependency, messaging and SCIM capture
  → Generated documentation (snapshot.GeneratedDocumentation — its own evidence type, see generated-documentation-evidence.md)
  → one immutable IqrSourceSnapshot (snapshot.EvidenceDomains)
```

Generated documentation (e.g. `autodoc/` folders written by an agent workflow) is not a source-evidence domain: it is analysed in the same pass and
stored next to the domains as `snapshot.GeneratedDocumentation`, compared with them, and never used as source evidence. A generated OpenAPI/GraphQL file
still appears in the Contracts domain as a contract file; its generated origin is recorded only in the generated-documentation evidence. See
[generated-documentation-evidence.md](generated-documentation-evidence.md).

## Domains

Contracts are in `shared/SourceEvidenceDomainContracts.cs`, namespace `BirkNext.SourceDomains`. Analyzers are in `Services/SourceAnalysis/Evidence/`.

| Domain | Reads | Support |
|---|---|---|
| Infrastructure | A provider family (see *Infrastructure Source Evidence* below): Terraform (HCL, tfvars, lock files, remote state), declarative manifests (Bicep, ARM, Kubernetes, Helm) and infrastructure automation scripts (Python, shell, PowerShell) | Terraform: Supported (static only). Manifests: Partial. Python/shell scripts: Supported (static); PowerShell: Partial. Script execution: never performed. Pulumi, CDK, CloudFormation, .tf.json: Unsupported. |
| Configuration | appsettings (reuses the files Architecture already parsed), launchSettings, Compose (reuses Architecture's parser), .env, .properties, YAML, Helm values. It also receives values other analyzers hand over in memory: tfvars, Bicep parameters, ConfigMaps and pipeline variables. | JSON: Supported. YAML: Partial. XML: Unsupported. |
| CI/CD | Azure Pipelines, GitHub Actions, GitLab CI, Jenkins | Partial. A YAML subset reader is used, scripts are matched by command family, and templates are listed but not expanded. |
| Contracts | OpenAPI (JSON goes through the existing `OpenApiExtractor`), GraphQL SDL and operations (through the existing Hot Chocolate parser), AsyncAPI, JSON Schema, protobuf, message contracts (reused from the integration-path analyzer), generated clients | Formats are mixed. Every contract carries its own `ParseSupport`. |
| Cross-domain | Links app ↔ infrastructure, app ↔ datastore, config ↔ infrastructure, pipeline ↔ infrastructure, pipeline ↔ tests, component ↔ contract and component ↔ telemetry. Also builds environment mappings and observability layers. | — |

### Common rules

- **Envelope.** Every domain result has the same envelope (`SourceDomainResult`): snapshot id, fingerprint, analyzer version, extraction time, status, technologies, diagnostics and limitations.
- **Status values.**
  - `Complete` and `Partial` describe how much was analyzed.
  - `NotDetected` means nothing of that kind is in the *selected* source. It is not the same as `Unsupported`.
  - `FailedAnalysis` is used only when the analyzer itself failed, and it marks only that domain.
- **Evidence states.** These are the Source Analysis states, `Confirmed` through `Conflict`.
- **Registry order.** `SourceEvidenceAnalyzerRegistry` orders analyzers from their declared `DependsOn` and stage, and rejects cycles. Stage 1 extracts and normalizes. Stage 3 links domains. Stage 4 builds the summaries.

## Invariants

Source Analysis never claims any of the following:

- A resource that is declared in source exists.
- A role assignment means the permission is in effect.
- A private endpoint or `public_network_access_enabled = false` means connectivity was verified.
- A diagnostic setting or App Insights resource means telemetry is flowing.
- A topic or consumer group means a consumer is processing it.
- A pipeline step means it ran or passed.
- A source contract means the runtime is compatible with it.

Two further rules:

- **Unresolved links are neutral.** "No matching declaration found in the selected source" is not "missing": the resource may be managed somewhere else.
- **Changes are source changes.** `SourceEvidenceDiff` reports a difference between two source snapshots, never a drift between source and runtime.

## Secrets

- `SourceEvidenceRedaction` is the single rule set. A value counts as sensitive if:
  - its key segment looks sensitive (password, secret, token, key, connection string, certificate, authorization, …); or
  - the value itself is shaped like a credential (PEM, JWT, `AccountKey=`, SAS, long opaque tokens).
- Sensitive values are classified, for example "Connection string detected". They are never stored, not even as a hash.
- The host of a connection string is kept only as a normalized reference, for linking.
- Raw values exist only in memory, in `SourceConfigurationModel`, during ingestion. Source Analysis extracts the structured evidence there.

## Consumers (`Services/SourceAnalysis/SourceEvidenceQueries.cs`)

Each consumer asks for a typed slice of one snapshot together with its provenance:

- **IQR** (`IqrSourceDomainsReview`) adds "Configured/Declared in source" lines to the Configuration, Connectivity, Contract, Message flow, Observability and Security domains. Runtime evidence is never replaced.
- **Security configuration evidence** is extracted from normalized configuration, including a project's `.env`, launchSettings and Compose. Source Analysis owns the immutable evidence; consumers interpret it without candidate approval.
- **Security Classification** and **Dependency Review** get context summaries only.
- **API Quality Review** is not wired to source contracts, because it has no source scope. Its priority order between runtime and trusted schemas is unchanged.

Snapshots taken before the evidence domains existed have `EvidenceDomains = null`. They show "Not analyzed" and are never reinterpreted.

## Adding an analyzer

Implement `ISourceEvidenceDomainAnalyzer`. Its `DomainAnalyzerInfo` declares the domain, version, stage, technologies, `DependsOn` and `Produces`.

The analyzer must:

- read the context (classified files and the earlier domains);
- set only its own domain result, through `context.Envelope(...)`;
- add its `DomainCapability` rows, marking pattern-based readers as `Partial`.

Then register the analyzer in `SourceEvidenceAnalyzerRegistry.Default`.

## Terraform ownership and consumers (Declared / Configured / Observed / Verified)

**Who parses Terraform:** only the Source Analysis Infrastructure analyzer (`HclReader` + `InfrastructureAnalyzer`).
- Nothing else reads `.tf` or `.tfvars`, runs the Terraform CLI, or reads state. `.tfstate` files are skipped with a limitation.
- A guard test enforces this rule: `InfrastructureConsumerMigrationTests.No_consumer_parses_terraform_outside_source_analysis`.

**The four states:**
- **Declared:** found in a Source Analysis snapshot.
  - Each resource carries its default name plus per-environment names resolved from tfvars (analyzer v2).
- **Configured:** owned by the target environment and review settings, for example the Integrations catalog.
  - Source evidence never writes configured values.
  - `SourceInfrastructureComparer` compares a configured value with the declarations. Its states are: Matches, Differs, Multiple source candidates, Not configured with a suggestion, No source declaration, Unresolved, and Source unavailable.
  - The UI shows suggestions through `SourceInfrastructureHint`. "Use detected value" only fills the form and records the provenance `DeclaredInSource`; the value is stored only when a person saves.
- **Observed:** runtime adapters, such as Azure metadata, Monitor and Blob. These stay independent of source evidence.
- **Verified:** checked behaviour. A source or audit value is never labelled Verified. The old "Source/runtime configuration verified" label is now "From source configuration (audited, not deployment-verified)".

**Shared helpers:** identity and environment normalization live in one place, `shared/SourceInfrastructureIdentityContracts.cs`:
- `InfrastructureIdentity` covers resource kinds, host suffixes, name normalization and the parent resource.
- `SourceEnvironments` covers environment labels and inference from names.

Consumers must not re-derive "`-qa-` means QA" or "x.servicebus.windows.net means namespace x" themselves.

**IQR:**
- Each run stores `SourceInfrastructureComparisons`, bound to the run's snapshot, fingerprint and analyzer version.
- The evidence source is reported as `SourceInfrastructure` ("Infrastructure as Code (Source Analysis, declared)").
- The seeded DEV values from a developer-side Terraform audit stay **Configured**. Their provenance is "Audited infrastructure configuration", and they never get a fake snapshot id.

## Source-consumer audit (2026-10-02)

The only source ingestion is `POST api/source-analysis/snapshots`, which runs `IqrSourceStore.AnalyzeAsync`. That method validates the archive once with `IqrSourceArchiveReader`, then runs every analyzer in a single pass and inserts one immutable `IqrSourceSnapshot`.

Guard tests (`SourceIngestionOwnershipTests`, plus the Terraform guard) fail the build if any of these appear:
- a second multipart source upload;
- a new archive reader;
- a review interface that takes raw archives.

| Feature | Source dependency | Status |
|---|---|---|
| Source Analysis (Architecture, Database, Observability, Infrastructure, Configuration, CI/CD, Contracts) | canonical | COMPLIANT |
| Dependency Review | snapshot scope (primary + related). The SBOM/lock and Renovate-override inputs are not source repositories. `RunAsync(archives)` is a documented test harness. | COMPLIANT |
| Security Classification | snapshot scope. Its specialized Roslyn analyzer gets the validated upload bytes at ingestion. | COMPLIANT / JUSTIFIED SPECIALIZED |
| Security configuration evidence | `SecurityExpectationsEvidence` plus normalized Configuration. The legacy fallback reads the same snapshot's stored Architecture. | COMPLIANT |
| IQR | shared domains via `IqrSourceDomainsReview`; configured-vs-source comparison; runs embed their exact snapshots. Its Wolverine/SCIM/path analyzers are specialized. | COMPLIANT / JUSTIFIED SPECIALIZED |
| Application messaging and SCIM (IQR) | snapshot scope only | MIGRATED: the dormant raw-archive `AnalyzeAsync(archives)` overloads were removed |
| Target Environment, Authentication, Endpoint/Browser Discovery | runtime/configured. URL-type classification is not a source environment. Event Hub settings hints use the shared suggestions. | RUNTIME-ONLY / CONFIGURED-STATE |
| API QR, FQR, Critical E2E | no source calls and no prerequisite; trusted SDL/contract artifacts are separate inputs | RUNTIME-ONLY |
| Dashboard, exports | read stored results | COMPLIANT |

## Infrastructure Source Evidence (Infrastructure analyzer v4)

```
snapshot files ──► TerraformEvidenceProvider ─────────────┐
               ├─► DeclarativeManifestEvidenceProvider ───┼─► InfrastructureEvidenceBuilder ─► InfrastructureEvidence (stored once)
               └─► InfrastructureScriptEvidenceProvider ──┘        ▲
                     reads CI/CD step script references ───────────┘   (Infrastructure DependsOn CI/CD; nothing is re-parsed)
consumers ──► InfrastructureEvidenceQueries (shared, pure) / SourceEvidenceQueries slices (backend, with provenance)
```

- **Sibling providers** (`IInfrastructureSourceEvidenceProvider`): each claims the files it understands, reads them once per immutable snapshot and writes
  normalized facts with provenance (file, line, Terraform address or script function) into one `InfrastructureEvidence`. `ProviderRuns` records what each
  contributed. Bicep, Helm or Kubernetes-specific providers can join the family without a new consumer model.
- **Terraform extras (v4):** `required_version`, `.terraform.lock.hcl` providers (versions and constraints, hashes dropped), `data "terraform_remote_state"`
  with the source layer matched by directory name (`Strong source match` at most — the backend is never contacted; outputs read but not declared in the matched
  layer are listed), count/for_each kept as written (`Conditional declaration`, never expanded), state and plan files listed as intentionally excluded.
- **Infrastructure scripts:** read statically and **never executed, sourced, imported or evaluated** — Python by a tokenizer (imports, functions, entry
  point, subprocess/os.system commands, file reads/writes/globs, environment variable names, CLI arguments, failure messages, exit statuses), shell by a
  quote-aware command reader (heredocs with an embedded Python program are read as Python), PowerShell line by line. A script is infrastructure automation
  only when its own content works on infrastructure source (resource types, Terraform files/blocks, terraform/az commands) or its pipeline/pre-commit
  context does — never because of its language, file name or folder; other scripts are listed apart with the reason. Purpose (validation, policy, naming,
  version, inventory, documentation, deployment, migration, reporting) comes from commands, file access, failure messages and outputs; enforcement from the
  exit paths; validation rules are the script's own failure messages. Generated artifacts are definitions: a copy is reported only when the source package
  contains one ("checked-in copy; not produced by this analysis").
- **Checks** aggregate scripts that enforce the same policy (`Implementations`), count the matching declarations of the same snapshot and carry
  `Check implemented in source` / `Not executed by analysis`. **Invocations** come from CI/CD `LocalScript` steps (pipeline analyzer v3: path, interpreter,
  `|| …`/continueOnError tolerance) and `.pre-commit-config.yaml` local hooks (a commit gate on developer machines, not CI). Configured as a gate is never
  "passed".
- **States never upgraded:** Declared in Terraform ≠ detected in a script ≠ validation logic available ≠ referenced by pipeline ≠ configured in BirkNext ≠
  confirmed mapping ≠ executed ≠ deployed ≠ observed ≠ healthy. `InfrastructureEvidenceLayers` keeps declared / source validation / pipeline configuration
  apart and always reports execution and runtime as `Not assessed`.
- **Configure-once:** script constants never become configuration values; Terraform values remain lower-tier proposals below explicit saved values.
- **History:** snapshots analyzed before v4 never had their scripts read; consumers say "Infrastructure script evidence was not available in this analysis
  version", and `SourceEvidenceDiff` compares automation only between two v4 snapshots.
- **Several Terraform sources:** suggestions use the requested snapshot, or the only snapshot with infrastructure evidence — never the newest of several.
