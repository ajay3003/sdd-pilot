# Browser Discovery presentation refactor

## A. Responsibility overlap found
Endpoint Discovery hosted the companion controls, connection/evidence summaries, Browser Quality navigation and summary, plus WCAG and browser performance in individual page details. Network discovery and browser evidence were mixed. The existing BrowserQualityWorkspace already provided the redesigned WCAG and performance views; BrowserCompanionPanel already owned pairing actions.

## B. Top-level navigation
General > Target Application > Authentication > Endpoint Discovery > Browser Discovery > Performance Thresholds > Core Web Vitals > Security Expectations > Feature Toggles > Validation > Integrations.

Target Environment section tabs use local component state, not URL-backed state. Browser Discovery follows that existing mechanism. The enclosing System Settings section navigation is unchanged. No new deep-link convention was introduced.

## C. Endpoint Discovery
Retains discovery session, saved analysis count, authenticated context, last observed traffic, network correlation, Overview, Pages, Shared / background, Backend integrations, communication tables, classifications and provenance. Its overview always labels the communication purpose, including the empty state. Browser Companion controls, Browser Quality summary/navigation, page WCAG and browser performance assessments no longer render here.

## D-E. Browser Discovery Overview and companion relocation
BrowserDiscoveryTab composes the existing BrowserQualityWorkspace with an Overview slot. Overview is selected initially. The existing BrowserCompanionPanel now lives here in Target Environment settings. Its compact responsive cards show target, current page, evidence page count, DOM, accessibility, performance, approved origins and last evidence timestamp. Pair, pair again, unpair, installation instructions and runtime messages retain their existing actions. Pairing buttons reuse shared button components.

Not connected, Paired / not reporting, and Connected remain distinct text states. An expired session and pending pairing also retain distinct states. Stored browser evidence remains visible independently of live connection status.

A concrete presentation bug was corrected: previously every evidence category used the same availability label based solely on a nonzero page count. DOM, accessibility and performance availability now inspect their respective evidence payloads in the existing discovery store. No collection or assessment semantics changed.

## F-G. WCAG and performance relocation
The existing WcagWorkspace and existing browser performance presentation are reused inside separate selected views. Unselected assessments are not rendered in the DOM. WCAG profile selection persists in the existing snapshot; criteria, filters, review semantics and calculations are unchanged. Performance retains its shared empty state and supported-measurement presentation; unavailable values remain unavailable.

## H. Network correlation ownership
The authoritative network correlation status is in Endpoint Discovery, using its existing active proxy state predicate. The companion overview no longer presents proxy ownership or a large authenticated-context block.

## I. State sharing
No evidence model, transport, persistence service or assessment engine was added. Both views use IEndpointDiscoveryService and its existing per-profile EndpointDiscoverySnapshot. BrowserCompanionRuntime still owns live pairing/reporting state and evidence ingestion. A DOM test holds the snapshot reference while switching top-level and inner tabs and verifies retained evidence. The existing companion change subscription is now also removed when the settings component is disposed.

## J-K. Visual and accessibility changes
Existing top-level navigation, shared segmented tab styling, buttons, status presentation, WCAG cards and performance cards are reused. Companion fields use a responsive compact grid with subtle borders and spacing. Endpoint introductory copy and browser-specific content were reduced so communication tables are the focus.

Top-level tabs support arrow keys, Home/End, roving tabindex, selected state and linked tab panels. Browser Discovery subtabs have the same keyboard behavior, selected states and labelled panels. Native pairing buttons have visible accessible names. Status meaning is expressed in text, independently of color.

## L. DOM tests
BrowserDiscoveryTabTests adds nine test cases covering top-level ordering, selection, Overview default, inner navigation and keyboard operation, pairing states, approved origins/current page, actual evidence summaries and timestamps, same-store retention, network status/traffic and communication ownership, and no companion/WCAG/performance duplication across all four Endpoint Discovery views. Existing architecture, companion and performance tests were updated to assert the new ownership. Existing WCAG and performance semantic tests remain in the focused suite.

## M. Files changed for this task
- AIAssisted/frontend/BirkNext.Web/Components/BrowserDiscoveryTab.razor (new)
- AIAssisted/frontend/BirkNext.Web/Components/BrowserQualityWorkspace.razor
- AIAssisted/frontend/BirkNext.Web/Components/BrowserCompanionPanel.razor
- AIAssisted/frontend/BirkNext.Web/Components/BrowserCompanionPanel.razor.css
- AIAssisted/frontend/BirkNext.Web/Components/EndpointDiscoveryTab.razor
- AIAssisted/frontend/BirkNext.Web/Components/FrontendAnalysisSettings.razor
- AIAssisted/frontend/BirkNext.Web.Tests/Components/BrowserDiscoveryTabTests.cs (new)
- AIAssisted/frontend/BirkNext.Web.Tests/Components/BrowserQualityArchitectureUITests.cs
- AIAssisted/frontend/BirkNext.Web.Tests/Services/BrowserCompanionQualityTests.cs
- AIAssisted/frontend/BirkNext.Web.Tests/Services/PerformanceQualityEngineTests.cs
- docs/BROWSER_DISCOVERY_REFACTOR.md

Concurrent Target Application / Authentication edits in this shared workspace were preserved; the broader working-tree diff includes their files and changes as well. The build guard identified a stale generated wwwroot/BirkNext.Web.styles.css, which was removed so scoped CSS could be generated normally.

## N-P. Verification
- Focused browser discovery, companion, endpoint, WCAG and performance tests: 122 passed, 0 failed, 0 skipped.
- Full frontend bUnit/unit suite: 3,245 passed, 0 failed, 0 skipped (includes concurrent work).
- Frontend Release build: succeeded, 0 errors. Initial compilation reported 17 warnings; final incremental build reported 0 warnings.
- Playwright desktop (1440 px) and mobile (390 px): top-level navigation, all browser views, empty performance state, no duplicate companion controls and browser overview overflow check passed. Screenshots visually inspected. API responses were mocked for this presentation check; it did not test live companion transport.

Workspace verification artifacts: browser-discovery-focused.log, browser-discovery-full.log, browser-discovery-build.log, browser-discovery-verify.cjs, browser-discovery-desktop.png, browser-discovery-mobile.png.

## Q. Deferred / unchanged boundaries
No required presentation work is deferred. Optional cross-link from Endpoint Discovery was not added. URL-backed inner tabs were not added because the existing Target Environment section tabs do not support them. Live extension/proxy acceptance was not rerun for this presentation-only change.

Core Web Vitals remains a separate configuration/reporting surface. The existing performance assessment already reads Profile.CoreWebVitals and Profile.Performance as thresholds for observed evidence; that overlap is unchanged. Browser evidence is not configuration, saved analysis presence is not browser evidence, pairing is not reporting, missing evidence is not zero, and NotTested/manual-only criteria are not failures. Transport, proxy, classification, authentication token handling, integrations, persistence and assessment calculations were not changed.

---

## Superseded (Browser Discovery = evidence, Frontend Quality Review = judgement)

Sections D–G above described a Browser Discovery that hosted Overview + WCAG + Performance. That structure has
been replaced so Browser Discovery stays an evidence surface and does not become a second Frontend Quality Review.

- Browser Discovery navigation is now **Overview / Pages / Evidence**. The WCAG and Performance subtabs were removed.
- `Services/BrowserDiscoveryPresentation.cs` normalizes `BrowserPageEvidence` into page rows, WCAG-principle
  evidence groupings and raw DOM/performance observations. No assessment model was duplicated.
- WCAG principles (Perceivable / Operable / Understandable / Robust) are used only to group observed evidence.
  They are never a conformance result; the grouping states that in text next to every use.
- `WcagWorkspace` (assessment profile selector, coverage, manual review) now renders in Frontend Quality Review,
  which is where profile selection, pass/fail and manual review belong. It takes an optional `Result` so the page
  keeps displaying the assessment its own review run produced.
- `BrowserQualityWorkspace` is no longer rendered by the application; it remains only as the host for the existing
  WCAG/performance semantic tests. Deleting or re-homing it is a follow-up.
