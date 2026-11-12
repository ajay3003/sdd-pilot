# Workspace Views — gap register

| Id | Pri | Gap | Why open / what closes it |
|---|---|---|---|
| WV-G1 | P3 | No custom-view editor. The person can add modules to a built-in view ("Add to …"), and that is the only customization | By design. A full editor was not requested and would be a new configuration platform |
| WV-G2 | P3 | Delivery Reports keeps its own audience selector (`DeliveryRole`: Scrum Master / Test Lead / "Project Leader") for which report view opens first | Separate per-browser convenience of the Delivery module. It could be aligned to the Workspace View label "Project Manager" in a follow-up |
| WV-G3 | P2 | Dashboard and Recommended Workflow differ per view through which shared capabilities are shown (and the audience wording of risks). There are no view-specific metrics such as "contract compatibility %" | Deliberate: only evidence-backed facts that already exist are shown; nothing is calculated per view |
| WV-G4 | P3 | Landing routes are used only by explicit links; switching a view never navigates (the brief prefers this) | — |
| WV-G5 | P2 | Recommended Workflow sends `api/source-context` several times in parallel (≈2–3.6 s each on the real workspace) | See MT7-G7: request de-duplication and a persisted snapshot identity projection |
| WV-G6 | P3 | Clean Windows / per-OS-profile persistence was not tested: preferences live in browser storage per browser profile | By design (a per-person browser preference, not shared state) |
