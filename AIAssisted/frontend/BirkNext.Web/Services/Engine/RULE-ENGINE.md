# Rule Engine — Developer Reference

This document describes the architecture of the Rule Engine used by BirkNext QA Review Studio.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────────┐
│  StandardsComplianceService                                         │
│  ─ discovers packs via index.json                                   │
│  ─ loads & validates each JSON rule pack                            │
│  ─ builds RuleContext from artifact text                            │
│  ─ maps engine output → StandardsComplianceReport                  │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ IRulePack[]
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  RuleEngine                                                         │
│  ─ iterates packs                                                   │
│  ─ calls pack.Execute(context)                                      │
│  ─ catches exceptions per pack (isolation)                          │
│  ─ returns List<RulePackResult>                                     │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ RuleContext (shared, read-only)
                      ┌────────┴─────────────────────────┐
                      ▼                                   ▼
        ┌─────────────────────────┐       ┌─────────────────────────┐
        │  StandardsKeywordRulePack│       │  Custom IRulePack        │
        │  (data-driven)          │       │  (code-driven)           │
        │  wraps StandardRulePack │       │  e.g. QaSpecRulePack     │
        │  loaded from JSON       │       │  e.g. QaTraceRulePack    │
        └─────────────────────────┘       └─────────────────────────┘
```

There are two orthogonal rule engines in the project:

| Engine | Location | Purpose |
|--------|----------|---------|
| **Rule Engine** (this document) | `Services/Engine/` | Standards compliance and QA auditing |
| **Extraction Rule Engine** | `Services/ExtractionRuleEngine.cs` | Classifying text blocks during artifact parsing |

These are independent systems and must not be conflated.

---

## Engine responsibilities

`RuleEngine` (`Services/Engine/RuleEngine.cs`) contains only generic infrastructure:

- **Execute** — runs each `IRulePack` in sequence against a shared `RuleContext`
- **Isolate** — catches exceptions per pack; a failing pack produces an error-flagged `RulePackResult` with empty findings; other packs continue
- **Score** — `ComputeCoverageScore` provides the shared scoring formula for data-driven packs

The engine has no knowledge of WCAG, OWASP, GDPR, ISO 25010, or any other standard. It knows only `IRulePack`, `RuleContext`, `RuleFinding`, and `RulePackResult`.

---

## Rule pack interface

```csharp
public interface IRulePack
{
    string RulePackId   { get; }   // stable identifier, e.g. "WCAG22"
    string RulePackName { get; }   // human-readable label, used in error messages
    RulePackResult Execute(RuleContext context);
}
```

All packs — whether data-driven or custom — implement this interface. The engine treats them identically.

---

## Shared context

`RuleContext` is created once per assessment and passed read-only to every pack:

```csharp
public sealed class RuleContext
{
    // Pre-concatenated artifact text — for keyword-based packs
    public string CombinedText { get; init; }

    // Parsed domain models — for structural packs
    public ConstitutionDocument? Constitution { get; init; }
    public SpecTree?             Spec         { get; init; }
    public PlanDocument?         Plan         { get; init; }
    public TaskTree?             Tasks        { get; init; }

    // Pre-computed sub-reports — shared so no pack triggers duplicate analysis
    public ArtifactTraceabilityReport?   Trace            { get; init; }
    public ConstitutionComplianceReport? ComplianceReport { get; init; }
}
```

Packs must not modify the context or trigger their own sub-analysis. If a pack needs data that requires non-trivial computation (e.g. traceability), that computation belongs in the pre-run phase, stored in `RuleContext` and shared.

---

## Rule pack types

### 1 — Data-driven rule packs (JSON)

Ideal for standards like WCAG, OWASP, GDPR, ISO 25010. Rules are defined entirely in JSON — no C# code per standard.

`StandardsKeywordRulePack` (`Services/Engine/Packs/StandardsKeywordRulePack.cs`) is the single executor for all data-driven packs. It:

1. Receives a `StandardRulePack` (loaded from JSON)
2. For each rule, searches `RuleContext.CombinedText` for required and optional keywords
3. Returns a `RulePackResult` with one `RuleFinding` per rule

**Scoring:**

| Keyword match           | Status    | Score weight |
|-------------------------|-----------|--------------|
| Any `requiredKeywords`  | Passed    | 1.0          |
| Any `optionalKeywords`  | Warning   | 0.5          |
| No match                | Failed    | 0.0          |

Final score = `(Σ weights / applicable rule count) × 100`, rounded to 1 decimal.

### 2 — Custom rule packs (code-driven)

For logic that cannot be expressed as keyword matching — traceability, graph traversal, dependency analysis, constitution-specific algorithms.

Implement `IRulePack` directly. Examples in this codebase:

| Pack | File | What it checks |
|------|------|----------------|
| `QaSpecificationRulePack` | `Packs/QaSpecificationRulePack.cs` | Acceptance criteria, requirement coverage, edge cases |
| `QaPlanRulePack` | `Packs/QaPlanRulePack.cs` | Implementation phases, ADR rationale, risk analysis, testing strategy |
| `QaTaskRulePack` | `Packs/QaTaskRulePack.cs` | Orphan tasks, testing tasks, requirement references |
| `QaTraceabilityRulePack` | `Packs/QaTraceabilityRulePack.cs` | Constitution → Spec → Plan → Task coverage chains |
| `QaConstitutionRulePack` | `Packs/QaConstitutionRulePack.cs` | Constitution rule coverage using pre-computed compliance report |
| `ConstitutionCoverageRulePack` | `Packs/ConstitutionCoverageRulePack.cs` | Wraps `IConstitutionComplianceService` as an `IRulePack` |

Custom packs follow the same `IRulePack` contract. They use parsed domain models from `RuleContext` (e.g. `context.Spec`, `context.Plan`) rather than raw text.

---

## Rule pack discovery (data-driven packs)

`StandardsComplianceService` discovers rule packs at runtime from `wwwroot/standards/index.json`.

**Discovery flow:**

1. `InitializeAsync()` fetches `standards/index.json`
2. For each entry, fetches the rule pack JSON at the given path
3. Validates the pack (see validation rules below)
4. Valid packs are stored in a dictionary keyed by `standardId` (case-insensitive)
5. `DiscoveredPacks` exposes all index entries in order (including failed loads)

**`index.json` entry schema:**

```json
{
  "standardId":  "WCAG22",              // free-form unique identifier string
  "label":       "WCAG 2.2",            // display name in the UI
  "description": "Web Content ...",     // subtitle in the UI
  "path":        "standards/wcag/2.2/rule-pack.json"
}
```

`standardId` is a plain string — no enum, no C# mapping required. New entries are picked up automatically on the next page load.

**Pack validation at load time:**

A pack is rejected (load error, not a crash) if:
- `standardId` is missing or blank
- `standardName` is missing or blank
- `rules` is empty
- Any rule has a missing `ruleId`
- Any rule has an unrecognised `severity` (not Critical / High / Medium / Low / Info)
- Any rule has empty `requiredKeywords` and empty `optionalKeywords`

---

## Versioning

Each rule pack carries two distinct version numbers:

| Field             | Tracks | Changes when |
|-------------------|--------|--------------|
| `standardVersion` | The external standard body's version (e.g. WCAG `"2.2"`) | The standard body publishes a new release |
| `rulePackVersion` | This JSON file's revision (e.g. `"1.1"`) | Rules are added, changed, or removed within the same standard version |

Reports record both values. Updating rules without a standard version change increments only `rulePackVersion`.

**Adding a new official version of an existing standard** (e.g. WCAG 2.3):

1. Create `standards/wcag/2.3/rule-pack.json`
2. Add a new entry to `index.json`
3. Optionally retire the old entry

No C# changes needed.

---

## Error handling guarantees

- An exception in any pack does not stop other packs from running.
- A pack that throws returns an error-flagged `RulePackResult` with `Error` set and empty `Findings`.
- Invalid JSON or a missing rule pack file surfaces as a `RulePackLoadResult` with `Error` set; the pack is excluded from execution.
- The `StandardsComplianceService.Assess()` method never throws; it returns a report with whatever successfully ran.
- The Blazor page renders load warnings for any `RulePackLoadResult` where `Error is not null`.

---

## How to add a new data-driven standard

1. Create `wwwroot/standards/<name>/<version>/rule-pack.json` — follow the JSON schema in `wwwroot/standards/README.md`.
2. Add an entry to `wwwroot/standards/index.json`.

Done. No C# changes required.

---

## How to add a custom rule pack

1. Create a class implementing `IRulePack` in `Services/Engine/Packs/`:

```csharp
public sealed class MyCustomRulePack : IRulePack
{
    public string RulePackId   => "my-custom-pack";
    public string RulePackName => "My Custom Pack";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();

        // Use context.Spec, context.Plan, context.Tasks, context.Trace, etc.
        // Do not call external services. Do not throw — return an error result instead.

        return new RulePackResult
        {
            RulePackId   = RulePackId,
            RulePackName = RulePackName,
            Findings     = findings,
            Score        = RuleEngine.ComputeCoverageScore(findings),
        };
    }
}
```

2. Instantiate and include the pack wherever `RuleEngine.Run` is called (e.g. in the service that orchestrates the relevant assessment).

No changes to `RuleEngine`, `IRulePack`, or any existing packs are needed.

---

## Output models

| Type | Produced by | Contains |
|------|-------------|---------|
| `RulePackResult` | Each `IRulePack.Execute()` | Findings, gaps, score, optional error |
| `RuleFinding` | Individual rules within a pack | Rule ID, category, status, severity, evidence, recommendation |
| `RuleGap` | Packs that detect missing coverage | Gap area, severity, optional item reference |

Severity and status are strings (`"High"`, `"Passed"`, etc.) within the engine layer. Consuming services map them to their own enums as needed.
