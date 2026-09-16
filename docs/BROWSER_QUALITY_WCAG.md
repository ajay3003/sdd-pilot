# Native WCAG coverage and evidence boundaries

## Repository audit

Initial repository: `BirkNext`, branch `008-traceability-first`, HEAD `b761195`.
Initial unrelated edits were in BrowserCompanionQualityTests, FrontendQualityReview and
BrowserCompanionRuntime. Additional runtime changes and commits appeared concurrently;
this implementation did not issue a commit, reset, restore or stash.

Before this change the companion had 14 passive DOM checks. Their mappings were:

| Native rules | WCAG | What the existing evidence proves |
| --- | --- | --- |
| image-alt | 1.1.1 | Presence of selected alternatives, not adequacy |
| control-label, heading-order, heading-h1, main-landmark | 1.3.1 | Associations or structural guidance; heading/landmark guidance alone is not a failure |
| page-title | 2.4.2 | Presence, not descriptive quality or SPA appropriateness |
| positive-tabindex, hidden-focusable | 2.4.3 | Suspicious structure, not actual traversal or meaningful sequence |
| link-name | 2.4.4 | Name presence, not contextual purpose |
| document-lang | 3.1.1 | Attribute presence, not correspondence with content |
| duplicate-id | 4.1.1 | Duplicate markup; not a 2.2 conformance failure |
| button-name, aria-reference, dialog-name | 4.1.2 | A conservative accessible-name approximation and reference existence |

The native engine did not run axe, interaction, or cross-page checks. The separate
axe service remains available as a reference; its configured tags and existing FQR
guidance target WCAG 2.2 A/AA. Native Browser Quality does not depend on Playwright,
CDP, proxy interception, API authentication, Lighthouse scores or credential access.
Browser automation is used only by the existing test harness and deterministic fixtures.

## Registry and result semantics

The registry contains all 50 WCAG 2.1 A/AA criteria and the six additional WCAG 2.2
A/AA criteria. A 2.2 view retains 4.1.1 as explicitly NotApplicable, giving 56 visible
registry entries and 55 applicable conformance criteria. AAA is not selectable.

Reference: https://www.w3.org/TR/WCAG22/

Every result distinguishes Pass, Fail, ManualReviewRequired, NotApplicable and
NotTested. Automation capability is Automatic, Partial or Manual. Classification
describes the implemented capability, not an aspirational checklist. Currently
37 criteria have partial check mappings and 19 have manual-only coverage. No whole
criterion is advertised as fully automatically provable by the implemented collector.

Explicit check execution records can Pass or Fail; they are never silently promoted
to criterion passes. Criteria requiring semantic judgement remain ManualReviewRequired
after a clean check. Missing execution evidence is not proof of success. Current
deterministic failures cannot be hidden by a manual approval.

Confidence is High for deterministic failures, Medium for partial measurements and
Low for cross-page structural inference. Human decisions use Manual evidence instead
of a numerical confidence. Media absence is N/A only with explicit zero counts and
a complete supported media scope; embedded/custom/canvas/shadow content prevents that proof.
Missing caption tracks are review candidates because captions may be open, external or live.

## Implemented collection

Passive snapshots extend the original checks with image-role naming, a limited
label-in-name comparison, media/track counts, input-purpose observations, sensory
instruction patterns, status/invalid associations, gesture/pointerdown/timer/moving
content hints, and navigation/component counts. Hints are not semantic failures.
Language-tag shape is checked; content-language correspondence remains manual.

Contrast uses sRGB relative luminance, foreground and ancestor background alpha
composition, 4.5:1 normal-text and 3:1 large-text thresholds. Disabled controls are
excluded. Transparent canvases, gradients/images, filters, opacity, blend modes,
shadows, non-sRGB colors and unsupported content remain uncertain. The algorithm
does not prove complex overlapping graphics, generated text or every rendered state.

The extension popup can explicitly run temporary text-spacing and 200% font-size
probes on a visible, approved non-production page. Styles and scroll positions are
restored in finally blocks. Probes detect new clipping and hidden elements, with a
1,500-element bound. They do not prove overlap-free rendering or functional equivalence.
Spacing values: line height 1.5, paragraph spacing 2em, letter spacing .12em,
word spacing .16em. The resize probe is text enlargement, not browser zoom.

Reflow observes actual 320 CSS px snapshots. Other widths are NotTested. The engine
does not pretend that setting body width changes viewport media queries. Orientation
and automatic browser viewport resizing remain unsupported.

A popup action opens a 30-second, maximum 100-step trusted Tab/Shift+Tab observation
window. It records structural focus evidence and possible repeated focus/absent
outline candidates. Synthetic keys are ignored. Valid modal repetition is not
automatically failed. Listeners are removed on completion/navigation. This does not
automate trusted keyboard traversal, prove absence of traps, test every indicator,
or establish keyboard functional equivalence. Enter/Space/arrows are not dispatched.

## Cross-page and manual scope

2.4.5, 3.2.3 and 3.2.4 use multiple PageAnalysis entries. Fewer than two current
structural snapshots gives NotTested. Comparison is limited to structure counts;
names, relative order of equivalent items, navigation paths and component equivalence
require manual assessment. 3.2.6 is represented, but has no dedicated automated proof.

Manual decisions persist in the existing environment-isolated discovery store,
scoped to criterion, WCAG version and page generation. Application reviews carry
the participating page identities/generations. Refresh clears only the selected
page's automated evidence. Review history remains and is visibly stale. Version
changes invalidate prior manual approvals; old review editors cannot approve a
new generation. A storage failure rolls back the new approval and reports an error.
History is bounded. Tester alias, comment, evidence note and timestamp are retained.
Notes reject likely credentials, markup, URLs, e-mail addresses and long identifiers.

## Safety and product integration

Interactive collection is allowlisted for Local, Development, QA, Test and RC
pairings. Production, Custom and unknown policies fail closed. The backend strips
interactive records for those policies as an additional guard. Re-pair after changing
the target environment policy. Probes never click, submit, alter field values or
send application messages. A tester's own interactions remain their responsibility.

New check evidence contains fixed check ids, counts, enum-like outcomes and bounded
structural selectors. The new probes use root/nth-child paths, with no element text,
attribute values or page data in the selector. The backend bounds and sanitizes
evidence before storage. No raw DOM, storage tokens, cookies, bearer or MSAL access
was added. The existing extension build scans forbidden credential APIs.

Endpoint Discovery exposes the matrix, status/level/automation filters, review
editor, version/level selection, page view and overview. FQR and HTML export retain
native criterion states, check evidence, confidence and manual history/staleness.
They never claim full conformance. Browser Quality remains optional under the
existing release policy; manual-review states are not automatically failures.

## Acceptance still required

This is not the requested verified-complete automatic engine. Reliable full-criterion
proof is still missing for the requested automatic interaction criteria, semantic
cross-page comparison and several DOM/ARIA/input-purpose edge cases. Hover/focus
popup behavior, safe input-context-change checks, validation triggering, orientation,
and complete viewport reflow remain unimplemented. Real M2LB managed-Edge acceptance
must be performed with the newly built companion and normal user authentication.
No real M2LB observations may be inferred from synthetic fixtures.
