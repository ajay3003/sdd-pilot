# Release Readiness — scope, empty-scope semantics and root causes (NEXT49, 2026-11-06)

Branch `008-traceability-first`. Builds on NEXT40 (ae3d59a5) and absorbs open item G4c. It reuses the shared governance engine (`CanonicalInterpretationService.Release`, `GovernancePolicies`, the review queue); no separate evaluator. Verified on the M2LB copy (`birknext_gaps`).

## P1 — why "Entire project" had 0 feature packs (confirmed defect)

Trace: imported ZIPs → Source Analysis snapshots → Delivery snapshot (work items) → canonical interpretation → release scope.

The interpretation history shows the cause:

| Recorded (UTC) | Trigger | Sources | Entities / relations |
|---|---|---|---|
| 2026-11-04 14:29 | scope acceptance 2 | 7 Source Analysis **+ Delivery** | 5,757 / 8,801 |
| 2026-11-05 14:35, 14:37 | Relationship Review | 7 Source Analysis only | 2,735 / 3,802 |

Feature packs, requirements, stories and tasks come **only** from an interpreted Delivery snapshot. The Relationship Review page's "Analyze relationships" sent `SourceSnapshotIds` but never a `DeliverySnapshotId`. Every re-analysis started from the page therefore dropped the whole release scope silently: 0 packs, 0 requirements, 0 tasks.

| Classification | Item |
|---|---|
| Confirmed defect | Re-analysis from Relationship Review omitted the Delivery snapshot → empty release scope |
| Confirmed defect | Empty scope evaluated anyway: **Requirements "Ready with limitations"** with 0 requirements; **Traceability "Ready"** with 0 tasks (vacuous truth) |
| Missing evidence (expected) | A project with documents but no imported Delivery snapshot has no feature packs; it is now explained, not silently 0 |
| Not a defect | M2LB and M2LB-language are different sources of one application; no name-based merging was added |

## Changes

### Backend (`CanonicalInterpretationService.Release.cs`)

- **Scope**: an empty scope is "**Release scope not resolved:** …" with the recorded reason — the latest analysis without a Delivery snapshot (naming the earlier analysis that had one), no Delivery analysis ever, or the chosen modules having no feature packs. Next action: analyze with the Delivery snapshot included.
- **No vacuous truth**:
  - Requirements with none in scope → Not assessed. The reason distinguishes "scope not resolved" from "no requirements written for the packs".
  - Traceability with no tasks → Not assessed ("zero tasks is not full traceability").
  - Test design was already Not assessed without requirements.
  - A required dimension Not assessed keeps the release Not assessed, never Ready.
  - Governance is still evaluated independently, so an identity conflict still blocks.

### Relationship Review

- The Delivery snapshot used by the latest analysis that had one is offered and **selected by default** ("Include Delivery snapshot …"), and sent as `DeliverySnapshotId`.
- Without any Delivery analysis, a note explains where feature packs come from.

### Release Readiness Overview (`ReleaseReadinessPresentation`)

- **What blocks release?** — each root cause once, with every dimension it affects. The M2LB identity conflict appears in Governance and Open review decisions: it is one cause, with both dimensions and a link to `correlation-review?tab=review&type=IdentityConflict`. "Deferring does not clear it."
- An unresolved scope is its own cause, covering Scope, Requirements, Traceability and Test design.
- **Required evidence not assessed** — gaps not already explained by the scope cause; optional dimensions are never required gaps.
- **What should I do next?** — ordered by prerequisite: scope → governance → decisions → requirements → traceability → test design → implementation.
- **Imported TRX** (G4c): "N imported results (P passed · F failed) — not mapped to this scope" under *Tests executed*, which stays **Not assessed**. A discovered test is not an executed one, and results are not attributed to the release scope without a confirmed link and current evidence.

## Tests

- Backend:
  - new `ReleaseScopeResolutionTests` (3): reason with an earlier Delivery analysis; reason without any; nothing in scope is never Ready.
  - `ReleaseReadinessTests` updated: PersonService with no tasks → Traceability Not assessed and release Not assessed (was Ready with limitations).
  - Release/governance/adoption subset: 44/44.
- Frontend:
  - new `ReleaseReadinessPresentationTests` (4): root cause once; gaps; prerequisite order; TRX counted as found.
  - `RelationshipReviewTests` +1: re-analysis keeps the Delivery snapshot.
  - Full suite **8228 passed, 0 failed**.

## Browser verification (M2LB copy, nb-NO, Edge, localhost) — 14/14

1. "Release scope not resolved" with the recorded reason.
2. Requirements and Traceability are Not assessed.
3. Causes = scope + identity conflict (once each).
4. The conflict links to the exact review item.
5. Next steps start with Scope.
6. Tests executed is Not assessed.
7. Relationship Review offers the Delivery snapshot, selected.
8. Analysis recorded: 5,757 entities, 8,801 relationships (written to the throwaway database only).
9. **Scope resolved: 38 feature packs in scope.**
10. Requirements assessed (676).
11. Still **Blocked** by the identity conflict (never auto-resolved).
12. The only write was that analysis.
13. No external host.
14. No page errors.

## Not done in this pass

The full P7–P12 redesign was not done:

- dashboard metrics row;
- grouped Scope & Evidence sections;
- a findings table with filters;
- the pre-freeze summary panel;
- comparing frozen reports;
- a history inspector.

The NEXT40 freeze, sign-off and history behaviour is unchanged and still correct (immutable reports, duplicate-freeze refusal, sign-off separate from freeze). TRX results are not mapped per requirement into the release scope (they are reported as found).
