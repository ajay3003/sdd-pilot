# Generated Documentation Explorer — reader, outline, search, diagrams and evidence

**Date:** 2026-11-14 · branch `008-traceability-first` · local commits only (no push, no deployment, no remote call).
**Builds on:** a6b20700 (Read with drawn diagrams), dd3650c5 (Health module focus), 8f4f6a1b (untrusted Markdown).

## Audit: what exists and who owns it

| Concern | Owner (code) | Notes |
|---|---|---|
| Document catalog | `DocumentationDrift.Catalog` and `Imports` over the client `SddLifecycle` (`GeneratedDocumentationSets`, content by fingerprint) | Scoped by project import, then module. No backend call except the OpenAPI/GraphQL structure reader. |
| Categories | `GeneratedDocumentationExplorer.RoleOf` | Extension and file-name heuristics, else the import's artifact kind. Not hardcoded to four groups; `Other` is kept. |
| Markdown | `MarkdownRenderingService.RenderUntrusted` | Markdig with HTML disabled; only web, mailto and relative links; no `<script>` or `on*` handlers; remote images replaced by text. Heading ids match `UntrustedHeadings`. |
| Diagrams | `GeneratedDiagrams` and `DiagramLayout`, drawn by `GeneratedDiagramView` as Blazor SVG | **Not Mermaid JS**: no script is loaded or executed. Sequence and flowchart diagrams are drawn; other types show "preview unavailable" plus the source; more than 80 elements falls back to steps. |
| Health | Generated Documentation Health (`GeneratedDocumentationDiagnosticService`) | Unchanged; the Explorer links to it. |
| Revisions | `DocumentationDrift.Versions` (same logical key across imports) | Drift owns comparison. |

## Root causes of the reported problems

1. **Narrow diagrams with inner scrollbars.** The whole reader was capped at `max-width: 60rem`, and the canvas was a scroll box (`overflow: auto; max-height: 34rem`). Zoom set the SVG's pixel width, so the canvas scrolled inside the page and dragging was impossible.
   - Found in acceptance: a zoomed canvas grew to the drawing's width (2,308 px instead of 1,086 px) because the figure had no width bound.
2. **Search was not navigable.** It listed matching lines (`L12 …`) with no highlight, no count and no way to jump to a match.
3. **Library.** Cards were titled by type, with the file path as the main text; the document's own title was not used.
4. **Health → Explorer link.** It passed `module=(repository)`, but the Explorer calls that module "Repository level", so the link fell back to the first module.

## What changed

**Reader**
- Prose keeps a comfortable line length (62rem). Tables, code blocks and diagrams use the full reader width. *Wide reader* lifts the prose limit too.
- Table headers stay visible while a long table scrolls.

**Library**
- Rows show the document's own first heading as its title, with the type and availability beside it and the path on demand.
- Search by title, path or type, and filter by type.

**Toolbar** (sticky): the four views, in-document search, *Wide reader* and *Copy link* (the exact document, view and section).

**Search**
- Debounced at 250 ms. Highlights matches in Read, counts them, and moves with ↑/↓, Enter or Shift+Enter; Escape clears.
- Source view marks matching lines.
- Highlighting rewrites only the text of the already-sanitized HTML: attributes and tags are never touched, and the term is matched in its HTML-encoded form, so it cannot inject markup.
- Results are cached per term.

**Outline and sections**
- Headings are indented H1–H6.
- Choosing one opens Read at that heading and records `?section=` in the URL.
- A deep link with `section=` opens the section, provided the document really has that anchor. The outline marks the current section.

**Diagram viewer** (`GeneratedDiagramView`, the same renderer)
- *Fit* draws at the reader's width.
- Zoom: + / −, the buttons, or the mouse wheel in the expanded view only, so the wheel still scrolls the page inline. *Reset* returns to 100%.
- Pan by dragging or with the arrow keys. A bounded viewport replaces nested scrollbars.
- *Expand* opens a modal dialog (`role=dialog`, `aria-modal`) with a large canvas. Focus moves into it, Escape closes it, and focus returns to the Expand button with the reading position unchanged.
- The caption names the diagram type and section and says it is documentation, not verified runtime architecture.

**Provenance (evidence and context)**
- It opens with the evidence kind: implementation-derived, not an approved specification, not imported source documentation, not runtime-verified, and "partially parsed" when that applies.
- *Systems named in this document*: registered systems and services of the active Target Environment that the document names by their exact name (or an explicit or confirmed alias), as a whole word. Each links to Systems & Scopes, focused on that system's connections. A similar name, such as `Person` inside `M2LB.Person.Api`, is not linked.
- *Revisions*: the same document in other imports (Drift's version list), with headings added or removed and whether the diagram sources changed. This is a reading aid; Documentation Drift owns comparison.
- Identifiers (fingerprint, set id, snapshot) move under *Technical details*.

**Health link:** the Explorer accepts `module=(repository)`.

## Verification

- **Frontend focused** (Explorer, Health, diagrams, Markdown, Drift and the related suites): passing, including 7 new tests:
  - library titles and filters;
  - search highlight, count, next and Source marking;
  - section deep link and current-section marking;
  - diagram zoom, keyboard pan, reset, expanded dialog with Escape, and caption;
  - highlighting never touches attributes or injects markup;
  - only exact registered names become system links, and the outline comparison ignores indentation;
  - provenance evidence kind and technical details.
- **Full frontend regression:** 8828/8828.
- **Backend partitions 1–5** passed (4176 passed, 11 skipped, 0 failed). **Partition 6 was not run**: Claude Code stopped it because the machine was low on memory. This task made no backend change.
- **Browser acceptance (Edge, isolated)**, all 36 checks passing:
  - API on :5097 with a disposable database, web on :5197; ports 5000 and 5173 untouched; no external request.
  - **Real data:** the local M2LB archive, imported read-only through Import Project into the disposable database.
  - Library titles: "System Architecture — M2LB Platform (Aspire AppHost view)", "Primary Dataflow — Aspire AppHost" and others.
  - The Aspire architecture document: its 2 real headings, section deep link, search "Api" (1 of 3, highlighted; the other 6 occurrences are inside the diagram), Source line marking, and provenance.
  - Systems named exactly: M2LB.Person.Api, M2LB.Frontend.Web, M2LB.Proxy.Api, Person and others, each a registry identity.
  - The Person data-flow document has 4 sequence diagrams:
    - the diagram is 1,060 px wide in a 1,086 px reader;
    - keyboard zoom reaches 125%;
    - dragging pans;
    - the zoomed canvas stays within the reader width;
    - Fit works;
    - Expand opens a focused dialog and Escape returns focus to Expand;
    - the reading position is kept.
  - No horizontal overflow at 1440, 1280, 1024 or 768 px.
- **Measured timings:**
  - first render 9.1 s (includes the WASM start);
  - section navigation 0.77 s;
  - search 0.37 s;
  - diagram expand 0.12 s.

## Limitations

- Only sequence and flowchart diagrams are drawn: ER, class and state diagrams show their source. There is no image export of a diagram; *Copy source* is available in Source.
- Cross-document search was not added. The page has no shared document index, and a client-side scan of every captured document was left out to avoid a second search engine.
- The structured architecture views (resources, services, dependencies) are still the existing *Structured interpretation* under Outline; component inventories in the prose are not regrouped.
- Health findings are linked, not shown inline. The Explorer does not run Health, and Health is evaluated per snapshot and module, not per document.
- Systems & Scopes scope filtering of documents: there is no reliable mapping from scope members to document sections, so none is invented.
