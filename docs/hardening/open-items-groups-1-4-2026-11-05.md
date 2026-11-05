# Open items — Groups 1–4 (2026-11-05)

Branch `008-traceability-first`. Nothing pushed. No live ADO, TT02, m2lbdev, Azure, Altinn or ID-porten calls were made; every browser check ran against the M2LB copy in the throwaway database `birknext_gaps`, with all non-localhost requests blocked by the browser harness.

## Summary

| Group | Item | Status | Commit |
|---|---|---|---|
| 1 | Task Alignment ignored the design inputs (Constitution, Data Model) → 0 design links | Fixed, verified in the browser (11 design links on M2LB) | ee63607f |
| 1 | Task text lost reference groups like "(FR-026, PS-08)" | Fixed (`ReferenceOnlyGroupRe` strips only reference-only groups) | ee63607f |
| 2 | Dashboard inventory counts gave no scope | Fixed (`DashboardPresentation.InventoryScope`) | ee63607f |
| 2 | Spec explorer "(N)" label was ambiguous | Fixed ("(N items)" with a title) | ee63607f |
| 2 | Source package role shown as unknown before Source Analysis | Fixed ("Not classified yet", "role pending Source Analysis") | ee63607f |
| 3 | Baseline across repository identities had to be re-confirmed on every run | Fixed: a remembered, revocable declaration (who/when/note) | 263501df |
| 3 | Generated diagram labels showed `\n` and `&quot;` raw | Fixed: labels are plain text, never HTML | 7ada547d |
| 3 | Traceability coverage bars wrong under nb-NO | Already fixed in NEXT42 (`CssUnits`); browser re-check 5/5 | — |
| 3 | Pipeline gating semantics / Dependency Review synthetic labels / WF-06 manual review count | Already implemented; no change needed | — |
| 3 | Relationship explorer filters | Deferred to NEXT48 (full Relationship Review redesign) | — |
| 4a | Azure DevOps | Offline only — see below | — |
| 4b | TT02 / m2lbdev | Offline only — see below | — |
| 4c | Release Readiness with imported TRX | Moved to NEXT49 (full Release Readiness redesign) | — |

## Root causes

- **Task Alignment design links = 0.** Two independent causes. The page built its review context from the specification and tasks only. After that was fixed (`TaskAlignmentContext.Build` now loads Constitution and Data Model), the count stayed 0 because `NormalizeReport` copied the report without `DesignLinkedTasks`/`TestImplementationTasks`, and `NormalizeFinding` dropped `DesignLinks`, `Purpose`, `Deviation` and `UnlinkedBusinessTask`. The session cache key now includes a SHA-256 fingerprint of the design inputs, so a changed constitution invalidates the cached result. Rules version bumped to `task-spec-rules-v4`.
- **Cross-repository baseline.** `BaselineEligibility` (NEXT43) only knew same-repository identity and a per-run reviewer declaration. A remembered declaration (`source_repository_associations`, migration `20261105210510_AddSourceRepositoryAssociations`) is now a third relation. Revoking sets `RevokedAt` (history kept), and the run is then gated on confirmation again.
- **Diagram labels.** `GeneratedDiagrams.Clean` trimmed quotes and `<br>` but not Mermaid's literal `\n` or HTML entities.

## Group 4 — what is verified locally and what still needs live access

### 4a Azure DevOps
Verified offline:
- Baseline comparison M2LB (main) vs M2LB-language (feature) on the imported ZIPs: different repository identities are refused as a silent baseline, accepted with an explicit or remembered declaration (browser 8/8 + NEXT43 11/11).
- The ADO workflow (connection model, Git sources, work-item import, provider mapping, NotConfigured states) is covered by fixture tests: backend 582 passed / 0 failed and frontend 201 passed / 0 failed in the Altinn / SkoleNaervaer / GitSource / AzureDevOps / TT02 filter set (run today).

Needs live validation (user action, explicit consent; no PAT, no stored secrets):
- Entra sign-in and token acquisition against the real organisation.
- Real repository listing, branch resolution and work-item paging limits.
- Comparison of a live ADO revision with the imported ZIP of the same commit.

### 4b TT02 / m2lbdev
Verified offline: endpoint resolution, environment binding, instance evidence (read-only Storage GET contract), testing flows and the local one-click path, all against fixtures (same run as above). The `tt02` host guard prevents accidental calls from tests.

Needs live validation: ID-porten/TestID authentication, a real TT02 instance read, and any submission (state-changing — only with explicit authorisation).

### 4c Release Readiness
Handled in NEXT49: imported TRX is execution evidence for the tests it names only; anything else stays Not assessed / Not verified, and no positive Ready status is derived from absence of findings.

## Tests

- Frontend full suite: 8198 passed (after Groups 1–2).
- Backend: targeted 114/114 after the association change; the Group 4 fixture set 582/582.
- New tests: `TaskAlignmentDesignInputTests` (3), the `SourcePending` test, `BaselineEligibilityTests` (remembered/revoked), `A_remembered_repository_declaration_makes_another_identity_a_baseline_until_it_is_revoked`, `GeneratedDiagramLabelTests` (6).

## Browser verification (M2LB copy, nb-NO)

- Groups 1–2: 8/8.
- Remembered association: 8/8 (declare → reload eligible without confirmation → Forget → confirmation again; revoked, not deleted; no external host; no page errors).
- Traceability bars: 5/5.
