# Standard Explorer Review Workflow

This document defines the repeatable review sequence to use for every Explorer in BirkNext.

The workflow is intended for explorers such as:

- Constitution Explorer
- Plan Explorer
- Task Explorer
- Data Model Explorer
- future explorer panels

The main principle is:

**Trust the data first, then review the UI, then polish.**

---

## 1. Architecture / Baseline Audit

Before changing anything, inspect the explorer and understand how it is built.

Review:

- page/component files
- child components
- services
- models / view models
- parser or extraction logic
- tests
- tabs / views
- interactions
- rendering approach
- CSS ownership

Identify whether rendering uses:

- declarative Razor
- child components
- Markdown rendering
- RenderFragment
- RenderTreeBuilder

Also identify which styles belong to:

- shared design language (`explorers-common.css`)
- explorer-specific scoped CSS

### Output

Produce a concise architecture report with:

- files involved
- views/tabs
- data flow
- rendering approach
- CSS ownership
- known risks

Do **not** redesign or refactor during this step.

---

## 2. Parser / Data Correctness Audit

If the explorer depends on parsed source files, verify the parser before trusting the UI.

Review:

- parser entry point
- tokenizer / Markdown handling
- supported headings and sections
- identifiers
- relationships
- metadata extraction
- malformed input handling
- fenced-code handling
- edge cases

Use real repository fixtures.

Establish trusted regression baselines from real files rather than assumptions.

Examples of baseline data may include:

- entity counts
- task counts
- phase counts
- relationship counts
- dependency counts
- requirement links
- enums
- indexes
- findings

### Testing principle

Separate:

- **generic behavior tests** — verify parser capability independent of a specific project
- **fixture regression tests** — verify that a known real sample still produces known output

Do not freeze incorrect behavior simply because current code does it.

If a parser defect is proven:

1. reproduce it with a focused failing test
2. make the smallest fix
3. rerun the regression suite

### Exit condition

The parser/data baseline should be trusted before UI work continues.

Prefer:

- all intended parser tests green
- known fixture baselines documented
- no unresolved ambiguity in core parsing behavior

---

## 3. UI Review Against Trusted Data

Review every tab/view using the trusted parser/model output.

For each view inspect:

- correctness of displayed data
- hierarchy
- readability
- spacing
- density
- badges
- empty states
- filters
- search
- actions
- details panels/drawers
- accessibility
- responsive behavior

Do not classify legitimate zero-data states as bugs if the parser correctly returned no data.

### Important

UI review must distinguish between:

- real rendering defect
- parser/model defect
- intentionally missing source data
- unsupported source syntax

---

## 4. Fix One Issue at a Time

After the audit, rank concrete issues and fix them individually.

Each implementation command should:

- target one specific issue
- preserve parser/model behavior unless that issue proves a data defect
- preserve existing explorer structure
- preserve shared/scoped CSS ownership
- avoid broad refactors
- run relevant tests/build
- report exact changes

Typical examples:

- wrong relationship direction
- unreadable badge text
- incorrect warning styling
- incomplete search behavior
- inaccessible truncated names
- missing optional-data presentation
- scroll/viewport defects

### Visual changes

For CSS/layout changes:

- build/test verifies code-level safety
- screenshot/manual review verifies actual visual correctness

Do not claim visual PASS without visual verification when browser access is unavailable.

---

## 5. Consistency / Accessibility Polish

After functional issues are stable, review cross-cutting UX consistency.

Typical areas:

- abbreviation clarity
- tooltips / accessible names
- semantic info vs warning styling
- full-name disclosure for truncated text
- keyboard/focus states
- search labels
- responsive layout
- terminology consistency

### Abbreviation policy

Use:

**Prominent context**

`ID + human-readable name`

Example:

`US1 · User Activated`

**Dense metadata**

Compact identifier + accessible/contextual meaning.

Example:

`FR-018`

with accessible meaning:

`Functional Requirement FR-018`

Do not expand every badge into verbose repeated text.

Do not rely only on hover for critical meaning.

---

## 6. Post-Fix Audit

After all identified issues are addressed, rerun the explorer review.

Check:

- original issues: fixed / partial / regression
- all tabs still work
- filters/search still work
- counts remain correct
- accessibility remains intact
- responsive behavior remains intact
- parser regression suite still passes

Also identify maintenance-only issues such as:

- dead code
- harmless CSS duplication
- unused state
- non-blocking cleanup

### Completion decision

Classify the explorer as:

- **Complete for this pass**
- **Complete with optional cleanup**
- **Still has functional blockers**

Do not keep polishing indefinitely when only low-value cosmetic work remains.

---

## 7. Project / Module Independence Audit

A real project fixture must not become the application contract.

For example, using Autorisasjon / `004-scim-user-sync` as a regression fixture is valid, but production code must not depend on its specific:

- task counts
- phase counts
- story names
- IDs
- paths
- requirement numbers
- section names
- dependency counts

The target architecture is:

**generic parser → generic semantic model → generic explorer UI**

with real projects used only as fixtures/examples.

### Static independence audit

Search production code for fixture-specific assumptions such as:

- hardcoded project/module names
- hardcoded story names
- fixed counts
- fixed IDs
- fixed sample paths
- arrays/indexes assuming a specific number of items

Classify each occurrence as:

- production coupling
- fixture/test-only
- documentation/comment
- harmless sample reference

### Dynamic independence audit

Run explorers against multiple real files from the repository's sample-data/spec folders.

Choose varied samples such as:

- small and large modules
- zero/many user stories
- zero/many dependencies
- zero/many parallel tasks
- sparse/rich traceability
- conceptual data models
- schema-oriented data models
- optional/missing sections

Verify actual parser/service output and UI compatibility.

### State isolation

Switch between projects/modules and confirm no stale state remains, such as:

- old counts
- old selected item
- old drawer content
- old story names
- old search/filter state when inappropriate
- old findings
- old relationships

### Compatibility result

Produce a matrix using:

- PASS
- PARTIAL
- NOT APPLICABLE
- FAIL

Then classify overall project/module independence as:

- **YES — verified**
- **MOSTLY — minor portability issues**
- **PARTIALLY — meaningful coupling exists**
- **NO — heavily fixture-specific**

---

# Recommended Command Sequence Per Explorer

Use this sequence consistently:

1. **Architecture / baseline audit**
2. **Parser/data correctness audit**
3. **Establish green regression baseline**
4. **UI review against trusted data**
5. **Fix highest-priority issue**
6. **Continue one issue at a time**
7. **Accessibility / terminology / abbreviation polish**
8. **Post-fix audit**
9. **Dead-code / maintenance cleanup if worthwhile**
10. **Project/module independence verification**
11. **Mark explorer complete for the pass**

---

# CSS Architecture Rule

Maintain the established ownership principle:

**Shared CSS owns the common design language; explorer scoped CSS owns unique structural/state layout.**

Examples of shared concepts:

- standard buttons
- common tabs
- generic empty-state language
- shared design tokens

Examples of explorer-specific concepts:

- Task tree hierarchy
- Task map structure
- dependency cards
- Data Model entity grids
- relationship rows
- explorer-specific semantic states

Do not move unique structural rules into shared CSS simply to work around scoped rendering issues.

---

# Rendering Rule

Prefer declarative Razor for complex styled explorer UI.

Avoid unnecessary `RenderTreeBuilder` / programmatic element generation where scoped CSS is expected, because generated elements may not receive the component scope attribute correctly.

---

# Testing Rule

Tests should prove two different things:

### Generic capability

Examples:

- arbitrary task counts work
- zero dependencies is valid
- multiple user stories work
- missing optional sections are valid

### Real fixture regression

Examples:

- a specific sample file still parses to its known baseline

Do not let fixture regression tests substitute for generic behavior tests.

---

# Final Principle

For every Explorer:

> **First prove what the source means. Then prove the model is correct. Then make the UI clear. Finally prove it works beyond the sample project that was used during development.**
