# Specification Explorer — UX audit (2026-11-13)

Starting HEAD: `f6c82ed9`. Components:
- `Pages/SpecificationExplorer.razor` (host);
- `Components/SpecExplorerPanel.razor` (tree, search, details);
- `Components/SpecificationRelationshipsPanel.razor` (Specification links, from `SpecificationReviewProjection`);
- the shared `Components/TraceabilityItemText.razor` (requirement and scenario text with Given/When/Then; used by Specification Explorer, the review candidate list and Traceability).

Measured on the real M2LB *Skole* specification ("Skoletjenesten — school attendance reporting"): 65 requirements, 65 tests, 11 success criteria, 48 clarifications, 205 preserved source blocks.

| Finding | Verified how | Root cause |
|---|---|---|
| Narrow detail panel with its own scrollbar | DOM: `.se-details` **280 px** wide, `max-height: 600px`, `overflow: auto` | `.se-main.has-details { grid-template-columns: 1fr 280px }` |
| Large gap above **Then** | DOM: When ends at y=482, Then starts at y=529 (**47 px**). A text node `"\n                "` sits between the When and Then step `<div>`s | The line break between two Razor expressions (`@ScenarioPart("When"…)` / `@ScenarioPart("Then"…)`) is kept as a whitespace text node, and the container is `white-space: pre-wrap`, so it renders as blank lines. Shared component, so every consumer had it |
| "65 scenarios dumped by default" in Specification links | Markup: every group is a closed `<details>` | **Not reproduced on HEAD**: links are already collapsed by default, with one count per relationship type. Unchanged |
| Large unstructured-source section | Real page | **Already compact**: one heading, a count, "No likely mapping gaps detected", and two on-demand buttons. Unchanged |

**Relationship semantics (unchanged):**
- "Tests linked directly to a requirement ID" and "Tests organized under a user story" are separate counts with their own denominators, and they overlap by design.
- User story containment is never counted as requirement coverage; reviewed coverage (MT-7) is elsewhere.
