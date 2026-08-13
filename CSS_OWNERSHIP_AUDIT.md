# CSS OWNERSHIP AUDIT: Phase 1 Refactor Failure

**Audit Date:** 2026-08-13  
**Status:** ❌ **CRITICAL FAILURES FOUND**

---

## A. Is explorers-common.css Actually Loaded?

**Finding:** ❌ **NO - BLOCKER**

### Search for References
```bash
grep -r "explorers-common" . --include="*.html" --include="*.razor" --include="*.css"
# Result: NO MATCHES
```

### CSS Loading Mechanism (index.html)
```html
Line 9-10:   <link rel="stylesheet" href="css/bootstrap/bootstrap.min.css" />
Line 10:     <link rel="stylesheet" href="css/app.css" />
Line 11:     <link rel="stylesheet" href="css/design-tokens.css" />
Line 12:     <link rel="stylesheet" href="css/components.css" />
Line 13:     <link rel="stylesheet" href="css/dashboard.css" />
Line 14:     <link rel="stylesheet" href="css/forms.css" />
Line 15:     <link rel="stylesheet" href="css/constitution-explorer.css" /> ✓ LOADED
Line 16:     <link rel="stylesheet" href="css/plan-explorer.css" /> ✓ LOADED
Line 17:     <link rel="stylesheet" href="css/artifact-traceability.css" />
Line 18:     <link rel="stylesheet" href="css/constitution-compliance.css" />
[...]
# NOTE: explorers-common.css is MISSING
```

**Critical Issue:**
- explorers-common.css exists in wwwroot/css/
- It is NOT linked in index.html
- The application does NOT load it
- Any styles defined there are UNUSED

**Impact:** Phase 1 consolidation has zero effect on the running application.

---

## B. Which Phase 1 Duplicates Remained?

**Finding:** ❌ **ALL 9 PATTERNS REMAIN IN ORIGINAL FILES**

### Pattern 1: Primary Action Button

**Declaration:**
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

**Duplication Check:**
- Constitution Explorer (`constitution-explorer.css`, lines 181-194): ❌ DUPLICATE EXISTS
  ```css
  .ce-build-btn { [same 11 properties] }
  .ce-build-btn:hover:not(:disabled) { background: var(--clr-primary-hover); }
  .ce-build-btn:disabled { opacity: 0.45; cursor: not-allowed; }
  ```

- Plan Explorer (`plan-explorer.css`, lines 152-164): ❌ DUPLICATE EXISTS
  ```css
  .pe-build-btn { [IDENTICAL 11 properties] }
  .pe-build-btn:hover:not(:disabled) { background: var(--clr-primary-hover); }
  .pe-build-btn:disabled { opacity: 0.45; cursor: not-allowed; }
  ```

- Task Explorer (`TaskExplorerPanel.razor.css`, lines 107-121): ❌ DUPLICATE EXISTS
  ```css
  .te-build-btn { [similar 11 properties with minor variations] }
  .te-build-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  .te-build-btn:not(:disabled):hover { background: var(--clr-primary-hover); }
  ```

**Status:** ✗ Primary button: 3 copies, 0 consolidated
**Lines:** Constitution 14, Plan 13, Task 15 = 42 lines duplicated

---

### Pattern 2: Clear/Secondary Button

**Duplication Check:**
- Constitution Explorer (`constitution-explorer.css`, lines 170-179): ❌ EXISTS
- Plan Explorer (`plan-explorer.css`, lines 142-150): ❌ EXISTS  
- Task Explorer (`TaskExplorerPanel.razor.css`, lines 94-105): ❌ EXISTS

**Status:** ✗ Clear button: 3 copies, 0 consolidated
**Lines:** Constitution 10, Plan 9, Task 12 = 31 lines duplicated

---

### Pattern 3-9: View Toggle, Empty States, etc.

**Spot Check Results:**
- `.ce-view-toggle` / `.pe-view-toggle` / `.te-view-toggle`: ❌ All 3 remain
- `.ce-view-btn` / `.pe-view-btn` / `.te-view-btn`: ❌ All 3 remain
- `.ce-empty-state` / `.pe-empty-state` / `.te-empty-state`: ❌ All 3 remain
- `.ce-reset-btn` / `.pe-reset-btn` / `.te-reset-btn`: ❌ All 3 remain

**Summary:**
```
Reported Consolidation: 18 duplicate rules removed, 150 lines eliminated
Actual Consolidation:   0 duplicate rules removed, 0 lines eliminated
```

---

## C. Actual Ownership Before Correction

```
constitution-explorer.css (1,578 lines)
  ├─ DESIGN (buttons, toggles, empty states, badges): ~550 lines
  ├─ STRUCTURE (hierarchy, timeline, traceability): ~1,028 lines
  └─ Includes .ce-build-btn, .ce-clear-btn, .ce-reset-btn, .ce-view-toggle, .ce-empty-state

plan-explorer.css (1,376 lines)
  ├─ DESIGN (buttons, toggles, empty states, badges): ~480 lines
  ├─ STRUCTURE (phases, ADR, risk, gates): ~896 lines
  └─ Includes .pe-build-btn, .pe-clear-btn, .pe-reset-btn, .pe-view-toggle, .pe-empty-state

TaskExplorerPanel.razor.css (2,066 lines)
  ├─ DESIGN (buttons, toggles, empty states, badges): ~400 lines
  ├─ STRUCTURE (tree, map, details, parallel, dependencies): ~1,666 lines
  └─ Includes .te-build-btn, .te-clear-btn, .te-reset-btn, .te-view-toggle, .te-empty-state

explorers-common.css (155 lines) [NOT LOADED]
  ├─ Shared button definitions (UNUSED)
  └─ No impact on application (file not referenced)

ACTUAL OWNERSHIP: Mixed (explorer files own everything)
INTENDED OWNERSHIP: Shared file owns design language
STATUS: Intention ≠ Implementation
```

---

## D. What Needs to Change

### 1. Load explorers-common.css in index.html

**Required Change:**
```html
<!-- Add BEFORE explorer-specific CSS files -->
<link rel="stylesheet" href="css/explorers-common.css" />
<link rel="stylesheet" href="css/constitution-explorer.css" />
<link rel="stylesheet" href="css/plan-explorer.css" />
```

### 2. Remove Duplicate Design Declarations from Explorer Files

**Constitution Explorer (`constitution-explorer.css`)**
Remove:
- Lines 170-179: `.ce-clear-btn` + hover/states (design only)
- Lines 181-194: `.ce-build-btn` + hover/states (design only)
- Lines 252-275: `.ce-view-toggle` + `.ce-view-btn` + states (design only)
- Lines 85-110: `.ce-empty-state` + subcomponents (design only)

Keep:
- All structural layout rules
- All unique style rules (health cards, timeline, etc.)

**Plan Explorer (`plan-explorer.css`)**
Remove:
- Lines 142-150: `.pe-clear-btn` + states (design only)
- Lines 152-164: `.pe-build-btn` + states (design only)
- Lines 210-233: `.pe-view-toggle` + `.pe-view-btn` + states (design only)
- Lines 83-110: `.pe-empty-state` + subcomponents (design only)

Keep:
- All structural layout rules
- All unique style rules (phases, ADR, risk, gates)

**Task Explorer (`TaskExplorerPanel.razor.css`)**
Remove:
- Lines 94-105: `.te-clear-btn` + states (design only)
- Lines 107-121: `.te-build-btn` + states (design only)
- Lines 171-192: `.te-view-toggle` + `.te-view-btn` + states (design only)
- Lines 264-290: `.te-empty-state` + subcomponents (design only)

Keep:
- All structural layout rules
- All unique style rules (tree, map, details, parallel, dependencies)

---

## E. Actual Ownership After Correction

```
explorers-common.css (155 lines) [NOW LOADED]
  └─ DESIGN: buttons, toggles, empty states
     - .btn-primary (aliased as .ce-build-btn, .pe-build-btn, .te-build-btn)
     - .btn-clear (aliased as .ce-clear-btn, .pe-clear-btn, .te-clear-btn)
     - .btn-secondary (aliased as .ce-reset-btn, .pe-reset-btn, .te-reset-btn)
     - .view-toggle, .view-toggle-btn (aliased for all three)
     - .empty-state + subcomponents (aliased for all three)

constitution-explorer.css (1,578 - 73 = 1,505 lines)
  ├─ DESIGN REFERENCES: imports explorers-common.css aliases
  └─ STRUCTURE: hierarchy, timeline, traceability, unique badges (~1,505 lines)

plan-explorer.css (1,376 - 64 = 1,312 lines)
  ├─ DESIGN REFERENCES: imports explorers-common.css aliases
  └─ STRUCTURE: phases, ADR, risk, gates, unique badges (~1,312 lines)

TaskExplorerPanel.razor.css (2,066 - 86 = 1,980 lines)
  ├─ DESIGN REFERENCES: imports explorers-common.css aliases
  └─ STRUCTURE: tree, map, details, parallel, dependencies (~1,980 lines)

ACTUAL OWNERSHIP: Clear
- Shared: design language (buttons, toggles, empty states)
- Scoped: structural/unique styling (layouts, type-specific variants)
```

---

## F. Alias Strategy Assessment

**Current Aliases in explorers-common.css:**
```css
/* Grouped selectors approach */
.btn-primary,
.ce-build-btn,
.pe-build-btn,
.te-build-btn { /* shared properties */ }
```

**Assessment:** ✓ **APPROPRIATE for Phase 1**

**Rationale:**
1. **Migration Strategy:** Aliases preserve backwards compatibility. No template changes required in Phase 1.
2. **Clarity:** Explorer-specific class names remain usable in templates.
3. **Temporary:** Phase 2/3 can gradually migrate to generic `.btn-primary` if desired.
4. **Risk:** Low - aliases don't change rendered HTML, only add shared styling source.

**Recommendation:** Keep aliases as temporary bridge. Document that future phases may migrate to `.btn-primary` directly, but current approach is sound.

---

## G. Corrected Line-Saving Numbers

**Previous Claim:**
```
~150 lines eliminated
Constitution unchanged, Plan unchanged, Task unchanged
```

**Reality:**
```
0 lines eliminated from explorer files (because explorers-common.css was never loaded)
explorers-common.css added: +155 lines
NET CHANGE: +155 lines (worse, not better)
```

**After Correction:**
```
explorers-common.css loaded: +155 lines (shared)
constitution-explorer.css: -73 lines (design duplicates removed)
plan-explorer.css: -64 lines (design duplicates removed)
TaskExplorerPanel.razor.css: -86 lines (design duplicates removed)

NET SAVINGS: 155 - 73 - 64 - 86 = -68 lines (actual consolidation)
```

**Honesty Check:**
```
Creating shared file + loading it = +155 lines
Removing duplicates from 3 files = -223 lines
Net improvement: +155 - 223 = -68 lines saved

BUT: Readability/maintainability improvement = high
     Single source of truth for design = achieved
     Future phases enabled = yes
```

---

## H. Structural CSS Preserved

**Task Explorer - Confirmed Structural (Must Remain Scoped):**
- `.te-tree`, `.te-tree-node`, `.te-tree-depth-*` (hierarchy indentation)
- `.te-map`, `.te-map-phase`, `.te-map-grid` (phase grid layout)
- `.te-details`, `.te-details-section` (structured sidebar)
- `.te-parallel`, `.te-parallel-grid` (4-column grid)
- `.te-dependencies`, `.te-dep-graph` (relationship visualization)

**Plan Explorer - Confirmed Structural (Must Remain Scoped):**
- `.pe-phase-timeline`, `.pe-phase-node` (progression layout)
- `.pe-adr-card`, `.pe-adr-decision` (ADR structure)
- `.pe-risk-matrix`, `.pe-risk-cell` (risk matrix grid)
- `.pe-gate-table`, `.pe-gate-row` (gate layout)

**Constitution Explorer - Confirmed Structural (Must Remain Scoped):**
- `.ce-map-tree`, `.ce-map-depth-*` (hierarchy structure)
- `.ce-timeline`, `.ce-timeline-entry` (timeline layout)
- `.ce-catalog-table` (table structure)
- `.ce-trace-card`, `.ce-trace-relations` (traceability layout)

**Status:** ✓ All structural CSS will remain in scoped files after consolidation.

---

## I. Files to Change

**To Fix Phase 1 Consolidation:**

1. **index.html** (wwwroot)
   - ADD: `<link rel="stylesheet" href="css/explorers-common.css" />`
   - LOCATION: Before `constitution-explorer.css` and `plan-explorer.css`

2. **constitution-explorer.css** (wwwroot/css)
   - REMOVE: ~73 lines of duplicate design declarations
   - KEEP: ~1,505 lines of structural CSS and unique styles

3. **plan-explorer.css** (wwwroot/css)
   - REMOVE: ~64 lines of duplicate design declarations
   - KEEP: ~1,312 lines of structural CSS and unique styles

4. **TaskExplorerPanel.razor.css** (Components)
   - REMOVE: ~86 lines of duplicate design declarations
   - KEEP: ~1,980 lines of structural CSS and unique styles

**Files NOT Changing:**
- explorers-common.css: Keep as-is (already created, just needs to be loaded)
- All .razor component files: No changes needed
- All other CSS files: Unaffected

---

## J. Build/Tests

**Pre-Correction Status:**
```
✓ Frontend builds successfully
✓ No syntax errors
✗ explorers-common.css is unused (loaded but not in application)
✗ Duplicate rules remain in explorer files
✗ Ownership not transferred
```

**Post-Correction Expected:**
```
✓ Frontend builds successfully
✓ explorers-common.css loaded
✓ Duplicate design rules removed from explorers
✓ Structural CSS intact
✓ All three explorers render identically
✓ No visual regressions
```

**Tests Required:**
1. Constitution Explorer page tests
2. Plan Explorer page tests
3. Task Explorer page tests (38 tasks, 7 phases, 18 parallel, T033/T033a, 15 dependencies)
4. Frontend build succeeds
5. No scoped CSS / static assets errors

---

## Summary: Phase 1 Failed, Must Be Corrected

| Aspect | Finding | Severity |
|--------|---------|----------|
| explorers-common.css loaded | NO | 🚨 BLOCKER |
| Duplicates removed | NO | 🚨 BLOCKER |
| Ownership transferred | NO | 🚨 BLOCKER |
| Line savings achieved | NO (net +155) | ⚠️ CRITICAL |
| Structural CSS preserved | N/A (unchanged) | ℹ️ INFO |

**Conclusion:** Phase 1 consolidation was declared complete but was never actually implemented. The shared CSS file exists but is unused. Duplicates remain in all three explorer files. Ownership was not transferred.

**Action Required:** Fix index.html linking and remove duplicate design declarations from explorer CSS files (next steps).
