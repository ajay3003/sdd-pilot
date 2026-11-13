# Specification Explorer — UX implementation (2026-11-13)

Audit: `docs/audits/birknext-specification-explorer-ux-audit.md`.

| Change | Files |
|---|---|
| **BDD spacing:** When and Then are emitted with no whitespace node between them. Defensive CSS: the scenario container and each step use normal whitespace, and only a step's own text keeps `pre-wrap`, so meaningful line breaks in the source stay. Steps are .15rem apart. **No text is trimmed or rewritten.** | `Components/TraceabilityItemText.razor` (+ `.razor.css`) — the shared component, so every consumer is fixed |
| **Focus view:** the selected item opens as a full-width `<section>` card below the structure (`se-focus`, `aria-labelledby`), with no 600 px cap or inner scrollbar. Opening moves focus to its heading; **Back to structure** (was "Close") returns focus to the same tree row. Expansion, search and hidden nodes are kept | `Components/SpecExplorerPanel.razor` (+ `.razor.css`) |
| **User Guide:** browsing, focus view, link semantics, unstructured content and where tests are prepared | `Components/Guide/GuideProject.razor` (Explorers article) |

**Unchanged:** parsing, the projection, IDs, counts, Specification links, the source-content sections, Traceability and Test Plan Suggestions links, and all data.

## Verification
- **New tests:**
  - no whitespace text node between steps, and the step text, including a multi-line Then, rendered exactly;
  - the focus view is a labelled section with a focusable heading, and Back to structure closes it with the tree rows unchanged.
- **Full frontend suite and browser:** see `docs/audits/birknext-specification-explorer-browser-acceptance.md`.
