# Sidebar navigation

The sidebar answers "where can I go?". It is not a status dashboard: each row shows a label and, at most, one short
applicability status. Reasons live in the status tooltip, on the destination page and on Technology Coverage.

## Ownership

| Concern | Owner |
| --- | --- |
| Sections, rows, routes, icons, row hints | `NavigationCatalog` (`AIAssisted/frontend/BirkNext.Web/Services/NavigationCatalog.cs`) |
| Which rows exist | Feature Visibility (`FeatureVisibilityService`, System Settings), unchanged |
| Applicability per review | `ApplicabilityEvaluator` (shared) via `ProjectApplicabilityState` |
| Sidebar status text, spoken text, tooltip | `NavStatusPresentation` |
| Collapsed sections | `NavigationSectionState` (session memory, no persistence) |
| Rendering | `Layout/NavMenu.razor` + `NavMenu.razor.css` |

Routes are unchanged. Collapsing a section is UI state; hiding a feature is Feature Visibility configuration.
N/A and Unsupported reviews stay visible.

## Row layout

`[icon] [label] [status]`, a CSS grid of `1.1rem minmax(0, 1fr) auto`. The label stays on one line and truncates
with an ellipsis. The full label is the link's `title` and its accessible name. The status sits in a right-aligned
column and never overlaps the label. All rows are 32px high. The selected row has a background plus a 3px left
indicator bar and keeps the same font weight, so a selected label is never wider than an unselected one. The
sidebar is 264px wide (was 250px).

## Status vocabulary and precedence

The status always comes from the shared evaluator, which gives each review exactly one `ApplicabilityStatus`.

| Evaluator status | Badge | Spoken |
| --- | --- | --- |
| PartiallyApplicable | Partial | Partial coverage |
| NeedsConfiguration | Setup | Needs configuration |
| NotEnoughEvidence | No evidence | Not enough evidence |
| NotApplicable | N/A | Not applicable |
| Unsupported | Unsupported | Unsupported technology |
| Applicable | none, or an overlay (below) | |

Only an **Applicable** review can show an overlay:

- **Extension**: the review is provided by a domain extension that is explicitly enabled for the project, for
  example Security Classification once the M2LB template is applied. Without the extension the review is N/A and
  the menu names neither M2LB nor "extension". The status is never derived from a hostname.
- **Provider**: the row names the provider it runs on. Environment Analysis (route `azure-environment`) shows
  "Azure" when Azure evidence exists. Otherwise it shows the evaluator status, for example "Unsupported" for an AWS
  project. The menu label stays generic; the page title says Azure.

No status uses a failure colour. Text carries the meaning, and the border style only reinforces it: dotted for
N/A, Unsupported and No evidence; dashed for Extension; filled for Provider. The short badge is `aria-hidden`, and a
visually hidden ", Not applicable" (or similar) completes the link's accessible name.

## Sections

Section headings are `<button>` elements with `aria-expanded` and `aria-controls`. All sections start expanded.
Collapsing is remembered for the browser session. The current page's section expands whenever you navigate to
it. Heading toggles do not close the mobile menu.

## Refresh

Statuses re-evaluate on navigation, when the workspace changes (project switch, workspace restore, Reset Local
Database), and after any Integrations change, including applying a template. If refreshes overlap, only the
latest result is published.

## Contrast

Section headings are `#94a3b8` on the `#1e293b` sidebar, 5.7:1 (they were `#64748b`, 3.0:1). Badges are
`#cbd5e1`, 10:1. Focus is a 2px `#93c5fd` outline on links and section buttons.
