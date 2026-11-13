# Snapshots (Snapshot Registry) — UX audit and change (2026-11-13)

Starting HEAD: `41d1cd06`. Page: `Pages/SnapshotBrowser.razor`. Data: the shared Snapshot Registry API, with pointers Latest, Latest completed, Current and Approved, plus retention classes.

## Verified facts (unchanged)
- **"Can it be deleted?"** calls only `DeletionCheckAsync`, a read-only check. Nothing is deleted.
- **Make current, Approve as current and Protect / Remove protection** each need an explicit confirmation step that states their effect. Approve is disabled unless the snapshot status is Completed, and the page explains why (`sb-approve-reason`).
- **Pointers** are only "explained" on the first click; viewing never moves one. The page legend defines Latest, Latest completed, Current and Approved.

## Changes (presentation only)
| Issue | Change |
|---|---|
| Read-only check looked like a lifecycle action (same row, same style) | Two labelled groups. **Read-only check:** "Check deletion eligibility", with "Nothing is deleted or changed". **Lifecycle actions · change state, confirmation required:** Make current, Approve as current, Protect / Remove protection |
| References "None recorded · not all stores checked" | "Reference discovery incomplete — none recorded in the checked stores; other consumers may exist" |
| Details in a sticky side column (26–34 rem) with its own scroll, hiding three table columns while open | Full-width focus view below the list; all columns stay |

**Not changed:** pointer, approval, protection and retention semantics, deletion rules, immutability, tabs, filters and the URL state.
