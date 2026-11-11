# BirkNext — System Understanding: implementation (2026-11-11)

- **Baseline:** `2c00b29c`.
- **Architecture:** `../architecture/birknext-system-understanding.md`.

| Area | Files |
|---|---|
| Model | `AIAssisted/shared/SystemUnderstandingContracts.cs` (new; also `SystemGuideLinks.IsSafeRoute`, shared by backend and frontend) |
| Store | `Models/SystemGuideRevisionRecord.cs`, `AppDbContext` (`system_guide_revisions`, two filtered unique indexes), migration `20261111160248_AddSystemGuideRevisions` (+ trigger `system_guide_revisions_no_rewrite`) |
| Service / API | `Services/SystemUnderstanding/SystemGuideService.cs` (catalog, seed, lifecycle, change awareness, `SystemGuideValidation`); `Controllers/SystemUnderstandingController.cs`; `Program.cs` (catalog entry `skolenaervaer` → embedded `SystemGuide.skolenaervaer.guide.json`, pack `skolenaervaer`); `BirkNext.Api.csproj` (embedded seeds) |
| Seed | `Services/SystemUnderstanding/Guides/skolenaervaer.guide.json` (Norwegian, curated draft) |
| Frontend | `Pages/SystemUnderstanding.razor(.css)`, `Components/SystemUnderstanding/SystemFlowDiagram.razor(.css)`, `GuideEvidenceList.razor(.css)`, `Services/SystemUnderstandingPresentation.cs`, `Services/SystemUnderstandingApiService.cs`, `Program.cs` |
| Navigation and help | `NavigationCatalog` (Workspace → System Understanding, icon `nav-icon-system-understanding`), `UserGuideContent` topic `system-understanding` + verified entry, `GuideGettingStarted` article, `DocumentationCatalog` row, `RecommendedWorkflow.razor` ("Understand the system flow" in the Understand stage) |
| Tests | Backend `SystemGuideTests` (15, real PostgreSQL); frontend `SystemUnderstandingPageTests` (16); nav tests updated for the new row |

## Decisions

- **Generic:**
  - nothing in the service, page or layout is Skolenærvær-specific;
  - a second synthetic system (orders) validates, versions and lays out with the same code (tests).
  - Skolenærvær is a catalog entry plus curated content.
- **Not a second evidence store:**
  - claims point at evidence (reviewed document, snapshot role, file/symbol/lines, contract, test id);
  - source recognition reuses `TestPackSources` and `IqrSourceStore`;
  - no source text is copied.
- **Honest classification:**
  - the seed has no RuntimeVerified or TestVerified claim;
  - tests that exist in source are TestDefined;
  - MU internals, SkoleAdapter, Tjeneste and the v2.0.1 / `/reports` generation are NeedsClarification;
  - Bufdata Parquet is Specified (not implemented in the reviewed snapshot).
- **Lifecycle:** the seed is a Draft, never auto-approved; published content is immutable in the database; supersession is kept; identical content creates no revision; concurrent edits get 409.
- **Authorization:** reads use the local session; changes need the configuration-write permission (there is no separate reviewer role in BirkNext; recorded as a gap).
- **Diagram:**
  - a dedicated SVG with a main lane and packed side lanes;
  - connection kinds differ by colour **and** line pattern;
  - keyboard-selectable nodes and edges;
  - a text equivalent, which is the only view on phones.
