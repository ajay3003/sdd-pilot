# Integration Topology

Integration Topology is a general BirkNext capability and the canonical view of how an environment's components connect.

> **Integration Topology**: the components of an environment, each component's interface inventory and the connections between them. Every
> element carries the provenance of every source that reported it.

A topology is not one application with one endpoint list. A single flow can involve many components, each with REST, GraphQL,
messaging, file or storage and external-application interfaces.

## Model (`shared/IntegrationTopologyContracts.cs`)

| Concept | Meaning |
|---|---|
| Component | A service, application or external system, with its location when a source states one |
| Interface | One interface of a component. Type: REST, GraphQL, Messaging, File, Storage, ExternalApplication, Database, Other. Direction: provides, calls, produces, consumes. It has an address (route, topic/queue, container or app), an operation (HTTP method, GraphQL operation) and an optional contract reference |
| Connection | From component (calls or produces) → to component (serves or consumes), with type, address and contract |
| Provenance | Which source said it (Target Environment, OpenAPI, Source Analysis, integration catalog, application metadata, browser observation, domain profile), with a safe reference and timestamp |
| Status | The strongest evidence: *Expected* (domain profile only) < *Configured* < *Discovered* < *Observed* (runtime evidence). *Conflict* and *Needs review* replace the level when sources disagree or a fact is unresolved; *Not evaluated* means no source said anything |

### Semantics

- Configured ≠ discovered ≠ observed. Only runtime evidence makes something *Observed*; source code or an OpenAPI document never does.
- Observed ≠ healthy. An existing interface ≠ a compatible contract (see [Contract Evolution](contract-evolution.md)).
- A resolved topology ≠ ready to execute. Execution also needs contracts, test data, authentication, a trusted target and scenario gates.

## How it is built (`Services/IntegrationTopology`)

`IntegrationTopologyService` reads, for one environment:
- the backend-trusted environment;
- the Integration Quality Review catalog;
- the Source Analysis snapshots.

It then runs every provider in order and merges their statements in `TopologyBuilder`. Nothing is stored. Every read rebuilds the topology from the current sources, and each source's timestamp is shown, so staleness is visible.

| Provider | Contributes | Status |
|---|---|---|
| Domain profiles (`ITopologyProfile`) | Expected components and explicit name aliases | Expected (never a connection) |
| Target Environment | Component membership and base locations (`TargetEnvironments:Trusted:n:Components:m` with `Name`, `BaseUrl`, `OpenApiPath`; backend configuration only) | Configured |
| Source Analysis | Components, declared routes, GraphQL and messaging interfaces, resolved HTTP/GraphQL dependencies, and messaging channels with producers and consumers. This is the existing source-architecture extraction; nothing is parsed again | Discovered |
| Application metadata (Altinn adapter) | The approved app as an external-application interface; its host comes from the central Altinn endpoint resolution | Configured, or Discovered after a metadata read |
| Integration catalog | Each enabled integration as a connection producer → consumer over its endpoint or topic | Configured |
| OpenAPI | **Only on Discover interfaces.** One GET of the document path a trusted component declares, on that component's own host (absolute URLs, other hosts, `..` and `//` refused), bounded to 15 s and 2 MB, no redirects, read with the existing OpenAPI extractor | Discovered |
| Browser observations | Method, host and path of REST/GraphQL calls the browser already holds (Endpoint Discovery), sent for merging; no query string, header or body | Observed |

### Merge, identity and conflicts

- **Component identity** is the normalized name, plus aliases a profile declares explicitly. Names are never matched by similarity.
- **Interface identity** is component + type + direction + operation + normalized address. REST path parameters and ids collapse to `{}`.
- **Duplicates merge.** The same interface from OpenAPI, Source Analysis and a browser observation is **one** interface with three provenance entries.
- **Conflicts are shown, never resolved by choice:**
  - two sources stating the same connection with different addresses (`/api/report` vs `/api/v2/report`);
  - a source dependency with conflicting targets;
  - an unresolved target (*Needs review*).
- **No connection is inferred** from similar names or from an observation.

## UI ownership

**Integration Topology** (`/integration-topology`, Quality & Testing) is the one canonical, read-only inventory of components, interfaces and connections.
- The default view shows sources and components (status and interface counts).
- Selecting a component shows its location, interfaces by type with sources, and its connections.
- All connections sit in a disclosure.
- *Refresh* re-reads the sources; *Discover interfaces* runs the OpenAPI provider and needs the integration-configuration permission.

Audit of existing UI that shows or edits endpoints, interfaces or relationships:

| Route / component | Responsibility | Editable | Overlap with topology | Action |
|---|---|---|---|---|
| System Settings → Target Environment → Target application / Target environment | Environment, type, target URL, execution context (browser profile) | Yes | No | KEEP (environment context; topology reads the backend-trusted components) |
| … → Endpoint Discovery tab (`EndpointDiscoveryTab`) | Runtime observations of this browser's traffic, plus API review host allowlists | Evidence only (refresh/delete observations) and allowlists | It was a parallel endpoint list | CONVERT: labelled as runtime observations and a discovery source feeding Integration Topology, with a link; it holds evidence, not configuration |
| … → Browser Discovery tab | Browser Companion page evidence (DOM, a11y, performance) | No | No | KEEP |
| … → Integrations tab (`IntegrationsPane`) | The integration catalog: configuration of integrations | Yes | Its connections feed the topology as *Configured* | KEEP as the configuration source; not an interface inventory |
| API Quality Review target picker | Choosing API targets to review | Selection only | Reads observations/configuration | KEEP (distinct responsibility) |
| Integration Quality Review → message-flow review | Documented message-flow design for review | Yes (documentation) | Documentation, not discovery | KEEP. IQR now also shows its integrations as resolved in the topology (`TopologyIntegrationConnections`, read-only, with provenance) |
| Source Analysis → architecture views | Source evidence of one snapshot | No | Topology consumes it | KEEP (provider view) |
| Skolenærvær Testing | Domain journeys | No endpoint editing | Consumes topology | Expected flow panel (`TopologyPathsPanel`, read-only) |
| Sample Projects (REST/Swagger/GraphQL fields) | Project/target setup | Yes | Target configuration | KEEP |
| Security Classification (Person GraphQL endpoint field) | Input for that review run | In memory | Small duplicate of a configured address | KEEP for now (noted; not an inventory) |

No editable endpoint or interface inventory competes with the topology. Critical E2E Regression is not changed and does not consume the topology.

## Skolenærvær: the first consumer

`SkoleNaervaerTopologyProfile` (pack folder) declares:
- **Expected components:** Altinn, MU, MM, Skoletjenesten, Person, SkoleAdapter, Bufdata.
- **Explicit aliases:** for example *Altinn (TT02)*, *Skolenærværsrapport*, *Meldingsutvekslermottak*.
- **Expected paths:**
  - *Rapportmottak*: Altinn → MU → MM → Skoletjenesten → Bufdata.
  - *Utdanningsdata*: SkoleAdapter → Skoletjenesten.
  - *Person events*: Person → Skoletjenesten.

It adds no connection or address. The Skolenærvær Overview shows the expected flow, read-only. A step resolves only when a source states that connection, and the panel shows the evidence level and whether it was observed. An unresolved boundary (for example Skoletjenesten → Bufdata, which has no delivery yet, ADR-028) stays unresolved; it is not a failure. Expected ≠ observed.

## Not yet done

- **No GraphQL introspection provider.** GraphQL interfaces come from Source Analysis, observations and the catalog.
- **No runtime correlation provider** that would mark a *connection* Observed. Observations mark interfaces only; a connection is never inferred from them.
- **The Contract Evolution graph** (`ContractDependencyGraph`) is still separate data. Its boundaries are contract-level, and could later be derived from topology connections.
