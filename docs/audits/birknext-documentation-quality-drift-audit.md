# BirkNext — Documentation Quality & Drift: audit (2026-11-10)

Branch `008-traceability-first`. **Baseline `3ca01b8c`** (after the Contract Testing final hardening). Working tree: only the user's
`appsettings.Local.json` and other sessions' untracked files; no overlapping Documentation Drift work.

## Current architecture (verified in code)

| Part | Where | Notes |
|---|---|---|
| Page `/documentation-drift` (alias `/spec-drift`), tabs Overview · Compare · Changes · Drift | `frontend/BirkNext.Web/Pages/SpecDrift.razor` | Compare is the default tab |
| Panel (all tab content) | `Components/DocumentationDriftPanel.razor` | Overview dashboard, Compare (5 modes), Changes, Drift cards |
| Catalogue, identity, lineage, text comparison | `Services/DocumentationDrift.cs` | authored = artifact revisions (`rev:{id}`), generated = generated set artifacts (`gen:{set}|{path}`); lineage = same archive root (`project:{name}`), else one import |
| Structured Data Model comparison (the only structured engine) | `Services/DocumentationDriftComposite.cs` | composite authored intent (union with provenance, explicit-change replacement, authored conflicts), concept-by-concept comparison |
| Card states / presentation | `Services/DocumentationDriftCards.cs`, `DocumentationDriftPresentation.cs` | |
| Persistence | `SddLifecycleState.DocumentationDriftComparisons` → `saved_workspaces.sdd_lifecycle_json` | no dedicated table; **capped at 50** |
| Project evidence strip ("1 of 2 required evidence roles ready") | `Components/ProjectContextStrip.razor`, `Services/ProjectEvidence/ProjectEvidenceResolver.cs` | required roles: application source + documentation; generated docs optional |
| Separate backend drift engine | `Services/SourceAnalysis/GeneratedDocumentation/*` | feeds Generated Documentation Health, not this page |

## Screenshot observations → verification

| Observation | Verified behaviour | Gap? |
|---|---|---|
| "1 of 2 required evidence roles ready" | project-level roles (application source, documentation); generated docs are an optional role there, so the count says nothing about module comparability | **Yes (UX)**: no explanation on the drift page; it reads as a blocker |
| 7 ready to compare, 5 generated missing (M2LB) | derived per module and area from the catalogue; independently confirmed by the oracle (7 comparable, 5 without generated Data Model) | no (correct); presentation only |
| 0 saved consistency comparisons | each area had to be selected and compared manually (Module, Area, Compare, Save) | **Yes**: no bulk comparison |
| 420 authored / 91 generated / 15 modules | inventory counts | not a quality signal; mixed with completion on the Overview |
| Compare mode with 3 authored vs 1 generated (AutorisasjonService) | composite intent: additive union; explicit "changed/replaces…" in a later pack supersedes; other contradictions = authored conflict (never drift) | no engine defect; applicability is evidence-based |
| Changes needs same-project lineage, one import | correct; empty state exists | moves not detected (renamed document = removed + added) |
| Drift shows 12 module areas, no evaluated differences | cards only; no findings list, no review | **Yes**: no findings workflow |
| Repeated large "Open comparison" buttons, whitespace | presentational | Yes (UX) |

## Confirmed gaps

| Id | Gap | Root cause | Change |
|---|---|---|---|
| DQ-G1 | Evidence readiness unexplained on this page | project strip is generic | evidence panel on the Overview: authored / generated / source, required for what, and why project readiness ≠ area comparability |
| DQ-G2 | No bulk comparison | per-area UI only | Compare all eligible / selected / changed through the same engine; idempotent |
| DQ-G3 | Missing evidence vs drift not separated in a findings model; candidate ≠ comparison | states only on cards | area states (Compared, Not assessed, Missing generated, Missing authored, Ambiguous, Not supported) and findings only from comparisons |
| DQ-G4 | No finding identity, severity, evidence model | items are saved rows | stable finding ids, expected vs actual, evidence, rule, severity rationale |
| DQ-G5 | No human review | "reviewed" = a saved comparison exists | append-only review decisions (6 actions), reopened when inputs change |
| DQ-G6 | No quality report | — | Markdown report through the existing download helper; "Not assessed" before comparison |
| DQ-G7 | Renamed/moved documents invisible in Changes | logical key = path | exact-content moves within a lineage |
| DQ-G8 | Saved comparisons capped at 50 | list cap | 200 + idempotent bulk (no duplicate saves) |
| DQ-G9 | Requirements/test/impact links from findings | none | PARTIAL: specification packs by provenance + module links to explorers/traceability/impact; no evidence-backed requirement or test link |
| DQ-G10 | Structured comparison for API, Architecture, Configuration documentation | only the Data Model extractor exists on this page | NOT SUPPORTED here (listed as such, never compared by text similarity) |
