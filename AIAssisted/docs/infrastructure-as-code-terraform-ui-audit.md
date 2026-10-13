# Infrastructure as Code / Terraform — UI audit

**Audit date:** 2026-10-13. **Branch:** `008-traceability-first`. **HEAD:** `89a5d311`. This is an audit only: no production code was changed, nothing was staged, committed or pushed.

**Semantics used throughout this document:**

- Terraform source detected ≠ infrastructure deployed.
- Terraform resource declared ≠ Azure resource exists.
- Terraform pipeline step found ≠ deployment succeeded.
- IaC configuration ≠ runtime evidence.
- File detected ≠ successfully parsed ≠ deployment verified.

---

## 1. Executive summary

**BirkNext already supports Terraform end to end.** Source Analysis owns the support:

- it classifies `.tf` and `.tfvars` files;
- a static HCL reader parses them (no evaluation);
- the Infrastructure analyzer extracts resources, data sources, modules, variables, tfvars values per environment, outputs, locals, providers (with versions), backends, access assignments and static dependencies;
- the result is persisted as `IqrSourceSnapshot.EvidenceDomains.Infrastructure`.

The frontend receives the same shared contract, with no DTO mapping, and renders it in a dedicated 11-tab workspace.

**Where it is in the UI:** Source Review → Source Analysis → (area group "Source evidence") → **Infrastructure**.

**Why you could not find it.** The classification is **G (multiple)**. Nothing is lost between the backend and the UI.

1. **Data (main cause).** Neither project analyzed in the main database contains Terraform: `M2LB (2).zip` and `Tiltak 2 - Sikkert digitalt økosystem.zip` both store `Infrastructure.Status = NotDetected`. The M2LB Terraform lives in a separate repository archive, `Downloads/Terraform-iac-m2lb.zip`, which has never been analyzed in the main database. With no Terraform in any snapshot, the Infrastructure tab correctly says "No supported Infrastructure as Code files detected".
2. **E — non-obvious location.** The tab is labelled "Infrastructure", not "Infrastructure as Code" or "Terraform". It is the 5th of 8 area buttons inside Source Analysis. No navigation entry, search term or other page links to it.
3. **F — Technology Coverage gap.** Technology Coverage never lists Terraform as a detected technology.
   - Terraform providers are mapped to "Microsoft Azure" (`cloud.azure`).
   - The `InfrastructureAsCode` capability is computed but rendered nowhere.
   - The page reads only the single newest Source Analysis snapshot across all environments.
4. **Environment Analysis by design.** Environment Analysis shows declared IaC only inside "Declared vs observed". That view needs Azure sign-in, a Target Environment and a captured observed snapshot, and Azure sign-in is not configured (no app registration exists).

**Real-project acceptance.** `Terraform-iac-m2lb.zip` was analyzed with the current Debug build against an isolated database (`birknext_iac_audit`). The run took 4.7 s and produced:

- status **Partial**;
- 98 resources, 26 data sources and 29 modules (12 local and analyzed, 17 external AVM modules not fetched);
- 106 variables, 159 outputs, 69 locals, 163 static dependencies and 20 access assignments;
- 5 providers with versions, 7 `azurerm` backends, and environments dev/qa from tfvars;
- in Pipeline Review: 24 infrastructure-plan steps, 2 apply steps and 16 infrastructure deployments.

**Recommendation.** Do not add a new page. Make the existing ownership discoverable:

- Technology Coverage shows "Terraform" as a detected IaC technology and links to the Source Analysis Infrastructure area.
- Source Analysis gets an `?area=` deep link, and the tab is labelled "Infrastructure as Code".

Fix the parser, diagnostic and Pipeline Review defects listed in §16 and §18. The scope is **Small–Medium**.

---

## 2. Current IaC architecture

```
ZIP upload (Source Analysis / Project Import)
  → IqrSourceArchiveReader.ReadDetailed          keeps .tf/.tfvars/.bicep/.bicepparam in Workspace.EvidenceFiles (memory only)
  → IqrSourceStore.AnalyzeValidatedAsync
      → SourceEvidenceAnalyzer.Analyze           registry: Infrastructure → Configuration → CI/CD → Contracts → CrossDomainLinker
          → SourceFileClassifier.Classify        role InfrastructureAsCode + technology "Terraform" / "Terraform variables" / …
          → InfrastructureAnalyzer (v2)          HclReader (static) → InfrastructureEvidence
          → ConfigurationAnalyzer                tfvars values also become Configuration entries (PendingConfiguration)
          → PipelineAnalyzer                     terraform init/plan/apply steps → InfrastructurePlan / InfrastructureDeploy
          → CrossDomainLinker                    PipelineDeploysInfrastructure, ConfigurationReferencesInfrastructureResource
      → TechnologyInventory.Detect               providers → cloud.azure/aws/gcp; formats → cloud.azure/kubernetes; capability InfrastructureAsCode
  → persisted: iqr_source_snapshots.EvidenceJson (EvidenceDomains + TechnologyCoverage)
  → GET api/source-analysis → frontend IqrSourceSnapshot (shared contract compiled into BirkNext.Web)
      → Source Analysis → Infrastructure → InfrastructureWorkspace (11 tabs)
```

**Consumers of `EvidenceDomains.Infrastructure`.** All of them read the stored evidence. None parses Terraform; this is guarded by `InfrastructureConsumerMigrationTests.No_consumer_parses_terraform_outside_source_analysis`.

| Consumer | What it reads | UI |
|---|---|---|
| Source Analysis → Infrastructure | Everything | `InfrastructureWorkspace.razor` |
| Source Analysis → Overview | Row metrics: resources, modules, access assignments, environments, technologies | `SourceEvidenceDomainsPresentation.Row` |
| Technology Coverage | Providers, Formats and Status → cloud technologies and an (unrendered) capability | `TechnologyCoverage.razor` |
| Pipeline Review | Resource directories become "Infrastructure in {dir}" areas for trigger coverage; terraform steps from CI/CD | `/pipeline-review` |
| Environment Analysis (Azure) | `DeclaredObservedComparer.Compare(Infrastructure, …)` | "Declared vs observed" tab |
| Integration Quality Review | `SourceEvidenceQueries` network/messaging/observability slices; `IqrInfrastructureComparison`; `SourceInfrastructure` evidence source | IQR source domains and runtime-evidence form hints |
| Event Hub runtime form | `GET api/source-analysis/infrastructure-suggestions` | `SourceInfrastructureHint` |

---

## 3. Terraform detection

**File classification** (`SourceFileClassifier.Classify`, `SourceEvidenceContext.cs:85-155`):

| Pattern | Role / technology | Parsed | Evidence |
|---|---|---|---|
| `*.tf` | InfrastructureAsCode / "Terraform" (`:89`) | Yes (HclReader) | Yes |
| `*.tfvars` | InfrastructureAsCode / "Terraform variables" (`:90`) | Yes (HclReader) | Yes, per environment |
| `*.tf.json` | **Not recognized.** It falls through to `ClassifyJson` and becomes Unknown/"json" or Application configuration. | No | No (gap) |
| `*.auto.tfvars` | `.tfvars` extension, so recognized | Yes | Yes |
| `.terraform.lock.hcl`, `*.hcl` (tflint, terragrunt, backend config) | **Not kept.** `.hcl` is not in `IsEvidenceFile`, so these are Unknown. | No | No. Lock files do carry exact provider versions (gap). |
| `*.bicep`, `*.bicepparam` | InfrastructureAsCode / "Bicep" (`:91`) | Pattern (Partial) | Yes |
| ARM (`$schema …deploymentTemplate`) | InfrastructureAsCode / "ARM template" (`:117`) | JSON, top-level only (Partial) | Yes |
| `azuredeploy.json` / `template.json` | Recognized only through the `$schema` content check, not by name | — | — |
| `Chart.yaml` | Helm chart (`:140`) | Metadata only | Module entry |
| Helm `templates/*.yaml` with `{{` | Helm template (`:141`) | Not rendered | Diagnostic count |
| YAML with `apiVersion` and `kind` | Kubernetes manifest (`:142`) | MiniYaml (Partial) | Yes |
| `values*.yaml` | Configuration / "Helm values" (`:143`) | Configuration domain | — |
| `docker-compose*.yml` | Configuration / "Docker Compose" (`:138`) | Configuration and Architecture | — |
| `Dockerfile` | ProjectMetadata (`:83`) | Technology inventory | — |
| `Pulumi.*.yaml` | InfrastructureAsCode / "Pulumi" (`:152`) | Unsupported (detected only) | Diagnostic |
| CloudFormation / CDK | No classifier | — | Only a capability text says "Unsupported" |

**Archive reader.**

- `IqrSourceArchiveReader.IsEvidenceFile` (`IqrSourceArchiveReader.cs:85-92`) keeps `.tf`, `.tfvars`, `.bicep` and `.bicepparam` in memory.
- The ignored folders are `bin`, `obj`, `node_modules`, `packages`, `.git`, `.vs`, `coverage` and `TestResults` (`:34-35`). A committed `.terraform/` provider cache is **not** ignored: cached module `.tf` files would be read as source.
- The guard test `Terraform_state_files_are_never_read` checks that `*.tfstate` is never read.

## 4. Terraform parsing

**`HclReader`** (`Services/SourceAnalysis/Evidence/HclReader.cs`) is a syntax-aware recursive-descent reader. It is not regex-based.

**What it handles.** Blocks with labels; attributes with raw expression text and line numbers; nested blocks; strings with `${}` interpolation; heredocs; and `#`, `//` and `/* */` comments.

**What it never does.** It evaluates nothing. `Literal()` resolves only plain strings, numbers and bools (`:181-188`).

**Errors.** Parse errors are collected and become `Diagnostics` of kind "Parse error". They are distinct from "not found", and a parse error makes the status Partial, not Failed.

| Question | Answer |
|---|---|
| Distinguishes resource/data/module/provider/terraform/variable/output/locals | Yes (`InfrastructureAnalyzer.cs:213-325`) |
| Nested blocks | Yes (`HclBlock.Blocks`, `AllAttributes` flattened as `block.attr`) |
| Resolves variable references | Literals only: `var.x`, `local.x` and `"${var.x}-…"` up to depth 3; sensitive defaults are never used (`:176-194`) |
| Resolves module references | Yes for local modules (`./`, `../`), including module-wiring dependencies (`:253-281`). External/registry modules are never fetched. |
| Follows local modules | Yes. Module directories are scanned as module scopes, and resources carry `ModulePath`. |
| Understands .tfvars | Yes. Values bind per environment (from the file name or folder) to root-module variables, and resolve per-environment resource names (`:329-364`). |
| Multiple environments | Yes. `Environments` come from tfvars file names, folder names and Bicep parameter files. |
| Evaluates expressions | **No, intentionally.** count/for_each are not expanded, functions are not called, and there is no state, plan or provider call. |
| Malformed or incomplete HCL | Safe: errors are collected, reading continues, and the status becomes Partial. |
| Parse errors vs not found | Distinct ("Parse error" diagnostic vs `NotDetected`). |

**Defect found on the real project.** A heredoc nested inside a bracketed expression ends the whole expression.

- In `HclReader.Expression()` (`HclReader.cs:150-166`), after reading a heredoc the code does `break` regardless of bracket depth.
- So `queries = { x = { body = <<-KQL … KQL } }` and `jsonencode({ … <<EOT … })` leave the remaining `}` and `,` to be parsed as body. That produces spurious "Parse error" diagnostics.
- Observed: 16 parse errors in `query_pack.tf` and three `workbook_*.tf` files.
- Some declarations in those files may be missed.

**Not extracted:**

- `required_version` (the Terraform core version constraint);
- `.terraform.lock.hcl` exact provider versions;
- `moved`, `import` and `check` blocks;
- `dynamic` blocks, which are seen only as nested blocks.

**Diagnostic text is garbled.** Parse-error messages pass through `SafeLabel`, which replaces quotes with `_`, so they read "Unexpected ___ at line 59". The message is cosmetic but does not help.

## 5. Source Analysis evidence

`InfrastructureEvidence` (`shared/SourceEvidenceDomainContracts.cs`, around lines 72-200) is produced by `InfrastructureAnalyzer.Analyze` (`:56-110`).

| Fact | Extracted | Where |
|---|---|---|
| Provider names, source, version constraint | Yes, from `required_providers` | `:311-319` |
| Provider credential settings (presence only) | Yes, as a flag and diagnostic; values are not read | `:301-309` |
| Backend type and setting **names** | Yes, including the Terraform Cloud `cloud` block | `:320-323` |
| Resource types, logical names, declared names (resolved literal) | Yes | `:215-251` |
| Provider-neutral category and detail | Yes (`InfrastructureCatalog`) | `InfrastructureCatalog.cs` |
| Data sources | Yes (`Kind = "data"`) | `:215` |
| Modules: source, version, local/analyzed, input names | Yes | `:253-281` |
| Variables: type, sensitive, default state, safe default | Yes | `:282-293` |
| tfvars values per environment (safe literals only) | Yes | `:329-364` |
| Outputs: name, sensitive, referenced ids | Yes | `:297-300` |
| Locals (names only) | Yes. Shown only as a count elsewhere; not rendered in the UI. | `:294-296` |
| Static dependencies (reference, depends_on, module-input, module-wiring) | Yes | `:239-249, 269-279` |
| Access assignments (azurerm role/KV policy, AWS IAM, GCP IAM, k8s bindings) | Yes | `:428-445` |
| Security-relevant settings (identity, network, TLS, public access, diagnostics…) | Yes, with redaction | `:382-426` |
| Remote-state references | Recorded generically as a `terraform_remote_state` data source. A **false "Unsupported provider 'terraform'" diagnostic** is raised for each. | `:221-222`; `InfrastructureCatalog.cs:87` |
| Terraform version constraint (`required_version`) | **No** | — |
| Workspaces (`terraform.workspace`) | No (only a reference prefix is skipped) | — |

## 6. Shared evidence provider

- **Storage.** The evidence is stored in `IqrSourceSnapshot.EvidenceDomains.Infrastructure`, inside `iqr_source_snapshots.EvidenceJson`. It is written once at upload (`IqrSourceStore.cs:101`) and never re-analyzed.
- **Consumer API.** `SourceEvidenceQueries` (`Services/SourceAnalysis/SourceEvidenceQueries.cs:34-50`) offers `Infrastructure(categories)`, `MessagingInfrastructure`, `ObservabilityInfrastructure`, `NetworkInfrastructure`, `IdentityInfrastructure` and `SecuritySettings`. Each slice carries provenance: snapshot id, fingerprint and analyzer version.
- **Snapshot provider.** `ReviewSourceEvidenceProvider` resolves Source Analysis snapshots for review scopes.
- **Provenance fields.** `SourceSnapshotId`, `SourceFingerprint`, `AnalyzerVersion` and `ExtractedAt` (set by the envelope, `SourceEvidenceContext.cs:69-72`); `File` and `Line` on every item; `EvidenceState` (Confirmed, StronglySupported …); `RuntimeState` ("declared, not verified"); `Limitations`; `Diagnostics`; and the per-format `Capabilities`.
- **Frontend contract.** The frontend compiles the same contract files (`BirkNext.Web.csproj:61-64`). There is no frontend DTO and nothing is dropped.

**Verdict.** The evidence is normalized into the shared model, it is not dropped, and it has several consumers. **Evidence is not lost anywhere.**

## 7. Technology Coverage

- **Route and nav.** `/technology-coverage`, under "Source Review → Technology Coverage" (`NavigationCatalog.cs:118`).
- **Snapshot read.** `TechnologyCoverageController.Get` (`:22`) reads `ListSourceAnalysisAsync(1)`: the single newest Source Analysis snapshot for the whole workspace. It is not per repository and not per environment.
- **Technology mapping.** In `TechnologyInventory.Detect` (`:135-146`):
  - Terraform providers map to `cloud.azure` (azurerm/azuread/azapi), `cloud.aws`, `cloud.gcp` or `cloud.kubernetes`;
  - formats map to `cloud.azure` (Bicep/ARM) or `cloud.kubernetes`;
  - **there is no `Terraform` technology id**.
  - `source.iac.terraform` exists only as an *analysis provider* (`TechnologyCoverageContracts.cs:73-76`). It appears in the "Providers" column of the collapsed "Full support matrix", which is static and shown for every project.
- **Capability.** `Capability.InfrastructureAsCode` is added when Infrastructure is Complete or Partial (`TechnologyInventory.cs:176`). No review requires it, and `TechnologyCoverage.razor` renders no capabilities.
- **Real project.** `Terraform-iac-m2lb.zip` shows 12 technologies: C#, Python, ASP.NET Core, Event Hubs, Kafka, Service Bus, SQL Server, Container images, NuGet, Azure Pipelines, **Microsoft Azure (39 files)** and TRX. **"Terraform" is not in the list.**

**Gap.** A project with `.tf` files should visibly show **Terraform** under Detected technologies (area Cloud, or a new Infrastructure area). The row should have Source = Full, with the limitation text already in the registry, and link to Source Analysis → Infrastructure. Today the backend never emits it, so this is not a frontend filter.

## 8. Environment Analysis

- **Route and nav.** `/azure-environment`, labelled "Environment Analysis" (`NavigationCatalog.cs:123-124`). It is behind the `FeatureVisibility:AzureEnvironmentAnalysis` flag (default true).
- **Responsibility (as implemented and documented).** A read-only **observed** Azure inventory: resources, topology, access, observations and target suggestions. It needs MSAL sign-in, a public-client app registration (none exists yet) and a Target Environment.
- **Declared IaC.** The page shows declared IaC only in the **"Declared vs observed"** tab:
  - `RenderDeclared`, `AzureEnvironmentAnalysis.razor:501-560`;
  - `GET api/azure-environment/comparison`, `AzureEnvironmentController.cs:73-90`;
  - `DeclaredObservedComparer`.
  - The tab exists only inside a captured observed snapshot (`RenderSnapshot`, `:306-331`).
- **Ownership.** Without a target, the page explicitly says that declared infrastructure "is in Source Analysis (Source evidence) without a target" (`:81`). Ownership is deliberate: Source Analysis owns declared IaC, and Environment Analysis owns observed runtime and the declared-vs-observed comparison.
- **Out of scope here.** App settings, slots, network, managed identity and Service Bus/Event Hub/Storage *runtime* configuration are observed Azure facts on this page. The corresponding *declared* settings are in Source Analysis → Infrastructure (Settings and Identity & RBAC tabs).

**Verdict.** Do not move IaC ownership here because of the page name. Environment Analysis is the runtime counterpart.

## 9. Pipeline Review

**`PipelineAnalyzer`** (CI/CD evidence v2) detects:

- Script regexes:
  - `terraform (-chdir=…)? init|validate|plan|fmt` → `InfrastructurePlan`, tool "terraform init/plan" (`PipelineAnalyzer.cs:44`);
  - `terraform apply|destroy` → `InfrastructureDeploy` (`:45`);
  - `az deployment … create|what-if`, `az bicep build` and `New-AzDeployment` → `InfrastructureDeploy` (`:46`).
- Azure DevOps tasks:
  - `TerraformTaskV*`, `TerraformCLI` and `TerraformTask*` with the `command` input (`:391-395`);
  - `TerraformInstaller` (`:67`);
  - `AzureResourceManagerTemplateDeployment` and `AzureResourceGroupDeployment` (`:68`).
- GitHub Actions: `hashicorp/setup-terraform` and `azure/arm-deploy` (`:90-91`).
- Targets: `-chdir` and the working directory, plus `-var-file` tfvars (`TerraformTargets`, `:442`).

**Pipeline Review** (`/pipeline-review`, `PipelineReviewBuilder`) uses this evidence for:

- infrastructure deployments ("Deploys: Infrastructure");
- `InfrastructureSequenceGap` (infrastructure before application; apply without a preceding plan, `:842-849`);
- IaC trigger coverage per "Infrastructure in {dir}" area (`:919-930`; areas from `Infrastructure.Resources`, `:564`);
- the story text "plans infrastructure";
- the CrossDomainLinker link `PipelineDeploysInfrastructure`.

**Real project.** 13 pipelines and 3 templates; 24 InfrastructurePlan steps and 2 apply steps; 16 infrastructure deployments; 3 InfrastructureSequenceGap findings.

**Semantic defects:**

1. **Init, fmt and validate count as "plan".** All of `init`, `validate`, `fmt`, `plan -refresh-only` (drift check) and even `TerraformInstaller` ("terraform install") become `InfrastructurePlan`. The plan-before-apply check (`:843`) accepts any `InfrastructurePlan` step whose tool starts with `terraform`, so `terraform init` followed by `terraform apply` with no plan passes.
2. **False positive on M2LB.** The finding "terraform apply in DEV/QA without a preceding plan" is reported as **Confirmed**, but the real pipeline does run a plan:
   - `plan-layer.yml` produces a tfplan artifact;
   - `apply-layer.yml` downloads and applies it in a job with `dependsOn: ['plan_<layer>']`.
   - The job names are template expressions (`plan_${{ replace(parameters.layerName,'-','_') }}`), which are not resolved, so the dependency on the plan job is not matched.
   - In addition, `terraform init + apply` is one script step, so the init and the apply share one `Order`.
   - When a job name or dependency is an unresolved expression, the result must be *not assessable*, not *Confirmed missing*.
3. **Root-level IaC is not tracked as an area.** Infrastructure in the repository root gives an empty path, which is filtered out (`.Where(a => a.Path.Length > 0)`, `:566`).

Distinctions the review already keeps: Terraform source exists ≠ used in CI/CD (it reports "No pipeline in this snapshot plans or deploys …" as Info, `:923-926`); a plan step ≠ apply; apply configured ≠ succeeded (run history comes only from the optional Azure DevOps metadata).

## 10. Current navigation (as implemented)

From `NavigationCatalog.cs:114-124`:

```
Source Review
  Source Analysis        /source-analysis          (flag SourceAnalysis)
  Technology Coverage    /technology-coverage      (always)
  Dependency Review      /dependency-review        (always)
  Pipeline Review        /pipeline-review          (always)
  Environment Analysis   /azure-environment        (flag AzureEnvironmentAnalysis; hint "read-only cloud environment inventory (provider: Azure)")
```

**Inside Source Analysis.** The area groups are defined in `DocumentationCatalog.cs:60-61` and rendered at `SourceAnalysis.razor:149-160`:

- "Core analysis": Overview, Architecture, Database, Observability;
- "Source evidence": **Infrastructure**, Configuration, CI/CD, Contracts.

**Search results.** There is no page, route, nav entry, hidden tab or orphan component named "Terraform", "IaC" or "Infrastructure as Code". The only IaC UI is:

- `Components/SourceDomains/InfrastructureWorkspace.razor`, rendered at `SourceAnalysis.razor:274` when `_area == "Infrastructure"`;
- the Overview "Source evidence" row (`SourceEvidenceDomainsPresentation.Row`, `:55-75`), with an "Open Infrastructure" button (`sa-open-infra`);
- `SourceInfrastructureHint` inside IQR's Event Hub runtime-evidence form;
- the Environment Analysis "Declared vs observed" tab.

**Visibility conditions for the Infrastructure workspace:**

1. A Source Analysis snapshot is selected. By default this is the newest (`SourceAnalysis.razor:359`); the user must pick the IaC repository's snapshot if several exist.
2. `selected.EvidenceDomains` is not null; older snapshots show `evidence-missing`.
3. Tabs appear only when Status is Complete or Partial. When it is NotDetected, the page shows "No supported Infrastructure as Code files detected."
4. There is **no deep link**: `?area=` is not supported. Only `?returnTo=` is.

**Word "Terraform" in the UI.** It appears only in:

- the provenance line ("· Terraform", via `SourceDomainHeader`);
- the Overview row's technologies;
- the empty-state sentence;
- the limitations and capabilities list.

## 11. End-to-end evidence trace

**Traced file:** `terraform/layers/05-integration/event_hubs.tf` in `Terraform-iac-m2lb.zip`.

| Boundary | Present? | Transformed / lost? |
|---|---|---|
| Archive import (`IqrSourceArchiveReader.IsEvidenceFile`) | Yes; `.tf` is kept in `EvidenceFiles` (memory only) | — |
| Classification (`SourceFileClassifier.Classify:89`) | Yes, InfrastructureAsCode / "Terraform" | — |
| Parser (`HclReader.Parse`) | Yes | Expressions are kept raw in memory, never stored |
| Analyzer (`InfrastructureAnalyzer.Terraform`) | Yes. The module block `event_hubs` (source `Azure/avm-res-eventhub-namespace/azurerm ~> 0.1.0`) is external, not fetched, `Analyzed=false`; the hubs are `azurerm_eventhub` (category Messaging). | Values are redacted via SafeLiteral |
| Normalized evidence (`EvidenceDomains.Infrastructure`) | Yes. 11 Messaging resources; `Technologies=["Terraform"]`; Status Partial (external modules, parse errors). | — |
| Persistence (`iqr_source_snapshots.EvidenceJson`) | Yes (511 KB) | — |
| Shared provider (`SourceEvidenceQueries.MessagingInfrastructure`) | Yes | — |
| Review consumers | IQR (messaging domain, Event Hub hints), Pipeline Review (area `terraform/layers/05-integration`), Technology Coverage (→ "Microsoft Azure") | **Terraform identity is reduced to "Microsoft Azure" in Technology Coverage** |
| Frontend DTO | Same shared record (`IqrSourceSnapshot`) | No loss |
| UI | Source Analysis → Infrastructure → Messaging / Resources / Evidence tabs | Visible, once this snapshot is analyzed and selected |

The precise UI gap: **not wired into Technology Coverage, and not discoverable from navigation.** It is not a missing backend, provider or DTO.

## 12. Real-project result

| Archive | Where analyzed | `.tf` / `.tfvars` | Infrastructure status |
|---|---|---|---|
| `M2LB (2).zip` (main DB `birknext`, 2026-10-12) | Main | 0 / 0 (also verified with `unzip -l` on all 7 local M2LB archives) | NotDetected (correct) |
| `Tiltak 2 - Sikkert digitalt økosystem.zip` (main DB) | Main | 0 / 0 | NotDetected (correct) |
| `Terraform-iac-m2lb.zip` (Downloads, 240 entries) | **Never in the main DB.** Analyzed for this audit in the isolated DB `birknext_iac_audit` with the Debug build on :5097, then stopped. | 121 / 2 | **Partial** |

`Terraform-iac-m2lb.zip` facts (from the stored snapshot `f151103b-…`):

- **Layers:** 7 (`00-identity` … `05-integration`).
- **Providers:**
  - azuread `hashicorp/azuread ~> 3.0`
  - azurerm `hashicorp/azurerm ~> 4.0`
  - null `~> 3.2`
  - random `~> 3.6`
  - time `~> 0.13`
- **Backends:** 7 × `azurerm`; only the setting names `use_azuread_auth` are recorded.
- **Resources:** 98 resources and 26 data sources, of 36 resource types. The top types are:

  | Resource type | Count |
  |---|---|
  | `azurerm_role_assignment` | 20 |
  | `azuread_group` | 7 |
  | `azurerm_mssql_database` | 7 |
  | `azurerm_eventhub` | 6 |
  | `azurerm_resource_group` | 5 |
  | `azuread_app_role_assignment` | 5 |
  | `azurerm_application_insights_workbook` | 4 |
  | `azurerm_private_endpoint` | 3 |
  | `azurerm_container_app` | 2 |
  | `azurerm_user_assigned_identity` | 2 |

- **Categories:**

  | Category | Count |
  |---|---|
  | Identity | 23 |
  | AccessControl | 20 |
  | Other | 14 |
  | Messaging | 11 |
  | Database | 7 |
  | Observability | 6 |
  | Networking | 5 |
  | ResourceContainer | 5 |
  | Compute | 3 |
  | ContainerRegistry | 2 |
  | Dns | 1 |
  | SecretStore | 1 |

  "Other" includes `azurerm_managed_redis` (catalog gap), `azuread_app_role_assignment` (arguably AccessControl), `terraform_remote_state` and the utility providers.
- **Modules:** 29. 12 are local and analyzed (`./modules/audience-app-reg`, `./modules/frontend-app-reg`, `./modules/container-app`). 17 are external Azure Verified Modules with version constraints, not fetched.
- **Other declarations:** 106 variables (15 redacted defaults or values), 159 outputs, 69 locals, 163 static dependencies, 20 access assignments and 0 unresolved references.
- **Environments:** dev and qa (basis: tfvars file name). 4 resources have per-environment declared names.
- **Diagnostics:** 16 parse errors (the nested-heredoc defect, §4) and 16 "Unsupported provider 'terraform'" (`terraform_remote_state`, a false diagnostic).
- **Configuration domain:** 98 entries from tfvars (Text 45, Number 16, Empty 15, Boolean 14, Identifier 8; sensitivity None for all).
- **CI/CD:** 13 pipelines and 3 templates (plan-layer, apply-layer, drift-check-layer).
- **Technology Coverage:** "Microsoft Azure (39 files)". **There is no Terraform row.**
- **Pipeline Review:**
  - 33 findings: TestGatingGap 14, PostDeployValidationGap 15, InfrastructureSequenceGap 3, SecurityValidationGap 1.
  - 16 infrastructure deployments.
  - The two "apply without plan" findings are false positives (§9).
- **Environment Analysis:** not exercised. There is no Azure sign-in and no observed snapshot. Declared-vs-observed would compare these 98 resources.

**Environment Analysis.** No IaC facts are shown without an observed Azure snapshot.

**Isolated DB.** It is kept for inspection. To drop it: `podman exec aiassisted_postgres_1 psql -U birknext -d postgres -c "drop database birknext_iac_audit"`.

## 13. Existing tests

**Backend** (`BirkNext.Api.Tests/Services/SourceAnalysis/Evidence/InfrastructureEvidenceTests.cs`, 10 tests):

- `Terraform_resources_are_read_with_provider_neutral_categories_and_resolved_names`
- `References_depends_on_and_module_wiring_are_static_dependencies`
- `Local_modules_are_analyzed_and_external_modules_are_never_fetched`
- `Identity_network_and_security_settings_are_declarations_not_effective_state`
- `Variables_outputs_providers_and_backend_never_expose_secret_values`
- `Tfvars_bind_to_the_root_module_per_environment_and_redact_sensitive_values`
- `A_non_Azure_fixture_works_and_an_unknown_provider_is_reported_not_guessed`
- `Bicep_is_partial_and_only_unsupported_formats_are_unsupported_not_failed`
- `No_iac_is_not_detected_never_failed`
- `Hcl_reader_handles_heredocs_comments_and_nested_interpolation_without_evaluating`

**Backend, `InfrastructureConsumerMigrationTests`:**

- `Terraform_state_files_are_never_read`
- `No_consumer_parses_terraform_outside_source_analysis`
- consumer/suggestion tests (8 in total)

**Other backend tests:**

- `PipelineEvidenceTests.Azure_pipeline_triggers_stages_steps_and_environments_are_extracted` and `.GitHub_actions_with_oidc_paths_and_actions_are_recognized`: terraform steps and setup-terraform.
- `PipelineReviewTests.Infrastructure_before_application_is_confirmed_and_the_reverse_or_apply_without_plan_needs_review`: the fixture `PipelineReviewFixtures.TerraformTemplate` has init → plan → apply in one job.
- `SourceEvidenceProviderAndConsumerTests.Ingestion_binds_every_domain_to_the_same_snapshot_and_never_persists_secrets`
- `ConfigurationEvidenceTests.Secrets_connection_strings_and_secret_references_are_classified_never_stored`: covers tfvars entries.
- `DeclaredObservedComparisonTests.Declared_and_observed_resources_match_on_kind_and_environment_name`
- `ServiceBusEvidenceTests`, `IqrSourceEvidenceTests`, `ObservedEvidenceConsumerTests`
- `TechnologyIndependenceTests` (`cloud.azure` detection; **no Terraform technology assertion**).

**Frontend:**

- `SourceEvidenceDomainsUiTests.Infrastructure_page_is_summary_first_with_filterable_resources_and_expandable_settings`
- `SourceEvidenceDomainsUiTests.No_iac_is_reported_as_not_detected_without_tabs_or_zero_counts`
- `SourceInfrastructureHintTests`
- `AzureEnvironmentAnalysisPageTests` (declared tab)
- `IntegrationsPaneTests.NoSecretOrTerraformIsRendered`

**Missing tests:**

- a heredoc nested in an object, list or function call (the real-project defect);
- `.tf.json`, `.terraform.lock.hcl` and `required_version`;
- a `terraform_remote_state` data source not reported as an unsupported provider;
- Technology Coverage showing Terraform for a project with `.tf` files (backend inventory and page);
- `terraform init` + `apply` without `plan` producing a finding;
- an unresolved template expression in a job name or `dependsOn` giving "not assessable";
- many `.tf` files across many layers (a scale fixture);
- Source Analysis deep link to the Infrastructure area;
- a policy test for identifier and IP literals in tfvars (§14).

## 14. Security / secret handling

**What is parsed.** Full `.tf` and `.tfvars` text, in memory only (`Workspace.EvidenceFiles`). Raw expressions are never persisted. State files are never read (guard test).

**What is persisted and displayed:**

- structure (names, types, files, lines);
- *safe literals* only, via `SourceEvidenceRedaction.SafeLiteral`;
- provider credential settings and backend settings as **names only**;
- `app_settings` keys, with sensitive ones shown as "[sensitive — value not shown]".

**Redacted:**

- variables marked `sensitive = true`;
- secret-shaped names (password, secret, token, key, sas, connection string, certificate, credentials, username …);
- secret-shaped values (PEM, JWT, credential `key=value`, long opaque tokens);
- connection-string-shaped values.

**Real-project scan.** The persisted snapshot has no password, client secret, account key, SAS or private key. 15 variable values were redacted, and 8 resource settings were masked.

**Finding (policy, Medium). Non-secret but sensitive identifiers are persisted and displayed in plain text.** These come from tfvars literals:

- tenant id;
- subscription id (the hub subscription);
- Entra object ids (service principal, group);
- a frontend client id;
- internal IP addresses and CIDR ranges (spoke subnets, hub firewall private IP, on-premises routes).

They appear in Source Analysis → Infrastructure → Variables and in the Configuration domain. Redaction is also **inconsistent**: the same key is shown for dev and "redacted" for qa (`dev_owner_pag_object_id`, `birk_route_prefix`).

These are not credentials, but they reveal tenant and network topology. Decide whether to apply one of these policies:

- (a) keep them (useful for declared-vs-observed matching);
- (b) mask GUIDs and private IPs by default and keep a hash or "present" marker;
- (c) mask only in display.

Variable values are not needed for structural IaC review. Matching needs them only for resource *names*, not ids or IPs.

**Other security observations:**

- **Backend keys** (`storage_account_name`, `key`) are names-only, and the backend values are not read. Good.
- **Provider credentials** are flagged by presence; values are not read. Good.
- **Sensitive outputs** keep their name and the sensitive flag, plus the referenced ids only.
- **`.terraform/` folders** are not in the ignored list. A committed provider or module cache would be analyzed as source. This is low risk but should be ignored.
- **Garbled diagnostics.** Redacted parse-error messages are unreadable but leak nothing.

## 15. Semantic quality

| Rule | Current UI/result | Verdict |
|---|---|---|
| Terraform detected ≠ deployed | The boundary note on every page; `RuntimeState = "Declared in source — not verified"` | OK |
| Resource declared ≠ exists | "A declaration does not prove the resource exists" | OK |
| Provider configured ≠ auth works | "credential settings declared (values not read)" | OK |
| Pipeline contains apply ≠ succeeded | Pipeline Review story: "deploys/plans"; run history only with ADO metadata | OK |
| IaC source ≠ runtime Azure evidence | Separate pages; "Declared vs observed" comparison | OK |
| Not evaluated ≠ failed | NotDetected/Unsupported/Partial; "This is not a failure" | OK |
| Unavailable ≠ zero | `Assessed()` hides counts when not assessed | OK |
| Configuration ≠ runtime | OK | OK |
| **Plan ≠ init/validate/fmt** | All are mapped to `InfrastructurePlan` and labelled "terraform init/plan" | **Violation** (§9) |
| **Unresolved ≠ missing** | "apply without a preceding plan" is Confirmed although the job and dependency names are unresolved template expressions | **Violation** (§9) |
| **Partial reason accuracy** | Partial is partly caused by false "Unsupported provider 'terraform'" diagnostics and nested-heredoc parse errors | **Misleading** (§4, §5) |
| Technology Coverage "Detection is not analysis" | Terraform is analyzed (Full) but shown as "Microsoft Azure", whose limitations text describes runtime ARM | **Misleading** (§7) |

## 16. Root cause of "I cannot find it in the UI"

**Classification: G — multiple problems.** None of them is evidence loss (not B, C or D).

1. **The data was never analyzed (primary).** No analyzed snapshot contains Terraform.
   - In the main DB, `iqr_source_snapshots` holds `M2LB (2).zip` → `Infrastructure.status = NotDetected`, and `Tiltak 2…zip` → NotDetected.
   - The Terraform repository (`Terraform-iac-m2lb.zip`) has never been uploaded. Path: `IqrSourceStore.AnalyzeValidatedAsync` → `InfrastructureAnalyzer.Analyze:67-75` (`files.Count == 0` → NotDetected).
2. **E — non-obvious location.** The only detailed IaC view is `SourceAnalysis.razor:269-274` (`_area == "Infrastructure"`), reached through a button labelled "Infrastructure" in the "Source evidence" group (`DocumentationCatalog.cs:61`). There is no nav entry, no "Terraform" or "Infrastructure as Code" label, and no `?area=` deep link.
3. **F — partial wiring in Technology Coverage.**
   - `TechnologyInventory.Detect:135-146` maps providers and formats to cloud technologies only, with no `iac.terraform` technology.
   - `TechnologySupportRegistry` has no IaC technology descriptors (only the analysis providers `source.iac.*`).
   - `TechnologyInventory.cs:176` adds the IaC capability, which `TechnologyCoverage.razor` never renders.
   - `TechnologyCoverageController.cs:22` reads only the newest snapshot. If the IaC repository is analyzed before an application repository, Technology Coverage stops reflecting it.
4. **Environment Analysis gating (by design).** Declared IaC appears only in "Declared vs observed" (`AzureEnvironmentAnalysis.razor:149, 306-331, 501`). That needs Azure sign-in, a Target Environment and an observed snapshot, and Azure sign-in is not configured.

## 17. Recommended UX

| Option | Fit with architecture | Duplication risk | Discoverability | Size | Ownership |
|---|---|---|---|---|---|
| A. IaC inside Source Analysis | Already implemented (Infrastructure area) | None | Low today | — | Correct (Source Analysis owns declarations) |
| B. Technology Coverage shows IaC technologies and Environment Analysis shows detailed resources | Technology Coverage part fits. Moving detail into Environment Analysis contradicts its observed-runtime role and gating. | High (detail twice) | Medium | Medium | Breaks the declared/observed split |
| C. IaC section in Environment Analysis | Would put declared evidence behind Azure sign-in and target gating | High | Low without Azure | Medium | Wrong owner |
| D. New Infrastructure as Code Review page | No review semantics exist beyond what Source Analysis, Pipeline Review and IQR already do | High | High | Large | Duplicates Source Analysis |

**Recommendation: A + the Technology Coverage half of B.**

1. **Source Analysis** stays the owner of detailed IaC evidence (providers, modules, resources, variables, outputs, backends, environments). Changes:
   - Rename the area button to **"Infrastructure as Code"**, or add the subtitle "Terraform · Bicep · ARM · Kubernetes · Helm".
   - Add a `?area=infrastructure` (generic `?area=`) deep link.
   - Show locals and environment basis in the Variables/Evidence tabs; they are already in the evidence.
2. **Technology Coverage** shows **Terraform** (and Bicep/ARM and Kubernetes/Helm) as *detected technologies* with Source support levels from the registry. Each row links to `source-analysis?area=infrastructure`.
3. **Pipeline Review** keeps owning Terraform *usage in CI/CD* (plan/apply, sequencing, trigger coverage), after the semantic fixes.
4. **Environment Analysis** keeps owning *observed* Azure and the declared-vs-observed comparison. Add a link to Source Analysis → Infrastructure; the no-target text already names it.
5. **No new page.**

**Process note.** Analyze `Terraform-iac-m2lb.zip` through Source Analysis or Project Import in the main workspace, then select that snapshot in Source Analysis.

## 18. Recommended implementation plan (not implemented)

**Backend:**

1. `TechnologyInventory.Detect`: from `domains.Infrastructure.Formats`, emit `iac.terraform`, `iac.bicep-arm` and `iac.kubernetes-helm` with Confirmed confidence and evidence files taken from resource/module `File`s. Keep the existing `cloud.*` mapping. No new scanner is needed: it reuses the stored evidence.
2. `HclReader.Expression`: after a heredoc, continue scanning when `depth > 0` instead of `break` (`HclReader.cs:165`).
3. `InfrastructureCatalog`:
   - add `terraform` as a built-in provider, so `terraform_remote_state` produces no Unsupported diagnostic;
   - optionally model remote-state references as a dependency kind;
   - add a category rule for `azurerm_managed_redis*`;
   - map `azuread_app_role_assignment` to AccessControl.
4. `InfrastructureAnalyzer`:
   - record `required_version` as a `TerraformVersionConstraint` (additive field);
   - optionally read `.terraform.lock.hcl` provider versions. This needs `.hcl` added to `IsEvidenceFile` and the classifier, scoped to `.terraform.lock.hcl` only.
5. `SourceFileClassifier`: recognize `*.tf.json` as Terraform (JSON). It can be Partial: record the file and count, and parse later.
6. `IqrSourceArchiveReader.Ignored`: add `.terraform`.
7. `PipelineAnalyzer` / `PipelineReviewBuilder`:
   - split `init`, `validate` and `fmt` (tool "terraform init/validate/fmt", kind Validation or InfrastructurePlan with a distinct tool) from `plan`;
   - make the plan-before-apply check require a tool that is `terraform plan`;
   - mark findings whose job or dependsOn identities contain unresolved `${{ }}` as `Unresolved` / not assessable.
8. Bump the analyzer versions (Infrastructure v3, CI/CD v3). Old snapshots keep their evidence, as the existing rule requires.

**Shared contracts** (additive only):

- `TechnologySupportRegistry` descriptors for `iac.terraform`, `iac.bicep-arm` and `iac.kubernetes-helm` (area Cloud, or a new `TechnologyArea.Infrastructure`, which needs presentation `Area()` labels);
- `InfrastructureEvidence.TerraformVersionConstraints`;
- a `ReviewCatalog` or technology row link target.

**Frontend:**

- `SourceAnalysis.razor`: `[SupplyParameterFromQuery] area`, plus the label "Infrastructure as Code" (`DocumentationCatalog.SourceAnalysisGroups`; keep the test id `area-infrastructure`).
- `TechnologyCoverage.razor`: a link from IaC technology rows to `source-analysis?area=infrastructure`; optionally render capabilities.
- `InfrastructureWorkspace.razor`: show locals and environment basis, and the Terraform version constraint in the Evidence tab.
- `AzureEnvironmentAnalysis.razor`: a link to the Source Analysis Infrastructure area in the Declared tab.

**Tests:** the missing tests listed in §13, plus regenerated `pipeline-review-shop*.json` fixtures.

**Real-project acceptance:**

- Re-analyze `Terraform-iac-m2lb.zip` in an isolated DB.
- Expect 0 "Unsupported provider 'terraform'" diagnostics and 0 nested-heredoc parse errors.
- Expect a "Terraform" row in Technology Coverage.
- Expect no Confirmed "apply without plan" finding.

**Constraints:** no new scanner (everything extends `InfrastructureAnalyzer`, `HclReader` and `TechnologyInventory`), and no M2LB-specific code.

**Scope: Small–Medium.**

- The UI and Technology Coverage wiring is Small: about 5 files, all additive, with no new evidence.
- The parser, catalog and Pipeline Review semantic fixes are Medium: analyzer version bumps, fixture regeneration and the Pipeline Review rules version.
- The redaction policy change (§14) is a separate decision.

## 19. Risks

- **Analyzer version bumps.** Old snapshots keep v2 evidence. Users must re-analyze to see the fixes. The existing "outdated evidence" notices must cover Infrastructure.
- **Technology Coverage scope.** It shows the newest snapshot only. Multi-repository projects (application repository plus IaC repository) will still show only one of them. Per-project aggregation is a larger change and is out of scope.
- **Redaction policy.** Masking GUIDs or IPs could break declared-vs-observed and IQR name matching if it is applied to names. Apply it to values, not resource names.
- **Pipeline Review.** Changing the plan classification and unresolved-expression semantics changes finding counts on existing snapshots. The rules version must be bumped.
- **Parser change.** It must not regress `Hcl_reader_handles_heredocs_comments_and_nested_interpolation_without_evaluating`.

## 20. Exact code references

| Area | File:line |
|---|---|
| Classifier (IaC roles) | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/SourceEvidenceContext.cs:85-155` (`.tf` :89, `.tfvars` :90, Bicep :91, ARM :117, Helm :140-141, k8s :142, Pulumi :152) |
| Archive evidence files / ignored dirs | `backend/BirkNext.Api/Services/Integrations/SourceEvidence/IqrSourceArchiveReader.cs:34-35, 85-92` |
| Analyzer pipeline / persistence | `backend/BirkNext.Api/Services/Integrations/SourceEvidence/IqrSourceStore.cs:101, 112`; `Services/SourceAnalysis/Evidence/SourceEvidenceAnalyzer.cs:69-110` |
| Infrastructure analyzer | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/InfrastructureAnalyzer.cs:17-20, 56-110, 135-365, 367-373, 382-445` |
| HCL reader / heredoc defect | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/HclReader.cs:140-174` (break at :165), `:181-188` |
| Provider catalog | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/InfrastructureCatalog.cs:87-92` |
| Redaction | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/SourceEvidenceRedaction.cs:19-31, 79-84, 98-106` |
| Shared contracts | `shared/SourceEvidenceDomainContracts.cs` (InfrastructureFormat :72, step kinds :255, labels :735-736); `shared/SourceInfrastructureIdentityContracts.cs` |
| Shared query API | `backend/BirkNext.Api/Services/SourceAnalysis/SourceEvidenceQueries.cs:34-50` |
| Technology inventory | `backend/BirkNext.Api/Services/SourceAnalysis/Technology/TechnologyInventory.cs:135-146, 176` |
| Technology registry | `shared/TechnologyCoverageContracts.cs:21, 73-76, 181-185, 303` |
| Technology Coverage API / page | `backend/BirkNext.Api/Controllers/TechnologyCoverageController.cs:22`; `frontend/BirkNext.Web/Pages/TechnologyCoverage.razor:68-105, 110-135` |
| Pipeline detection | `backend/BirkNext.Api/Services/SourceAnalysis/Evidence/PipelineAnalyzer.cs:44-46, 67-68, 90-91, 391-395, 442` |
| Pipeline Review rules | `backend/BirkNext.Api/Services/PipelineReview/PipelineReviewBuilder.cs:19, 37, 316, 564-566, 842-849, 919-930` |
| Environment Analysis | `frontend/BirkNext.Web/Pages/AzureEnvironmentAnalysis.razor:81, 149, 306-331, 501-560`; `backend/BirkNext.Api/Controllers/AzureEnvironmentController.cs:73-90`; `backend/BirkNext.Api/Services/AzureEnvironment/DeclaredObservedComparer.cs:29` |
| IQR consumers | `backend/BirkNext.Api/Services/Integrations/SourceEvidence/IqrSourceDomainsReview.cs:23-26`; `IqrInfrastructureComparison.cs:20`; `IqrSourceReview.cs:89`; `frontend/BirkNext.Web/Components/EventHubRuntimeEvidenceForm.razor:21-54` |
| Navigation | `frontend/BirkNext.Web/Services/NavigationCatalog.cs:114-124`; `frontend/BirkNext.Web/Services/DocumentationCatalog.cs:60-64` |
| Source Analysis areas | `frontend/BirkNext.Web/Pages/SourceAnalysis.razor:149-160, 269-279, 307, 317-320, 359` |
| Infrastructure UI | `frontend/BirkNext.Web/Components/SourceDomains/InfrastructureWorkspace.razor` (tabs :158); `frontend/BirkNext.Web/Services/SourceEvidenceDomainsPresentation.cs:21, 51-75` |
| Frontend contract linking | `frontend/BirkNext.Web/BirkNext.Web.csproj:61-64` |

## 21. Confidence table

| Question | Answer | Confidence |
|---|---|---|
| Does BirkNext detect .tf files? | Yes (`.tf` and `.tfvars`; not `.tf.json`) | High |
| Does it parse Terraform? | Yes, with a static syntax-aware HCL reader (no evaluation). It has a nested-heredoc defect. | High |
| Does it detect providers? | Yes: name, source, version constraint and credential-settings presence | High |
| Does it detect resources? | Yes, with type, logical/declared name, category and settings (98 on the real project) | High |
| Does it detect modules? | Yes. Local modules are analyzed; external modules are recorded with source and version but not fetched. | High |
| Does it detect tfvars? | Yes, per environment, with redaction | High |
| Does Source Analysis expose Terraform evidence? | Yes: Source Analysis → Infrastructure (11 tabs) | High |
| Does shared evidence expose Terraform? | Yes: `EvidenceDomains.Infrastructure` and `SourceEvidenceQueries` | High |
| Does Technology Coverage show Terraform? | No. Only "Microsoft Azure" or other cloud rows. | High |
| Does Environment Analysis show Terraform/IaC? | Only in "Declared vs observed", after Azure sign-in, a target and an observed snapshot | High |
| Does Pipeline Review detect Terraform commands? | Yes (init/plan/apply/destroy, tasks, setup-terraform). Plan semantics are too loose and there is a false-positive finding on M2LB. | High |
| Is Terraform currently visible in the UI? | Yes, when a snapshot containing Terraform is analyzed and selected. None of the user's analyzed snapshots contains it. | High |
| Is evidence being lost between backend and frontend? | No (shared contracts, no DTO) | High |
| Is a new page necessary? | No | High |
| Can this be fixed generically? | Yes. All fixes extend existing analyzers, the registry and presentation, with no project-specific code. | High |
| Are secret values safely handled? | Credentials, keys and secrets: yes. Tenant, subscription and object ids and private IPs from tfvars are persisted and shown in plain text, with inconsistent redaction (policy decision needed). | Medium |
