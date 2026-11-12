# Test Plan Suggestions — gap register (task 20 v2)

| Id | Pri | Gap | Why it is open / what would close it |
|---|---|---|---|
| TPS-G1 | P2 | Only System Understanding guides produce drafts. Specification Explorer acceptance scenarios and Specification Review findings are not scenario sources yet | They carry requirements and acceptance criteria but no curated scenario identity. Closing it needs an explicit mapping (requirement id → scenario), not title matching |
| TPS-G2 | P2 | The only curated guide is Skolenærvær, and it is shipped as an unreviewed draft. So every expected result is "from an unreviewed guide draft" until a person clarifies it or the guide is published | This is intentional honesty, not a defect. Publishing the guide (System Understanding review lifecycle) turns the basis into ReviewedGuide |
| TPS-G3 | P3 | Drafts are not written into Requirements Traceability or Test Coverage & Overlap Review, and Project Quality does not count them | The brief forbids false green readiness. Drafts are "prepared", never coverage. A read-only "prepared drafts" fact in Traceability would need its own design |
| TPS-G4 | P3 | Environment availability and tester permissions are always "not verified here" | BirkNext has no verified source for a tester's permissions in a target environment. A Target Environment readiness fact could later feed this |
| TPS-G5 | P3 | No per-project access control. A proposal belongs to one guide (system), and reading needs the local session, writing `IntegrationConfigurationWrite` | BirkNext has no per-project ACL anywhere. Cross-system proposals are never combined or compared |
| TPS-G6 | P3 | Azure DevOps: export only (BirkNext JSON + text). No import format is verified, and no Test Case id is read back | By design (no ADO writes). A verified reader of existing ADO Test Cases could set `VerifiedAzureDevOpsTestCaseId` |
| TPS-G7 | P3 | Suite edits (rename, add, move) are draft changes. Removing a suite is not offered | Moving every case out leaves an empty suite, which the export skips. A remove action is a small follow-up |
| TPS-G8 | P3 | Business-semantic change: a changed expected result is a *potential rule change* only when both sides rest on a reviewed basis. No natural-language interpretation is attempted | Deliberate: no behaviour change is claimed from text alone |
