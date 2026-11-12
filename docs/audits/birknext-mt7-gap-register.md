# MT-7 — gap register

| Id | Pri | Gap | Why open / what closes it |
|---|---|---|---|
| MT7-G1 | P2 | The curated Skolenærvær guide names no specification requirement id, so on real data every association is a weak suggestion found through a named test | Content, not code. A guide author adds `RequirementId` + the spec file to the scenario evidence. Once the guide is **published**, those references become explicit, with no per-association decision needed |
| MT7-G2 | P2 | Workspace Requirements Traceability (`FR-nnn` of the selected SDD Specification document) and the canonical requirements (`module/feature/FR-nnn`) are separate id spaces. The coverage panel sits beside the matrix, not inside its rows | Joining them needs the workspace specification to carry its feature-pack identity. Matching by id text would merge different packs' FR-001 |
| MT7-G3 | P3 | Relationship Review decisions need an authenticated BirkNext session but no specific permission. Any signed-in user can confirm an association | Same policy as every existing Relationship Review decision. A reviewer role would apply to the whole registry, not only to MT-7 |
| MT7-G4 | P3 | Concurrent decisions on the same association: proven on PostgreSQL only by design (unique `(scope, sequence)` index). The automated test is sequential (in-memory provider) | A PostgreSQL concurrency test for the shared registry |
| MT7-G5 | P3 | The shared `MetaBadge` type badge renders text below 12 px (seen in Relationship Review, all widths) | Design-system change for every page, not part of MT-7 |
| MT7-G6 | P3 | "Decide in Relationship Review" opens the review tab without the "Scenario coverage" type preselected | The page has no query parameter for kind/type yet |
| MT7-G7 | P2 | Performance: every page load still asks `api/source-context`, which reads every stored snapshot row (125 MB of evidence JSON on the real workspace) even when it skips most of it. Recommended Workflow sends 4 identical requests in parallel | A persisted identity projection per snapshot (needs a migration), and request de-duplication in the client. See the performance audit |
| MT7-G8 | P3 | Coverage requires a relationship interpretation that includes both source snapshots and a Delivery snapshot. Without it the state is Unknown | By design: no requirement identity exists without the Delivery snapshot |
