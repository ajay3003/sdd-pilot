# Testing workspace UI modernization: Manual and Technical Testing (2026-11-16)

**Method.** The real UI was reviewed in a browser before any change. The Release build ran on fresh disposable data (LendoPlus, with a plan
and runs). Screenshots and layout metrics were taken at 1920×1080, 1440×900 and 1280×800, and the same script repeated them after the changes.

**Scope.** Changes use the shared workspace styles (`manual-testing.css`, which both workspaces use) and the existing components.
There is no new page, component library or navigation registry, and no functional screen was replaced by a dashboard.

## Problems found (before)

| # | Where | Problem (observed) |
|---|---|---|
| 1 | Sidebar (every page) | Each group of the workspace view was expanded, about 25 module rows, so the sidebar was longer than a screen. Groups were collapsible, but nothing was closed by default. |
| 2 | Plan & prepare | 3.9 screens at 1440×900, with 18 candidates. Every candidate took three lines (title, purpose, chips), and every group was always open. The real project has 604 candidates. "Evidence in this project" took about a third of a screen above the steps even when a plan already existed. |
| 3 | Technical Testing home | Area cards in a 3 + 1 layout: Critical E2E sat alone on a second row. |
| 4 | Technical Testing | The "environment not bound to a project" information used the red error style. |
| 5 | Execute & document | Cards touched each other, with no gap. The run history gave the test-case title a narrow column, so titles wrapped across five lines, while step counts spelled out zeros ("1 pass · 0 fail · 0 blocked · 0 not run"). |

## Changes

| # | Change | Behaviour kept |
|---|---|---|
| 1 | Sidebar groups without a remembered choice start **closed**, except the view's first group and the group of the current page. A person's open/close choice is remembered per view, as before. Navigating into a closed group still opens it and marks the page. | Every module stays one click away. Search and All modules are unchanged. |
| 2a | Candidate rows take **two lines**: the title with its type and status chips on one line, the purpose on one truncated line, and the full text in Details. | Selection, Details, filters and "Select visible" are unchanged. |
| 2b | Candidate groups are **collapsible headings** with their count and how many are selected. With more than 40 matching candidates only the first group starts open; with fewer, all are open. The tester's choice per group wins. | Selection counts include candidates in closed groups, as before. |
| 2c | "Evidence in this project" is a **disclosure** with a one-line summary (candidates, sources, discovery time). It is open until a plan exists. | Same content, one click away. |
| 3 | The Technical Testing areas use a narrower card minimum, so **all areas sit on one row** at desktop widths. | Same per-area content and links. |
| 4 | New shared `.mt-note` (information) and `.mt-note-warn` styles. Unbound environment = information; bound to another project = warning. Red stays reserved for errors. | Same text. |
| 5 | Cards stacked one after another keep a gap; cards in a grid keep the grid gap. The run history sets column widths that favour the test case. Step counts show only the outcomes that occurred, with the full counts in the cell's tooltip. | The defect and evidence wording is unchanged, because reports and scripts read it. |

## Measurements (1440×900; page height in screens)

| Page | Before | After | Note |
|---|---|---|---|
| Plan & prepare (18 candidates) | 3.93 | 3.00 | With hundreds of candidates, closed groups shrink the page far more. |
| Technical Testing home | 1.62 | 1.34 | Areas on one row. |
| Integration Testing | 1.35 | 1.07 | The sidebar is no longer the longest column. |
| Execute & document | 1.25 | 1.20 | Fewer wrapped lines in the history. |
| Manual Testing home, Discover | 1.03–1.05 | 1.04–1.05 | Unchanged. |

There is no horizontal overflow at 1280, 1440 or 1920 on any checked page. Screenshots are in the acceptance folder (`shots-ui-before`,
`shots-ui-after`).

## Verification

- **bUnit:**
  - The sidebar default; a remembered choice survives a remount; navigating into a closed group opens it.
  - With many candidates, groups start closed, open and close from their headings, and count selections.
  - Step counts are compact.
  - The existing Technical Testing, Manual Testing and Plan & prepare tests.
- **Browser (`ui-accept`):**
  - Sidebar: only the current group is open; a group opens with the keyboard (Enter); it stays open after a reload; it closes again; opening a page in a closed group opens that group; the groups fit without scrolling.
  - Plan & prepare: the evidence summary opens on demand; a group closes and reopens; a candidate row is two lines.
  - Technical Testing: the areas sit on one row; the information note style.
  - Execute: the history steps fit on one line; stacked cards keep a gap.
  - No horizontal overflow at the three widths.
- **Regression on the final revision:**
  - Frontend: 9193/9193.
  - Browser (Release build, fresh disposable database):

    | Suite | Result |
    |---|---|
    | ui | 30/30 |
    | nav | 41/41 |
    | nav2 | 32/32 |
    | tt | 76/76 |
    | ttnav | 48/48 |
    | utps | 106/106 |
    | mt a | 23/23 |
    | mt b (after an API restart) | 48/48 |
    | mt c | 7/7 |
    | itx | 46/46 |
    | open-now | 16/16 |
    | brand | 17/17 |

  - Backend: unchanged since `420916bb`.
- **Found by the regression and fixed:**
  - **All modules hidden.** With the new default, the sidebar's *All modules* link sat in a closed group, so nav and nav2 could not click it. The group holding All modules now always starts open, because it is the way to every module.
  - **Five areas.** A project with five Technical Testing areas wrapped to 4 + 1. Five or six areas now use three columns.
  - **Two scripts.** Scripts that clicked rows of closed groups now open the group first, as a person would.

## Not changed (deliberately)

- The guided step list stays on the Manual and Technical Testing homes. It overlaps with the stage tabs, but it is the shared guided-workflow
  component, and it carries each stage's status and next action, which the tabs do not.
- The page structure is unchanged: no merged pages and no new tabs inside pages. Deep links, Back/Forward and every existing test id keep
  working.
