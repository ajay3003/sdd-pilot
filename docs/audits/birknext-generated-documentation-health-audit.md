# Generated Documentation Health — audit and P0 redesign (2026-11-13)

Branch `008-traceability-first`.

## Ownership (verified)

- **Page:** `Pages/GeneratedDocumentationHealthPage.razor` handles project and snapshot selection. The panel is
  `GeneratedDocumentationHealthPanel`.
- **Module inventory section:** `Components/GeneratedDocumentation/AutodocDiagnosticsSection.razor`, which renders the backend
  `AutodocDiagnostics` (`shared/GeneratedDocumentationContracts.cs`).
- **Rules:** `Services/AutodocPresentation.cs` holds State, OverallHealth, IsReviewItem and the filters. The health engine itself is in
  the backend and was **not changed**.

## Why "Partial" (P0 question)

`AutodocPresentation.State` is **Partial** when *any* module has `Usage == Partial`: generated output is present, but its declared outputs
are incomplete. Having generated documentation in all 16 modules does not make the overall state complete. The rule is correct.

The defect was that the reason was not shown anywhere. A new `StateReason` line names the modules with partial output. When nothing is
partial, it says that availability says nothing about freshness or correctness.

## Changes (P0 UX)

1. **The per-row "Open in Explorer" is removed**, and so is the Action column. The module name is the single control in a row. It opens
   one module **focus panel** below the table, replacing inline expanded rows. The panel contains:
   - the factual review reasons;
   - one "Browse this module's generated documents (N)" link to the Explorer, scoped to that module;
   - the existing facts (generator, output, artifacts, freshness, missing required documents, consistency candidates, parser errors,
     type basis).
2. **One canonical Needs-review list.** Each item states its reasons, built from the module's own diagnostics only:
   - output in error;
   - parse errors;
   - missing required documents;
   - potentially stale;
   - partial output;
   - consistency candidates ("not a confirmed defect").

   Selecting an item opens the same focus panel. There is no second copy of the details.
3. Freshness wording is unchanged. "Not enough evidence" is never shown as stale, and a timestamp alone never makes a document stale. This
   was already the backend rule and is restated in the existing summary.

## Tests

`AutodocDiagnosticsSectionTests`:

- **Changed:** the Explorer-link test now checks there is no per-row link and no Action column, and that the link appears in the focus
  panel only for modules with output.
- **New:** review reasons and the shared focus panel, including closing it.
- **New:** the Partial reason names the partial modules, and the Available reason is honest.

## Not done (remaining gaps)

- Per-document Explorer links in the focus panel. The module DTO carries artifact kinds, not paths. One module-scoped link is offered
  instead.
- A compact header.
- The diagnostics run progress and duration.
- Real M2LB verification of the 5 observed review candidates.
- Browser and responsive acceptance.
- The User Guide update.
