# Data Model: Delta Impact Analysis

**Branch**: `003-delta-impact-analysis`  
**Depends on**: `trace_links` table (002-traceability-coverage)

---

## No new database tables

Delta Impact Analysis is a **read-only derived view** over existing data. It adds zero new tables or migrations — all data comes from:
- `scenarios` table (requirements and tests)
- `trace_links` table (Covers links)

---

## Service-layer types (ImpactAnalysisService.cs)

```
RiskLevel: enum { High, Medium, Low }

ImpactedTest:
  Test: Scenario
  Link: TraceLink

RegressionItem:
  Test: Scenario
  Reason: string

RequirementImpactSummary:
  TotalLinkedTests: int
  AcceptedTests: int      // = TotalLinkedTests (v1: all scenarios accepted)
  MissingCoverage: int    // 1 if 0 tests, 0 otherwise
  RiskLevel: RiskLevel

RequirementImpact:
  Requirement: Scenario
  LinkedTests: ImpactedTest[]
  RegressionRecommendation: RegressionItem[]
  Summary: RequirementImpactSummary

RequirementRiskItem:
  Requirement: Scenario
  RiskLevel: RiskLevel
  LinkedTestCount: int

ImpactSummary:
  TotalRequirements: int
  HighRiskCount: int
  MediumRiskCount: int
  LowRiskCount: int
  Requirements: RequirementRiskItem[]
```

---

## Risk calculation

```
RiskLevel = linkedTestCount switch {
    0 => High,
    1 => Medium,
    _ => Low
}
```

---

## Architecture position

```
┌─────────────────────────────────────────────────────────────┐
│                    Impact Engine                             │
│                                                             │
│  ImpactAnalysisService                                      │
│  ├── reads from AppDbContext (scenarios, trace_links)       │
│  ├── computes RiskLevel per requirement                     │
│  ├── builds RegressionRecommendation                        │
│  └── aggregates ImpactSummary                               │
│                                                             │
│  Future plugins into this engine:                           │
│  ├── AI Change Auditor    → adds AiSession → Req links      │
│  ├── Spec Drift Detection → adds Commit → Req links         │
│  ├── AI Link Suggestions  → reads engine output             │
│  └── AI QA Auditor        → reads engine output             │
└─────────────────────────────────────────────────────────────┘
         ↓ reads from
┌─────────────────────────────────────────────────────────────┐
│            trace_links table (002-traceability-coverage)     │
│  source_id | source_kind | target_id | target_kind | type   │
└─────────────────────────────────────────────────────────────┘
```

Future features extend the impact engine by:
1. Adding new `SourceKind` values to `trace_links` (no schema change)
2. Creating new service methods that read those new link kinds
3. The engine aggregates across all link kinds to build a richer picture
