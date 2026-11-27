# BirkNext V12 — shared design system (2026-11-27)

**Scope:** reuse what exists; do not start a second component system. This document records:
- the shared patterns V12 settled;
- the components that already implement them;
- what was applied in V12;
- what remains.

## 1. Patterns and their existing implementation
| Concept (brief §17) | Existing implementation | V12 decision |
|---|---|---|
| ScopeToolbar | `Components/SpecificationScopePicker.razor` (module + specification), `ProjectSelector` in `PageHeader` | Keep. An application-level scope bar (APP-1) is the next consolidation step: **NOT IMPLEMENTED** |
| SearchToolbar / FilterChips | `.msb-toolbar` + Discover `ActiveChips()` | Keep as the reference for filter rows: **NOT IMPLEMENTED** as a component (MT-5) |
| ScenarioRow | Discover `Row` render fragment; Plan `RowItem` | Row = one target; states opened / selected / in plan are independent and visible without colour alone: **IMPLEMENTED** (CSS) |
| StatusBadge | `msb-status msb-tone-*` in Manual Testing; `StatusChip`, `SemanticStatusBadge` elsewhere | Manual Testing keeps `msb-status`. The 337 class families elsewhere are inventoried (APP-2): **NOT IMPLEMENTED** |
| ReadinessSummary | `ScenarioPreparationPanel` (`CountText`: «x av y påkrevde punkter oppfylt») | Keep. Basis label «Vurdert av tester»: **IMPLEMENTED** |
| ResultCount / ShowMore / Pagination | `Shared/Components/Results/ShowMore.razor`, `Pager.razor` | Keep. Silent `.Take(n)` on other pages is inventoried (APP-3): **NOT IMPLEMENTED** |
| Dialog / UnsavedChangesGuard | `AddScenariosToPlan` dialog pattern (focus in/out, Escape, «ulagrede valg» question); `NavigationLock` | The reference pattern. Other modals (APP-4): **NOT IMPLEMENTED** |
| EmptyState | `Shared/Components/Layout/EmptyState.razor` (5 users), `msb-empty` in Manual Testing | Inventoried (APP-7): **NOT IMPLEMENTED** |
| PrimaryActionBar | `.msb-selection` (sticky bulk bar), `.mtw-primary-actions` | **One primary action per context**: **IMPLEMENTED** in Finn og forstå |

## 2. Rules (apply to every new or changed screen)
1. **One primary button per context.** Secondary actions use `btn-outline-secondary` or links.
2. **Status is text plus colour, never colour alone.** A state that is shown only by weight or colour gets a second cue (outline, icon, text).
3. **Every clickable control is a `button` or `a`.** No clickable `span`/`div` without role and keyboard handling.
4. **Closing a panel or dialog returns focus** to the control that opened it.
5. **Progress counts what was done, not a percentage that hides blocked or failed:** «x av y behandlet».
6. **Wording:** «Vurdert av …» for recorded assessments; «Bekreftet» only for a real confirmation (for example a requirement link).
7. **Tokens:** use `design-tokens.css`. No new palette.

## 3. Applied outside Manual Testing in V12
| Finding | Status | Change |
|---|---|---|
| APP-8: search-clear «×» was a clickable `<span>` | **IMPLEMENTED** | A real `button` with `aria-label` and a visible focus ring, in Artifact Traceability, Constitution Explorer and Plan Explorer. Look unchanged. Their test classes pass (110 / 110 with execution). |

## 4. Remaining (ranked; for the next UX iteration)
1. APP-1: one application scope bar.
2. APP-4: one dialog component (focus trap, Escape, focus return) replacing about 8 modal families and 9 native `confirm()`.
3. APP-2/7: StatusBadge and EmptyState consolidation.
4. APP-3: tables with honest counts and pagination instead of silent `.Take(n)`.
5. APP-5: one UI language per page and per shared control.
6. APP-6: semantic tokens for User Story / AC / Test case / Readiness / Source evidence.
