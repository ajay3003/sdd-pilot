# Technology & Analysis Coverage — audit and P0 redesign (2026-11-13)

Branch `008-traceability-first`.

## Ownership (verified)

- **Page:** `Pages/TechnologyCoverage.razor`.
- **Wording and rules:** `TechnologyCoverageGuidance` and `TechnologyCoveragePresentation`.
- **Review applicability:** `ProjectApplicabilityState` / `ReviewCatalog`, a single evaluated status per review.
- **Technology support registry:** `TechnologySupportRegistry`.
- **Source technology coverage:** comes from the Source Analysis snapshot.

No registry or engine was added.

## Findings

- **"Generic project".** The rule is "no domain extension applies" (`DomainExtensions` is empty). It is not a classification defect and
  not a guess. The label is now **"General project profile — multiple technologies detected"** when more than one technology is detected,
  and "Domain project (…)" when an extension applies.
- **"Ready" and "Full".** "Ready" meant a review *can run*, and "Full" meant BirkNext's capability covers the project; neither is a result.
  They now read **"Ready to run"** and **"Full capability"** / **"Partial capability"**, under a **"Capability coverage"** column header.
  The status enums are unchanged.
- **File counts.** These were already reconciled and covered by tests: Outside scope = Total − Analyzed − Recognized-not-analyzed, never
  negative. The snapshot does not break "other" files down further, and the page says so.

## Changes

- **Three tabs** via `?tab=`: **Project coverage** (default) · **Detected technologies** · **BirkNext capabilities**.
  - Each tab renders only its own content, so detected technologies and the general reference no longer share the landing page.
  - The general reference is open on its own tab and lists **all** known technologies by default. "Only this project's technologies" is an
    explicit option.
- The **"View files not analyzed"** panel moved to Project coverage, next to the attention item that opens it. It is presented as a
  BirkNext limitation.

## Tests

- `TechnologyCoverageUxTests` and `TechnologyIndependenceUiTests` now open each tab through the URL.
- A new test covers the three tabs: order, default, and that the tabs are disjoint.
- New checks cover the profile wording and Ready to run / Full capability.
- The focused suites passed 389/389, including Dependency.

## Not done

- A selectable review detail view.
- The detected-technology card catalog: it is still a table, now on its own tab.
- Real M2LB and browser acceptance.
- The User Guide update.
