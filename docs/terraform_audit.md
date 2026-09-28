For readability, the following tables use these exact path aliases:

| Alias | Repository-relative directory/file |
|---|---|
| `DEV` | `terraform/environments/dev.tfvars` |
| `I/` | `terraform/layers/05-integration/` |
| `A/` | `terraform/layers/04-apps/` |
| `CA/` | `terraform/layers/04-apps/modules/container-app/` |
| `S/` | `terraform/layers/02-shared/` |

**D. Exact namespace, FQDN, and resource-group sources**

The namespace is assembled explicitly in Terraform, then passed into the namespace module. It is not a literal DEV string or a name generated internally by the module.

`terraform/layers/05-integration/locals.tf:4`:

```
evhns = "evhns-m2lb-${var.environment}-${var.location_short}-001"
```

Inputs:

- `terraform/environments/dev.tfvars:1`: `environment = "dev"`.
- `terraform/layers/05-integration/variables.tf:17–20`: `location_short` defaults to `"nwe"`.
- `terraform/layers/05-integration/event_hubs.tf:14`: `module.event_hubs.name = local.names.evhns`.

Result: **`evhns-m2lb-dev-nwe-001` — Derived; Confirmed configuration.**

The module declaration is `Azure/avm-res-eventhub-namespace/azurerm`, version constraint `~> 0.1.0`, at `event_hubs.tf:4–6`. Its implementation is an external dependency; no cached module copy was used as evidence.

The FQDN is explicitly derived in `terraform/layers/05-integration/locals.tf:36`:

```
evh_namespace_fqdn = "${module.event_hubs.resource.name}.servicebus.windows.net"
```

Result: **`evhns-m2lb-dev-nwe-001.servicebus.windows.net` — Derived from namespace name**, with an actual Terraform expression supporting the derivation.

The adapter layer independently assembles the same FQDN in `terraform/layers/04-apps/locals.tf:62`.

Resource-group source, `terraform/layers/05-integration/locals.tf:3`:

```
rg = "rg-m2lb-${var.environment}-integration-${var.location_short}"
```

`terraform/layers/05-integration/resource_group.tf:3–6` assigns it to `azurerm_resource_group.integration.name`.

Result: **`rg-m2lb-dev-integration-nwe` — Derived; Confirmed configuration.**

DEV variable-file selection is established by `.azuredevops/templates/plan-layer.yml:29,79–80`, which selects `terraform/environments/${{ parameters.environment }}.tfvars` and passes the environment parameter.

**E–G. Event Hub creation, captured tables, and Debezium configuration**

Business hubs are created by:

`terraform/layers/05-integration/connect_topics.tf:51–60`, resource `azurerm_eventhub.cdc_data`:

```
for_each = local.debezium_source_enabled && var.debezium_source_tables != "" ? toset([
  for t in split(",", var.debezium_source_tables) : trimspace(t)
]) : toset([])

name = replace(
  "${var.debezium_topic_prefix}.${var.debezium_source_database}.${each.key}",
  "/[^a-zA-Z0-9._-]/",
  "_"
)

namespace_id      = module.event_hubs.resource_id
partition_count   = 1
message_retention = 7
```

This is an **explicit Terraform naming expression**, matching the Debezium naming convention described in that file’s comments. Each business resource instance receives `partition_count = 1` and `message_retention = 7`; these are not merely module defaults.

The authoritative captured-table source is `terraform/environments/dev.tfvars:207`:

```
debezium_source_tables = "dbo.Person,dbo.Tiltak,dbo.Bestilling,dbo.TjenesteType,dbo.TiltaksStatusType,dbo.AvslutningsGrunnType,dbo.Barn,dbo.BarnStatusType,dbo.BarnType,dbo.Kommune,dbo.KjønnType,dbo.TvangsProtokoll,dbo.HjemmelType,dbo.TvangsProtokollStatusType,dbo.Romning,dbo.RomningKategoriType"
```

There are **exactly 16 entries**, independently read from Terraform. No additional captured table appears in the DEV include list.

`terraform/layers/05-integration/locals.tf:79` separately configures `BirkM2LB.dbo.debezium_signal` as a signaling collection. That does **not** make it a seventeenth entry in `table.include.list`.

The input-to-connector trace is:

| DEV source | Variable declaration | Connector key in `I/locals.tf` |
|---|---|---|
| `DEV:204`, `debezium_source_hostname = "10.31.19.31"` | `I/variables.tf:46` | `database.hostname`, line 44 |
| `DEV:205`, `debezium_source_port = "50806"` | `I/variables.tf:64` | `database.port`, line 45 |
| `DEV:206`, `debezium_source_database = "BirkM2LB"` | `I/variables.tf:52` | `database.names`, line 46 |
| `DEV:207`, `debezium_source_tables` | `I/variables.tf:58` | `table.include.list`, line 51 |
| `DEV:208`, `debezium_topic_prefix = "m2lb-cdc-dev"` | `I/variables.tf:70` | `topic.prefix`, line 52 |

The hostname also appears as networking input `birk_ip` at `DEV:90`, with `birk_ports = ["50806"]` at line 91. The Debezium-specific variables above are the direct producer configuration.

Debezium deployment evidence:

| Property | Exact source | Finding |
|---|---|---|
| Container App | `I/kafka_connect.tf:11–17` | `azurerm_container_app.kafka_connect` |
| App name | `I/locals.tf:5`; `I/kafka_connect.tf:12` | Derived `ca-m2lb-debezium-dev-nwe-001` |
| Image | `I/kafka_connect.tf:46,213` | ACR-prefixed `quay.io/debezium/connect:3.0`, pinned by SHA-256 digest |
| Distributed worker | `I/kafka_connect.tf:38–42,66–108` | Worker group and three internal storage topics; singleton replica |
| Replicas | `DEV:209`; `I/kafka_connect.tf:41–42` | Minimum 1, maximum 1 |
| SQL Server connector | `I/locals.tf:43` | `io.debezium.connector.sqlserver.SqlServerConnector` |
| Kafka destination | `I/kafka_connect.tf:63–64` | Namespace FQDN plus `:9093` |
| Registration | `I/kafka_connect.tf:206–241` | Sidecar submits `local.connector_config_json` to local Kafka Connect REST API |
| Snapshot | `I/locals.tf:64` | `snapshot.mode = "initial"` |

`I/debezium.tf` defines a separate send-only SAS authorization rule. **It is not the Container App deployment file.**

**H. Producer authentication**

**Confirmed configuration: SAS authorization with SASL/PLAIN over TLS.**

Exact sources:

- `terraform/layers/05-integration/connect_auth.tf:6–13`: `azurerm_eventhub_namespace_authorization_rule.connect_debezium`; `listen = true`, `send = true`, `manage = false`.
- `terraform/environments/dev.tfvars:203`: `eh_local_auth_enabled = true`.
- `terraform/layers/05-integration/event_hubs.tf:24`: `local_authentication_enabled = var.eh_local_auth_enabled`.
- `terraform/layers/05-integration/kafka_connect.tf:118–165`:
  - `CONNECT_SECURITY_PROTOCOL`, `CONNECT_PRODUCER_SECURITY_PROTOCOL`, `CONNECT_CONSUMER_SECURITY_PROTOCOL`: `SASL_SSL`.
  - Corresponding `*_SASL_MECHANISM`: `PLAIN`.
  - Corresponding `*_SASL_JAAS_CONFIG`: secret reference `connect-kafka-jaas`.
- `terraform/layers/05-integration/locals.tf:53–63`: schema-history producer/consumer and connector producer use the same mechanisms and directory-based secret references.
- `terraform/layers/05-integration/kafka_connect.tf:29–35`: Key Vault secret bindings authenticated with the Debezium user-assigned identity.
- `terraform/layers/04-apps/debezium_kv_rbac.tf:5–9`: `Key Vault Secrets User` for that identity.

**Connection-string-style credential provisioning is documented separately:** `wiki/runbooks/debezium-kv-secrets-runbook.md:53–68` describes SASL username `$ConnectionString` and provisioning the authorization rule’s connection string into the JAAS secret.

Terraform explicitly says that secret is populated out-of-band (`connect_auth.tf:3–4`). Consequently, Terraform proves the mechanism and reference, but does not prove the currently stored secret contents. No credential values are included here.

**I–K. Consumer managed identities, RBAC, and adapter sources**

Identity creation and Container App attachment are implemented in:

- `terraform/layers/04-apps/modules/container-app/container_app.tf:6–10`: `azurerm_user_assigned_identity.this`.
- Same file, lines 28–30: `UserAssigned` identity attached to the Container App.
- Same file, lines 106–113: `AZURE_CLIENT_ID` and `AZURE_TENANT_ID`.
- Same file, lines 93–98: injects the Event Hub namespace FQDN, not a SAS credential.

| Consumer | Exact caller and keys | Naming source | Result |
|---|---|---|---|
| Person | `A/ca_person_adapter.tf:3–8`, `module.person_adapter`, `container_app_name`, `container_name`, `uami_name` | `A/locals.tf:15,18` | `ca-m2lb-person-adp-dev-nwe-001`; `person-adapter`; `id-m2lb-person-adp-dev-nwe` |
| Hendelse | `A/ca_hendelse_adapter.tf:15–20`, `module.hendelse_adapter`, same keys | `A/locals.tf:16,19` | `ca-m2lb-hendelse-adp-dev-nwe-001`; `hendelse-adapter`; `id-m2lb-hendelse-adp-dev-nwe` |
| SCIM | `A/ca_scim_adapter.tf:18–23`, `module.scim_adapter`, same keys | `A/locals.tf:17,20` | `ca-m2lb-scim-adp-dev-nwe-001`; `scim-adapter`; `id-m2lb-scim-adp-dev-nwe` |
| Tjeneste | `A/ca_tjeneste_api.tf:41–46`, `module.tjeneste_api`, same keys | `A/locals.tf:10,29` | `ca-m2lb-tjeneste-dev-nwe-001`; `tjeneste-api`; `id-m2lb-tjeneste-api-dev-nwe` |

Container names are direct literals. App and identity names are derived and inherited through module inputs. Calling the container name a “logical application” is a product interpretation of that field.

The Event Hub input is explicitly supplied through `EventHub__FQDN`:

- Person: `A/ca_person_adapter.tf:24–27`.
- Hendelse: `A/ca_hendelse_adapter.tf:38–41`.
- SCIM: `A/ca_scim_adapter.tf:49–52`.
- Tjeneste: `A/ca_tjeneste_api.tf:61–64`.

The cross-layer identity chain is:

1. `CA/outputs.tf:16–18`: exports `uami_principal_id`.
2. `A/outputs.tf:176–178,201–203,226–228,71–73`: exports Person, Hendelse, SCIM, and Tjeneste principals.
3. `I/data.tf:34–42`: references `04-apps` state.
4. `I/adapters_eh_rbac.tf:5–9`: maps those four principals.
5. `I/adapters_eh_rbac.tf:13–19`: assigns `Azure Event Hubs Data Receiver`, with `scope = module.event_hubs.resource_id`.

This confirms **managed-identity/Entra receiver configuration at namespace scope**. It does not prove which topics each application subscribes to, or that a deployed process is actively consuming.

For SCIM, additional messaging evidence is:

- `A/adapters_sb_rbac.tf:7–19`: Service Bus namespace receiver role.
- `A/ca_scim_adapter.tf:7–14,43–46`: sender scope for `entra.brukere`.
- `CA/data_plane_assignments.tf:15–20`: implements the sender assignment.

**SCIM consumption of BIRK CDC hubs: Not established.**

**L. Application Insights**

The configuration chain is explicit:

| Source | Block/key |
|---|---|
| `S/app_insights.tf:4–22` | `module.app_insights`, Application Insights component module; `application_type`, `workspace_id`, `sampling_percentage` |
| `S/locals.tf:7` | `local.names.appi`, deriving `appi-m2lb-dev-nwe-001` |
| `S/outputs.tf:57–65` | `app_insights_id`, sensitive `app_insights_connection_string` output |
| `A/data.tf:23–31` | Shared-layer remote-state reference |
| `A/locals.tf:57` | `local.ca_shared.app_insights_connection_string` reference |
| `CA/container_app.tf:119–122` | `APPLICATIONINSIGHTS_CONNECTION_STRING` environment setting |
| `A/workbook_app_insights_overview.tf:18–28` | `azurerm_application_insights_workbook.app_insights_overview`; `source_id` points to the shared Application Insights resource |

**Application Insights is confirmed as configured monitoring for the module-based applications.** This does not prove telemetry arrival or instrumentation in every service, and the Kafka Connect Container App does not use this module. No dashboard URL was invented.

**M. Consumer group, health URLs, and contracts**

- **Adapter consumer group: Not established in Terraform.** No adapter `$Default` configuration was found.
- Kafka Connect’s `GROUP_ID = "connect-debezium-cluster"` at `I/kafka_connect.tf:67–68` is worker coordination configuration, not an adapter consumer-group value.
- Person and Hendelse omit `ingress`; `CA/variables.tf:250–262` defaults it to `null`.
- `CA/container_app.tf:38–50` emits no ingress block for that default.
- `CA/outputs.tf:26–28` returns an empty FQDN when ingress is absent.
- Generic health-path support exists at `CA/container_app.tf:148–185`, but `health_probe_path_prefix` defaults to empty at `CA/variables.tf:278–281`; neither adapter enables it.

Therefore, **an externally usable Person/Hendelse health URL or worker URL is not established**.

Producer/consumer JSON-schema artifacts, schema-registry configuration, contract paths, and contract versions are **not established**. JSON converter schema settings and Debezium schema history do not establish a producer–consumer contract relationship.

**Source-of-truth table**

| BirkNext field | Current value | Terraform source file | Block/key | Evidence classification | Confidence |
|---|---|---|---|---|---|
| Namespace | `evhns-m2lb-dev-nwe-001` | `I/locals.tf:4`; `I/event_hubs.tf:14`; `DEV:1`; `I/variables.tf:20` | `names.evhns` → `module.event_hubs.name` | Derived; module input | Confirmed |
| Namespace FQDN | `evhns-m2lb-dev-nwe-001.servicebus.windows.net` | `I/locals.tf:36` | `evh_namespace_fqdn` | Derived from namespace | Derived |
| Resource group | `rg-m2lb-dev-integration-nwe` | `I/locals.tf:3`; `I/resource_group.tf:4` | `names.rg` → resource `name` | Derived | Confirmed |
| Region code | `nwe` | `I/variables.tf:17–20` | `location_short.default` | Direct default | Confirmed |
| Source database | `BirkM2LB` | `DEV:206`; `I/locals.tf:46` | `debezium_source_database` → `database.names` | Direct input | Confirmed |
| Source host | `10.31.19.31` | `DEV:204`; `I/locals.tf:44` | `debezium_source_hostname` → `database.hostname` | Direct input | Confirmed |
| Source port | `50806` | `DEV:205`; `I/locals.tf:45` | `debezium_source_port` → `database.port` | Direct input; string in Terraform | Confirmed |
| Topic prefix | `m2lb-cdc-dev` | `DEV:208`; `I/locals.tf:52` | `debezium_topic_prefix` → `topic.prefix` | Direct DEV literal | Confirmed |
| Producer technology | Debezium SQL Server CDC | `I/kafka_connect.tf:46`; `I/locals.tf:43` | Image; `connector.class` | Direct configuration; descriptive label | Confirmed |
| Producer authentication | SAS | `I/connect_auth.tf:6–13`; `I/kafka_connect.tf:118–165` | Authorization rule; SASL settings | Direct configuration | Confirmed |
| Default consumer authentication | Managed Identity | `CA/container_app.tf:6–30,106–113`; `I/adapters_eh_rbac.tf:13–18` | UAMI attachment; receiver RBAC | Inherited through module inputs | Confirmed for named consumers |
| Captured tables | 16 | `DEV:207`; `I/locals.tf:51` | `debezium_source_tables`; `table.include.list` | Direct list | Confirmed |
| Business hub names | Prefix + database + table | `I/connect_topics.tf:56` | `cdc_data.name` | Derived, including sanitization | Derived; one seed mismatch |
| Business partitions | `1` | `I/connect_topics.tf:58` | `cdc_data.partition_count` | Direct per resource instance | Confirmed |
| Business retention | 7 days | `I/connect_topics.tf:59` | `cdc_data.message_retention` | Direct per resource instance | Confirmed |
| Person app/identity | Seeded names | `A/ca_person_adapter.tf:6–8`; `A/locals.tf:15,18`; `CA/container_app.tf:6,13` | Module inputs → resources | Derived; inherited | Confirmed |
| Person topic mapping | Confirmed in BirkNext | No topic-specific subscription source found | — | Seed assertion only in inspected evidence | Not established |
| Other suggested mappings | Eight suggestions | No topic-specific subscription source found | — | Earlier audit claims retained in BirkNext | Not established |
| Technical owner | `platform-team` | `I/locals.tf:17–22` | Tags `owner`, `support-contact` | Direct tags; “Technical owner” is interpretation | Confirmed tags |
| Monitoring provider | Application Insights | `S/app_insights.tf:4`; `CA/container_app.tf:119–122` | Component + app environment setting | Inherited through module inputs | Confirmed configuration |
| Consumer group | Unknown/null | No adapter group setting found | — | Unconfigured in inspected source | Not established |
| Monitoring/dashboard URL | Null | Workbook resource exists; no seeded URL proven | — | No URL established | Not established |
| Person/Hendelse health or worker URL | Unset | `CA/variables.tf:250–281`; adapter callers | No ingress or HTTP health opt-in | No URL established | Not established |
| Contract relationship | Not configured | No contract artifact relationship found | — | No contract source established | Not established |

**Topic source table — all 16 business integrations**

Every row’s authoritative source is **`DEV:207`, `debezium_source_tables`**, passed into **`I/locals.tf:51`, `table.include.list`**.

Every creation source is **`I/connect_topics.tf:51–60`, `azurerm_eventhub.cdc_data`**. The table lists the resulting full Event Hub names; none is a literal full-name entry in Terraform.

| Topic/table | Terraform file and source list entry | Event Hub creation source/result | Consumer mapping source |
|---|---|---|---|
| Person | `DEV:207`: `dbo.Person` | `I/connect_topics.tf:56` → `m2lb-cdc-dev.BirkM2LB.dbo.Person` — Derived | Manual/known-domain assertion in seed: Person Adapter. Terraform mapping not established |
| Tiltak | `DEV:207`: `dbo.Tiltak` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.Tiltak` — Derived | Prior-audit suggestion: Tjeneste API; original application evidence unavailable |
| Bestilling | `DEV:207`: `dbo.Bestilling` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.Bestilling` — Derived | Prior-audit suggestion: Tjeneste API; original application evidence unavailable |
| TjenesteType | `DEV:207`: `dbo.TjenesteType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.TjenesteType` — Derived | Prior-audit suggestion: Tjeneste API; original application evidence unavailable |
| TiltaksStatusType | `DEV:207`: `dbo.TiltaksStatusType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.TiltaksStatusType` — Derived | Prior-audit suggestion: Tjeneste API; original application evidence unavailable |
| AvslutningsGrunnType | `DEV:207`: `dbo.AvslutningsGrunnType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.AvslutningsGrunnType` — Derived | Prior-audit suggestion: Tjeneste API; original application evidence unavailable |
| Barn | `DEV:207`: `dbo.Barn` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.Barn` — Derived | Prior-audit suggestion: PersonBiRKAdapter; original application evidence unavailable |
| BarnStatusType | `DEV:207`: `dbo.BarnStatusType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.BarnStatusType` — Derived | Not established |
| BarnType | `DEV:207`: `dbo.BarnType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.BarnType` — Derived | Not established |
| Kommune | `DEV:207`: `dbo.Kommune` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.Kommune` — Derived | Not established |
| KjønnType | `DEV:207`: `dbo.KjønnType` | Same expression → **`m2lb-cdc-dev.BirkM2LB.dbo.Kj_nnType`** — Derived; differs from seed | Not established |
| TvangsProtokoll | `DEV:207`: `dbo.TvangsProtokoll` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.TvangsProtokoll` — Derived | Prior-audit suggestion: Hendelse BiRK Adapter; Terraform mapping not established |
| HjemmelType | `DEV:207`: `dbo.HjemmelType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.HjemmelType` — Derived | Not established |
| TvangsProtokollStatusType | `DEV:207`: `dbo.TvangsProtokollStatusType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.TvangsProtokollStatusType` — Derived | Not established |
| Romning | `DEV:207`: `dbo.Romning` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.Romning` — Derived | Prior-audit suggestion: Hendelse BiRK Adapter; Terraform mapping not established |
| RomningKategoriType | `DEV:207`: `dbo.RomningKategoriType` | Same expression → `m2lb-cdc-dev.BirkM2LB.dbo.RomningKategoriType` — Derived | Not established |

The local origin of the suggestions is /C:/Users/ajaan/source/sdd-repos/BirkNext/AIAssisted/backend/BirkNext.Api/Services/IntegrationQuality/KnownIntegrationTemplates.cs:227, copied into /C:/Users/ajaan/source/sdd-repos/BirkNext/AIAssisted/backend/BirkNext.Api/Services/Integrations/M2lbDevIntegrationSeed.cs:43.

For **each of the five Tjeneste suggestions**, the requested A–D classification is **D: original source cannot be found in the supplied material**. Their immediate source is previous analysis recorded in BirkNext; the inspected files do not establish whether that earlier analysis inferred the relationships or read application configuration. Thus neither A nor B is demonstrated, and C cannot be asserted as a proven inference.

The same limitation applies to Barn, TvangsProtokoll, and Romning. Hendelse’s two relationships remain **Suggested mappings in BirkNext; Not established by this Terraform audit**. Namespace receiver RBAC alone does not establish topic mapping.

**N. Technical-topic table**

| Technical topic | Terraform file/resource | Purpose/config source | Partitions | Retention/cleanup |
|---|---|---|---:|---|
| `m2lb-cdc-dev` | `I/connect_topics.tf:64–70`, `cdc_schema_changes`; name from `DEV:208` | Schema-change topic; `name = var.debezium_topic_prefix`; purpose documented at lines 62–63 | 1 | `message_retention = 7`; cleanup policy not explicitly set |
| `schemahistory` | `I/connect_topics.tf:40–44`, `schemahistory` | `I/locals.tf:54`, `schema.history.internal.kafka.topic` | 1 | `message_retention = 7`; cleanup policy not explicitly set |
| `connect-configs` | `I/connect_topics.tf:7–15`, `connect_configs` | `I/kafka_connect.tf:87–88`, `CONFIG_STORAGE_TOPIC` | 1 | `cleanup_policy = "Compact"`; tombstone retention 1 hour |
| `connect-offsets` | `I/connect_topics.tf:18–26`, `connect_offsets` | `I/kafka_connect.tf:91–92`, `OFFSET_STORAGE_TOPIC` | 25 | `cleanup_policy = "Compact"`; tombstone retention 1 hour |
| `connect-status` | `I/connect_topics.tf:29–37`, `connect_status` | `I/kafka_connect.tf:95–96`, `STATUS_STORAGE_TOPIC` | 5 | `cleanup_policy = "Compact"`; tombstone retention 1 hour |

The three Connect purposes are proven by worker environment settings referencing their resources, not inferred from names. Their one-hour tombstone setting is **not** a general one-hour or seven-day message retention policy.

**O. BirkNext values not directly established by Terraform**

1. **The unsanitized `KjønnType` Event Hub name conflicts with the creation expression.** The captured SQL table name itself is confirmed.
2. **Person’s “Confirmed” consumer-topic mapping** is not proven by this package.
3. **All eight suggested consumer-topic mappings** lack topic-specific subscription evidence here.
4. **Managed Identity as a default for every integration** extends beyond the narrower evidence: Terraform configures four named receiver identities, without mapping them to every business hub.
5. **“Technical owner”** interprets the Terraform `owner` and `support-contact` tags.
6. **Application Insights as universal producer/consumer monitoring** would overstate the source: the module-based applications are wired; Kafka Connect telemetry delivery is not established.
7. **Consumer group, health URL, worker URL, dashboard URL, and contract relationships** remain unestablished. Their unknown/unconfigured state should not be filled with assumptions.

No seeded value was corrected during this audit.

**P. Useful Terraform information intentionally not added to BirkNext**

| Fact | Classification |
|---|---|
| Topic-name sanitization and the `KjønnType` mismatch | Useful for IQR |
| Namespace-wide receiver permission versus actual subscriptions | Useful for IQR |
| Event Hubs public network access disabled; private endpoint configured (`I/event_hubs.tf:26–35`) | Useful for IQR |
| Business/schema-topic partition and retention policies | Useful for IQR; business values already seeded |
| Kafka endpoint on port 9093 | Useful for IQR |
| Debezium 3.0 image and pinned digest | Useful only as technical detail |
| Distributed Connect with one worker replica | Useful only as technical detail |
| `snapshot.mode = initial` and signaling collection | Useful only as technical detail |
| JSON converters; key schemas disabled, value schemas enabled (`I/kafka_connect.tf:171–185`) | Useful only as technical detail |
| Connect topic partition counts and compaction/tombstone policies | Useful only as technical detail |
| Application module ignores subsequent image/environment/probe changes (`CA/container_app.tf:201–228`) | Useful for IQR: source configuration does not establish current runtime state |
| Terraform paths, module versions, archive hash, and state-layer references | Not useful in product; developer provenance only |
| Additional captured tables | None found; signaling collection reported separately |
