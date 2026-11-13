# Delivery Management — audit, import anomaly and destination split (2026-11-13)

Branch `008-traceability-first`. The starting HEAD was 859a29f9.

## Architecture map (verified)

| Concern | Owner |
|---|---|
| Snapshot store | Backend `DeliverySnapshotStore` over the `delivery_reporting` schema (`DeliverySnapshotRecord` with a stable `Guid Id`, plus `DeliveryReportSourceRecord`). This store is separate from the Source Analysis Snapshot Registry. The registry is consulted only for the deletion check. |
| Import | `DeliveryReportingImportService`: preview → in-memory `DeliveryImportStagingStore` → commit. |
| Overview | `DeliveryOverview.razor` (`delivery-overview`) reads the same API and store. |
| Reports and snapshot management | `DeliveryReporting.razor`. Before this task it had five tabs including **Snapshots**. |
| Navigation | `NavigationCatalog`: the Delivery Management section, the shared Delivery workspace, and the Project Manager and Scrum Master views. |

## Import anomaly (M2LB.zip as Baseline, M2LB-language.zip as Current; the list showed one snapshot)

Each possible cause was checked against the code and the source bytes. I could not reproduce the exact screenshot session; its
server responses were not available.

| Question | Finding |
|---|---|
| F. Can one import overwrite another? | **No.** Every commit inserts a new `DeliverySnapshotRecord` with `Id = Guid.NewGuid()`. There is no update-by-role or update-by-label path. |
| L. Identity by label? | **No.** Identity is the record id. The label is a display name only, and the code says so. |
| J. Role reverts? | **No.** The role is stored as sent. Role uniqueness is not enforced, so two snapshots can share a role, and assigning a role never displaces another snapshot. |
| E. Duplicate detection? | A duplicate needs the same archive fingerprint or the same Git commit, and returns **409** with a warning (it never overwrites). The two archives differ: 8,248,351 vs 9,182,662 bytes, with different entry dates. Neither has a ZIP comment, so neither has a Git commit. **The duplicate rule cannot match them.** |
| H. Stored but filtered? | The list is per reporting project. A snapshot imported into another project (G) is not shown. The role and search filters are client-side and visible. |
| D/B. Expired preview | The staging store is **in memory**: 6 slots, 30-minute lifetime, lost on API restart. A confirmation after expiry, eviction or restart returns **404 "The preview expired. Choose the ZIP again."** and nothing is stored. |
| I/K. Frontend refresh | After a successful commit the page reloads the canonical list. Without a snapshot in the result it reloads nothing, which is correct. |

**Verified UI defect that makes a failed second import silent.** `dlr-import-error` rendered *above* the preview, while the confirm
button sat at the bottom of a dialog that grows with every feature and warning (28–33 features and many encoding warnings for M2LB). A
404 on an expired preview was therefore shown far above the user's viewport. The dialog stayed open, the list kept one snapshot, and the
user saw no explanation. That matches the screenshots. Which branch the original session took (expired/evicted preview, never confirmed,
or another project) cannot be proven without its server log. It was **not** an overwrite.

## Changes

1. **Three destinations in order:** Delivery Snapshots → Delivery Overview → Delivery Reports.
   - `delivery-snapshots` is a second route of the **same** page component, with the same API, store and session. There is no second
     snapshot list and no second state.
   - Delivery Reports no longer has a Snapshots tab, or an import button of its own. It links to *Manage Delivery Snapshots*.
   - Legacy `delivery-reporting?tab=snapshots` links (and Azure DevOps operation outcomes) redirect to `delivery-snapshots?project=…`.
   - The nav catalog, shared workspace, Project Manager and Scrum Master views, and documentation catalog all include the new
     destination. Workspace View visibility rules are unchanged.
2. **Readiness from the stored list only**, with the next actions the state allows:
   - 0 snapshots: Import first snapshot.
   - 1 snapshot: Open Delivery Overview, plus Import comparison snapshot. The page explains that comparison needs a second distinct
     snapshot.
   - 2 or more: Compare snapshots, plus Open latest change report when one exists.
3. **Compact import dialog.**
   - The dialog body scrolls inside the viewport. A **sticky footer** holds the action, the "Importing…" busy state and any failure
     (`role=alert`).
   - The detected-feature list and warnings are collapsed by default.
   - Warnings are **grouped by the code the import recorded**. No categories were invented, and every original message and path is kept.
4. **Labels:** "Project Leader" is shown as **Project Manager** (`DeliveryIntelligenceHistory.Label`, User Guide). The `ProjectLeader`
   enum and saved preferences are unchanged.
5. **User Guide:** a paragraph on the three destinations, readiness, and the sticky import footer.

## Tests

The tests are in `DeliveryReportingPageTests`:

- legacy redirect and readiness;
- Reports without a snapshot tab or import, with the manage link;
- readiness for 0 and 1 snapshots;
- a failed confirmation shown in the footer, with grouped and collapsed warnings, no success notice, and an unchanged list;
- nav order and the Project Manager label.

Updated expectations:

- `NavMenuLayoutTests` section order;
- the feature-visibility module count, 48/54 → 49/55, for one more module;
- the no-project test now renders the Snapshots destination.

## Not done in this task (remaining gaps)

- **Snapshot focus view.** The snapshot detail is still an expanding row, not a full focus view.
- **Checklist vs verified wording.** The table cell still reads "8/12" under "Tasks". It should say "checked" so it is never read as
  verified delivery.
- **Functional Quality Scope.** Count semantics and the focus view were not changed.
- **Persistence acceptance** (import, restart the API, import, restart, verify ids) and the concurrent, timeout and retry cases were
  **not run** in isolated infrastructure in this task. The backend already serializes commits per preview (gate) and per project (scoped
  write lock), and returns the stored snapshot when a confirmation is retried. Existing backend tests cover this; it was not re-run here.
- **Encoding.** The decoder audit (UTF-8 → Windows-1252 fallback provenance) was not re-run.
- **Browser acceptance** of the new destination was not run.
