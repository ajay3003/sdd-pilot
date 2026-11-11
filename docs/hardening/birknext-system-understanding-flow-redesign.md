# BirkNext — System Understanding flow redesign (2026-11-11)

- **Baseline:** `da5f4a72` (+ counts `d7fbbad2`).
- **Audit:** `../audits/birknext-system-understanding-flow-ux-audit.md`.

## Decision

The free-graph SVG was replaced by a purpose-built **journey presentation**. It is computed from the same guide revision (`SystemGuideContent`).
- **No graph library**, and no second topology engine, resolver or evidence store.
- Integration Topology remains the technical graph; it is linked from the page header.

## Presentation model (`SystemUnderstandingPresentation.BuildJourney`, generic)

| Part | Rule |
|---|---|
| Stages | Components of the `main` lane in flow order (a guide without lanes uses its main-report flow) |
| Connectors | Only between neighbouring stages, so the primary row cannot cross. A missing link is a visible gap, never an invented connection |
| Returns | A backward edge between stages (e.g. acknowledgment) sits on its own track below the stages it spans. Tracks are packed so that returns on one track never overlap |
| Bypasses | A forward edge that skips stages is listed as a pill under the journey (the primary row stays clean) |
| Groups | One card per side lane, titled by the flow that covers it (e.g. "Identitet (Person)", "Utdanning", "Plasseringer (Tjeneste)"). It shows the internal chain, and attachments to stages with direction; request and response are merged into one relationship with "answer" |
| Unplaced | Edges naming unknown components are listed as such, never drawn |
| Placement helpers | `StageColumns`, `TwoRowColumns`, `TwoRowStage`, `TwoRowConnector`, `TwoRowReturn` (generic for any stage count) |

The model is rebuilt only when the guide content changes. Selection, focus and search never recompute it.

## Rendering (`SystemFlowDiagram.razor(.css)`)

- **HTML instead of SVG:** names and responsibilities wrap and are never clipped. Each stage shows a "Stage n" caption, the name, the responsibility and an evidence indicator (glyph + word).
- **Connectors:** thin; the main journey in the primary colour; scheduled/export connections dashed and neutral. The short data label sits above the arrow; the full transport and format are in the details.
- **Acknowledgment:** a dashed neutral return track under MU…MM with its full phrase ("Applikasjonskvittering v2"). It is never an arc over the stages.
- **Responsive (container queries):**
  - wide (≥ 1051 px container): one row;
  - medium (821–1050 px): two intentional rows of three, with "continues ↓" at the break;
  - narrow (≤ 820 px): a vertical journey, the return as a note, and supporting groups stacked.
  - Text is never scaled down to fit.
- **Interaction and accessibility:**
  - Every stage, connector, return, chain link and attachment is a native `<button>`, so Enter and Space work, with `aria-pressed` and a directional `aria-label`.
  - Visible focus.
  - Dimming does not rely on colour alone (the flow chips and the text say which flow).
  - Reduced motion is respected.
- **Text alternative:** built from the same journey model (main journey order, hops with transport and format, returns) plus the guide's flows. It is not a hand-copied text.
- **Unchanged:** the node and edge detail panels (inputs, outputs, dependencies, success, typical failure, steps, contract and version, uncertainty, evidence with links) and the flow filter chips.

## Tests

| Test | What it asserts |
|---|---|
| `The_journey_puts_the_six_main_stages_in_order_with_one_connector_between_neighbours` | Real seed: stage order; connectors; one return on track 0 spanning MU…MM; three groups; the education chain; attachments; every edge placed exactly once |
| `Synthetic_layouts_A_to_O_follow_independently_stated_rules` | Fixtures A–O (B six stages, E request/response merge, F return, H gap, I parallel edges, M twelve stages with track packing, N cycle in a lane, O unknown relationship, plus a bypass) |
| `The_rendered_flow_has_six_stage_buttons…` | Rendered order; no clipped responsibilities; groups; text alternative |
| Existing page tests | Kind classes (`k-ack`, `k-main`, `k-identity`), details, keyboard (native buttons), dimming |
