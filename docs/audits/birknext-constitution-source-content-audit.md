# Explorer source content (Constitution, Plan, Task, Specification, Data Model) — audit (2026-11-13)

Starting HEAD: `01cb16e1`. All five Explorers render preserved, unmapped source through **one shared component**, `Components/MarkdownSourceNotesPanel.razor`, classified by `UnmappedSourceContent`. Fixing presentation there fixes every Explorer, and no second source reader was created.

## What the panel already did (kept)
- A compact summary: block count, categories and a neutral verdict ("No likely mapping gaps…" / "N potential mapping gaps").
- Informational, change/history and reference groups, collapsed by default.
- Technical raw source last and collapsed.
- Classification computed once per document.

Categories, block identity, line ranges and order come from the parser and are not changed.

## Confirmed problems
1. **Technical raw source:** once opened, every block was an `<article>` with its own heading ("Lines 1–1 · Text · Structural") and a `<pre>`. On real documents this means 340 (Constitution) or 521 (Tasks) heading cards: hundreds of headings and very long pages.
2. **Potential mapping gaps:** each gap was a full card, all rendered (dozens on the Constitution).

## Change (presentation only)
- **Raw source becomes a compact reader:**
  - an ordered list with one row per block: a small line-range gutter and the verbatim text in `<pre>`;
  - block type and category in the row's tooltip and in screen-reader text, with no heading per block;
  - every `data-source-*` attribute kept, plus `data-block-type` and `data-category`;
  - rows render 200 at a time behind an explicit "Show next N blocks (M of T not shown yet)" button, so blocks are never dropped and are always counted;
  - text is selectable and copyable.
- **Gaps become compact rows:** the first 6 are shown, the rest behind "Show all N potential mapping gaps". The reason and line range stay on each row, and gaps are still never promoted into the structured model.
