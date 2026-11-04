# Delivery Change Report redesign — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed.

Principle followed: redesign through **progressive disclosure, not by removing information**: summary first → drill down → evidence last.

## Done

**Phase A** (`2d7cd1a7`)

1. **Shared sticky report header** for every tab:
   - title;
   - "Current: 4 Nov 2026 → Baseline: 3 Nov 2026";
   - metadata "38 features · 138 user stories · 6 scenarios · 817 AC · Partial comparison" (plus the scope label for scoped reports);
   - the Reporting project selector and Import snapshot;
   - tabs (Overview | Stories | Snapshots | Report history | Functional Quality Scope) underneath.

   It is sticky on desktop and scrolls normally on phones, where a pinned header would take most of the screen.
2. **Overview headline cards:**
   - Delivery change: changed, new, specification changed, removed, tasks checked.
   - Current delivery: features, user stories, acceptance criteria, open tasks.
   - Quality: need attention, unresolved and removed-story references, hygiene warnings, comparison warnings.

   Every figure is still available under **All figures**.
3. **Delivery attention vs Project hygiene warnings.**
   - Feature issues and scope changes stay in "Delivery attention".
   - Items without a feature and suspected encoding damage move to **Project hygiene warnings · N**, collapsed by default (real M2LB: 7).
4. **Snapshots:** View and Compare stay inline; Rename, Correct effective date and Delete move to a keyboard-operable **⋯** menu.
5. **Report history as cards.** Each card shows:
   - title, version and generated time, period, scope;
   - Baseline → Current with source and fingerprint;
   - coverage, direction, changed and attention badges;
   - actions Open report, Export and ⋯ Delete.
6. **Visual rules:** fewer borders (whitespace and dividers); badges wrap inside cards; no horizontal overflow at 390 px.

**Phase B: Stories**

7. **List + detail.** On desktop (≥ 1100 px) the feature list stays on the left (≈ 38 %) while the opened feature's detail opens on the right (≈ 62 %, sticky, scrollable). The detail keeps its existing tabs: Overview, Phases, User Stories, Acceptance criteria, Tasks, Changes, Evidence. On narrow screens the detail replaces the list, as before.
8. **Work type / Quality in the Stories toolbar** (Non-technical default).
   - Classification comes from the server's canonical classification (shared `ClassifyFeature` + technical change intents, the same as Delivery Overview).
   - Without the API it falls back to the shared classifier.
9. **Type semantics on cards:** a small `MetaBadge` "Feature" (type, not status) and the work category as plain text, so category is never styled as a status.

**Tests and acceptance**
- Page test for header, cards, hygiene split, ⋯ menu and history cards.
- Component test for the work filter default and the split layout.
- Full frontend suite: 8008/8008.
- Browser (1440/390 × light/dark, throwaway DB, real M2LB data): no overflow, no errors, no external requests.

## Not done in this pass (left for a follow-up)

- **Stories tree:** a left-panel outline with Feature → User Story / Scenario group / Shared-foundation nodes and their own MetaBadges. The panel is still the card list grouped by module.
- **Story detail header:** the "Overview | Acceptance Criteria | Tests | Relations | Source Evidence" layout for a *user story*. Story detail today is a section inside the feature's User Stories tab.
- **Functional Quality Scope:** a visual summary (quality distribution), compact item cards and a right-side detail drawer.
- **One shared detail drawer** across Stories, Functional Quality Scope, Snapshots and Changes.
