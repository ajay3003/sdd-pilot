# BirkNext — System Understanding flow acceptance (2026-11-11)

**Environment:**
- Microsoft Edge, with every non-localhost request aborted.
- The Release frontend on `:5497`.
- An isolated API on `:5496` serving the shipped Skolenærvær guide: the version rewritten from the real sources in task 8, published through the real lifecycle.

**Geometry** is measured from real bounding boxes, not asserted from the model.

## Browser acceptance (`su11/accept.cjs`)

| ID | Check | main | after API restart |
|---|---|---|---|
| SF-01/02 | The guide opens; the main journey is Altinn → MU → MM → SkoleService → Period lock → Bufdata | PASS | PASS |
| SF-03 | At 1440 px the six stages share one row, left to right | PASS | PASS |
| SF-04 | No stage, connector or return overlaps another; no connector label on a stage | PASS | PASS |
| SF-05 | The acknowledgment is a dashed return *below* the stages, spanning only MU…MM | PASS | PASS |
| SF-06 | Each primary connector lies exactly between its two neighbours (no crossing) | PASS | PASS |
| SF-07/08 | Supporting groups Identity (Person), Utdanning (BiRK → CDC / Event Hub → SkoleAdapter → SkoleService), Placements (Tjeneste) | PASS | PASS |
| SF-09…15 | Altinn, MU, MM, SkoleService, Person, Tjeneste and SkoleAdapter open their details with evidence | PASS | PASS |
| SF-16/17 | Acknowledgment details (positive receipt; back to MU); MM → SkoleService transport (both generations' routes) and format | PASS | PASS |
| SF-18 | All six flow filters highlight their path and dim the rest (none inert) | PASS | PASS |
| SF-19/20 | An evidence link opens Skolenærvær Testing; Back returns | PASS | PASS |
| SF-21 | Integration Topology opens; Back returns | PASS | PASS |
| SF-22 | The text alternative states the same journey as the diagram, plus the return and side flows | PASS | PASS |
| SF-23/24 | Keyboard: Enter on a focused stage and on a focused connection | PASS | PASS |
| SF-Z | No external request, page error or console error | PASS | PASS |

**Visual (`SF-L`, 6/6):** at 1440, 1280, 1024, 768 and 390 px there is no overlap, no label on a stage, no page overflow, the smallest text is 12.8 px, and **no name is broken inside a word**.
- 1440: one row.
- 1280: two intentional rows.
- 1024, 768 and 390: vertical journey.

**Totals:** main 25/25, after-restart 25/25, visual 6/6.

## Screenshot inspection (failures found by looking, then fixed)

| Iteration | Found in the screenshot (geometry checks had passed) | Fix |
|---|---|---|
| 1 | 1024 px and below: the vertical journey put all arrows *after* the six stages | DOM order interleaves stage, connector, stage… |
| 1 | 1440 px: names broken mid-word; acknowledgment label clipped; side chains wrapped awkwardly | Narrower connectors, untruncated return label, vertical chains |
| 2 | 1440 px: the step number overlapped "Skoletjenesten" | The step became a caption above the name |
| 3 | 1280 px: single words still broke ("Skoletjen-esten") because six stages do not fit about 920 px without shrinking text | Two intentional rows at medium widths (no text shrinking) |
| 4 | 1440 px: "Skoletjenesten" short by a few pixels | Connector maximum 3.25 rem; name 0.9 rem (14.4 px) |
| Harness | SF-19 used a stage without linked evidence; the name-break check had a newline bug | Fixed in the harness |

**Final inspection:**
- 1440: one calm row with thin connectors, and the return track under MU…MM.
- 1280: two rows of three with "JSON continues ↓".
- 390: a vertical journey, readable cards and stacked groups.
- The rejected patterns are all gone: no overlapping arrows, no diagonals, no labels over nodes, no clipped essentials, no oversized arc.

## Real Skolenærvær relationships (from the task 8 oracle)

| Relationship | Shown as | Evidence classification |
|---|---|---|
| Altinn → MU | Stage connector "XML"; details: webhook + daily polling, XML data elements | SourceImplemented (MU), Needs clarification (the namespace Altinn sends) |
| MU → MM | "Kryptert XML"; Service Bus reference + blob | SourceImplemented; generation-dependent version (Conflicting) |
| MM → SkoleService | "JSON"; `/reports` (English) or `/rapporter` (Norwegian) | SourceImplemented per generation |
| SkoleService → Person | Identity group, lookup + events | SourceImplemented |
| BiRK → CDC/Event Hub → SkoleAdapter → Utdanning | Education group chain; attachment into SkoleService | Code exists; CDC not live (Specified / Needs clarification) |
| SkoleService → Tjeneste | Placement group (at period lock) | SourceImplemented |
| SkoleService → Period lock → Bufdata | Stages 5–6, "Intern" (scheduled job) and "Parquet" | One generation only; Bufdata consumer not evidenced |
| MM → MU acknowledgment | Dashed return | SourceImplemented (positive only) |

Nothing is shown as runtime-verified.

## Automated tests

| Suite | Result |
|---|---|
| Focused frontend (SystemUnderstanding, UserGuide, HelpCenter) | **56/56 PASS** |
| Full frontend at `857cc849` | **8637/8637 PASS** |
| Full backend at `857cc849`, six sequential partitions | **6527 passed, 0 failed, 19 skipped (6546)**: 1049 / 1098 / 840 / 652 / 520 / 2368 |
