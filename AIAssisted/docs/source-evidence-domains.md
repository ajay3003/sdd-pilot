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
