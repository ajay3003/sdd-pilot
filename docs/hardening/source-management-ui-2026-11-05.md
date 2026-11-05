# Source Management UI modernization — NEXT46 (2026-11-05)

Branch `008-traceability-first`. Route `/sources`. Audit → redesign → implementation. All existing functions are kept: NEXT35 roles and associations, NEXT38 Git update states, check-all, import revision, and earlier-import matching.

## Issues found

| # | Issue | Kind |
|---|---|---|
| 1 | "No source packages yet" shown while sources were still loading (rows built only after the Git links request) | functional (false empty state) |
| 2 | A failed linked-repository read was silently ignored (`LinksAsync` error dropped) | functional |
| 3 | The "associated applications" metric didn't say whether it counted sources or applications | ambiguous metric |
| 4 | Analysis status was one long sentence ("Docs: Analyzed · Source: Partial · …") | scannability |
| 5 | Text-heavy 7-column table; a narrow layout with the detail panel below the whole table | layout |
| 6 | Header CTA "Import source" was an inline text link | weak CTA |
| 7 | "Detected from content" repeated as a full role line on every row | noise |
| 8 | Review-required only a number in the summary; rows didn't show it | prominence |
| 9 | ZIP vs Git update paths not explained | under-explained |
| 10 | No search, filter or sort | findability |
| 11 | Sparse tabs: Snapshots was one sentence, History a single line for ZIPs, Evidence two plain lines | sparse tabs |
| 12 | The role editor mixed detected capabilities, detected role and assigned roles; no "effective role" | role semantics |
| 13 | Busy state on check / find / save buttons was not shown; `_busy` could stay set if a call threw (no `finally`) | NEXT45 |
| 14 | Tabs weren't keyboard-navigable (no roving tabindex or arrow keys) | accessibility |

## Redesign

- **Header:** title and lead, then a toolbar group: secondary actions "Check all linked repositories (N)" (a `BusyButton`, read-only, explained in its tooltip) and "Snapshot Registry", plus the primary **Import source**. A collapsible "ZIP or Git — how sources are updated" explains both update paths.
- **Summary tiles that filter the list:** sources (with the ZIP·Git split), **requiring review** (warning tone when > 0, with the reason), analyzed, partly analyzed, not analyzed, and applications — labelled "Distinct applications these sources serve". The tiles are computed from the same row facts as the list, so they always agree.
- **Toolbar:** search by name. The origin filter appears only when both origins exist; the capability filter only when there is more than one capability. Also an analysis-status filter, a "Requires review" toggle, sort (name / last updated / analysis), a live "N of M shown" count, and Clear filters. Review-required rows sort first.
- **List:** cards instead of a table. Each card shows:
  - the name, an origin chip and a "Requires review" chip;
  - confirmed role chips, or one quiet "Role detected from content";
  - capability chips (up to 4, then "+N");
  - **analysis chips per dimension** — "Docs · Analyzed", "Source · Partial" — with the state in the text as well as the colour;
  - a meta line: applications, short snapshot id, commit, last updated.
  - Hover, selected and review states are styled.
- **Inspector:** beside the list at 75rem and wider (sticky, scrolls on its own); stacked on smaller screens, where opening a source moves focus to the inspector heading so it is announced and scrolled into view. It shows the review callout with its reason and a link to Project Evidence, and groups the update actions: Git → Check for updates + Import revision; ZIP → Upload new version. Navigation links and a close button are separate.
- **Tabs** (roving tabindex; Arrow/Home/End):
  - **Overview:** origin, roles, capabilities, update path, analysis chips, applications.
  - **Capabilities & Roles:** three cards — detected capabilities, detected role, and **effective role** (what Project Evidence and every review read) — then the assigned-role chips editor.
  - **Associations:** source → application at a glance, plus the active-environment state.
  - **Snapshots:** the current snapshot's full id with Copy and Open in Source Analysis, commit/branch and import time.
  - **Analysis & Evidence:** a per-dimension list, capabilities, and entry points (Source Analysis, Technology Coverage, Generated documentation).
  - **History:** a timeline of imports and update checks, plus earlier-import matching for Git sources.
  - Designed empty states throughout: no associations, no snapshot, no history.
- **Bulk check result:** a dismissable card list with status chips instead of a second table.
- **Loading (NEXT45):** a skeleton until the load finishes; a failed repository read shows a `LoadError` with Retry; loads are guarded by `LoadSequence`; `BusyButton` on check, find and save; `finally` blocks release busy state.

`SourceManagementPresentation` (new, pure) owns the row facts, analysis bucket and chips, summary, filtering, sorting and invariant dates. The page only reads the shared records.

## Tests

- New `SourceManagementRedesignTests` (6):
  - the analysis bucket from dimensions;
  - the summary (distinct applications);
  - filters, sorting and review-first ordering;
  - loading is never "no sources";
  - a links error shows Retry and recovers;
  - toolbar filtering, tile filtering and keyboard tabs.
- `SourceManagementTests` updated for the card list (the table assertion is replaced).
- Frontend suite: 8219 passed, 0 failed.

## Browser verification (M2LB copy, 7 ZIP sources, nb-NO, Edge, localhost only) — 19/19

1. Cards, not a table.
2. The tiles agree with the list: 7 sources, 3 analyzed, 4 partly analyzed, 0 not analyzed, 1 distinct application.
3. Per-dimension chips.
4. The primary CTA is in the header.
5. Search shows "5 of 7 shown".
6. Clear filters restores the list.
7. At 1440px the inspector sits beside the list.
8. The overview fields are present.
9. ArrowRight moves between tabs.
10. Detected, effective and assigned roles are separate ("Primary application source — detected (no role assigned)").
11. Snapshot copy and open work.
12. The history timeline shows.
13. Close works.
14. At 390px there is no horizontal scroll.
15. At 390px, opening a source focuses the inspector and brings it into view.
16. Read-only: no API writes.
17. No external host.
18. No page errors.

## Remaining gaps

- Filter state is not in the URL (it resets on navigation).
- There is no Git source in the M2LB copy, so Git rows were verified with bUnit fixtures only (check, check-all, import revision, matches).
- Snapshot "Copy id" uses the browser clipboard API; where it is refused, the label stays "Copy id" and the id remains selectable text.
