# Generated Documentation Explorer — audit and P0 redesign (2026-11-13)

Branch `008-traceability-first`. The starting HEAD was fdcee492.

## Ownership (verified)

- **Page:** `Pages/GeneratedDocumentationExplorer.razor`. It is read-only. Content is resolved by fingerprint from the existing
  generated-content store (`DocumentationDrift.Text`); nothing is copied or persisted.
- **Interpretation:** `Services/GeneratedDocumentationExplorer.cs`. It provides sections, change log, data model and search, plus `View`.
- **Diagram reading:** `Services/GeneratedDiagrams.cs` reads Mermaid sequence and graph/flowchart deterministically. erDiagram, classDiagram
  and stateDiagram are recognised but not read.
- **Safe Markdown:** `Services/MarkdownRenderingService.cs`, which is shared across the app.
- **Health, drift, source:** Generated Documentation Health, Documentation Drift and Source Analysis. The links to them are unchanged.

## Before

You chose a module, then a card, clicked a per-card **Open** button, landed on **Overview** (metadata only), and then had to pick Structure
or Source to read anything. There was no URL state for the view. Diagrams appeared only as participant/step and edge text lists.

## Changes (P0)

1. **Read is the default view.** The tabs are **Read · Outline · Source · Provenance**. Outline keeps the former `Structure` id, so
   existing links and tests keep working.
   - `?view=read|outline|structure|source|provenance` is honoured on arrival. The former `overview` maps to Provenance.
   - The selected document and view are written to the URL with `replace`.
2. **The whole card selects and opens the document.** There is no separate Open/Details button.
3. **The Read view is the document itself, in source order.**
   - `ReadSegments` splits the content into Markdown runs and recognised diagram fences. Every line is kept, and ordinary code fences stay
     in the Markdown.
   - Markdown is rendered with the new `MarkdownRenderingService.RenderUntrusted`:
     - raw HTML is shown as **text** (Markdig `DisableHtml`);
     - only safe link schemes are kept;
     - **remote images are not loaded** — their alt text is shown instead.
   - Headings get segment-scoped ids.
4. **Diagrams are drawn.** `GeneratedDiagramView` renders an **SVG** from `DiagramLayout`, a pure, deterministic geometry:
   - sequence diagrams: participants as columns with lifelines, and messages in source order, with alternative paths dashed;
   - graphs: a left-to-right layered layout by longest path, cycle-safe.
   - Only declared nodes, edges and messages are drawn; nothing is invented.
   - Each diagram has Diagram · Steps · Source modes, plus zoom (50–250 %), Fit and Expand. The canvas scrolls to pan.
   - Diagrams over 80 elements fall back to Steps with a stated reason.
   - The caption says it is a documentation diagram, not verified runtime architecture.
   - No Mermaid library was added: there was none in the repository, and downloads are not authorised. Labels are Blazor-escaped text.
5. **Outline** lists the document's own headings, with a filter. Clicking one switches to Read and scrolls to the heading. The structured
   interpretation (data model, release-grouped change log, contract structure) is kept below it in a disclosure.
6. **Provenance** now starts with the former Overview facts: title, type, lines, size, headings, structured items, fingerprint and
   partial-coverage note.

## Security defect found and fixed (shared renderer)

`MarkdownRenderingService.IsSafeUrl` treated any URL without `://` as relative. So `[x](javascript:alert(1))` kept its `javascript:`
href in **every** Markdown view of the app. The existing tests only asserted that "a link exists".

The rule now blocks any scheme other than http, https and mailto. A scheme is letters, digits, `+`, `-` or `.` before the first `:`,
where that `:` comes before any `/`, `?` or `#`. The check runs after HTML-decoding the href and removing whitespace and control
characters.

Tests:

- Strengthened: javascript, data and vbscript links render as `href="#"`.
- New cases: `JavaScript:`, percent-encoded `%6Aavascript:` and `file:` are blocked.
- New cases: relative links, anchors, `../x?y=1:2` and `https://…/a:b` are kept.

## Parser status semantics

"Partially parsed" means some content has no structured interpretation. All content is still shown in Read and Source; it does not mean
the document failed. Read states this once, at the end of a partially parsed document.

## Tests

`GeneratedDocumentationStructureTests` adds tests for:

- Read is the default, and a sequence diagram is drawn with 3 participant boxes and ordered labels;
- the source mode shows the original;
- zoom and fit;
- unsafe HTML, script, a remote image and a javascript link are neutralised, while tables render;
- the outline navigates to an anchor and keeps the structured change log;
- `?view=` deep links (source, provenance, overview→provenance, structure, nonsense→read);
- Provenance holds the facts, and switching documents shows no stale content and updates the URL;
- read segments keep every line, and the graph layout draws exactly the declared edges.

## Not done (remaining gaps)

- Real M2LB `Aspire/autodoc` verification.
- Browser and responsive acceptance.
- Source view line numbers and copy.
- A change-log timeline in Read (the release grouping exists in Outline).
- Lazy mounting of diagrams.
- erDiagram, classDiagram and stateDiagram drawing.
- The User Guide article update.
