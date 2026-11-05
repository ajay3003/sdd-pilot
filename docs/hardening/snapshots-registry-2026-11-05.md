# Snapshots Registry & Comparison Hub — NEXT47 (2026-11-05)

Branch `008-traceability-first`. Route `/snapshots`, together with Source Management (`/sources`). Audit against code and stored evidence on the M2LB copy (`birknext_gaps`, 42 registry snapshots, 10 of them Source Analysis), then implementation. No backend or evidence change.

## Audit: confirmed bugs vs UX weaknesses

| # | Finding | Classification | Outcome |
|---|---|---|---|
| B1 | **The same snapshot had two analysis verdicts.** The registry rates a Source Analysis snapshot by `SourceSetupOutcome.Overall` over its six areas (application code, architecture, IaC, configuration, CI/CD, contracts). Source Management (NEXT46) rated it by Project Evidence capability dimensions. On the M2LB copy, Terraform-BasicOpsInfra_M2LBM01 showed "Analyzed" in Source Management and "Partial" in the registry. | Bug (parallel definition) | Fixed: Source Management uses the shared rule over the same areas; browser check confirms 7/7 snapshots agree |
| B2 | "8 snapshots, 0 fully analyzed, 8 partial" — verified against stored evidence. Every M2LB Source Analysis snapshot really has CI/CD = Partial, and the Terraform packages also have Architecture = Unsupported. The registry calculation is **correct**, not a defect. | Not a bug | Explained: per-area outcomes with meaning; Unsupported marked as a BirkNext limitation |
| B3 | Selecting snapshots quickly could show the wrong lineage: `SelectAsync` had no stale-response guard. | Bug (race) | Fixed with `LoadSequence`; loading skeleton in the inspector; browser check confirms the last selection wins |
| B4 | The list said "No saved references" while the References tab said some stores were "not checked". | Misleading | States: found / "None recorded · not all stores checked" (Source Analysis) / "No saved references" (fully registry-recorded types) |
| B5 | Make current / Approve / Protect / Remove protection changed lifecycle on the first click, with no explanation. | Misleading / unsafe UX | Each action first shows its effect (what changes, what does not), then Confirm or Cancel; a notice after success |
| B6 | Busy state stayed on if a pointer or retention call threw (no `finally`). | Bug (NEXT45) | Fixed |
| U1 | KPI labels had no definitions; processing completion read like coverage. | UX | Each KPI states its definition; a note says processing ≠ coverage; sixth KPI "requiring attention" = source snapshots with a failed area (deterministic, names in tooltip) |
| U2 | Header had no contextual actions. | UX | Source Management + Import source |
| U3 | Detail panel sat below the table. | UX | Inspector beside the table at 80rem and wider; the table drops Type/Project/Actions while it is open; sticky with its own scroll |
| U4 | Tab names were technical. | UX | Overview · Analysis coverage · Evidence · References & consumers · Lineage & revisions · Technical diagnostics; roving tabindex and Arrow/Home/End |
| U5 | Lifecycle state was not visible in the detail. | UX | "Lifecycle" row: Latest / Latest completed / Current / Approved / Protected, or "Historical" |
| U6 | Lineage was two plain lists. | UX | Provenance chain (import → this snapshot → N consumers) above the recorded edges; only recorded relations, never invented across sources |
| U7 | Diagnostics had no copy and didn't say why there is no re-run. | UX | Copy id; immutability row: "re-analysis creates a new snapshot" |

## Already in place (verified, unchanged)

- Filters and the open snapshot/tab are in the URL.
- Server-side paging and filter counts are true.
- The evidence link carries `?snapshot=<id>` (never a newer or unrelated snapshot).
- The deletion check is read-only; deletion only happens in the owning module.
- Approve is only possible for a completed analysis.
- Baseline/comparison across modules (brief §6): `BaselineEligibility` (NEXT43) plus remembered, revocable repository associations (Group 3) are shared by Code Risk and Test Coverage. Same-repository revisions are eligible. Cross-source comparison needs an explicit (or remembered) reviewer declaration. A ZIP without Git identity is never matched by file name.

## Tests

- `SnapshotBrowserTests` updated for: six KPIs with definitions, reference states, confirm-then-apply (no write on the first click; Cancel), the next action in Analysis coverage, and the lineage chain.
- New: stale selection ignored; keyboard tabs and new tab names.
- `SourceManagementRedesignTests` reworked: the bucket comes from the shared rule over the real M2LB Terraform areas (Partial, as in the registry); Unsupported is a BirkNext limitation; Failed / Nothing analyzable buckets.
- Frontend suite: **8221 passed, 0 failed**. No backend change.

## Browser verification (M2LB copy, nb-NO, Edge, localhost) — 16/16

1. Source Management and the registry agree for all 7 sources.
2. Six KPIs with definitions; the total equals the registry total (42).
3. Header subtitle and actions.
4. Reference states.
5. The inspector sits beside the table.
6. Keyboard tabs.
7. Analysis coverage shows the areas and a next action.
8. The provenance chain.
9. The evidence link keeps the exact snapshot.
10. Copy id and the immutability note.
11. Make current explains its effect with no write.
12. Cancel changes nothing.
13. Rapid selection: the last one wins.
14. No API writes.
15. No external host.
16. No page errors.

## Limitations

- "Requiring attention" covers Source Analysis snapshots with a failed area only. Payload-missing-but-referenced is not queryable from the registry list and is not counted.
- Reference indexing of snapshot ids stored *inside* consumer payloads (brief §4) is still "not checked" — reported as such, not indexed.
- Revision is "Unknown" for every M2LB ZIP snapshot (no Git identity), shown honestly.
- Permissions: lifecycle actions are local-user actions recorded by the backend; there is no separate permission model (none exists in BirkNext).
