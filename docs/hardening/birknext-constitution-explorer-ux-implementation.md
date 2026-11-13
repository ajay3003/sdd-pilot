# Constitution, Plan and Task Explorer — UX implementation (2026-11-13)

One commit covers three queued tasks, because their main defect is one shared component. Audit: `docs/audits/birknext-constitution-source-content-audit.md`.

| Task | Change | Files |
|---|---|---|
| 8 Constitution Explorer (and every Explorer) | Technical raw source as a **compact reader**: one row per block, line gutter, verbatim text, metadata on hover/focus and for screen readers, rendered 200 at a time with an explicit "Show next…" (never dropped). **Potential mapping gaps** as compact rows: first 6 shown, the rest on request | `Components/MarkdownSourceNotesPanel.razor` (+ `.razor.css`) |
| 9 Plan Explorer | **Plan Summary rendered as Markdown** with the shared safe renderer (`MarkdownRenderingService`: raw HTML escaped, safe link schemes only). `**Names:**` and similar markers were shown as raw text in a `<p>`. `white-space: pre-wrap` removed from the summary (it would turn line breaks between HTML blocks into gaps). The source text is unchanged | `Components/PlanExplorerPanel.razor`, `wwwroot/css/plan-explorer.css` |
| 10 Task Explorer | The 300 px side panel (620 px cap, own scrollbar) becomes a **full-width focus section** below the task structure. Opening focuses it; **Back to tasks** (was an icon-only "×") returns focus to the same row (rows get stable DOM ids and `tabindex=-1`). Expansion, filters and search are kept. "Marked done in Task artifact" wording was already in place | `Components/TaskExplorerPanel.razor` (+ `.razor.css`) |
| 7 follow-up | Focus moves in Specification Explorer are non-fatal in hosts without JS focus support | `Components/SpecExplorerPanel.razor` |

**Unchanged:** parsers, classification, block identity, line ranges, order, counts, risk, complexity and constitution semantics, task status, evidence, and every tab.

## Remaining gaps (not done in this pass, recorded honestly)
- **Constitution:** no principle-first reordering of the overview and no page-width change. The narrow column in the screenshot is the module chooser card, which is shared by every Explorer.
- **Plan:** "What this plan means for testing" is not regrouped into categories (no existing classification to reuse), and the risk/complexity cards are not merged.
- **Task:** the filter toolbar is not regrouped, and the testing-work view is unchanged.
- No browser acceptance of tasks 8–10 yet: see the browser acceptance section once run.
