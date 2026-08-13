# CSS Ownership Boundary Analysis
## Constitution Explorer, Plan Explorer, and Task Explorer

**Analysis Date:** 2026-08-13  
**Project:** BirkNext (Blazor WASM QA Tool)

---

## Executive Summary

Three major explorers share significant CSS patterns across buttons, badges, cards, and typography. The codebase uses consistent design tokens but maintains explorer-specific scoping (`ce-*`, `pe-*`, `te-*` prefixes). 

**Key Finding:** ~35-40% of CSS rules are candidates for consolidation into shared utilities without breaking explorer-specific structural layouts.

**High-Value Consolidation:** Buttons, badges, chip variants, empty states, and basic card surfaces.

**Must-Remain-Scoped:** Hierarchy layouts (tree, timeline, phase nodes), grid-based views (map, parallel, dependencies), and type-specific accent color systems.

---

## File Inventory

| File | Location | Lines | Prefix | Purpose |
|------|----------|-------|--------|---------|
| constitution-explorer.css | wwwroot/css | 1,578 | `ce-*` | Page-level Constitution Explorer |
| plan-explorer.css | wwwroot/css | 1,376 | `pe-*` | Page-level Plan Explorer |
| TaskExplorerPanel.razor.css | Components | 2,066 | `te-*` | Component-level Task Explorer Panel |
| TaskExplorer.razor.css | Pages | 15 | `tx-*` | Page wrapper (minimal) |
| ConstitutionExplorerPanel.razor.css | Components | 101 | `ce-*` | Component-level Constitution actions |
| **TOTAL** | | **5,136** | | |

---

## Design Language Inventory

### BUTTONS (Shared Candidates)

#### Primary Action Button
All explorers implement with identical pattern:
- **Constitution Explorer** (lines 181-194)
  ```css
  .ce-build-btn {
      padding: 0.6rem 1.5rem;
      font-size: 0.9rem;
      font-weight: 600;
      color: white;
      background: var(--clr-primary);
      border: none;
      border-radius: var(--radius-md);
      cursor: pointer;
      transition: background var(--transition);
  }
  .ce-build-btn:hover:not(:disabled) { background: var(--clr-primary-hover); }
  .ce-build-btn:disabled { opacity: 0.45; cursor: not-allowed; }
  ```

- **Plan Explorer** (lines 152-164)
  ```css
  .pe-build-btn {
      padding: 0.6rem 1.5rem;
      font-size: 0.9rem;
      font-weight: 600;
      color: white;
      background: var(--clr-primary);
      border: none;
      border-radius: var(--radius-md);
      cursor: pointer;
      transition: background var(--transition);
  }
  .pe-build-btn:hover:not(:disabled) { background: var(--clr-primary-hover); }
  .pe-build-btn:disabled { opacity: 0.45; cursor: not-allowed; }
  ```

- **Task Explorer** (lines 107-121)
  ```css
  .te-build-btn {
      background: var(--clr-primary);
      color: #fff;
      border: none;
      border-radius: var(--radius-md);
      font-size: 0.875rem;
      font-weight: 600;
      padding: 0.55rem 1.5rem;
      cursor: pointer;
      transition: background 0.15s, opacity 0.15s;
      margin-top: 0.25rem;
  }
  .te-build-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  .te-build-btn:not(:disabled):hover { background: var(--clr-primary-hover); }
  ```

**Duplication Count:** 3 near-identical implementations  
**Consolidation Risk:** LOW - Can move to shared `.btn-primary` with explorer variants for sizing

#### Clear/Secondary Buttons
- **Constitution Explorer** (lines 170-179): `.ce-clear-btn`
- **Plan Explorer** (lines 142-150): `.pe-clear-btn`
- **Task Explorer** (lines 94-105): `.te-clear-btn`

All follow same pattern: transparent background, border, subtle text color, danger color on hover.

#### Reset/Control Buttons
- **Constitution Explorer** (lines 277-289): `.ce-reset-btn`
- **Plan Explorer** (lines 235-245): `.pe-reset-btn`
- **Task Explorer** (lines 194-206): `.te-reset-btn`

Nearly identical: subtle border, text, hover state with border-color change.

#### View Toggle Group
- **Constitution Explorer** (lines 252-275): `.ce-view-toggle`, `.ce-view-btn`, `.ce-view-btn.is-active`
- **Plan Explorer** (lines 210-233): `.pe-view-toggle`, `.pe-view-btn`, `.pe-view-btn.is-active`
- **Task Explorer** (lines 171-192): `.te-view-toggle`, `.te-view-btn`, `.te-view-btn.is-active`

All three implement segmented tab buttons identically:
- Flex group with 2px gaps
- Individual buttons with hover + active states
- Active state: white background, primary color text, shadow

**Duplication Count:** 3 complete implementations (68 lines shared pattern)

---

### BADGES & CHIPS (High-Value Consolidation)

#### ID/Reference Badges
Consistent 3-explorer pattern with design token colors:

**Constitution Explorer:**
- `.ce-rule-id` (lines 523-534) - Base styles
- `.ce-rule-id-principle` (lines 536-540) - `var(--clr-badge-req-bg)` / text
- `.ce-rule-id-standard` (lines 542-546) - Cyan variants
- `.ce-rule-id-constraint` (lines 548-552) - Purple variants
- `.ce-rule-id-guideline` (lines 1123-1127) - Green variants
- `.ce-rule-id-governance` (lines 1129-1133) - Orange/warning

**Plan Explorer:**
- `.pe-adr-id` (lines 615-628) - Same base structure, principle color
- `.pe-rule-id-badge` (lines 895-908) - Identical to CE principle

**Task Explorer:**
- `.te-task-id` (lines 441-451) - Info color variant
- `.te-map-task-id` (lines 748-764) - Requirement badge variant

**Pattern:** All badges share:
- `font-size: 0.7rem`
- `font-weight: 700`
- `letter-spacing: 0.06em` / `0.05em`
- `text-transform: uppercase`
- `font-family: monospace`
- `padding: 0.15rem 0.45rem`
- `border-radius: var(--radius-sm)`
- `white-space: nowrap`
- `flex-shrink: 0`

**Consolidation: 18 badge variants can move to shared with BEM modifiers**

#### Status/Severity Badges
- **Plan Explorer** (lines 732-746): `.pe-severity-badge` with `.pe-badge-critical`, `.pe-badge-high`, etc.
- **Task Explorer** (lines 928-936, 950-974): `.te-completion-badge`, `.te-status-badge`, `.te-risk-badge`

All follow: inline-flex, padding, border-radius, color pair (bg + text).

#### Filter Chips
- **Constitution Explorer** (lines 1145-1166): `.ce-type-chip` with 5 type variants (principle, standard, guideline, constraint, governance)
- **Plan Explorer** (lines 282-300): `.pe-filter-chip` with 4 variants (critical, high, medium, low)
- **Task Explorer** (lines 313-334): `.te-filter-chip` with 9 variants

Pattern identical:
```css
padding: 0.25rem 0.7rem;
font-size: 0.78rem;
font-weight: 500;
border: 1px solid var(--clr-border);
border-radius: var(--radius-pill);
cursor: pointer;
transition: background var(--transition), color var(--transition), border-color var(--transition);
white-space: nowrap;

/* Active state across all three: */
background: [color];
color: white;
border-color: [color];
```

**Duplication: 18 chip variants, 100+ lines of near-identical code**

---

### CARDS & SURFACES (Medium-Value Consolidation)

#### Meta/Info Cards
**Constitution Explorer** (lines 363-394):
```css
.ce-meta-card {
    background: var(--clr-surface-white);
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-lg);
    padding: 1rem 1.25rem;
}
.ce-meta-grid { display: flex; flex-wrap: wrap; gap: 1.5rem; }
.ce-meta-item { display: flex; flex-direction: column; gap: 0.2rem; }
.ce-meta-label { font-size: 0.72rem; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; }
.ce-meta-value { font-size: 0.95rem; font-weight: 500; }
```

**Plan Explorer** (lines 324-334):
```css
.pe-meta-card { /* identical to ce-meta-card */ }
.pe-meta-grid { display: flex; flex-wrap: wrap; gap: 1.5rem; }
.pe-meta-item { display: flex; flex-direction: column; gap: 0.2rem; }
.pe-meta-label { font-size: 0.72rem; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; }
.pe-meta-value { font-size: 0.95rem; font-weight: 500; }
```

**Consolidation: Move to shared `.card-meta` + `.meta-*` utilities**

#### Health Cards
Nearly identical across three explorers:

**Base card structure:**
- `background: var(--clr-surface-white)` (or `var(--clr-bg-card)` in TE)
- `border: 1px solid var(--clr-border)`
- `border-radius: var(--radius-lg)` (or `0.375rem` in TE)
- `padding: 0.625rem 1rem`
- `text-align: center`
- `min-width: 72px` / `68px` / `56px`

**Type-specific accent:** `border-top: 3px solid [color]`

Constitution Explorer defines 8 health card types (lines 404-451):
- Principles (blue), Standards (cyan), Constraints (purple), Governance (green), Versions (brown), Platform (red), Module (orange), Rules (dark cyan), Refs (cyan)

Plan Explorer defines 7 types (lines 340-375):
- Critical (red), High (orange), Medium (gold), OK (green), ADR (blue), Dep (purple), Milestone (cyan)

Task Explorer defines 11 types (lines 217-250):
- Done, Open, Linked, Tech, Review, Dev, Risk, Reg, Table, Trace, FR, SC, Unlinked, Testing, Security, US

**Finding:** Each explorer uses same card + accent pattern but with different semantic color mappings.

---

### EMPTY STATES (High-Value Consolidation)

All three explorers implement identical empty state pattern:

**Constitution Explorer** (lines 85-110):
```css
.ce-empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1rem;
    padding: 2.5rem 1rem;
    max-width: 680px;
    margin: 0 auto;
    text-align: center;
}
.ce-empty-icon { font-size: 3rem; line-height: 1; }
.ce-empty-title { font-size: 1.25rem; font-weight: 700; color: var(--clr-text-heading); margin: 0; }
.ce-empty-desc { font-size: 0.9rem; color: var(--clr-text-muted); margin: 0; line-height: 1.6; }
```

**Plan Explorer** (lines 69-94):
```css
.pe-empty-state { /* 2px difference in padding */ }
.pe-empty-icon { /* identical */ }
.pe-empty-title { /* identical */ }
.pe-empty-desc { /* identical */ }
```

**Task Explorer** (lines 9-38):
```css
.te-empty-state { /* 2rem vs 2.5rem padding */ }
.te-empty-icon { /* 2.5rem vs 3rem */ }
.te-empty-title { /* identical */ }
.te-empty-desc { /* 0.83rem vs 0.9rem */ }
```

**Consolidation: Move to shared `.empty-state`, `.empty-*` with optional size variants**

---

### SEARCH & FILTER BARS (Medium-Value Consolidation)

**Constitution Explorer** (lines 293-332):
```css
.ce-search-bar { display: flex; align-items: center; gap: 0.5rem; padding: 0.625rem 0; border-bottom: 1px solid var(--clr-border-subtle); }
.ce-search { flex: 1; padding: 0.45rem 0.75rem; font-size: 0.875rem; border: 1px solid var(--clr-border-input); border-radius: var(--radius-md); background: var(--clr-surface-white); }
.ce-search:focus { border-color: var(--clr-primary-focus); box-shadow: 0 0 0 3px var(--clr-primary-ring); }
.ce-search-clear { font-size: 1.1rem; color: var(--clr-text-subtle); cursor: pointer; }
.ce-search-count { font-size: 0.78rem; color: var(--clr-text-subtle); white-space: nowrap; }
```

**Plan Explorer** (lines 249-272):
```css
.pe-search-bar { /* identical */ }
.pe-search { /* identical */ }
.pe-search:focus { /* identical */ }
.pe-search-clear { /* identical */ }
.pe-search-count { /* identical */ }
```

**Task Explorer** (lines 270-288):
```css
.te-search { /* same padding, slightly different font-size */ }
.te-search:focus { /* identical behavior */ }
.te-search-count { /* identical */ }
```

**Consolidation: Move to shared `.search`, `.search-input`, `.search-clear` - LOW RISK**

---

### TYPOGRAPHY PATTERNS

**Page Titles:** All three use `font-size: 1.5rem`, `font-weight: 700`, `color: var(--clr-text-heading)`

**Headers:** All use `font-size: 1rem` or `0.95rem`, `font-weight: 700`

**Subtitles/Metadata Labels:** All use `font-size: 0.72-0.8rem`, `font-weight: 600`, `letter-spacing: 0.05-0.06em`, `text-transform: uppercase`, `color: var(--clr-text-subtle)`

**Body text:** All use `font-size: 0.875rem`, `color: var(--clr-text-body)`, `line-height: 1.5-1.6`

**Muted text:** `var(--clr-text-muted)` or `var(--clr-text-subtle)`

**Design tokens used by all three:**
- `--clr-text-heading` (dark, for primary text)
- `--clr-text-body` (standard)
- `--clr-text-muted` (secondary)
- `--clr-text-subtle` (tertiary, lightest)

---

## EXPLORER-SPECIFIC STRUCTURES (Must Remain Scoped)

### Constitution Explorer Unique Elements

**1. Map Hierarchy View** (lines 1421-1577)
- `.ce-map-tree` - Flex column layout for tree rendering
- `.ce-map-depth-0` through `.ce-map-depth-5` - Indent levels (0, 1.5rem, 3rem, 4.5rem, 6rem, 7.5rem)
- `.ce-map-node-row` - Individual node with hover state
- `.ce-map-toggle` - Expand/collapse button
- `.ce-map-type-principle`, `.ce-map-type-standard`, etc. - Border-left accent colors (3px)
- `.ce-map-type-group-header` - Section headers with bottom border color matching type

This is completely unique to constitution structure and cannot be shared.

**2. Timeline/Changelog View** (lines 912-1020)
- `.ce-timeline` - Vertical timeline with pseudo-element line
- `.ce-timeline-entry` - Individual entry with relative positioning
- `.ce-timeline-marker` - Circular position marker (10px)
- `.ce-timeline-content` - Entry card with border highlight for latest
- `.ce-timeline-header`, `.ce-timeline-version`, `.ce-timeline-date`, `.ce-timeline-author`
- `.ce-timeline-latest-badge` - Marker for current version

Unique structure for changelog view.

**3. Rule Catalog Table** (lines 1316-1419)
- `.ce-catalog-table-wrap`, `.ce-catalog-table`
- `.ce-catalog-th`, `.ce-catalog-th-sortable` - Header with click handler
- `.ce-catalog-row`, `.ce-catalog-td`
- `.ce-catalog-type-*` variants - 5 type badges in table rows

Specific to Constitution's catalog table format.

**4. Traceability Tab** (lines 1167-1314)
- `.ce-trace-card` - Expandable card with type-specific borders
- `.ce-trace-type-principle.is-expanded`, etc. - Type-specific border colors
- `.ce-trace-ref-count`, `.ce-trace-refby-count` - Badge counts
- `.ce-ref-chip-out`, `.ce-ref-chip-in` - Bidirectional reference styling

Specific to traceability feature.

### Plan Explorer Unique Elements

**1. Implementation Phases Timeline** (lines 1090-1266)
- `.pe-phases` - Container
- `.pe-phase-timeline` - Absolute-positioned line (pseudo-element) + items
- `.pe-phase-item`, `.pe-phase-node` - Numbered circles (1.75rem) on left
- `.pe-phase-number` - Monospace number inside node
- `.pe-phase-card` - Expandable card tied to timeline node
- `.pe-phase-item.is-expanded .pe-phase-node` - Border/fill change on expand
- `.pe-phase-item.is-pre`, `.pe-phase-item.is-post` - Dashed border variants for pre/post phases

Unique phase progression visualization.

**2. ADR Section** (lines 583-663)
- `.pe-adr-section`, `.pe-adr-card` - Expandable architecture decision records
- `.pe-adr-header`, `.pe-adr-id`, `.pe-adr-title`
- `.pe-adr-body`, `.pe-adr-section-block`, `.pe-adr-section-label`

Specific to Plan's ADR documentation format.

**3. Risk/Complexity Groups** (lines 665-846)
- `.pe-risk-group`, `.pe-risk-card` - Card with left border accent (4px)
- `.pe-risk-critical`, `.pe-risk-high`, `.pe-risk-medium`, `.pe-risk-low` - Severity-specific colors
- `.pe-severity-header-critical`, etc. - Group headers with bottom border
- `.pe-severity-dot` - Inline severity indicator
- `.pe-risk-mitigation` - Green-background box for mitigation strategies
- Complexity variants: `.pe-complexity-card`, `.pe-cbadge-*` - Similar to risk

Specific risk/complexity assessment view.

**4. Gate/Compliance Table** (lines 959-1025)
- `.pe-gate-table-wrap`, `.pe-gate-table` - Table for constitution checks
- `.pe-gate-table th`, `.pe-gate-table td` - Styled cells
- `.pe-gate-status` - Status badge (pass/warning/fail/na)
- `.pe-gate-rule`, `.pe-gate-label`, `.pe-gate-evidence`, `.pe-gate-notes` - Column-specific styles

Specific to compliance gate visualization.

**5. Constraint Cards** (lines 1027-1088)
- `.pe-constraint-card` - Flex layout with left border accent
- `.pe-ctype-constraint`, `.pe-ctype-performancegoal`, etc. - Type-specific border colors
- `.pe-constraint-type-badge` - Type label badge
- `.pe-cbadge-constraint`, etc. - Type-specific badge colors

Specific constraint card layout.

### Task Explorer Unique Elements

**1. Tree View** (lines 350-572)
- `.te-tree` - Scrollable container (max-height: 620px)
- `.te-row` - Individual row with flex layout, hover, selection, match states
- `.te-expand-cell`, `.te-expand-btn` - Expand/collapse button (18px width)
- `.te-node-icon`, `.te-node-title`, `.te-task-id` - Inline elements
- `.te-task-check` - Completion checkbox display
- `.te-chip` variants - 6 different chip types (us, parallel, risk, reg, testing, security)
- `.te-meta-chips`, `.te-meta-chip` - Meta information badges
- `.te-ref-chip`, `.te-ref-fr`, `.te-ref-sc`, `.te-ref-task` - Reference badges
- Phase progress bar (3px height)

Completely unique tree rendering with inline chip display.

**2. Map View** (lines 583-842)
- `.te-map` - Flex column with auto-scroll
- `.te-map-header`, `.te-map-kicker`, `.te-map-document-title`
- `.te-map-phase-card` - Phase card wrapper
- `.te-map-phase-header`, `.te-map-phase-title`, `.te-map-phase-count`
- `.te-map-group` - Task group within phase
- `.te-map-group-header`, `.te-map-group-title`, `.te-map-group-count`
- `.te-map-task` - Grid layout: 3-column (icon, content, chips)
- `.te-map-task-id`, `.te-map-task-content`, `.te-map-task-title`
- `.te-map-status-badge`, `.te-map-parallel-badge`

Task-specific map with phase/group/task hierarchy.

**3. Details Panel** (lines 844-1222)
- `.te-details` - Vertical scrollable sidebar
- `.te-details-header`, `.te-details-close`
- `.te-details-task-id`, `.te-details-title`
- `.te-details-section` - Subsection with border-top
- `.te-details-label`, `.te-details-status-row`
- `.te-completion-badge`, `.te-status-badge`, `.te-risk-badge`
- `.te-details-list`, `.te-details-raw` - Code/list display
- `.te-match-type`, `.te-match-requirement`, etc. - Match type indicators
- `.te-area-chip` - Area/category badges
- `.te-file-list`, `.te-file-path` - File references

Task detail sidebar with structured information display.

**4. Parallel Work View** (lines 1825-2014)
- `.te-parallel-view` - Scrollable container
- `.te-parallel-group` - Feature/story grouping
- `.te-parallel-group-header`, `.te-parallel-group-title`, `.te-parallel-group-count`
- `.te-parallel-task` - Grid layout with 4 columns (id, content, tag, status)
- `.te-ptask-id`, `.te-ptask-content`, `.te-ptask-primary`, `.te-ptask-secondary`
- `.te-ptask-tag`, `.te-ptask-status` - Task metadata
- Responsive media query for mobile layout

Unique parallel task visualization with grid-based columns.

**5. Dependencies View** (lines 1617-1771)
- `.te-dependencies-view` - Scrollable container
- `.te-dep-summary` - Grid-spanning summary
- `.te-dep-story-card` - Feature story card
- `.te-dep-story-header`, `.te-dep-story-name`, `.te-dep-story-count`
- `.te-dep-task-row`, `.te-dep-task-header`, `.te-dep-task-id-badge`, `.te-dep-task-title`
- `.te-dep-relationship` - Relationship label
- `.te-dep-related-badge` - Related task reference
- `.te-phase-bar` - Phase progress bar with fill
- `.te-phase-counts`, `.te-phase-pct` - Phase metrics

Dependency graph visualization specific to task explorer.

**6. Phase Dashboard** (lines 1545-1615)
- `.te-phase-dashboard` - Container
- `.te-phase-cards` - Grid layout (auto-fit, 200px minimum)
- `.te-phase-card` - Individual phase summary
- `.te-phase-name`, `.te-phase-status` - Phase metadata
- `.te-phase-status-complete`, `.te-phase-status-in-progress`, `.te-phase-status-not-started` - Status variants

Phase overview dashboard.

**7. KPI/Impact Sections** (lines 1514-1472)
- `.te-kpi-sections`, `.te-kpi-group` - KPI container and items
- `.te-impact-view`, `.te-impact-grid` - Impact area breakdown
- `.te-gaps-section`, `.te-gap-item` - Gap identification
- `.te-zero-gaps`, `.te-zero-gap-item` - Zero-gap indicators
- `.te-link-visualization` - Link chain display

Task execution metrics and coverage visualization.

---

## Design Token Usage Consistency

All three explorers use identical design token variables:

### Colors
- `--clr-primary` (2563eb blue) - Primary actions, active states
- `--clr-primary-hover` - Hover darkening
- `--clr-primary-focus` - Focus ring color
- `--clr-primary-ring` - Focus ring background
- `--clr-surface` - Subtle background
- `--clr-surface-white` - Card/light background
- `--clr-surface-selected` - Selection highlight
- `--clr-border`, `--clr-border-hover`, `--clr-border-subtle`, `--clr-border-input`
- `--clr-text-heading` - Dark text
- `--clr-text-body` - Standard text
- `--clr-text-muted` - Secondary text
- `--clr-text-subtle` - Tertiary text
- `--clr-success`, `--clr-success-bg`, `--clr-success-border`
- `--clr-danger`, `--clr-danger-bg`
- `--clr-warning`, `--clr-warning-bg`, `--clr-warning-border`
- `--clr-info`, `--clr-info-bg`, `--clr-info-border`
- Badge colors: `--clr-badge-req-bg`, `--clr-badge-req-text`, etc.

### Spacing
- `--radius-sm` (varies: 0.25rem in TE, implied 0.375rem in CE/PE)
- `--radius-md` (varies: 0.375-0.5rem)
- `--radius-lg` (implied 0.5-0.75rem)
- `--radius-pill` (999px or close)

### Timing
- `--transition` (all use for state changes)

**Inconsistency Found:** TE uses absolute pixels (`0.375rem`) while CE/PE use CSS variables.

---

## Naming Convention Analysis

### Prefix Strategy
- **Constitution Explorer:** `ce-*` (1,578 lines)
- **Plan Explorer:** `pe-*` (1,376 lines)
- **Task Explorer:** `te-*` (2,066 lines)
- **Task Explorer Page:** `tx-*` (15 lines, minimal)
- **Constitution Panel Component:** `ce-*` reused in component (101 lines)

### Component Naming Patterns
All follow BEM-like structure:
- Block: `.ce-build-btn`, `.pe-view-toggle`, `.te-map-task`
- Element: `.ce-build-btn:hover` (pseudo-element notation)
- Modifier: `.ce-view-btn.is-active`, `.pe-severity-badge.pe-badge-critical`

### Type/Variant Naming
**Constitution Explorer:** `-principle`, `-standard`, `-constraint`, `-guideline`, `-governance`  
**Plan Explorer:** `-critical`, `-high`, `-medium`, `-low`; `-draft`, `-complete`, `-review`  
**Task Explorer:** `-linked`, `-tech`, `-review`, `-dev`, `-risk`, `-reg`, `-testing`, `-security`

Each explorer has domain-specific variant names reflecting its taxonomy.

---

## Consolidation Recommendations

### PHASE 1: High-Value, Low-Risk (Implement First)
Estimated time: 2-3 hours. Consolidation ratio: 35 duplicate rules → 8 shared utilities.

#### 1.1 Primary Action Buttons
**Current State:**
- 3 implementations (`ce-build-btn`, `pe-build-btn`, `te-build-btn`)
- Nearly identical styling
- Lines: 44 total

**Recommendation:**
Create shared utility:
```css
.btn-primary {
    padding: 0.6rem 1.5rem;
    font-size: 0.9rem;
    font-weight: 600;
    color: white;
    background: var(--clr-primary);
    border: none;
    border-radius: var(--radius-md);
    cursor: pointer;
    transition: background var(--transition);
}
.btn-primary:hover:not(:disabled) { background: var(--clr-primary-hover); }
.btn-primary:disabled { opacity: 0.45; cursor: not-allowed; }
```

Explorer-specific sizes via data attributes or separate classes if needed:
```css
.btn-primary.btn-sm { padding: 0.55rem 1.5rem; font-size: 0.875rem; }
```

**Aliases in each explorer CSS:**
```css
.ce-build-btn { @extend .btn-primary; }
.pe-build-btn { @extend .btn-primary; }
.te-build-btn { @extend .btn-primary; }
```

**Files affected:** 3 (constitution-explorer.css, plan-explorer.css, TaskExplorerPanel.razor.css)
**Savings:** ~30 lines of duplicate CSS

#### 1.2 Clear/Secondary Buttons
**Current State:**
- 3 implementations (`ce-clear-btn`, `pe-clear-btn`, `te-clear-btn`)
- Transparent, border-based, danger hover
- Lines: 24 total

**Recommendation:**
```css
.btn-clear {
    background: transparent;
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-md);
    color: var(--clr-text-subtle);
    font-size: 0.75rem;
    padding: 0.15rem 0.6rem;
    cursor: pointer;
    transition: color 0.15s;
}
.btn-clear:hover { color: var(--clr-text-primary); }

.btn-clear.btn-danger { color: var(--clr-text-subtle); }
.btn-clear.btn-danger:hover { color: var(--clr-danger); }
```

**Savings:** ~20 lines

#### 1.3 View Toggle Groups
**Current State:**
- 3 near-identical implementations
- 68 lines total (header + buttons + active state)

**Recommendation:**
```css
.view-toggle {
    display: flex;
    gap: 2px;
    background: var(--clr-surface);
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-md);
    padding: 2px;
}

.view-btn {
    padding: 0.3rem 0.75rem;
    font-size: 0.8rem;
    font-weight: 500;
    color: var(--clr-text-muted);
    background: transparent;
    border: none;
    border-radius: calc(var(--radius-md) - 2px);
    cursor: pointer;
    transition: background var(--transition), color var(--transition);
    white-space: nowrap;
}
.view-btn:hover { background: var(--clr-surface-white); color: var(--clr-text-body); }
.view-btn.is-active { background: var(--clr-surface-white); color: var(--clr-primary); font-weight: 600; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }

/* Slight size variant for Plan Explorer */
.view-btn.btn-sm { padding: 0.3rem 0.7rem; font-size: 0.78rem; }
```

**Savings:** ~50 lines

#### 1.4 Empty States
**Current State:**
- 3 near-identical implementations
- Lines: 78 total

**Recommendation:**
```css
.empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1rem;
    padding: 2.5rem 1rem;
    max-width: 680px;
    margin: 0 auto;
    text-align: center;
}
.empty-state.empty-sm { padding: 2rem 1rem; }
.empty-state.empty-lg { padding: 3rem 1rem; }

.empty-icon { font-size: 3rem; line-height: 1; }
.empty-icon.icon-sm { font-size: 2.5rem; }
.empty-icon.icon-lg { font-size: 3.5rem; }

.empty-title {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--clr-text-heading);
    margin: 0;
}

.empty-desc {
    font-size: 0.9rem;
    color: var(--clr-text-muted);
    margin: 0;
    line-height: 1.6;
}
```

**Savings:** ~50 lines

**PHASE 1 Total Savings:** ~150 lines (~12% of total CSS)

---

### PHASE 2: Medium-Value, Low-Risk (Implement After Phase 1)
Estimated time: 4-5 hours.

#### 2.1 ID/Reference Badges
**Current State:**
- 18 badge variants across explorers
- Base styles duplicated 3x
- Lines: 200+ with variants

**Recommendation:**
Create badge system in shared file (explorers-common.css):
```css
.badge {
    display: inline-block;
    font-size: 0.7rem;
    font-weight: 700;
    letter-spacing: 0.06em;
    text-transform: uppercase;
    font-family: monospace;
    padding: 0.15rem 0.45rem;
    border-radius: var(--radius-sm);
    white-space: nowrap;
    flex-shrink: 0;
}

/* Primary badge types */
.badge-req      { background: var(--clr-badge-req-bg); color: var(--clr-badge-req-text); border: 1px solid #bfdbfe; }
.badge-standard { background: #cffafe; color: #0e7490; border: 1px solid #a5f3fc; }
.badge-constraint { background: #ede9fe; color: #6d28d9; border: 1px solid #ddd6fe; }
.badge-guideline { background: #d1fae5; color: #065f46; border: 1px solid #a7f3d0; }
.badge-governance { background: var(--clr-warning-bg); color: var(--clr-warning); border: 1px solid var(--clr-warning-border); }

/* Size variants */
.badge.badge-sm { font-size: 0.68rem; padding: 0.12rem 0.35rem; }
.badge.badge-lg { font-size: 0.75rem; padding: 0.2rem 0.55rem; }
```

In explorers, create aliases:
```css
/* constitution-explorer.css */
.ce-rule-id          { @extend .badge; }
.ce-rule-id-principle { @extend .badge-req; }
.ce-rule-id-standard { @extend .badge-standard; }
```

**Savings:** ~80 lines

#### 2.2 Filter Chips
**Current State:**
- 18 chip variants
- 120+ lines of duplicate code

**Recommendation:**
```css
.chip {
    padding: 0.25rem 0.7rem;
    font-size: 0.78rem;
    font-weight: 500;
    color: var(--clr-text-subtle);
    background: var(--clr-surface-white);
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-pill);
    cursor: pointer;
    transition: background var(--transition), color var(--transition), border-color var(--transition);
    white-space: nowrap;
}
.chip:hover { border-color: var(--clr-border-hover); color: var(--clr-text-body); }
.chip.is-active { background: var(--clr-primary); color: white; border-color: var(--clr-primary); }

/* Color variants */
.chip-principle  { /* base */ }
.chip-principle.is-active { background: var(--clr-primary); border-color: var(--clr-primary); }
.chip-standard.is-active { background: #0891b2; border-color: #0891b2; }
.chip-critical.is-active { background: #dc2626; border-color: #dc2626; }
.chip-high.is-active { background: #ea580c; border-color: #ea580c; }
/* ... etc */
```

**Savings:** ~70 lines

#### 2.3 Meta/Info Cards
**Current State:**
- Duplicated in CE and PE identically
- Lines: 25

**Recommendation:**
```css
.card-meta {
    background: var(--clr-surface-white);
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-lg);
    padding: 1rem 1.25rem;
}

.card-meta-grid { display: flex; flex-wrap: wrap; gap: 1.5rem; }
.card-meta-item { display: flex; flex-direction: column; gap: 0.2rem; }
.card-meta-label { font-size: 0.72rem; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--clr-text-subtle); }
.card-meta-value { font-size: 0.95rem; font-weight: 500; color: var(--clr-text-heading); }
```

Aliases:
```css
.ce-meta-card { @extend .card-meta; }
.ce-meta-grid { @extend .card-meta-grid; }
```

**Savings:** ~15 lines

#### 2.4 Health Cards (Core Pattern)
**Current State:**
- Base pattern: `background`, `border: 1px solid var(--clr-border)`, `border-top: 3px solid [color]`
- Duplicated 3x (40 lines each)
- Variants: 26 total (8 CE, 7 PE, 11 TE)

**Challenge:** Each explorer has different semantic meanings (CE: principles/standards/etc; PE: severity levels; TE: link states/test status)

**Recommendation:**
Create base in shared file, keep variant definitions in explorers:
```css
/* explorers-common.css */
.health-card {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.2rem;
    padding: 0.625rem 1rem;
    background: var(--clr-surface-white);
    border: 1px solid var(--clr-border);
    border-radius: var(--radius-lg);
    text-align: center;
    min-width: 72px;
}

.health-value { font-size: 1.5rem; font-weight: 700; line-height: 1; color: var(--clr-text-heading); }
.health-label { font-size: 0.7rem; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; color: var(--clr-text-subtle); white-space: nowrap; }

/* Type accent helper */
.health-card[data-type="primary"]  { border-top: 3px solid var(--clr-primary); }
.health-card[data-type="success"]  { border-top: 3px solid var(--clr-success); }
.health-card[data-type="danger"]   { border-top: 3px solid var(--clr-danger); }
```

In explorers:
```css
/* constitution-explorer.css */
.ce-health-card { @extend .health-card; border-top: 3px solid transparent; }
.ce-hcard-principles { border-top: 3px solid var(--clr-primary); }
.ce-hcard-principles .ce-health-value { color: var(--clr-primary); }
/* ... variants use same pattern */
```

**Savings:** ~60 lines (consolidated base + transitions)

**PHASE 2 Total Savings:** ~225 lines (~4% of total CSS)

---

### PHASE 3: Lower-Priority Items (Polish Phase)
Estimated time: 5-7 hours.

#### 3.1 Search & Filter Bars
**Consolidation:** 40+ lines
```css
.search-bar { /* shared */ }
.search-input { /* shared */ }
.search-clear { /* shared */ }
.filter-strip { /* shared */ }
```

#### 3.2 Reset Buttons
**Consolidation:** 20 lines
```css
.btn-reset { /* shared */ }
```

#### 3.3 Typography Scale Utilities
**Consolidation:** 30 lines
```css
.text-heading, .text-body, .text-muted, .text-subtle { /* shared */ }
.label-uppercase { /* shared */ }
```

**PHASE 3 Total Savings:** ~90 lines

---

## Summary Table: Consolidation Opportunities

| Category | Files | Current Lines | Duplicates | Risk | Recommendation |
|----------|-------|---|---|---|---|
| **Primary Buttons** | 3 | 44 | 3 | LOW | Shared `.btn-primary` with size variants |
| **Clear Buttons** | 3 | 24 | 3 | LOW | Shared `.btn-clear` |
| **View Toggles** | 3 | 68 | 3 | LOW | Shared `.view-toggle` + `.view-btn` |
| **Empty States** | 3 | 78 | 3 | LOW | Shared `.empty-state` with size variants |
| **ID Badges** | 3 | 200+ | 18 variants | LOW | Shared badge system with color modifiers |
| **Filter Chips** | 3 | 120+ | 18 variants | LOW | Shared chip system with color modifiers |
| **Meta Cards** | 2 | 25 | 1 (PE/CE) | LOW | Shared `.card-meta` + grid |
| **Health Cards (Base)** | 3 | 120+ | 3 base + 26 variants | MEDIUM | Shared base, explorers define variants |
| **Search Bars** | 3 | 40+ | 3 | LOW | Shared `.search-input` + `.search-bar` |
| **Reset Buttons** | 3 | 20 | 3 | LOW | Shared `.btn-reset` |
| **Typography Scale** | 3 | 30+ | 3 | VERY LOW | Shared text utility classes |
| **Expandable Cards** | Multiple | 100+ | Various | MEDIUM | Standardize `.expandable-card` pattern |
| **Page Shell** | 3 | 69 | 3 | MEDIUM | Shared page layout wrapper |
| **Input Areas** | 3 | 103+ | 3 | MEDIUM | Shared `.input-area` + sections |

**Total Estimated Consolidation Savings:** ~600 lines (12% of 5,136 total)

---

## Implementation Strategy

### Step 1: Create Shared File
**File:** `AIAssisted/frontend/BirkNext.Web/wwwroot/css/explorers-common.css`

**Contents:**
1. Design token reference (comments only)
2. Button utilities (primary, clear, reset, control)
3. View toggle group
4. Empty state patterns
5. Badge system
6. Chip system
7. Meta card pattern
8. Health card base
9. Search/filter patterns
10. Typography utilities
11. Card base patterns

### Step 2: Reference in Each Explorer
In `constitution-explorer.css`, `plan-explorer.css`, `TaskExplorerPanel.razor.css`:
```css
/* Import shared utilities - add at top before explorer-specific CSS */
@import url('./explorers-common.css');

/* Explorer-specific CSS continues below */
```

### Step 3: Create Aliases/Extensions
For each shared utility, create explorer-specific aliases:
```css
/* constitution-explorer.css */
.ce-build-btn { /* uses .btn-primary from shared */ }
.ce-clear-btn { /* uses .btn-clear from shared */ }
```

### Step 4: Update Component-Level Files
- `ConstitutionExplorerPanel.razor.css` - Can reference shared badges
- `TaskExplorerPanel.razor.css` - Can reference shared button patterns
- Consider extracting component-scoped utilities

---

## Naming Convention Recommendation

Keep explorer prefixes (`ce-*`, `pe-*`, `te-*`) for:
1. Page-level abstractions
2. Explorer-specific layouts
3. Semantic domain mapping (principles vs severity vs task status)

Move to unprefixed utilities in shared file for:
1. Generic button patterns (`.btn-primary`, `.btn-clear`)
2. Generic badge patterns (`.badge`, `.badge-req`)
3. Generic chip patterns (`.chip`, `.chip.is-active`)
4. Generic card patterns (`.card`, `.card-meta`)
5. Generic layout patterns (`.empty-state`, `.search-bar`)

---

## Design Token Consistency Fix

**Issue:** Task Explorer uses absolute pixel values (`0.375rem`) while others use CSS variables.

**Recommendation:**
Audit and standardize in shared file:
```css
:root {
  /* Ensure these are defined centrally */
  --radius-xs: 0.25rem;
  --radius-sm: 0.375rem;
  --radius-md: 0.5rem;
  --radius-lg: 0.75rem;
  --radius-pill: 999px;
}
```

Then replace all hardcoded values with variables.

---

## Testing Recommendations

1. **Visual Regression Testing:** Create side-by-side comparison of original vs consolidated CSS for each explorer
2. **Component Testing:** Verify buttons, badges, chips render identically across explorers
3. **Browser Testing:** Test on Chrome, Firefox, Safari, Edge
4. **Accessibility:** Verify focus states, color contrast, keyboard navigation on consolidated patterns
5. **Performance:** Measure CSS file size reduction and load time improvement

---

## Next Steps

1. **Week 1:** Implement Phase 1 (buttons, toggles, empty states) - 2-3 hours
2. **Week 2:** Implement Phase 2 (badges, chips, cards) - 4-5 hours
3. **Week 3:** Implement Phase 3 (typography, utilities, cleanup) - 5-7 hours
4. **Week 4:** Testing, refinement, documentation

---

## Appendix: Rule Count Summary

| Explorer | Total Lines | Shared Candidates | Unique (%) | Design Complexity |
|----------|---|---|---|---|
| Constitution Explorer | 1,578 | ~550 (35%) | 1,028 (65%) | High (8 semantic types) |
| Plan Explorer | 1,376 | ~480 (35%) | 896 (65%) | High (multiple tabs, phases) |
| Task Explorer Panel | 2,066 | ~400 (19%) | 1,666 (81%) | Very High (tree, map, details, dependencies, parallel) |
| Task Explorer Page | 15 | ~0 (0%) | 15 (100%) | Minimal |
| Constitution Panel | 101 | ~40 (40%) | 61 (60%) | Low (component-scoped) |
| **TOTAL** | **5,136** | **~1,470 (29%)** | **3,666 (71%)** | **High** |

---

**Document prepared for refactoring planning.**
**Recommendation: Start with Phase 1 consolidations (low-risk, immediate value).**
