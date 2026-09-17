# Browser Quality: profile consistency and UI redesign

## A. Root cause

Both discovery call sites used `ProfileId="Profile.Id"` for `WcagWorkspace`. Because the parameter is a **string**, Razor passed
the literal `Profile.Id`, not the environment identifier. The profile selector read the real environment's `Snapshot.Wcag`,
but the results came from `Discovery.GetAssessment("Profile.Id")`. That separate store entry defaulted to Norwegian WCAG 2.1.
The result was a real WCAG 2.2 selector alongside a 48-criterion Norwegian assessment, and profile changes could be persisted
under the wrong environment key.

The call sites now use `ProfileId="@Profile.Id"`. Configuration, selector, criteria, results, header and summary are derived from
the **same snapshot**. The object parameter is retained so evidence and restored-store changes trigger child renders, even when
the environment identifier is unchanged. The selector uses explicit two-way binding; profile selection persists under the real
environment id. Tests check that no `Profile.Id` store entry is created.

User-facing names are now **Norwegian legal baseline — WCAG 2.1** and **Extended assessment — WCAG 2.2**. Saved version/level
configurations retain their identities, criteria and review provenance, using **General A + AA assessment** or **General level A
assessment**. Unknown historical identity stays **Saved assessment — Profile unknown**. No legal membership was changed: the
Norwegian baseline still has its configured 48 criteria; the extended profile has 55. The older general 2.2 A+AA set retains 56,
including its existing obsolete-criterion handling, rather than being silently relabelled as either current profile.

## B. Counter semantics

All headline criterion counts refer to unique criterion ids in the selected profile, not page-result rows.

| Counter | Exact meaning |
| --- | --- |
| Criteria | Configured profile membership, including criteria with no result records yet. |
| Failed criteria | Aggregate criterion status is Fail; any current failure takes precedence. |
| Require manual review | Aggregate status is ManualReviewRequired from an explicit review-required result. It is not the manual-only count. |
| Manual assessment required | Criterion automation capability is Manual. Shown as **10 of 48** for the legal baseline: a subset/property, never an extra total or failure count. Completed manual reviews do not alter that capability. |
| Not yet assessed | Aggregate status is NotTested, including unexecuted criteria and criteria whose partial execution cannot establish a complete result. |
| Criteria with evidence | At least one result has an execution timestamp or a current, non-stale manual review. Each criterion counts once. The card explicitly says **Execution or current manual review**. |
| Pages with browser evidence | Actual saved application pages with BrowserEvidence, independent of manual review and connection state. |
| Criteria with execution evidence | At least one result has LastTested, including genuine cross-page comparisons. Manual reviews do not create an execution timestamp. |
| Browser pages with execution | Distinct page identities with criterion execution timestamps; application/process result slots are excluded. |
| Application comparisons | Application-scope result records with an actual execution timestamp. |
| Manual reviews | Current, non-stale recorded manual decisions, displayed separately from browser execution. |

The cards explicitly state that they overlap. A criterion can have partial evidence and remain NotTested. A current manual review
can contribute evidence while the browser evidence badge still says Awaiting browser evidence.

## C. Why there were 3 assessment instances

The engine creates one application/process **assessment slot** for each cross-page criterion regardless of page count:
2.4.5 Multiple Ways, 3.2.3 Consistent Navigation and 3.2.4 Consistent Identification. These three slots are computed placeholders,
not collected browser evidence and not necessarily saved/manual decisions. With no pages, they are NotTested with no LastTested
or manual review. The ambiguous headline count was removed. Criterion details explicitly identify an unexecuted application /
process assessment slot as **not browser evidence**.

## D. What 0 / 1 Pages / evidence meant

The old cell rendered `EvidenceCount / Instances.Count`. Its first number counted results with execution or a valid manual review;
the second counted all result slots, including placeholders. Neither was a reliable browser-pages/browser-evidence pair.
Thus `0 / 1` meant **zero evidence-bearing results, one placeholder slot**. The combined label is gone. Evidence cells now show
No evidence, browser pages with execution, application comparisons and/or manual reviews with explicit labels. Scope and slot
explanations live in criterion details.

## E–J. Layout, styling and accessibility

- **E — Layout:** Browser Quality has independent WCAG and Performance tabs at the top. WCAG is selected by default.
- **F — Empty state:** The WCAG header names the selected profile, version and criterion count, followed by a browser-evidence
  badge and the conformance limitation. A shared empty-state panel says “No browser evidence collected yet” with Companion
  instructions. All-NotTested matrices start collapsed behind **Show all 48 criteria** (count follows the profile).
- **G — Criteria:** Four native, keyboard-operable principle disclosures: Perceivable, Operable, Understandable and Robust.
  Each criterion appears once. Columns are Criterion, Title, Level, Automation, Status and Evidence. Scope and detailed provenance
  move to details. Status, level, automation and search filters appear only when the matrix is expanded; they wrap at narrow widths.
  Textual status chips distinguish Failed, Require manual review, Not tested, Evidence available and Not applicable.
- **H — Performance:** A separate empty state when no browser performance evidence exists. Saved page cards show LCP, INP, CLS
  and navigation TTFB, with existing detailed timing/findings behind a disclosure. Unsupported/missing values display **Not available**;
  real measured zero remains zero. No WCAG scrolling is required to reach this view.
- **I — Design system:** Reuses Card, MetricCard, EmptyState, StatusChip, SecondaryButton, PrimaryButton and GhostButton, plus
  existing tab-bar/tab-btn, form styles and design tokens. Local CSS arranges the components, wraps cards/filters, constrains table
  overflow, and improves badge/helper-text contrast. No custom button style was introduced. At a 1024px viewport the actual settings
  content area was 436px wide; its scroll width remains 436px, with horizontal overflow confined to table regions.
- **J — Accessibility:** Tabs have tablist/tab/tabpanel roles, selected state, controlled panel ids, roving tabindex, ArrowLeft/
  ArrowRight/Home/End navigation and focus movement. Controls have explicit labels and unique ids. The criteria button has
  aria-expanded/aria-controls. Native details/summary expose principle state and keyboard interaction. Tables have captions,
  column/row headers and named, focusable scroll regions. States use words as well as colour. Failed and awaiting badges meet the
  tested contrast checks using existing palette tokens.

## K–L. Tests

Semantic/component coverage includes restored legal, extended and older general profiles; exact configured membership; switching
both directions; store replacement after mounting; actual environment-id persistence; 10-of-48 subset presentation; zero browser
evidence; NotTested versus failure; real zero versus unavailable performance; application slots versus execution; manual evidence
without browser evidence; and no automated violation being presented as conformance.

DOM tests cover default tabs, independent Performance access, empty panels, collapsed/expanded criteria, all four unique groups,
accessible profile/filter labels, textual statuses, shared styled buttons and card components, and saved evidence while disconnected.

The production Blazor browser script covers three isolated scenarios: empty legal assessment, synthetic saved browser evidence,
and a restored old WCAG 2.2 configuration. It checks keyboard focus and selection, native disclosure interaction, profile switching,
storage keys, search, unavailable values, rendered styling and 1024px containment. It also runs nine scoped axe scans (WCAG 2 A/AA
and 2.1 AA tags) over WCAG, Performance and expanded laptop layouts: no detected violations. These scans do not establish full
WCAG conformance of BirkNext or the target application.

## M. Files changed in this task

Paths below are relative to `AIAssisted/frontend/` unless stated otherwise.

- `BirkNext.Web/Components/BrowserQualityWorkspace.razor` and `.razor.css` — tabs and Performance surface.
- `BirkNext.Web/Components/WcagWorkspace.razor` and `.razor.css` — consistent snapshot, bound profile, shared review actions.
- `BirkNext.Web/Components/WcagCoverage.razor` and `.razor.css` — header, summary cards, empty state, filters, groups and evidence labels.
- `BirkNext.Web/Components/EndpointDiscoveryTab.razor` — workspace integration and correct environment-id binding.
- `BirkNext.Web/Models/WcagModels.cs` — user-facing unknown-profile wording.
- `BirkNext.Web/Services/WcagProfiles.cs` — purpose-based profile names; membership unchanged.
- `BirkNext.Web/Services/BrowserQualityAssessmentService.cs` — separate browser-page, application-comparison and manual-review presentation counts.
- `BirkNext.Web.Tests/Components/BrowserQualityRedesignTests.cs` — new semantic/DOM regression cases.
- `BirkNext.Web.Tests/Components/BrowserQualityArchitectureUITests.cs` and `WcagCoverageTests.cs` — updated interaction expectations.
- `BirkNext.Web.Tests/Services/WcagArchitectureTests.cs` and `WcagAssessmentTests.cs` — profile wording/provenance assertions.
- Repository `scripts/verify-browser-quality.cjs` — real-browser verification and screenshots.
- This report.

No Browser Companion collection, proxy, discovery-ingestion or Norwegian legal-membership logic was changed in this redesign.
Earlier Companion reporting work in this conversation is separate.

## N–P. Results and build

| Verification | Result |
| --- | --- |
| Focused WCAG / Browser Quality / Performance / shared-button tests | **124 passed**, 0 failed |
| Full frontend suite | **3,224 passed**, 0 failed, 0 skipped |
| Production Chromium DOM / UX verification | **3 scenarios, 97 assertions passed**, including 9 accessibility scans |
| Debug frontend build | Succeeded, 0 errors |
| Release frontend build | Succeeded, 0 errors |
| Diff whitespace check | Passed |

Logs: `AIAssisted/frontend/quality-focused.log`, `quality-full-frontend.log`, `quality-debug-build.log`, `quality-release-build.log`.
Browser machine-readable result: `test-results/browser-quality/verification.json`.

A pre-existing physical `wwwroot/BirkNext.Web.styles.css` blocked normal builds and shadowed generated scoped styles. It was
preserved at `BirkNext.Web/obj/browser-quality-preexisting-styles.backup`, outside wwwroot. Builds now use the generated bundle;
the final browser verification uses normally served styles with no stylesheet substitution.

## Q. Visual evidence

- [WCAG empty state](test-results/browser-quality/empty-wcag.png)
- [Performance empty state](test-results/browser-quality/empty-performance.png)
- [Performance with synthetic measurements](test-results/browser-quality/saved-performance.png)
- [Expanded criteria](test-results/browser-quality/empty-criteria.png)
- [Laptop layout](test-results/browser-quality/empty-laptop.png)
- [Restored general WCAG 2.2 profile](test-results/browser-quality/saved-version-wcag.png)

The surface uses a restrained white-card hierarchy, compact profile header, five wrapping summary cards, an instructional empty
panel and a clear disclosure action. Performance has its own page cards and metric grid. Full criteria remain accessible without
dominating the no-evidence view.

## R. Remaining limits

No requested redesign feature is deferred. Tests use isolated synthetic evidence and mocked loopback responses, not the user's
authenticated M2LB session. No new evidence was collected from M2LB. Full manual WCAG conformance assessment and broader
application-wide accessibility remain outside this UI task.
