# BirkNext — Project Quality & Recommended Workflow: gap register (2026-11-11)

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Closed

| ID | Gap | Status | Evidence |
|---|---|---|---|
| PQ-C1 | The two pages worded the same next action differently | PASS | One presentation; cross-page bUnit test (2 projects); browser PQ-05, PQ-17 |
| PQ-C2 | False Passed/Healthy verdicts (scores, partial traceability, document-based release, reviewed-with-gaps, specification with findings, API card) | PASS | Unit tests; browser PQ-04, PQ-15 |
| PQ-C3 | No execution evidence; document review counted as verification | PASS | Execution evidence card; PQ-03, PQ-13 |
| PQ-C4 | Optional document type in the denominator | PASS | Unit test |
| PQ-C5 | Import banner as the main signal | PASS | Compact confirmation; PQ-02 |
| PQ-C6 | No stage view; progress not evidence-based | PASS | Five stages from recorded state; PQ-07/08 |
| PQ-C7 | No help integration or Project Quality article | PASS | HelpLink + Learn how; PQ-09/12 |

## Remaining

| ID | Gap | Status | Note |
|---|---|---|---|
| PQ-G1 | Dismiss / defer / not-applicable for recommendations | NOT SUPPORTED | Would need persisted, scoped and audited state; not built (no fake dismissal) |
| PQ-G2 | Needs-attention consolidation with finding identities (source binding repeated across sections) | NOT RUN | Items have no finding ids; dedup only by key today |
| PQ-G3 | "Top Recommendations" from document reports competes with the workflow next action | PARTIAL | Stays inside the collapsed detailed analysis |
| PQ-G4 | Approve is unreachable (`RequiresApproval = false` everywhere) | OPEN | Governance never reaches Passed because of it; no false pass |
| PQ-G5 | Browser project switch | PARTIAL | Importing a second project in the browser adds the package but keeps the current project; a switch is verified by the cross-page bUnit tests, not in the browser |
| PQ-G6 | Dead code (`DashboardMetricsService`, unused Recommended Workflow release helpers) | OPEN | Cleanup candidate |
| PQ-G7 | Synthetic scenarios A–L ledger | PARTIAL | Covered: A (no project), B/C (synthetic import, documents not reviewed), E-like (no runs), K (provider failure), restart. Not covered: F (failed runs), G (integration observer), I (stale snapshot), J (cross-project authorisation; BirkNext has no project-level permissions, WP-R1), L |
| PQ-G8 | Real M2LB acceptance | PARTIAL | M2LB-language imported into a disposable database (source analysed, "M2LB application Found"); it did not become the current project in the browser (PQ-G5), so its own next action was not compared in the browser |
| PQ-G9 | Mixed scopes on the Dashboard (session, application, environment) | OPEN | Documented in the audit; execution evidence is environment-scoped by design |
| PQ-G10 | Scratch database `birknext_pq3_20261111` | OPEN (needs approval) | Kept |
