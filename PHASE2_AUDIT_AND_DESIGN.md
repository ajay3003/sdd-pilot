# Phase 2: Integration Relationship + Contract Source Model
## Audit and Design Document

**Date:** 2026-09-08  
**Status:** Audit Complete, Design Ready for Implementation

---

## Current Integration Model (Before Phase 2)

### Frontend Model: IntegrationConfig
**Location:** FrontendAnalysisModels.cs, lines 64-78

**Current Fields:**
```csharp
id: string                          // Unique identifier (GUID)
name: string                        // Integration display name
type: IntegrationType               // Enum: REST, GraphQL, EventHub, ServiceBus, Kafka, RabbitMQ, File, SOAP
endpoint: string?                   // URL or connection endpoint
resource: string?                   // Resource name (queue, topic, hub)
consumer: string?                   // CURRENT SEMANTICS: Generic consumer reference (unknown purpose)
authType: IntegrationAuthType       // Enum: None, ApiKey, BearerToken, BasicAuth, ManagedIdentity, ConnectionString, SasToken
healthUrl: string?                  // Health check endpoint URL
workerUrl: string?                  // Worker/processing endpoint URL
monitoringUrl: string?              // Monitoring/observability URL
owner: string?                      // Integration owner/contact
enabled: bool                       // Whether integration is active
```

### Backend Model: IntegrationConfigDto
**Location:** IntegrationQualityModels.cs, lines 17-31

**Current Fields:** (identical to frontend)
```csharp
Same as IntegrationConfig above
```

### Current Consumer Semantics
**Finding:** The `Consumer` field exists but its semantics are unclear/undocumented.

**Observed Usage Patterns:**
- In IntegrationTargetRegistryService, ConsumerGroup is a separate field (for messaging)
- IntegrationConfig has a single `consumer` string field
- No validation or usage logic currently visible

**Decision for Phase 2:**
- PRESERVE existing `consumer` field (may mean consumer group for messaging)
- ADD new `logicalConsumerService` field (for logical service that consumes this integration)
- ADD new `logicalProducerService` field (for logical service that produces into this integration)
- Keep semantics separate from transport-layer consumer groups

---

## Phase 2 Extension Model (After Implementation)

### New Enum: ContractSourceType
```csharp
public enum ContractSourceType
{
    Auto = 0,              // Auto-detect from integration config
    OpenApi = 1,           // Swagger/OpenAPI specification
    GraphQlSchema = 2,     // GraphQL schema/introspection
    Assembly = 3,          // .NET assembly reference
    SchemaFile = 4,        // Schema file (JSON/YAML/protobuf)
    Endpoint = 5,          // Contract endpoint/registry
    Manual = 6,            // Manually entered
    Unknown = 7            // Undetermined
}
```

### New Enum: ContractMetadataReadiness
```csharp
public enum ContractMetadataReadiness
{
    NotConfigured = 0,     // No relationship metadata provided
    Partial = 1,           // Incomplete relationship metadata
    Ready = 2              // Complete relationship metadata
}
```

### Extended IntegrationConfig (Frontend)
**Location:** FrontendAnalysisModels.cs

**New Fields to Add:**
```csharp
// Contract Relationship Metadata
[JsonPropertyName("logicalProducerService")] 
public string? LogicalProducerService { get; set; }        // Name of service producing into this integration

[JsonPropertyName("logicalConsumerService")] 
public string? LogicalConsumerService { get; set; }        // Name of service consuming from this integration

[JsonPropertyName("contractName")] 
public string? ContractName { get; set; }                  // Name of contract/message/schema exchanged

[JsonPropertyName("contractSourceType")] 
public ContractSourceType ContractSourceType { get; set; } = ContractSourceType.Unknown

[JsonPropertyName("contractSourceLocation")] 
public string? ContractSourceLocation { get; set; }        // URL, file path, or reference to contract source

[JsonPropertyName("contractMetadataReadiness")] 
public ContractMetadataReadiness ContractMetadataReadiness { get; set; } = ContractMetadataReadiness.NotConfigured
```

### Extended IntegrationConfigDto (Backend)
**Location:** IntegrationQualityModels.cs

**New Fields:** (identical to frontend for consistency)
```csharp
Same as Extended IntegrationConfig above
```

---

## Readiness Determination Rules

### NotConfigured
- All new contract fields are null/empty
- ContractMetadataReadiness = NotConfigured

### Partial
- At least one contract field is populated
- But missing required fields based on integration type
- ContractMetadataReadiness = Partial

### Ready
- Minimum viable metadata for contract discovery:

**EventHub/ServiceBus:**
- Required: LogicalProducerService OR LogicalConsumerService
- Required: ContractSourceLocation OR ContractSourceType != Unknown
- Ready if both producer/consumer OR source is complete

**REST:**
- Required: ContractSourceType = OpenAPI OR contractLocation = swagger URL
- Ready if source is resolvable

**GraphQL:**
- Required: ContractSourceType = GraphQlSchema
- Required: ConsumerService populated
- Ready if GraphQL endpoint exists in environment config

**Kafka/RabbitMQ:**
- Required: LogicalProducerService OR LogicalConsumerService
- Required: ContractName OR ContractSourceType != Unknown
- Ready if producer/consumer/contract meaningful

---

## Backward Compatibility

### Legacy Integration Loading
Old profiles without new fields:
- New fields deserialize as null
- ContractMetadataReadiness defaults to NotConfigured
- Integration remains valid/enabled
- No migration required
- No fake data injected

### Serialization
- All new fields have [JsonPropertyName] attributes
- Safe JSON round-trip for storage
- Browser local storage compatible

---

## Examples: How Each Integration Type is Modeled

### EventHub Example
```csharp
new IntegrationConfig
{
    Id = "eh-child-updated",
    Name = "Child Updated Events",
    Type = IntegrationType.EventHub,
    Endpoint = "myeventhub-namespace",
    Resource = "child-updated-hub",
    Consumer = null,                                   // Keep for backward compatibility
    AuthType = IntegrationAuthType.ConnectionString,
    Owner = "Family Services Team",
    
    // NEW: Phase 2 relationship metadata
    LogicalProducerService = "Hendelse Adapter",       // System that sends events
    LogicalConsumerService = "Hendelse",               // System that processes events
    ContractName = "ChildUpdated",                     // Contract/event name
    ContractSourceType = ContractSourceType.Assembly,  // Contract defined in assembly
    ContractSourceLocation = "BirkNext.Contracts.ChildUpdated, BirkNext.Contracts v1.0",
    ContractMetadataReadiness = ContractMetadataReadiness.Ready
}
```

### REST Example
```csharp
new IntegrationConfig
{
    Id = "rest-hendelse-api",
    Name = "Hendelse API",
    Type = IntegrationType.REST,
    Endpoint = "https://hendelse-api.example.com",
    AuthType = IntegrationAuthType.BearerToken,
    Owner = "API Team",
    
    // NEW: Phase 2 relationship metadata
    LogicalProducerService = "Hendelse Adapter",
    LogicalConsumerService = null,                     // REST is request/response, not strict producer/consumer
    ContractName = null,                               // OpenAPI describes the contract
    ContractSourceType = ContractSourceType.OpenApi,
    ContractSourceLocation = "https://hendelse-api.example.com/swagger/v1/swagger.json",
    ContractMetadataReadiness = ContractMetadataReadiness.Ready
}
```

### GraphQL Example
```csharp
new IntegrationConfig
{
    Id = "graphql-hendelse",
    Name = "Hendelse GraphQL",
    Type = IntegrationType.GraphQL,
    Endpoint = "https://hendelse-api.example.com/graphql",
    AuthType = IntegrationAuthType.BearerToken,
    Owner = "Platform Team",
    
    // NEW: Phase 2 relationship metadata
    LogicalProducerService = "Hendelse",               // Service exposing GraphQL
    LogicalConsumerService = "BirkNext Frontend",      // Consuming system
    ContractName = null,                               // GraphQL schema is the contract
    ContractSourceType = ContractSourceType.GraphQlSchema,
    ContractSourceLocation = "https://hendelse-api.example.com/graphql",  // Introspection endpoint
    ContractMetadataReadiness = ContractMetadataReadiness.Ready
}
```

---

## Implementation Approach

### Phase 2 Deliverables

1. **Backend Model Extension**
   - Add enums: ContractSourceType, ContractMetadataReadiness
   - Extend IntegrationConfigDto with new fields
   - Add readiness computation logic

2. **Frontend Model Extension**
   - Add enums: ContractSourceType, ContractMetadataReadiness
   - Extend IntegrationConfig with new fields
   - Keep aligned with backend

3. **Integrations UI Enhancement**
   - Add "Contract Relationship" collapsible section
   - Fields for Producer, Consumer, Contract, Source Type, Source Location
   - Show readiness status (NotConfigured/Partial/Ready)
   - NO compatibility analysis shown yet

4. **Integration Quality Review Preparation**
   - Update IntegrationQualityRequest to accept relationship metadata
   - Update IntegrationStatus to show metadata readiness
   - NO contract comparison yet

5. **Validation & Security**
   - Validate source locations by type
   - Ensure no secrets stored in metadata
   - Reuse existing SSRF/URL validation
   - Path validation for artifact references

6. **Backward Compatibility**
   - Ensure legacy integrations load
   - Missing fields = null/NotConfigured
   - No migration script needed

7. **Testing**
   - Round-trip serialization tests
   - Legacy integration loading tests
   - EventHub/REST/GraphQL relationship examples
   - Readiness computation tests
   - Secret filtering tests

---

## Not in Phase 2

❌ Contract comparison
❌ Breaking change detection
❌ Compatibility PASS/FAIL analysis
❌ Contract discovery from endpoints
❌ Schema extraction from sources
❌ Separate contract guard page
❌ New producer/consumer registry
❌ Contract versioning

These belong to Phase 3+.

---

## Security Validation

### URL-Based Sources (OpenApi, Endpoint)
- Must use existing SSRF validation
- HTTPS only
- No private IP ranges
- No localhost

### Assembly/Schema References
- Must not allow path traversal (../)
- Prefer logical artifact IDs over absolute paths
- No shell metacharacters

### Secret Detection
- Never store connection strings with credentials
- ContractSourceLocation must not contain SharedAccessKey, Password, Token, Bearer
- Validate on set, reject if sensitive

---

## Integration with Phase 1 Discoveries

Phase 1 discovered integrations can be imported into configured integrations:

1. User sees discovered integration from Phase 1
2. Clicks "Apply" or "Add to configured"
3. Creates new IntegrationConfig
4. ContractMetadataReadiness = NotConfigured initially
5. User can then add relationship metadata in Phase 2 UI

---

## Contract Metadata Readiness Computation

```
NotConfigured:
  if all contract fields null/empty
  if no ProducerService AND no ConsumerService
  → NotConfigured

Partial:
  if some contract fields populated
  but missing required fields for integration type
  → Partial

Ready:
  if sufficient metadata per integration type rules
  → Ready

Type-specific rules (detailed in Readiness Rules section above)
```

---

## Next Steps

1. Extend backend IntegrationConfigDto
2. Add enums (ContractSourceType, ContractMetadataReadiness)
3. Extend frontend IntegrationConfig
4. Add readiness computation
5. Extend Integrations UI
6. Add validation rules
7. Write backend tests (round-trip, legacy, examples)
8. Write frontend tests (display, edit, readiness)
9. Release build verification
10. Prepare for Phase 3 (contract discovery)

---

**Status:** Ready to begin implementation phase.
