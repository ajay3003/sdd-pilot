# BirkNext status wording, logic and gap audit — 2026-11-03

Read-only audit of the whole BirkNext codebase (Blazor frontend, ASP.NET API, shared contracts) on branch `008-traceability-first`
at `fd362621`. Four areas were swept in parallel — frontend status vocabulary, review pages, cross-module consistency, backend status
logic — and every **High** finding below was re-checked in code before it was written down. Medium/Low findings were traced by the
sweep; items marked *suspected* were not fully traced. Paths are relative to `AIAssisted/` (`FE` = `frontend/BirkNext.Web/`).

Findings introduced by today's own work (tasks #8–#10) are marked **[new today]**.

## Summary

| Area | High | Medium | Low |
|---|---|---|---|
| A. Wrong results / false success | 2 | 9 | 4 |
| B. Cross-module disagreements and recommendations | 3 | 10 | 6 |
| C. Status vocabulary | 6 | 3 | 0 |
| D. Backend contracts & HTTP mapping | 1 | 5 | 5 |
| **Total** | **12** | **27** | **15** |

Most impactful, in recommended fix order:

1. Runtime review results from one Target Environment are shown after switching to another (A1).
2. Document Quality Review: a successful run can be marked Failed and crash the page (A2).
3. Headless auth diagnostic reports `Completed` for blocked/crashed runs (D1).
4. Recommended Workflow recommends Document Quality Review forever, even after it ran (B1).
5. Detail pages of Source Analysis still use the old generic `Complete`/`Partial`/`Unsupported` words, and the same enum value has four labels (C1, C2).
6. Integration journey with no mandatory steps is reported `Completed — every mandatory step was verified` (A6); interrupted journey runs stay `Running` forever (A7).

---

## A. Wrong results, false success, stuck states

**A1 — High — Runtime results survive a Target Environment switch.** `FE/Services/RuntimeReviewSessionService.cs:19-74` keeps one result per
review and stores `ProfileName`/`Environment`, but nothing compares them with the active profile. `FE/Components/FrontendAnalysisSettings.razor:2313-2330`
(`SetActiveProfileAsync`) changes the active environment without clearing any result (the only `Clear*Result` calls are in System Settings and the
local-data reset). FQR (`FE/Pages/FrontendQualityReview.razor:1348`), AQR (`FE/Pages/ApiQualityReview.razor:885`) and the Dashboard then show
environment A's report as the current result for environment B. *Verified.*

**A2 — High — Document Quality Review marks a successful run Failed and rethrows.** `FE/Pages/QualityReview.razor:1230` saves the result, then
`autoSave.SaveNowAsync()` (≈1248) and `BuildDiagnosticExportAsync` (≈1252) run in the same `try`; if either throws, the catch (≈1291) calls
`MarkFailed` (session report → null) while the report is on screen, then `throw;` surfaces the Blazor error UI. A failed re-run also keeps the
previous `_report` visible. *Verified.*

**A3 — Medium — FQR saved as Completed when no engine assessed anything.** `FE/Services/FrontendQualityReviewOrchestrator.cs:216-219` sets
`PreflightBlocked` when nothing was assessed, but the page only checks `QualityReport is null` (`FrontendQualityReview.razor:1610`) and saves
Completed (1685). The Dashboard then shows "No critical findings" (green) beside "Partial assessment" (`FE/Pages/Dashboard.razor:739-753`); AQR's card
says "API targets reviewed" when 0 completed (`Dashboard.razor:784`).

**A4 — Medium — FQR busy flag has no `finally`.** `RunReview` (`FrontendQualityReview.razor:1536-1689`) resets `_running` only on explicit returns;
an exception in `ResolveAsync`, `ApplyRecommendedScopeAsync` or `GetActiveContextAsync` leaves the spinner and the session `Running` forever.

**A5 — Medium — AI Change Auditor shows two opposite risk verdicts for an empty result.** Backend sets `OverallRiskLevel = Low` when nothing is
affected (`backend/BirkNext.Api/Services/AIChangeAuditService.cs:126-128`); the page hard-codes "High Risk" for 0 regression tests
(`FE/Pages/AIChangeAuditor.razor:169-175`). An empty result should read "Not assessed".

**A6 — Medium — Integration journey with zero mandatory steps is "Completed".** `backend/.../IntegrationJourneyRunner.cs:80-85`: with
`mandatory.Count == 0`, `relevant.Length == mandatory.Count` and `All()` are vacuously true → `Completed`, "Every mandatory step was verified."
(maps to `Verified` in `JourneyReadinessComposer.cs:39`). *Verified.*

**A7 — Medium-High — Interrupted journey runs stay `Running`.** `IntegrationJourneyRunStore.cs:30-40` has no recovery after a crash/restart (Active
Event, CDC and Performance all have one); `JourneyReadinessComposer.cs:37-45` maps Running/Cancelled to `NotRun`, so an interrupted run reads "never run".

**A8 — Medium — Security Classification runs with nothing to check and says "completed".** `FE/Pages/SecurityClassificationReview.razor:521`
blocks only on a broken scope; with no snapshot the status reads "Source security review completed · 0 security concerns" (via
`FE/Services/ClassificationPresentation.cs:74`) while the backend marks it `NotTestable` (`ClassificationReviewService.cs:338`).

**A9 — Medium — Critical E2E batch shows only its last flow.** `_lastRun = batch.Runs.LastOrDefault()` (`FE/Pages/CriticalE2ERegression.razor:744`)
hides earlier failures in "Run all"; `_lastRun` is not cleared on a new run, and the card uses the current environment name, not the run's.

**A10 — Medium — Pipeline Review: backend outage reads as "Pipeline source required".** `FE/Services/PipelineReviewApiService.cs:37` turns
`HttpRequestException` into an empty source list; `Load()` (`FE/Pages/PipelineReview.razor:290-297`) has no generation guard.

**A11 — Low — Re-run failures keep the old result on screen** (AQR `RunReview` ≈1213 never clears `_report` while the session report is nulled;
same pattern in DQR, A2). **A12 — Low** Security Classification: an `OverviewAsync` failure after a successful run is shown as a run error
(`SecurityClassificationReview.razor:931-947`). **A13 — Low** IQR auto-select reload lacks the `IsCurrent(_generation)` guard
(`FE/Pages/IntegrationQualityReview.razor:1308,1313`) and `RunAsync` has no re-entry guard. **A14 — Low, suspected** Task Alignment overlapping
loads can commit out of order (`FE/Pages/TaskAlignment*.razor:465-476`).

**A15 — Medium [new today] — Source Analysis Overview: Observability card ignores `Unsupported`.** `SourceObservabilitySnapshot.Status` is an
`ArchitectureStatus`; `FE/Services/SourceAnalysisDashboard.cs` `Observability()` never checks `Unsupported`, so an unsupported snapshot shows
"Detected"/"Needs review" with counts instead of "Not assessed". *Verified.*

## B. Cross-module disagreements and recommendation logic

**B1 — High — Document Quality Review is recommended forever.** `FE/Services/WorkflowReadinessService.cs:582-589` falls through to
`reviews.FirstOrDefault()` — built in sidebar order with no run-state check — so the Dashboard keeps saying "Start Document Quality Review"
after a completed run, and "Required review decisions complete" is effectively unreachable. *Verified.*

**B2 — High — "Done" means Approved in Governance but any decision in the Workflow.** Governance counts only Approved
(`FE/Services/DashboardPresentation.cs:522-531`); the Workflow and release readiness count Reviewed/Approved/Reviewed-with-gaps
(`WorkflowReadinessService.cs:92,323`). Result: Workflow "3 of 3 decisions recorded · Passed" beside Governance "0 of 3 approved · In progress". *Verified.*

**B3 — Medium — "Reviewed with gaps" is a completed decision and a blocker.** `ArtifactReviewPresentation.cs:22,57` and the workflow treat
NeedsChanges as complete; the Dashboard turns it into a non-informational attention item (`DashboardPresentation.cs:286-288`) that the new hero
counts as a **blocker** (`SddDashboard.cs`) **[new today: hero]**.

**B4 — Medium — "Document types" counted four ways.** Workspace summary 5 roles (incl. Data Model), SDD evidence KPI "of 4 core" (Spec, Plan,
Tasks, Constitution — `DashboardQualitySummary.cs:15,27`), pack completeness 3 roles (`ArtifactPackReview.cs:73`), package counts 6 roles incl.
Research (`ProjectEvidencePackages.cs:63`). The KPI calls Constitution core, the readiness row calls it optional (`DashboardPresentation.cs:678`).

**B5 — Medium [new today] — Package counts differ in the same view.** The header counts the current application's imports
(`Dashboard.razor:82`); "Imported evidence packages (N)" lists every workspace import, including other applications' "Not associated" ones.

**B6 — Medium — A missing core document has two statuses.** Review availability row "Needs setup" (`DashboardPresentation.cs:681`) vs artifact card
"Not applicable — reviews that need it do not apply" (`:777-779`).

**B7 — Medium — Target Environment readiness has three rules.** Project inputs need a valid non-placeholder URL on the active profile
(`ProjectInputs.cs:158-181`); runtime setup accepts one configured-but-inactive environment (`WorkflowPaths.cs:181,190`); sidebar applicability
accepts any non-empty URL (`TechnologyCoverageApiService.cs:110,116`). Sidebar shows no badge while pages say "Placeholder URL / Needs setup".

**B8 — Medium — Project evidence "blocking" vs "missing" definitions differ** (`ProjectEvidenceResolver.cs:32` vs `:131`): with one required role
in NeedsDecision the headline is "Partial" and the text claims "All N required roles are bound…" (false).

**B9 — High (dead end) — Runtime "Project evidence" step cannot be resolved.** `WorkflowPaths.cs:204-207` requires `RequiredReadyAll`, but the
detail lists `BlockingLabels`, which omits Partial roles and Available-not-Ready Test Pack rows → "2 / 3 required ready. Missing: ." with an
empty list; `MissingEvidenceAction` returns null for the same state (`WorkflowReadinessService.cs:604,619`).

**B10 — Medium — Runtime setup recommended before open document reviews** (`WorkflowReadinessService.cs:517-521` vs its own rule at :551-553).
**B11 — Medium, suspected** — a runtime review can be recommended with no target (guard only for `Partial`, :585).
**B12 — Medium** — "Source binding not established" raised for source-only projects with nothing to bind (`DashboardPresentation.cs:301-304`).
**B13 — Medium [new today]** — overall readiness can never be "Ready" for a document-only project: optional "Runtime evidence: Not assessed" and
"Source: Not provided" count as non-ready inputs in the evidence chart/hero (`SddDashboard.cs`).
**B14 — Low [new today]** — Review execution chart counts `Unknown` applicability (e.g. Change Traceability not configured) as Blocked.
**B15 — Low** — Needs attention KPI counts non-informational items; the panel's "All areas (N)" counts all (`Dashboard.razor:259`).
**B16 — Low** — "Blocked" in Delivery Overview = explicitly blocked; in Delivery Reports also Regressed (`DeliveryReportingPresentation.cs:38`).
**B17 — Low** — Test Data empty-state "Configure catalog"/"Register…" buttons open the User Guide (`FE/Pages/TestDataPage.razor:200-201`).
**B18 — Low** — unknown `?section=` in System Settings renders a blank page (no fallback, `SystemSettings.razor:2123-2126`); all in-app links are valid.
**B19 — Low** — "1 test environment configured" for a Partial target next to "Incomplete" (`DashboardPresentation.cs:237`).

## C. Status vocabulary

**C1 — High — Source Analysis has two status systems.** The new taxonomy (`SourceEvidenceStates`) covers only the Overview, legend and coverage
**[new today]**; every detail page still uses `SourceStatusPresentation`/`SourceDomainText` with `Complete`, `Partial`, `Unsupported`,
`Not detected`, `Not found`, `Ready` (`ArchitectureWorkspace.razor:14`, `DatabaseWorkspace.razor:265-270`, `ObservabilityWorkspace.razor:11,94,146`,
`SourceDomains/SourceDomainHeader.razor:7`, `SourceDomainLimitations.razor:12`, `InfrastructureWorkspace.razor:12,41`, `PipelineWorkspace.razor:30,126`,
`ConfigurationWorkspace.razor:181`, `TestEvidenceWorkspace.razor:34,36`, Quality rows `SourceAnalysis.razor` via `SourceAnalysisReview.cs:170-217,316`).
Dead code: `StatusLegend` in `SourceAnalysis.razor`, `SourceAnalysisOverview` card builders, `SourceEvidenceDomainsPresentation.cs:67`.

**C2 — High — Same enum value, several labels.**
- `ArchitectureStatus.Unsupported`: "Not assessed" (`SourceAnalysisReview.cs:119,194`), "Unsupported" (`SourceAnalysisOverview.cs:44`), Overview card "Detected" (A15).
- `DatabaseAnalysisStatus.Unsupported`: "Not detected" (`DatabaseWorkspace.razor:268`) and "Not assessed" on the same page (`:288-290`), "Unsupported" (`SourceAnalysisOverview.cs:45`).
- `ReviewCategoryState`: Dependency Review "Ready" (green) / Dependency Health "Assessed" (info); Issue → "Issue" vs "Unavailable" (`DependencyReviewPresentation.cs:19` vs `DependencyHealthPresentation.cs:61-72`).
- `FrontendQualityEngineOutcome`: SafetyBlocked → "Not assessed" vs "Safety blocked"; EngineError → "Failed" vs "Engine error" (`FrontendQualityEngineOutcomePresentation.cs:160-174` vs `FrontendQualitySurfaceResults.cs:120-123`).
- `AltinnReadinessState.NotAssessed`: "Not verified" / "Not assessed" / "Optional" / "Needs review".
- `RuntimeReviewStatus` default: "Unavailable" (`SystemSettings.razor:2897`) vs "Not Run" (`Dashboard.razor:1244`).
- The same snapshot "predates the area": Import Project "Not analyzed" vs Source Analysis "Not assessed".

**C3 — High — "Ready" has at least seven meanings** (delivery gate verdict, required config fields present, test identity approved, source
evidence detected, dependency category assessed, setup complete, positive completeness). A bare "Ready" reads as release-ready.

**C4 — High — "Partial" has at least eight meanings** (extraction incomplete, heuristic parser, found in part of the source, some features have
spec.md, no source detected, runtime only observed, partly met, partial parse result).

**C5 — High — Success words/colours for "present, imported or configured".** Recommended Workflow renders "Imported" with the Approved style
(green) (`FE/Pages/RecommendedWorkflow.razor:524,531,555`); Source Analysis detail headers show "Complete"/"Detected" green; the Dashboard's
legacy analysis cards use "Healthy" for score ≥ 80 (`Dashboard.razor:1234-1237`) although FQR forbids it (`FrontendQualityResultModels.cs:27`);
System Settings shows "Pass" for a merely configured port/sink (`SystemSettings.razor:1769,2772`); Altinn "Configured" is green.

**C6 — High — "Not assessed" rendered red in FQR.** `StateClass` defaults to `blocked` (`FrontendQualityEngineOutcomePresentation.cs:177-188`),
contradicting the comment that Not assessed is not Blocked. *Verified.*

**C7 — Medium — "Never evaluated" has eight spellings:** Not assessed / Not Assessed / Not analyzed / Not run / Not Run / Not evaluated /
Not checked / Not verified / Unavailable. **"Absent"** has six: Not detected in this snapshot / Not detected / Not found / None found /
Not found in source / Unsupported.

**C8 — Medium — "Complete", "Ready to review", "Unavailable" each carry 3–4 meanings**; mixed capitalisation ("Not Ready"/"Mostly Ready" vs
"Not ready"); two different enums named `PerformanceReadinessState` (`shared/PerformanceTestContracts.cs:40`, `FE/Models/WasmPerformanceModels.cs:101`).

**C9 — Medium — Tone inconsistencies.** "Detected" blue in the new taxonomy, green on detail pages; "Partial evidence" and the architecture
conflict render neutral grey on the detail header but red/amber on the Overview; "Running" and "Unavailable/not configured" render amber in
System Settings and the Dashboard; `ArchitectureEvidenceState.Inferred` amber in Pipeline Review, blue elsewhere.

## D. Backend contracts and HTTP mapping

**D1 — High — Headless auth diagnostic reports `Completed` on failure.** `HeadlessDiagnosticReport.Status` has no initializer (defaults to
`Completed`, `shared/HeadlessAuthDiagnosticContracts.cs:48`); `HeadlessEvidenceRun.Block()` never sets it
(`backend/.../HeadlessDiagnosticService.cs:39-47`); `HeadlessRunStatus` has no `Failed`. Busy gate, lost browser control, timeout and the
catch-all all return 200 `Completed`. *Verified.*

**D2 — Medium — Target detection keeps `Success = true` when the state is Failed** (`TargetEnvironmentDetectionService.cs:128` vs
`ApplyTypedOutcome` 716-723); `AwaitingUserContinuation` is treated as authenticated (`InteractiveBrowserDetectionStrategy.cs:256`); timeouts and
internal errors are 200 bodies.

**D3 — Medium — `catch (Exception)` mapped to client errors:** 409 in `BrowserCompanionController.cs:34,86`, `CriticalE2EController.cs:60,70,78`,
`ManagedEdgeCdpController.cs:47`; 400 with `ex.Message` (internal message leak) in `RecommendedWorkflowController.cs` (7 sites) and
`WorkspacePersistenceController.cs` (11 sites); TT02 unreadable response 400 instead of 502 (`AltinnTestingController.cs:35`).

**D4 — Medium — "Another run in flight" mapped five ways:** 409 (fuzzing, performance), 200 + persisted `SafetyBlocked` history row (Active
Event — concurrency is recorded as a safety-policy block), 200 `Blocked` (journeys), 200 `Completed` (headless, D1).

**D5 — Medium (latent) — Seven shared enums serialize as numbers** (no `JsonStringEnumConverter`, no global converter in either `Program.cs`):
`DiagnosticStatus`, `CoverageClassification`, `CoverageEvidenceStatus`, `ProjectionRenderExecutionStatus`, `ProjectCompatibilityStatus`,
`SchemaDependencyStatus`, `XmlSchemaValidationStatus`. Reordering values silently remaps stored/older JSON.

**D6 — Medium — Duplicate/overlapping enums.** `ArchitectureStatus` and `DatabaseAnalysisStatus` are identical; `DiagnosticStatus` and
`ProjectCompatibilityStatus` are identical; completeness uses Complete/Ready/Full; run lifecycle uses Passed/Completed,
Failed/ExecutionFailed/FailedToExecute, Partial/PartiallyCompleted/CompletedWithLimitedEvidence; ~10 synonyms of "not assessed"
(NotAssessed, NotAssessable, NotEvaluated, NotRun, NotTested, CannotAssess, NotTestable, Unknown, NotVerified, NothingAssessed).
`ApiReviewTargetStatus` and `HeadlessRunStatus` have no Failed value.

**D7 — Low** — the same failure gets different statuses across runners (unexpected exception: NotVerified journeys, Inconclusive Active Event,
Blocked Critical E2E). **D8 — Low** — API review: target exceptions become `NotTested` (`ApiReviewEngine.cs:127,132`); "No internal details in
error responses" passes when no error response was observed (`:540`); CORS allowed-origins passes on an empty observation set (*suspected*, `:1290`).
**D9 — Low** — fuzzing coordinator is memory-only; an escaped cancellation becomes Failed (*suspected*). **D10 — Low** — Azure DevOps list
endpoints return 200 + empty list on failure (`DeliveryAzureDevOpsController.cs:69-70`). **D11 — Low** — several statuses are plain strings
with no enum link (`AltinnEnvironmentContracts.cs`, `BrowserCompanionContracts.cs`, `CriticalE2EContracts.cs`).

## Checked and found consistent

Integration review run decision vs `CanRun`; IQR "Nothing assessed"; Dependency Review next-step gating; Performance Test Review
run/poll/start guards; Document Quality Review `CannotRun`; Documentation Drift compare guard; Task Alignment zero tasks; Azure Combine;
`JourneyReadinessComposer.Fold`; Critical E2E release gate; Active Event zero-event outcome; Performance/Active Event/CDC run recovery;
all in-app `?section=`/`?tab=` links.

## Recommended remediation plan

1. **Correctness first (A1, A2, D1, A6, A7, B1, B9, A15):** scope runtime results to the environment they ran against (clear or mark
   "from <environment>" on switch); move DQR post-save work out of the failure path; initialise headless `Status` and add `Failed`;
   zero-mandatory journeys → Not assessed; journey run recovery; NextAction skips reviews that already ran; list Partial/Test-Pack items in the
   runtime evidence step; Observability card honours Unsupported.
2. **One vocabulary:** extend the Source Analysis taxonomy to the detail pages (`SourceStatusBadge` takes a `SourceEvidenceState`), then adopt
   a shared app-wide set for "never evaluated" (Not assessed), "absent" (Not detected in this snapshot / Not found), "ready to start"
   (Ready to run) vs "passed"; never "Ready"/"Complete"/"Healthy" for presence or configuration; fix FQR `StateClass` default and
   Recommended Workflow's "Imported" style.
3. **One rule per fact:** single definitions for core document types, Target Environment readiness, review "done" (decision vs approval),
   and package counts; make optional inputs NotApplicable in readiness counts.
4. **API hygiene:** `catch (Exception)` → 500 without `ex.Message`; one mapping for "run already in flight" (409); global
   `JsonStringEnumConverter` in both `Program.cs` files.
