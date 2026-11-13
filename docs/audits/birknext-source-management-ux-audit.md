# Source Management — UX audit (2026-11-13)

Starting HEAD: `535e5fa3`. Page: `Pages/SourceManagement.razor`. Logic: `SourceManagementPresentation` (buckets, chips, summary, filters) and `SourceContextPanel` (logical sources, versions, analysis baseline; backend `SourceBaselineService`).

## What was already right (kept)
- **Header:** *Import source* (primary, `project-import?import=1`) and *Snapshot Registry* are both visible.
- **Source versions panel:**
  - logical sources and the snapshot count per source;
  - "Baseline selected" or "One version";
  - "Grouped by archive name only — not verified repository identity";
  - the analysis baseline vs deployment baseline explanation.

  The newest import is never chosen automatically (`SourceBaselineService.Authoritative`).
- **Empty state:** *No sources yet* with Import source; no metrics.
- **Inspector:** roles, applications, history and the full per-area analysis.

## Confirmed problems (real M2LB data: 2 logical sources, 10 snapshots, 7 imported packages)
1. **Five repeated "· Partial" chips on every row** (Application code, Architecture, Configuration, CI/CD, Contracts), for 7 rows. The overall state (*Partly analyzed*) was never said once per row.
2. **Six equal summary tiles**, even for a single source.
3. **"Requiring review" and analysis completeness were not distinguished** anywhere. "0 requiring review" next to "7 partly analyzed" invites reading the first as "all fine".
4. A **search and filter toolbar for a single source.**

## Not changed (data integrity)
Analysis buckets and their calculation (`SourceSetupOutcome.Overall`), per-area statuses, capabilities, roles, associations, snapshots, baselines, provenance and Git update checks.
