# Canonical interpretation & correlation — Phase 0 audit map (2026-11-03)

Scope: queue item #16 ("one interpretation engine, many consumers"). Inputs: section B of `global-configuration-and-evidence-ownership-audit-map-2026-11-03.md`
(parsers and rule differences) and a fresh read of the work-item interpreters.

## 1. Interpreters found

| Implementation | Owner | Input | Output | Consumers | Rule model | Duplicates | Alias logic | Confidence | Provenance | Persisted / versioned | Decision |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `DeliveryWorkItemRelationships.Classify` | Delivery (backend) | stored snapshot feature + task | `DeliveryTaskRelationship` + group + note | Delivery report, Overview, Stories, exports | 8 ordered rules, pure | Story-tag and phase regexes duplicated in `DeliverySnapshotParser` | none (never maps US1 → US-1.1) | `DeliveryMappingConfidence` (Explicit / PhaseTitle / GroupLevel / None) | plain-language note | frozen in weekly report JSON; `ParserVersion` 4 | **lifted into the shared engine** |
| `DeliverySnapshotParser` story/scenario/removed headings | Delivery (backend) | spec.md headings + raw lines | stories (US n / S n, Removed) | Delivery | regexes | — | — | — | file + line | stored per snapshot | **regexes moved to shared `SpecStoryHeadings`** (parser references them) |
| `ArtifactTraceabilityService` orphan tasks | Traceability (frontend) | TaskTree + SpecTree | gaps | Requirements Traceability page | "no FR/SC reference" | its own notion of an unlinked task, ignorant of shared/foundational work | — | — | — | per run | **consumes shared engine** (classification on gaps + removed/unresolved story issues) |
| `DeliveryWorkItems.RelationshipLabel` (frontend) | Delivery UI | relationship | label | Delivery tree/list/legend | switch | copy of the label words | — | — | — | — | **delegates to shared `WorkItemInterpreter.Label`** |
| `TaskExplorerService` / `SpecExplorerService` | Documents (frontend, compiled into backend) | Markdown | TaskTree / SpecTree | all document consumers | token parsers | — | — | — | node provenance | workspace | keep (normalised parser output the engine reads) |
| `RequirementReferenceParser` | shared | text | FR/SC ids | TaskExplorer, Traceability | regex | — | normalises FR-1 / FR-001 | — | — | — | keep (already shared) |
| `TaskSpecAlignmentService` positional fallback | Task Alignment | tasks + spec | alignment | Task Alignment Review | heuristic | partly overlaps story linking | — | AlignmentStatus | — | per run | follow-up: read the shared classification instead of positional story guesses |
| Topology resolver, ContractEvidence, ProjectEvidencePackages capabilities | Integrations / Project Evidence | source evidence | systems, contracts, package roles | IQR, Project Evidence, Topology | per-domain rules | system/package aliases in two places | MU ↔ Meldingsutveksleren in topology profile | Bound / Ambiguous | evidence refs | snapshot-bound | follow-up (not migrated in this pass) |

## 2. What #16 delivers (scoped)

1. `shared/WorkItemInterpretation.cs` — typed inputs (`InterpretedFeature`, `InterpretedStory`, `InterpretedTask`), `WorkItemInterpreter.Classify` with a
   documented rule order and a **RuleId per outcome** (WI-SHARED, WI-PHASE, WI-MALFORMED, WI-REMOVED, WI-EXPLICIT, WI-FUNCTIONAL, WI-SCENARIO, WI-UNRESOLVED),
   `RuleSetVersion = "work-items/1"`, shared labels and explanations, and `SpecStoryHeadings` (the story / scenario / removed-marker ingest rules).
2. Delivery is an adapter onto the engine: behaviour unchanged (all 96 Delivery tests pass). New reports record `InterpretationRuleSet`, and each task row records
   its `RelationshipRule`. Older frozen reports are read as they are and never reclassified.
3. Traceability consumes the same engine: an untagged setup/polish task is labelled *Shared & Foundational* (informational, not a defect), and references to removed or
   undefined stories become review issues with the same note Delivery shows. The classification renders as a type badge (`MetaBadge`), not a status.
4. Tests: regression cases A–F plus explicit-over-phase, malformed tags, determinism, heading ingest parity, and a Traceability consumer test.

## 3. Not done (reported, not faked)

- No persisted `CanonicalEntity` / `CanonicalRelation` tables and no interpretation snapshot; the rule-set version is recorded on Delivery reports only.
- Cases G–N (cross-module identities, capability candidates, contract occurrences, aliases, symbol correlation, test VERIFIES) need the source/contract/test
  correlation layer — not started.
- Human-reviewed associations shared across consumers: not started (Delivery's direction review stays Delivery-owned).
- Removed-story detection in Traceability reads story headings only (SpecTree has no raw Markdown), so a struck-through heading that the spec parser does not
  recognise as a story is missed there (Delivery reads the raw lines).
- Enums are still named `DeliveryTaskRelationship` / `DeliveryMappingConfidence`, because they are persisted in frozen reports; renaming needs a JSON alias.
