# Data Model: Traceability & Coverage

**Date**: 2026-06-04  
**Branch**: `002-traceability-coverage`

---

## Design Principle: Open-Ended Extensibility

The `TraceLink` entity is designed as the **foundation** for future cross-artifact traceability features:

| Future feature | New link kind |
|---|---|
| AI Change Auditor | Requirement → AiSession |
| Delta Impact Analysis | Requirement → CodeChange |
| Spec Drift Detection | Requirement → Commit |
| AI QA Auditor | Requirement → AiSession (read) |

All of these can be supported by adding new `SourceKind`/`TargetKind` string values without any schema change.

---

## TraceLink Entity

```
trace_links table
-----------------
id              UUID    PK
project_id      VARCHAR(200)    NOT NULL
source_id       UUID            NOT NULL      ← who covers / who links
source_kind     VARCHAR(50)     NOT NULL      ← "Scenario" | future: "Commit", "CodeChange", "AiSession"
target_id       UUID            NOT NULL      ← what is covered / what is linked
target_kind     VARCHAR(50)     NOT NULL      ← "Scenario" | future: same set
link_type       VARCHAR(50)     NOT NULL      ← "Covers" | "RelatedTo"
created_at      TIMESTAMPTZ     NOT NULL
created_by      VARCHAR(200)    NULL
notes           TEXT            NULL
```

**No FK constraints.** Referential integrity is enforced in `TraceLinkService`. This deliberate choice avoids migration work when future entity types (commits, AI sessions, code changes) are added.

### Indexes
```sql
CREATE INDEX ix_trace_links_project_target
    ON trace_links (project_id, target_kind, target_id);
-- Used by coverage query: "what covers this requirement?"

CREATE INDEX ix_trace_links_project_source
    ON trace_links (project_id, source_kind, source_id);
-- Used by orphan query: "what does this test cover?"
```

---

## C# Entity

```csharp
// BirkNext.Api/Models/TraceLink.cs
public class TraceLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;
    public Guid SourceId { get; set; }
    public string SourceKind { get; set; } = TraceLinkArtifactKind.Scenario;
    public Guid TargetId { get; set; }
    public string TargetKind { get; set; } = TraceLinkArtifactKind.Scenario;
    public TraceLinkType LinkType { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }
}

// BirkNext.Api/Models/TraceLinkType.cs
public enum TraceLinkType
{
    Covers,
    RelatedTo
    // Future: Implements, Verifies, References
}

// BirkNext.Api/Models/TraceLinkArtifactKind.cs
public static class TraceLinkArtifactKind
{
    public const string Scenario = "Scenario";
    // Future: "Commit", "CodeChange", "AiSession"
}
```

---

## Coverage Computation (v1)

Inputs: all accepted Scenarios + all TraceLinks for a project (both filtered to `source_kind='Scenario'`, `target_kind='Scenario'`).

```
Covered requirement:
  ∃ TraceLink: target_id = req.Id
             ∧ link_type = Covers
             ∧ source scenario exists
             ∧ source scenario.Kind = Test
             ∧ source scenario.ReviewStatus = Accepted

Orphan test:
  ∄ TraceLink: source_id = test.Id
             ∧ link_type = Covers
             ∧ target scenario exists
             ∧ target scenario.Kind = Requirement
             ∧ target scenario.ReviewStatus = Accepted

Excluded from all calculations:
  - Scenarios where ReviewStatus ≠ Accepted
  - Scenarios where Kind = NeedsClarification
```

---

## Derived Types (service layer)

```csharp
public record TraceLinkWithTest(TraceLink Link, Scenario Test);

public class TraceabilityMatrixRow
{
    public Scenario Requirement { get; init; }
    public IReadOnlyList<TraceLinkWithTest> LinkedTests { get; init; }
    public CoverageStatus CoverageStatus { get; init; }  // Covered | NotCovered
}

public class CoverageSummary
{
    public int TotalRequirements { get; init; }
    public int CoveredRequirements { get; init; }
    public int NotCoveredRequirements { get; init; }
    public double CoveragePercent { get; init; }
    public int OrphanTests { get; init; }
}

public enum CoverageStatus { Covered, NotCovered }
```

---

## Future Evolution Path

When "Requirement → Commit" is needed:
1. Create a `Commit` entity (Guid PK, CommitSha, ProjectId, etc.)
2. Create a `CommitTraceLinkService` that creates TraceLinks with `SourceKind="Commit"`
3. Query: `WHERE source_kind='Commit' AND target_kind='Scenario'`
4. **Zero schema changes required.**

When "Requirement → AiSession" is needed — same pattern with `SourceKind="AiSession"`.
