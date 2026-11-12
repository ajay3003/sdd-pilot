# Workspace Views governance — implementation

| File | Change |
|---|---|
| `Services/NavigationCatalog.cs` | `WorkspaceView.TechnicalTester = 7`. `WorkspaceViews.All` holds eight views in selector order. Every focused view is defined by audience workflow groups (`Groups(...)`) over existing catalogue routes. Labels Project Manager / Manual Tester, with stable ids. Brief descriptions and landing routes |
| `Services/WorkspaceFocus.cs` | `Risks(sourceContext, view)` + `AudienceLead(view)`: the same risk code, route and severity, with an audience-specific leading sentence |
| `Components/WorkspaceFocusPanel.razor` | Functional test-preparation facts (Test Plan Suggestions + MT-7 coverage) for Manual Tester **and** Test Lead |
| Help Center | Governance article: the eight views and their purposes; views ≠ permissions; Dashboard/Workflow follow the view while the facts stay the same. Getting started: Manual Tester label |
| Tests | `NavigationWorkspaceTests`: eight views in order with stable ids and labels; specialized views distinct and smaller than half the catalogue; Technical Tester ≠ Developer ≠ Manual Tester; Test Lead covers functional + technical; persistence by id after the rename; Technical Tester round-trip; invalid saved view → All Modules; one risk identity in every view with audience wording; every visible module reachable once in every view (incl. Manual Tester, Technical Tester); presets name real modules. `ManualTestingViewTests` relabelled |

**Unchanged:**
- persistence format (`birknext.navigation.v1`);
- Feature Visibility;
- the recommendation engine and Project Quality;
- the backend, authorization and source baseline;
- Test Plan Suggestions.
