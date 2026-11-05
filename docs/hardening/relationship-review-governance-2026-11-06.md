# Relationship Review — governance audit and redesign (NEXT48, 2026-11-06)

Branch `008-traceability-first`. Builds on NEXT39 (c17ba986). It extends the existing canonical interpretation engine (`CanonicalInterpretationService`, `CorrelationRules`, `CanonicalReviewQueue`, governance gate); no new engine. Verified on the M2LB copy (`birknext_gaps`: 4,807 entities, 3,802 relationships in the latest interpretation, 1 identity conflict).

## Findings

### Confirmed defects (fixed)

| # | Defect | Root cause | Fix |
|---|---|---|---|
| D1 | The Overview showed **Conflicts 0** while governance **blocked release readiness** on the Utdanning ↔ SkoleAdapter identity conflict. | `SummaryAsync` counted only relations in review state `Conflict`. An alias declared for two entities is stored as an *ambiguous* `CandidateSameAs` relation (rule `CR-K-CONFLICT`), which the queue and governance classify as `IdentityConflict`. | `CanonicalReviewQueue.IdentityConflicts` uses the queue's own classifier and grouping; the summary uses it. Overview, queue and gate now count the same thing (1 on M2LB). |
| D2 | A **rejected candidate was still counted as a candidate** (1,362 instead of 1,361 on M2LB). | The candidate count excluded only `Confirmed`. | Candidates exclude `Confirmed` and `Rejected`. |
| D3 | The totals did not reconcile: established + candidates ≠ relationships, with no explanation. | No bucket for ambiguous, unresolved or rejected relations. | `CanonicalSummaryView.NotCounted` (init property, backward compatible). Live: 3,802 = 2,439 established + 1,361 candidates + 2 not counted. |
| D4 | An identity conflict could be confirmed or rejected with no reason, which can unblock the release gate. | No rationale rule. | `CanonicalReviewQueue.NeedsRationale`: the backend refuses Confirm/Reject on an identity conflict without notes. Defer needs none and leaves the block in place. The UI disables "Same entity" / "Separate entities" until a rationale is typed. |
| D5 | Candidate test links said "looks related … **by naming only**". | Wrong basis: candidates come from requirement ids *mentioned* in class comments, prose comments or strings (assertion messages, test data), never from name or keyword matching. | The explanation now states the real basis and that a candidate is never coverage. |

### Verified, not defects

- **P4 generic-term matching** (e.g. "audit tests → FR-006", "event payload → FR-001"): there is no keyword or name matching in the engine. The `DotNetXunitTestDiscoveryProvider` extracts requirement ids (FR/SC/NFR/AC/US patterns) only, and grades them: trait = Confirmed, structured or doc comment = StronglySupported, other mentions = Inferred → Candidate. Words like "event", "audit" or "user" never create a link. The 1,361 suggestions are id mentions, mostly class-level comments applied to each test of the class.
- Ambiguous test ids across feature packs are already scoped to the test's module, or dropped (`scopeDropped` limitation).
- Decisions are append-only: a new decision supersedes the previous one (`SupersedesId`), history is preserved, and rejected relations never count.

### UX weaknesses (fixed)

- The Overview had no governance focus. It is now a dashboard:
  - **six metrics**: Entities, Established, Suggested, Decisions required, Identity conflicts (marked "part of the decisions"), Not counted; each defined and clickable;
  - the **accounting line**;
  - **Governance attention** near the top: the gate decision with up to 3 findings, and "Resolve N identity conflict(s)" opening the queue filtered to conflicts.
- Header: "Discover, validate and govern relationships across project evidence." with context — Target Application, environment, last analyzed, rule set, sources used.
- Needs Review: "**Required decisions**" vs "**Optional suggestions**", each explained.
- **Identity-conflict workflow**: both entities side by side (type, name, identity, source identity); what Same entity / Separate entities / Defer mean; a rationale field; the decision is recorded with reviewer and time.
- Deep link `?tab=review&type=IdentityConflict`.
- "If this changes…" renamed to **Impact preview**. It uses the existing `CanonicalImpactView`, which already separates confirmed, candidate and unverified dependents.
- Keyboard tabs (roving tabindex, Arrow/Home/End).
- Shared loading in the governance panel (NEXT45).

## Tests

- Backend: new `RelationshipAccountingTests` (4):
  - the Overview counts the conflict the gate blocks on;
  - established + candidates + not counted = every relation (a rejected candidate is excluded);
  - a rationale is required for Confirm/Reject; Defer keeps the block; a new decision supersedes the earlier one;
  - the candidate explanation states its real basis.
- Canonical, correlation, governance and release-readiness suites: 130 → 134 passing. Full backend suite: see the commit message.
- Frontend: `RelationshipReviewTests` +3 (reconciled overview leading to the conflict; the conflict needs a rationale and the deep link applies the filter; keyboard tabs). Intro assertion updated. Full suite **8224 passed, 0 failed**.

## Browser verification (M2LB copy, nb-NO, Edge, localhost) — 12/12

1. Header and context (7 of 10 sources, correlation/4).
2. Overview conflicts 1 = gate finding 1.
3. 3,802 = 2,439 + 1,361 + 2.
4. Governance attention shows "Blocked — 1 blocking finding".
5. "Resolve" opens Utdanning ↔ SkoleAdapter side by side.
6. Required and optional are named apart.
7. Same/Separate are disabled until a rationale is typed; nothing submitted.
8. Suggestions are test links.
9. Keyboard tabs.
10. No API writes.
11. No external host.
12. No page errors.

## Not done in this pass (remaining scope of the brief)

- P9 explorer master–detail with an inspector (the explorer keeps NEXT39's search and chips).
- P12 snapshot revalidation UX beyond the existing `RevalidationRequired` state.
- P16 virtualization (the queue is server-paged at 20; the explorer caps at 5 entities).
- Recent-activity panel on the Overview (the decision history stays under History & Governance).
- The reasoning basis per candidate (class comment vs assertion string) is not carried into the relation: `TestInput` only passes ids; the explanation names the possible bases.

## Compatibility

`NotCounted` is an init property, so older clients ignore it. The candidate explanation text changed, so the next interpretation records new explanations for candidate relations. Earlier interpretations stay frozen; existing decisions are unaffected (decisions key on the relation key, not the text).
