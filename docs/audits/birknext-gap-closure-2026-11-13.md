# Gap closure, batch 1 (2026-11-13)

This batch closes gaps recorded in the task 16–24 audits. Each item lists the evidence that it is closed.

| Gap (audit) | Closed by |
|---|---|
| User Guide not updated (16, 19–24) | Guide entries added: EF Core context extraction and inline keys (Explorers); glossary row kinds (Source Analysis); Read/diagrams/Outline/Source/Provenance and the Health focus (Generated documentation); status wording, gates, providers and Changes (Pipeline Review); Technology & Analysis Coverage tabs; Dependency Review inventory semantics. |
| Delivery task counts could read as verified delivery (17) | The snapshot table header is now "Tasks (checklist)", and cells read "8/12 checked". |
| Pipeline Changes had no route to import another snapshot (24) | The single-snapshot empty state links to Source Management. |
| GDE Source view had no line numbers or copy (20) | The Source view shows the original lines with CSS-counter line numbers (not part of the text), plus a Copy source button using the clipboard pattern already in the app. A test rejoins the lines and compares them to the source exactly. |
| DME: inline `IX` and composite `UQ` not extracted (16) | `AddInlineKeys`: a bare `IX` indexes its own column, `IX (a, b)` indexes the listed columns, and `UQ (a, b)` becomes a UNIQUE constraint. Each key is added once. `in IX…`/`in UQ…` references and notes such as `UQ (EXC-03)` are ignored. Tested. |
| Health: no per-document Explorer links (21) | The backend `AutodocModuleInventory` now carries `ArtifactPaths` and `MissingExpectedDocumentNames` (empty in older results). The module focus links each document directly into the Explorer. Tested. |
| Health wording defect, found while closing gaps (21) | "Partial" also means *generator not traced*, not only "required output missing". The state reason and review reasons now use each module's own `UsageReason`, and missing documents are named. |

Focused suites: frontend 674/674. Backend (generated documentation and test coverage) 66 passed, 1 skipped (pre-existing).

Still open: browser, responsive and real-M2LB acceptance for every module, and the large redesign items listed in each audit.
