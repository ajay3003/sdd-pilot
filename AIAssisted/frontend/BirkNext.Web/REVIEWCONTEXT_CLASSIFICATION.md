# ReviewContext Architecture Classification

## Overview
Comprehensive classification of all services by their relationship to ReviewContext. Follows producer/consumer pattern established in Phase 1-3 refactoring.

## Legend
- **Producer**: Creates ReviewContext via ReviewContextFactory; entry point for analysis pipelines
- **Consumer**: Accepts pre-built ReviewContext as parameter; uses semantic models within
- **Hybrid**: Acts as both producer (builds ReviewContext) and consumer (passes it downstream)
- **Independent**: No ReviewContext involvement; operates on raw artifacts or extraction candidates

---

## Classification Table

### Core Semantic Model Builders (Entry Points)

| Service | Role | Pattern | Notes |
|---------|------|---------|-------|
| ReviewContextFactory | Producer Helper | Factory | Static helper that assembles ReviewContext from 5 semantic models |
| ConstitutionAnalysisService | Independent | Parser | Parses ConstitutionDocument → ConstitutionSemanticModel |
| SpecExplorerService | Independent | Parser | Parses SpecTree → SpecificationSemanticModel |
| PlanAnalysisService | Independent | Parser | Parses PlanDocument → PlanSemanticModel |
| TaskExplorerService | Independent | Parser | Parses TaskTree → TaskSemanticModel |
| DataModelAnalysisService | Independent | Parser | Extracts DataModelSemanticModel from various sources |

### Analysis Services (Producers/Hybrids)

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| DeliveryReadinessService | Hybrid | Producer → Consumers | Builds ReviewContext once; distributes to 4 downstream services |
| QaAuditorService | Hybrid | Producer/Consumer | Optional context param (consumer) with fallback to building (producer) |
| QAReadinessService | Hybrid | Producer/Consumer | Optional context param (consumer) with fallback to building (producer) |
| ConstitutionComplianceService | Consumer | Optional Param | Accepts optional ReviewContext (prepared for future use) |
| ArtifactTraceabilityService | Consumer | Required Param | Requires ReviewContext; does not build semantic models |

### Traceability & Extraction Services

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| IArtifactTraceabilityService | Consumer | Interface | Defines contract: ReviewContext is required |
| TaskSpecAlignmentService | Consumer | Receives Review Context | Takes ReviewContext; analyzes task-spec alignment |
| ExtractionCandidateMetricsService | Independent | Service | Works with extraction candidates and links; no ReviewContext |
| ExtractionSessionService | Independent | Session Service | Manages extraction candidates; no ReviewContext dependency |

### Model Builders & Transformers

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| FlowModelBuilder | Independent | Markdown Parser | Static builder; transforms spec markdown + candidates into FlowModel |
| DocumentViewModelBuilder | Independent | Markdown Parser | Static builder; transforms spec markdown + candidates into DocumentViewModel |
| SpecExplorerService (Build methods) | Independent | Parser | Parses markdown; builds semantic models (not consumers of ReviewContext) |

### Specialized Review Services

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| FrontendQualityReviewService | Independent | Analysis | Custom analysis; no ReviewContext |
| DashboardMetricsService | Independent | Metrics | Dashboard data aggregation; no ReviewContext |
| DashboardSnapshotService | Independent | Snapshot | Health snapshot generation; no ReviewContext |
| QualityReviewService | Independent | Analysis | Quality metrics; no ReviewContext |
| StandardsComplianceService | Independent | Analysis | Standards validation; no ReviewContext |
| ReportExportService | Independent | Export | Report generation; no ReviewContext |

### UI Session Services

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| RuntimeReviewSessionService | Independent | Session | Session state management; no ReviewContext |
| QualityReviewSessionService | Independent | Session | Session state for quality review; no ReviewContext |
| TaskAlignmentSessionService | Independent | Session | Session state for task alignment; no ReviewContext |

### Infrastructure & Configuration

| Service | Role | Pattern | ReviewContext Usage |
|---------|------|---------|---------------------|
| ReviewContextValidator | Consumer Helper | Validator | Validates ReviewContext integrity (preparation) |
| ArtifactParserService | Independent | Parser | Parses artifacts; no ReviewContext |
| ConstitutionAnalysisService | Independent | Analysis | Analyzes constitution; produces semantic model |
| IArtifactTraceabilityService | Consumer Interface | Interface Contract | Defines ReviewContext requirement |
| IConstitutionComplianceService | Consumer Interface | Interface Contract | Optional ReviewContext parameter |
| IQAReadinessService | Consumer Interface | Interface Contract | Optional ReviewContext parameter |
| IQaAuditorService | Consumer Interface | Interface Contract | Optional ReviewContext parameter |

---

## Producer Chain Flow

```
Main Entry: DeliveryReadinessService.Assess()
    ├─ Builds ReviewContext (Producer)
    │   ├─ ConstitutionAnalysisService.BuildSemanticModel()
    │   ├─ SpecExplorerService.BuildSemanticModel()
    │   ├─ PlanAnalysisService.BuildSemanticModel()
    │   ├─ TaskExplorerService.BuildSemanticModel()
    │   └─ DataModelSemanticModel()
    │
    └─ Distributes ReviewContext to Consumers:
        ├─ ArtifactTraceabilityService.Analyze(reviewContext)
        ├─ ConstitutionComplianceService.Analyze(..., reviewContext)
        ├─ QAReadinessService.Assess(..., reviewContext)
        └─ QaAuditorService.Audit(..., reviewContext)
```

---

## Compliance Checklist

- ✓ Phase 1: Core services refactored to accept ReviewContext
  - DeliveryReadinessService builds once and distributes
  - QAReadinessService accepts optional ReviewContext
  - QaAuditorService accepts optional ReviewContext
  
- ✓ Phase 2: Model builders verified (legitimate producers)
  - FlowModelBuilder: Transforms markdown (independent)
  - DocumentViewModelBuilder: Transforms markdown (independent)
  
- ✓ Phase 3: ArtifactTraceability fixed
  - ArtifactTraceabilityService.Analyze requires ReviewContext
  - DeliveryReadinessService passes context to all consumers
  
- ✓ Phase 4: Extraction metrics centralized
  - ExtractionCandidateMetricsService: Dedicated service for duplicate calculations
  - ExtractionReviewList: Delegates to service (consumer)
  
- ✓ Phase 5: Classification complete
  - All services classified by role
  - Producer/consumer patterns documented

---

## Key Patterns Observed

1. **Producer Pattern**: Services that build ReviewContext via ReviewContextFactory
   - Single build location: DeliveryReadinessService
   - Optional fallback builds in QAReadinessService, QaAuditorService

2. **Consumer Pattern**: Services that accept and use pre-built ReviewContext
   - Required: ArtifactTraceabilityService
   - Optional with fallback: QAReadinessService, QaAuditorService, ConstitutionComplianceService

3. **Independent Pattern**: Services with no ReviewContext involvement
   - Markdown parsers: FlowModelBuilder, DocumentViewModelBuilder
   - Extraction handlers: ExtractionCandidateMetricsService, ExtractionSessionService
   - Dashboard/UI services: DashboardMetricsService, RuntimeReviewSessionService, etc.

---

## Next Steps (Phase 6-7)

- Phase 6: Add/update tests for all refactored services
- Phase 7: Final audit against original ReviewContext cleanup requirements
