# BirkNext — System Understanding flow UX audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `da5f4a72`.
- **Renderer audited:** `Components/SystemUnderstanding/SystemFlowDiagram.razor(.css)` and `SystemUnderstandingPresentation.Layout`.

## Existing renderer

- **Drawing:** one fixed-size SVG.
  - Every component is a 144 × 68 px box in a 226 × 138 px grid.
  - The main lane is on row 0; each side lane is packed into the first free row next to its anchor.
  - Edges are SVG paths: straight lines on a row, cubic curves between rows, and an arc above the row for a backward edge on the same row.
- **Interaction:**
  - Nodes and edges are keyboard buttons.
  - Flow chips dim other flows.
  - Details render below the diagram.
  - A text list is built from `Flows`.
- **Data:**
  - It reads the guide revision only (`SystemGuideContent`); there is no second evidence store and no topology engine.
  - Integration Topology keeps its own technical graph.

## Root causes of the observed defects

| Defect (screenshot) | Root cause in code |
|---|---|
| Truncated node descriptions | SVG `<text>` cannot wrap. `Clip(responsibility, 24)` cut every responsibility after 23 characters ("Rapportøren fyller ut o…") |
| Tiny text | Fixed 11 px responsibility and 10.5 px labels, scaled further down by `max-width: 100%` on a 1,300 px wide SVG |
| Overlapping edge labels, labels near nodes | Labels at fixed offsets from curve midpoints (`y - 8`, `mid - 6`). Nothing measured text or avoided boxes |
| Crossing and diagonal connections | Every side-lane edge is a cubic curve between rows. Several lanes attach to the same stage (SkoleService), so their curves converge and cross |
| Large purple acknowledgment arc | A backward edge on the main row is drawn as an arc **over** the main stages (`a.Y - 30`), in a saturated colour |
| Too many colours | Seven connection kinds, each with its own saturated colour plus a pattern |
| Weak main vs. supporting separation | Side lanes share the canvas and the column grid of the main lane; the grouping is positional only |
| Poor use of width / unbalanced | Fixed column width; the SVG is either scaled down (small text) or narrower than the card |
| No responsive layout between 760 px and desktop | Only one breakpoint, which hides the SVG entirely below 760 px |

**Conclusion:** the defects come from the *rendering model* (free graph on a fixed SVG grid), not from the data. A graph library would not fix the reading problem. The fix is a purpose-built journey presentation built from the same guide data (see the redesign report).

## Shared data verification

The components, edges and flows come from the guide revision. The task 8 real-source rewrite of the guide is the evidence basis.
- The main flow is `altinn → mu → mm → skole`.
- Delivery is `skole → periodelaas → bufdata`.
- Acknowledgment is `mm → mu`.
- Person and Tjeneste are side lanes.
- Utdanning is `birk → eventhub → skoleadapter → skole`.

No relationship is added by the presentation.
