# Snapshot selector identity — report (2026-11-04)

Branch `008-traceability-first`. This is a bug fix for "two snapshots both show *Current 2026-11-04* in Delivery Reports". Nothing was pushed.

## SNAPSHOT SELECTOR IDENTITY AUDIT MAP

**Column key**

| Short name | Meaning |
|---|---|
| Value | What the option value / lookup key is |
| Name/date/role/index | Whether any of these is used as identity |
| Duplicate labels possible | Before this fix |
| Wrong item possible | Whether a selection could resolve to the wrong item |
| Registry summary | Whether the selector reads Snapshot Registry summaries |

**Delivery**

| Selector | Entity | Value | Name/date/role/index | Duplicate labels possible | Wrong item possible | Registry summary | Sort | Decision |
|---|---|---|---|---|---|---|---|---|
| Delivery Reports — current snapshot | Delivery snapshot | `SnapshotId` | no | **yes** ("Current 2026-11-04" twice) | no (values distinct) | Delivery summary list | effective ↓, imported ↓ (no tie-break) | **fixed**: unique labels, pointer rule, id tie-break |
| Delivery Reports — baseline snapshot | Delivery snapshot | `SnapshotId` | no | yes | no | same | same | **fixed** |
| Delivery Reports — default current | Delivery snapshot | — | **role** (`FirstOrDefault(Role == Current)`) | — | first role match in list order | — | — | **fixed**: explicit current-pointer rule |
| Delivery Reports — snapshot list (Snapshots tab) | Delivery snapshot | row by `SnapshotId` | no | yes (stored names) | no | — | API order | **fixed**: shared sort, generated names normalised |
| Delivery Reports — report history | Weekly report | `ReportId` | no | titles repeat by design | no | registry (GeneratedReport) | generated ↓ | keep |
| Delivery baseline suggestion | Delivery snapshot | id | role as tie-break only (one Baseline among same-day) | — | never picks between ambiguous same-day candidates | — | — | keep (suggestion only) |

**Other consumers**

| Selector | Entity | Value | Name/date/role/index | Duplicate labels possible | Wrong item possible | Registry summary | Sort | Decision |
|---|---|---|---|---|---|---|---|---|
| Source Analysis snapshot selector | SA snapshot | `SnapshotId` | no | no (date · status · files · pointers · fingerprint) | no | **yes** | analyzed ↓ | keep |
| Snapshots page (`/snapshots`) | any | row by (type, id) | no | — | no | **yes** | created ↓ | keep (pointer legend already separate) |
| Correlation Review interpretation history | interpretation | `Id` | no | — | no | registry | created ↓ | keep |
| Documentation Drift — both versions | artifact version | `v.Id` (both sides) | no | — | no | — | — | keep |
| Documentation Drift — imports | import | `ImportId` | no | — | no | — | — | keep |
| Performance / AQR / IQR run history | run | `RunId` | no | — | no | registry (ReviewRun/Execution) | — | keep |
| Critical E2E history | run | run id | no | — | no | registry | — | keep |
| Functional Quality Scope | scope / frozen scope snapshot | `custom:{id}` / `profile:{id}` | no | — | no | registry (frozen) | — | keep |
| Impact Analysis, Governance, Release Readiness | — | — | — | — | — | — | — | Governance and Release Readiness are not built yet (#24/#25); they must use the shared selector model |

**Search audit:** I searched `.Label ==`, `DisplayName ==`, `First(…Name…)`, role-based lookups, and option values that are not ids. No snapshot lookup used a name, date, role or index. The hits were fixed row labels (`"REST"`, `"GraphQL"`) and provenance labels, which are not snapshot identity. Every non-id `<option value>` is a filter or enum (module, state, role, method).

## 1. Exact Delivery root cause
It was **not** a lookup bug:
- Both `<select>` elements already used `SnapshotId` as the option value.
- The preview, suggestion and generation endpoints take `projectId + snapshotId`.
- Reports persist `BaselineSnapshotId` / `CurrentSnapshotId`.

The defect was identity *presentation* plus role semantics:
1. **Role and date were baked into the stored name.** The import named an unnamed snapshot `"{Role} {yyyy-MM-dd}"`, so two Current imports on the same day were both "Current 2026-11-04".
2. **The role is a per-snapshot import attribute that is never demoted.** Every Current import stays "Current", and the option text repeated it. The only difference between the two options was the task count.
3. **The default selection was `FirstOrDefault(Role == Current)` in list order.** That is role-based selection, and list order had no tie-break for the same instant.

## 2. Duplicate labels vs duplicate values
Duplicate **labels**, not values. Verified in the browser: the two "Current 2026-11-03" options had distinct values and were individually selectable.

## 3. Any backend lookup by name, date or role?
No.
- The baseline suggestion uses role only to choose among *several same-day candidates*, and only when exactly one is marked Baseline; otherwise it refuses and lists the candidates.
- The listing now has an id tie-break.

## 4–5. Current and baseline selector fixes
- Options are built by the shared `SnapshotSelectionLabels.Labels` and are unique per list:
  - base: effective date (with time when it is not midnight);
  - then: import time, if several share the effective date;
  - then: source;
  - last resort only: short id.
  - then the compact Delivery summary and the badges, as text.
- The option value stays `SnapshotId`; its tooltip shows the full id.
- Both selectors and the Snapshots list use one deterministic sort.

## 6. Report lineage
Unchanged and verified: reports store the exact ids. Browser generation with current = the *earlier-imported* duplicate "Current 2026-11-03" (`81301573…`) and baseline `06341c72…` was stored with exactly those ids.

## 7. Snapshot naming, before and after

| | Name |
|---|---|
| Before (default) | "Current 2026-11-04" |
| After (new imports) | the source: the archive name without extension, or "repository · branch" for Azure DevOps |

- Names someone typed are kept.
- Stored history is not rewritten. Generated "{role} {date}" names (`IsGeneratedRoleDateName`) are only *displayed* from metadata; the stored name is visible as a tooltip in the snapshot list.

## 8. Pointer badge semantics
Delivery's **current pointer** is the newest snapshot imported with role Current (newest effective date, then newest import, then id).

| Snapshot | Badge |
|---|---|
| The current pointer | "Current" |
| Other snapshots imported as Current | "earlier current import" |
| Snapshots imported as Baseline | "imported as baseline" |

- Badges are text inside the option, so they don't rely on colour.
- The Snapshot Registry's own pointers (Latest / Latest completed / Current selected / Approved) are unchanged and remain registry pointers. Delivery's import roles are domain roles, not registry pointers.

## 9. EffectiveAt vs ImportedAt
Both are kept separately. Effective date is the primary label. Import time is the disambiguator and is always said as *imported*.

## 10. Comparison direction
The person's selection is always respected:
- **Swap** is explicit.
- A **later baseline** blocks generation.
- A **reduced inventory** needs explicit confirmation.
- Nothing is reversed automatically.

## 11. Same-day chronology wording
"Same effective date. Source revision order cannot be confirmed from the effective dates."
- **When both have Azure DevOps commit metadata from the same repository:** it then says which commit is later, or warns when the current snapshot's commit is earlier.
- **Otherwise:** "The current snapshot was imported later in BirkNext; import order is not proof of a newer source revision."

The direction label reads "Same effective date — source order not confirmable from dates". The caveat is now also shown when a same-day pair is a reduced inventory, which previously suppressed it.

## 12–13. Shared selector contract and Snapshot Registry
**`shared/SnapshotSelection.cs`:**
- `SnapshotSelectionOption`: SnapshotId, type, EffectiveAt, CreatedAt, source, summary, pointers, selectable/disabled reason.
- `SnapshotSelectionLabels`:
  - `Sort`, the one ordering (`SortExplanation` is shown under the Delivery selectors);
  - `Labels`, unique labels;
  - `IsGeneratedRoleDateName`;
  - `ResolveLegacyLabel`: unique → id; ambiguous → message, never First(); missing → message.

The Source Analysis selector already reads registry summaries and needs no change. The contract is generic, so Governance and Release Readiness (#24/#25) can use it.

## 14–21. Other consumers
Source Analysis, Project Evidence, Traceability, Topology, AQR/IQR/FQR, Performance/Critical E2E, Documentation Drift and Impact were audited (map above). All select by id, and none had this defect class. Governance and Release Readiness don't exist yet.

## 22–24. Legacy selections, ambiguity and missing snapshots
**Legacy:** no persisted selection stored a snapshot name. Delivery selections are page state, and reports store ids. `ResolveLegacyLabel` exists and is tested for any future import of label-based saved state.

**Ambiguous:** "2 snapshots match the saved legacy label 'Current 2026-11-04'. Select the intended snapshot." It never picks the first.

**Missing:** when a snapshot the person chose disappears (deleted):
- "The previously selected current snapshot is unavailable. Select the intended snapshot."
- The selector stays empty. The latest snapshot is never substituted.

## 25–26. Sort and refresh stability
- Sort: effective ↓, imported ↓, id (backend list and shared sort).
- Once the person has chosen, a refresh (import, rename, delete) keeps the exact chosen ids. Only an unchosen selection follows the default rule.

## 27. Browser acceptance
- Isolated stack, throwaway `birknext_ci2`. One same-day snapshot there was renamed to reproduce two "Current 2026-11-03" snapshots with the same role and effective time, imported 17 s apart.
- Viewports: 1440/390 × light/dark.

Options shown:
- 3 Nov 2026 12:03 · imported 3 Nov 12:04 · 33 features · 1658/1742 tasks · Current
- 3 Nov 2026 12:03 · imported 3 Nov 12:03 · 29 features · 1413/1471 tasks · earlier current import
- 3 Nov 2026 10:00 · M2LB-language (parser v4) · 33 features · 1658/1742 tasks · earlier current import
- 3 Nov 2026 08:00 · M2LB-main (parser v4) · 29 features · 1413/1471 tasks · imported as baseline

Results:
- The default is the pointer.
- Selecting the other one changes the value to exactly its id.
- The same-day wording appears.
- The generated report stored the exact chosen ids.
- No overflow, no console errors, no external hosts.

## 28. Payload and performance
- Delivery snapshot list: 2.4 KB, 0.1 s.
- Labels are built client-side from the list once per list change; no payload is loaded.

## 29. Automated tests
**Frontend:**
- `SnapshotSelectionTests` (12, including theory cases): disambiguation order, pointers and summary as text, sort independent of input order, generated-name recognition, legacy unique/ambiguous/missing, the Delivery current pointer and badges.
- Page tests: two same-day "Current" options are unique and each is selected and generated by exact id; a chosen snapshot that disappears is reported and never replaced.

**Backend `DeliverySnapshotIdentityTests` (6):**
- two same-day unnamed imports get a neutral name, distinct ids and a stable order;
- same-day wording separates import order from source order;
- Azure DevOps commit order and a reversed pair;
- a reduced same-day pair keeps both warnings;
- the baseline suggestion never picks between same-day candidates.

**Full suites:** see §29a.

## 29a. Full suites (exact)

| Suite | Passed | Skipped | Failed |
|---|---|---|---|
| Frontend | **7992** | 0 | 0 |
| Backend | 5794 | 13 | **2** |

The two backend failures:
- `LoopbackHttpTestServerTests.UnexpectedHandlerFailure_StillFailsTheOwningTest` (32 s)
- `DatabaseMigrationLockTests.ConcurrentMigrationsOfAFreshDatabase…` (55 s)

Both **pass in isolation (2/2 in 5 s)**. They are the same load-dependent tests seen in earlier full runs: the isolated stack was still running when this suite started, and the machine was memory-constrained. Neither touches snapshot selection. They are recorded as open test-reliability debt for #27, not as passes.

## 30. Commits
See the git log after this report.

## Confirmations
**Identity**
- Snapshot identity is the SnapshotId.
- Labels are never authoritative identity.
- Dates are metadata.
- Current, Latest, Approved and Baseline are pointer or role metadata.

**Same-day selection**
- Two snapshots with the same effective date are individually selectable.
- Two legacy snapshots with the same old name are distinguishable.
- Delivery Reports select both same-day "Current" snapshots correctly.
- A person's selection is not overridden by pointer changes or refreshes.

**Reports and history**
- Generated reports persist exact current and baseline snapshot ids.
- Frozen reports stay tied to them.
- No historical reader falls back to latest: a missing chosen snapshot is reported.

**Wording and presentation**
- Imported-later is never presented as a newer source revision.
- Same-day wording separates source chronology from BirkNext import chronology.
- Registry summaries remain the Source Analysis selector source.
- Selectors load summaries only, never payloads.

**Safety and scope**
- Selection is stable when the order changes.
- Ambiguous legacy names are surfaced, never resolved with First().
- Snapshot endpoints validate project and type: `projectId + snapshotId`; another project's or type's id is not found.
- Major selectors were audited.
- No new registry or pointer system was introduced.
- Browser acceptance covered same-day duplicates at desktop and mobile, light and dark.
- No real external traffic was generated.
- `appsettings.Local.json` was not intentionally modified.
- User testdata was preserved.
- Nothing was pushed.
