# NEXT56 — Relationship Review explorer and review queue redesign (2026-11-06)

Branch `008-traceability-first`. Builds on NEXT48 (c1642763): reconciled accounting, rationale required for identity conflicts, and the governance dashboard. Frontend only. It reuses the existing canonical interpretation API (`EntitiesAsync`, `EntityAsync`, `ImpactAsync`, `ReviewItemsAsync`, `DecideAsync`); there is no new engine, endpoint or schema. Nothing pushed.

**Status: partially completed.**

- Done:
  - Entity browsing for large graphs: a paged list beside an inspector.
  - Evidence inspection.
  - Grouped review of candidate relationships.
  - Focused decisions: one inspector per question.
  - Source evidence beside an identity conflict.
- Confirmed, rejected and deferred decisions are untouched. They are stored and applied by the backend exactly as before.
- **`Utdanning ↔ SkoleAdapter` is not resolved**: no decision was made by code, tests or the browser check.

## Audit — what NEXT48 left

| # | Gap (brief §) | Before |
|---|---|---|
| R1 | Queue (P6/P7) | 20 full cards per page. Each card carried its badges, reason, an evidence `<details>`, the alternatives and three buttons with a note field: every decision UI was rendered at once. Nothing was grouped, so related candidate links of one test or one requirement were scattered across pages. |
| R2 | Identity conflict evidence (P8) | The two sides showed the display name and identity only. The **aliases and who declared them**, and the occurrences per source snapshot, were not shown, yet they are exactly what a person needs to decide. |
| R3 | Explorer (P9) | A search rendered up to **5** entities, each with all of its relationships inline (`CanonicalCorrelationPanel`). There was no list/detail split, no paging beyond five, no keyboard navigation, and established relationships were not separated from candidates. |
| R4 | Loading | The queue error was a paragraph with an inline Retry. The explorer had no stale-response protection. |

## Changes

### Review queue: grouped list + one inspector (`Pages/CorrelationReview.razor`)

- **Left**: the page of questions as compact rows: type, "Source *verb* Target", the reason (two lines) and "Affects N consumers".
- **Group by** review type (the default), source entity or target entity. Grouping keeps the server's priority order and orders groups by first occurrence (`Services/CorrelationQueueGroups.cs`). The candidate links of one test or one requirement sit together.
- The queue stays server-paged at 20, with Previous and Next disabled while loading.
- **Right: the inspector** of the selected question:
  - type and status;
  - source and target, each with type, readable name and full identity;
  - the relation verb;
  - the reason, and the affected consumers by name;
  - **all evidence** (kind, reference, detail), with the rule and rule-set version.
- Then the focused decision for that question only:
  - **choose an alternative or "None of these"** for an ambiguity;
  - **Confirm / Reject / Defer** with an optional note;
  - or the identity-conflict workflow (Same entity / Separate entities with a required rationale, or Defer).
- **The next question comes up after a decision**: a decided item leaves the reloaded page, and the first remaining item is selected.
- Queue failure: `LoadError` with Retry.

### Identity conflicts: source evidence side by side

Beside the conflict, each entity's compact inspector shows:

- name and type, and the identity with **Copy id**;
- what it is declared as and how it was resolved;
- incoming and outgoing counts;
- **aliases with their state and who declared them, and the rule**;
- **source occurrences** per snapshot.

On the M2LB copy this shows the actual cause:

- **Utdanning**: `M2LB.SkoleAdapter.Worker`, *Proposed*, "declared by test pack expected aliases (conflicting declaration) · rule CR-K-CONFLICT".
- **SkoleAdapter**: the same alias, *Proposed*, "declared by domain profile skolenaervaer; test pack expected aliases (conflicting declaration)".
- SkoleAdapter's own aliases `M2LB.Skole.BiRK.Worker` and `SkoleAdapter` are *Declared* by the domain profile.

The options remain:

- Same entity / Separate entities: both need a written rationale, which the backend also enforces.
- Defer: nothing changes, and release readiness stays blocked.

### Explorer: paged list + entity inspector (`Components/RelationshipExplorer.razor`, `Components/RelationshipEntityInspector.razor`)

- The search (type chips, type select, text) is unchanged.
- **Results**: a server-paged list of 25 at a time with "Load N more" and "25 of 40 entities". It shows each entity's type, readable name and declared identity.
  - It is a `listbox`: ↑/↓/Home/End move the selection; the first match is selected.
  - A newer search makes an older response stale (`LoadSequence`).
- **Inspector**:
  - readable name, type, full identity with **Copy id**, declared-as, resolved-by and key attributes;
  - aliases with their declaring source;
  - source occurrences (collapsible);
  - **outgoing and incoming relationships grouped by relation type, each group stating "N established · M candidate or not counted"**. Established relationships are listed first; every relationship carries its status badge. Eight are shown per group, with "Show all".
  - **Impact preview** (the shared `CanonicalImpactSummary`).
  - Loading, error and Retry use the shared components.
- Nothing renders the whole graph: one page of entities and one entity's detail.

### Visual

- Two-column list and inspector on desktop; the inspector is sticky.
- One column below 900 px.
- Long identities and names wrap. No horizontal overflow at 390 px.
- After reviewing the screenshot: the relation explanation is no longer repeated when it equals the reason.

## Tests

- New in `RelationshipReviewTests` (4):
  1. **The identity conflict shows each entity's source evidence side by side**: names, aliases "declared by …", occurrences, and no relationship list beside a conflict. **No decision is made** (`DecideAsync` never called), and Same entity stays disabled without a rationale.
  2. The review queue: three candidate links; grouping by source puts both links of one test together; selecting the second opens its evidence and affected consumers; **Confirm decides exactly that relation** (`rel-s3`).
  3. The explorer:
     - "2 of 30 entities" with "Load 25 more";
     - the inspector shows name, identity and **established apart from candidate** ("0 established · 1 candidate or not counted");
     - ArrowDown selects and loads the next entity.
  4. `CorrelationQueueGroups` keeps the priority order and groups by first occurrence.
- Updated:
  - `CanonicalCorrelationUiTests`: the status and affected consumers are read from the inspector. The explorer reads one page of 25 (was 5); the regression check that the active project, never the literal `_scope`, is sent is kept.
- Relationship and correlation subset: **100 passed, 0 failed**. Full frontend suite: **8259 passed, 0 failed**.

## Browser verification (M2LB copy, Edge, localhost only) — 14/14, read-only

Script `next56-e2e.cjs`; screenshots `next56-conflict.png`, `next56-queue.png` and `next56-explorer.png`.

1. The identity conflict is open in the inspector: **Utdanning ↔ SkoleAdapter**.
2. Each side shows its identity and source evidence. Utdanning has 1 outgoing relationship; SkoleAdapter has 1 incoming; the aliases and their declaring sources are listed.
3. A decision needs a rationale; nothing was decided.
4. Suggestions: 20 compact items (one page) with one inspector.
5. Grouped by source entity.
6. Selecting an item shows its reason, evidence and rule.
7. Explorer, "FR-001": **25 of 40 entities**, inspector "FR-001 · 0 outgoing · 6 incoming".
8. Relations grouped with established apart: "Verifies — 0 established · 6 candidate or not counted". The name-only test links stay candidates and are never counted.
9. ArrowDown selects the next entity.
10. No overflow at 1440 px.
11. No overflow at 390 px.

Also: no POST/PUT/DELETE (no decision, no analysis), no external host, no page errors.

## Not done (remaining acceptance criteria)

- **P8 graph-change preview per option** ("what changes if Same entity"): each entity's Impact preview is available in the explorer, but the conflict does not compute the merged or separated outcome.
- **P12 snapshot revalidation UX** beyond the existing `RevalidationRequired` state.
- **P16 server-side grouping and virtualization**: grouping is per page of 20, and the explorer pages 25 at a time; neither is virtualized.
- **P5 recent activity on the Overview**: the decision history stays under History & Governance.
- **P11 history inspector**: History & Governance is unchanged.
- **Per-candidate reasoning basis**: whether a class comment or an assertion string mentioned the requirement is not carried into the relation. The NEXT48 limitation stands.
