# Azure Environment Analysis

Read-only analysis of a deployed Azure environment into immutable **Observed** evidence. It sits next to the other three kinds of evidence:

| Evidence | Owner | What it means |
|---|---|---|
| **Declared** | Source Analysis (Infrastructure evidence) | It is written in IaC in a source snapshot. |
| **Configured** | Target Environment / review settings | A person typed it in. |
| **Observed** | Azure Environment Analysis | The Azure control plane reported it at capture time. |
| **Verified** | Individual reviews | A behaviour was checked. |

Observed is never Pass/Fail. If an area cannot be read, it is shown as *Not authorized*, *Throttled* or *Not assessed*, never as a failed environment.

## Sign-in (`Services/AzureEnvironment/AzureSignIn.cs`)

- **Configuration.** The feature is off unless both `AzureEnvironment:Enabled` and `AzureEnvironment:ClientId` are set. `ClientId` must be a **public client** app registration:
  - platform "Mobile and desktop applications";
  - redirect URI `http://localhost`;
  - delegated permission *Azure Service Management → user_impersonation*.

  No client secret, certificate, username or password exists or is accepted. `TenantId` defaults to `organizations`.
- **Interactive sign-in.** Uses MSAL with authorization code + PKCE. The sign-in page opens in a **dedicated Microsoft Edge profile**, `%LocalAppData%\BirkNext\AzureSignInEdgeProfile`. The profile:
  - is never the normal Edge profile;
  - is never the managed-Edge, headless-diagnostic, Companion or proxy profile (`AzureSignInProfile.Resolve` refuses each of them);
  - is never launched with remote debugging, and never headless.

  MFA, Conditional Access and PIM apply exactly as they do for the person.
- **Device-code sign-in.** This is the fallback for runtimes that are not on the local workstation (`AzureEnvironment:AllowDeviceCode`).
- **Token handling.**
  - Tokens are held only in the MSAL in-memory cache. No cache serialization is registered.
  - Tokens are never persisted, logged or returned to the browser. The status DTO has no token field.
  - A token is lost on restart and removed on sign-out.
- **PIM.** BirkNext never activates a PIM role, and the guard test forbids PIM request endpoints.
  - **Refresh access** asks Entra for a new token (`AcquireTokenSilent(...).WithForceRefresh(true)`) after the person has activated a role themselves.
  - Eligible and active roles are listed separately, from `role*ScheduleInstances?$filter=asTarget()`.
  - Permissions come from `Microsoft.Authorization/permissions`. They are never inferred from group names.

## Read-only boundary (`AzureManagementClient.cs`)

- **Allowed requests.** `IAzureManagementClient` exposes exactly two operations:
  - **GET** on `management.azure.com`. Each GET is checked first by `AzureReadOnlyPolicy.CheckGet`.
  - **Predefined Resource Graph queries.** `AzureGraphQuery` has a private constructor, so no user input can become KQL. Resource-group narrowing is applied to the results.
- **Refused paths.** These are refused before anything is sent:
  - `list*`, `regenerate*`, secrets/keys/certificates, credentials and authorization rules;
  - app settings, connection strings, auth settings and publishing credentials (only `config/web` is read);
  - start/stop/restart operations;
  - absolute URLs, traversal, and requests without an `api-version`.
- **Paging links.** A paging `nextLink` is followed only on the management host.
- **Errors and retries.**
  - **401:** the token is refreshed once, then the session is marked Expired.
  - **429/503:** retried at most 3 times, and only while `Retry-After` is 15 s or less.
  - **Logging:** only the status and error code are logged.
- **Owner/Contributor.** A person with these roles still gets exactly the same requests. Write access is stated in the UI, not used.

Guard tests in `AzureReadOnlyBoundaryTests` enforce:

- no PUT/PATCH/DELETE;
- exactly one POST (Resource Graph);
- no data-plane SDK clients;
- no credential APIs;
- no PIM requests;
- no token logging;
- no token-cache persistence.

## Collector (`AzureEnvironmentCollector.cs`)

The collector runs these steps in order:

1. Subscriptions.
2. Effective permissions per subscription.
3. Resource Graph inventory (`resources` + `resource-groups`).
   - If Resource Graph is unavailable, it falls back to ARM lists and reads key resource types by id. This is reported as Partial.
4. Bounded detail reads (`MaxDeepReads`):
   - Event Hubs → hubs → consumer groups;
   - Service Bus → topics/queues → subscriptions;
   - blob container **names**;
   - PostgreSQL databases;
   - `sites/config/web`;
   - diagnostic settings.
5. Role assignments.
6. The signed-in person's PIM roles.
7. Topology and observations.
8. Capability matrix per area.

`AzureResourceNormalizer` maps ARM types onto the provider-neutral `InfrastructureCategory` and `InfrastructureResourceKind`. It uses `InfrastructureIdentity.KindOfType`, the same table Source Analysis uses. It keeps only **whitelisted, non-secret properties** and never the raw properties object. In addition:

- instrumentation keys, connection strings, passwords and keys are never read;
- secret-shaped values are dropped;
- tag values that look sensitive are shown as `[value not shown]`.

### Topology

- **Confirmed** relationships come from Azure resource ids:
  - containment and parent/child;
  - hosting plan or environment;
  - subnet;
  - private-endpoint target;
  - user-assigned identity;
  - diagnostic destination;
  - telemetry workspace;
  - role assignment matched by principal id.
- **Inferred** relationships come from naming only: a telemetry component and an app that share a name stem in the same resource group. They are labelled as not confirmed.

Observations are neutral statements, flagged "For review" where reviewers usually look. Examples:

- public network access;
- local auth enabled;
- TLS below 1.2;
- anonymous blob access;
- no purge protection;
- open inbound NSG rules;
- no diagnostic setting.

A diagnostic setting is only reported as absent when it was actually read.

## Snapshots and the shared provider (`AzureEnvironmentEvidence.cs`)

- **Storage.** Snapshots go in the `azure_environment_snapshots` table, one JSON document each. They are immutable and scoped to a BirkNext Target Environment, the same way Source Analysis snapshots are.
- **Provider.** `IAzureEnvironmentEvidenceProvider` is the read side for consumers:
  - `ResolveAsync` returns the exact snapshot, or the newest one; it never substitutes.
  - `LookupAsync` takes a kind plus a name or host. Its result is Observed / NotObserved / MultipleObserved / UnableToVerify / NoSnapshot / Disabled.
  - `TargetSuggestionsAsync` returns copy-only values; nothing is saved.
- **Feature flag.** Visibility is controlled by `FeatureVisibility:AzureEnvironmentAnalysis`, which defaults to enabled. When it is off, nothing is offered, and stored snapshots are kept.

## Declared vs observed (`DeclaredObservedComparer.cs`)

The comparer uses the **Infrastructure evidence of one Source Analysis snapshot** and does not re-parse Terraform. It compares it with **one Azure snapshot**, within an explicit environment. That environment is:

- chosen for the comparison, or
- stated when the Azure scope was analyzed, or
- inferred from the Target Environment name.

The environment selects the per-environment declared name from tfvars.

| State | Meaning |
|---|---|
| **Declared and observed** | The same kind and name were found. |
| **Configuration differs** | A comparable setting differs: location, SKU, TLS, public network access, HTTPS, local auth (inverted), purge protection, RBAC, partitions, retention, delivery count, version… |
| **Declared only** | Not observed in the selected scope. It may be undeployed, deployed elsewhere, or invisible with your permissions. |
| **Observed only** | No matching declaration. It may be managed elsewhere. |
| **Ambiguous match** | Several observed resources have the name. |
| **Unable to verify** | The name is computed in source, or the area or child read was not fully authorized. |

Child resources are matched within their declared parent. A topic in the QA namespace does not match the DEV namespace.

Not compared, each with a stated limitation:

- role assignments;
- diagnostic settings;
- non-Azure providers and Kubernetes;
- kinds without a provider-neutral identity (plans, NSGs).

## Consumers

| Consumer | Use |
|---|---|
| Integration Quality Review | `IqrObservedAzureReview`: each configured catalog value (namespace, hub, consumer group, checkpoint storage/container, Application Insights, Service Bus entities) is looked up in the newest Azure snapshot. The result is stored on the run (`ObservedAzureComparisons`) and shown in the panel "Configured vs. observed in Azure". It adds Observed lines to Configuration/Connectivity, never changes a domain state or outcome, and never re-queries Azure. |
| Target Environment | `GET api/azure-environment/target-suggestions` and the page's suggestions tab: application URLs, messaging hosts and telemetry components to copy. **Nothing is saved.** |
| Security / Observability | `GET api/azure-environment/lookup` returns a resource with its observations and relationships. No Security or Observability review consumes it yet: those reviews are source-scoped, and mixing in observed state needs its own result semantics. |
| Impact Analysis | Not integrated (optional in the task). |

## Not done in Phase 1

- Sovereign clouds (only public `management.azure.com`).
- Several tenants in one sign-in (configure `TenantId` per tenant).
- Key Vault object names (deliberately not read).
- App settings and connection strings (POST-only list operations; never called).
- Metrics.
- Defender / Policy compliance.
- AKS in-cluster inventory.
- Cost.
