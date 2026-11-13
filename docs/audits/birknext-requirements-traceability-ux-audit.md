# Requirements Traceability — UX audit and change (2026-11-13)

Starting HEAD: `41d1cd06`. Page: `Pages/ArtifactTraceability.razor`, with labels in `TraceabilityLabels`. Shared Requirement → Code → Test evidence comes from the existing shared interpretation; nothing was recomputed.

## Verified
| Item | Finding | Change |
|---|---|---|
| Green checkmarks beside assessed transitions | `SemanticStatus.Completed` (positive intent, green ✓) labelled "Assessed": it can read as validated or approved coverage. Its actual meaning is that every item of the transition was examined for explicit references | Badge is now `SemanticStatus.InventoryComplete` (informational, "does not confirm quality") labelled **"Assessment completed"**. Not-assessed stays neutral |
| Plan → Tasks | Already "Not assessed" with its reason (no stable Plan-to-Task identifiers) and no percentage (`TraceabilityLabels.PlanToTaskNotAssessed`; test asserts no "%") | Unchanged |
| Low explicit-reference percentages | Already neutral bars, never red or amber (existing test) | Unchanged |
| Document scope vs shared scope | Already separate panels with their own labels | Unchanged |

**Unchanged:** all counts and denominators (rules, requirements, tasks), observation classification, policy significance, MT-7 rules, the matrix and the tabs.

## Not done in this pass (gaps)
- No compact list for the reference observations (229 on the real project) or for the matrix.
- No focus view for long requirement text.
