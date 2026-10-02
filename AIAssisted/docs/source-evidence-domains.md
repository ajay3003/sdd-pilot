# Source Analysis: shared source-evidence domains

Source Analysis owns the **source evidence**. Each review owns its own **interpretation**.

```
upload (POST api/source-analysis/snapshots)
  → IqrSourceArchiveReader: bounded in-memory workspace (nothing written to disk)
  → Architecture → Database → Observability
  → SourceEvidenceAnalyzer: Infrastructure · CI/CD · Configuration · Contracts → cross-domain links
  → Security Expectations, Classification, Dependency, messaging and SCIM capture
  → one immutable IqrSourceSnapshot (snapshot.EvidenceDomains)
```

## Domains

Contracts are in `shared/SourceEvidenceDomainContracts.cs`, namespace `BirkNext.SourceDomains`. Analyzers are in `Services/SourceAnalysis/Evidence/`.

| Domain | Reads | Support |
|---|---|---|
| Infrastructure | Terraform (HCL), tfvars, Bicep, ARM, Kubernetes, Helm | Terraform: Supported (static only). The others: Partial. Pulumi, CDK and CloudFormation: Unsupported. |
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
- Raw values exist only in memory, in `SourceConfigurationModel`, during ingestion. Security Expectations consumes them there.

## Consumers (`Services/SourceAnalysis/SourceEvidenceQueries.cs`)

Each consumer asks for a typed slice of one snapshot together with its provenance:

- **IQR** (`IqrSourceDomainsReview`) adds "Configured/Declared in source" lines to the Configuration, Connectivity, Contract, Message flow, Observability and Security domains. Runtime evidence is never replaced.
- **Security Expectations** reads candidates from the normalized configuration, including a project's `.env`, launchSettings and Compose. Dedupe, approval and snapshot binding are unchanged.
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
