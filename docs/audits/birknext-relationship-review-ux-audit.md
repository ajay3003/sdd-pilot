# Relationship Review — UX audit and change (2026-11-13)

Starting HEAD: `41d1cd06`. Page: `Pages/CorrelationReview.razor` (Overview, Needs Review, Relationships, History & Governance). The entity evidence comes from the shared `Components/RelationshipEntityInspector.razor`, and decisions use the existing decision API (`DecideAsync`). No engine, registry or decision store was added.

## Verified issues
| Issue | Root cause | Change |
|---|---|---|
| "Source evidence — 0 occurrences" beside listed alias declarations | The count is `d.Occurrences`: **located appearances in analysed source snapshots**. Declarations and aliases are separate evidence lists, so the label said "evidence" for one specific kind | Summary is now "Located in source · N occurrence(s)". The empty state says no located appearance is recorded and that the declaration and aliases above are evidence. The count itself is unchanged |
| Rationale field far below the identity conflict | Two full entity inspectors (aliases, declarations, occurrences for both sides) rendered between the summary and the decision | The inspectors sit in one disclosure ("Inspect source evidence for both entities…") between the side-by-side summary and the options. Options, rationale and Same / Separate / Defer follow directly |

**Unchanged:**
- the three options and their explanations;
- rationale required for Same and Separate (buttons disabled without it), and Defer keeps release readiness blocked;
- no automatic decision;
- counts, governance gate, history and the impact preview.

## Not done in this pass (gaps)
- The review queue keeps its two-column layout (2fr/3fr, not a narrow panel).
- No paging changes for optional suggestions, and no Relationships-tab empty-state change.
- The history presentation is unchanged.
