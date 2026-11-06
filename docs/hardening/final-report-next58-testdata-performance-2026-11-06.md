# NEXT58 — Test Data and Performance Test Review completion

Branch `008-traceability-first` · 2026-11-06 · local commits only (no push, no deployment, no live systems).

Status legend: **Implemented** · **Source verified** · **Automated-test verified** · **Browser verified** · **Runtime unverified** ·
**Blocked by unavailable external test package**.

| Module | Status before | Status after |
|---|---|---|
| NEXT53 Delivery | Done | Done (no regression: browser 18/18, tests green) |
| NEXT54 Test Data | Partial | **Partial** — every gap is implemented and tested, but the real Tiltak 2 import → automatic fixture → save → reload flow is blocked because the package is unavailable |
| NEXT55 Performance Review | Partial | **Done** — all four layouts are implemented, automated-test verified and browser verified (real k6 execution not run, per the constraints) |
| NEXT56 Relationship Review | Done | Done (no regression: browser 14/14) |
| NEXT57 Release Readiness | Done | Done (no regression: browser 14/14) |

## 1. Initial gaps found (audit)

**NEXT54 Test Data**
- Required-field table: it showed only field, required, source and status. It had no expected type, no validation issue and no guidance, and it did not distinguish a stale mapping, an incompatible asset or an unresolved schema element.
- Readiness: the eight dimensions existed, but the evidence facts behind them were not visible.
  - The Mapping dimension stayed Ready after a mapped asset changed or was removed.
- Lifecycle:
  - Generate silently discarded manual XML edits.
  - Changing a mapped identity after validation left a "Valid" XML that no longer matched the mapping, and it could still be saved.
  - A failed save or approval had no Retry.
- Auto-draft:
  - An unreadable catalog was ignored (`var (catalog, _)`). The idempotency check then saw no assets, so a duplicate draft was possible.
  - Failures surfaced as `[]` (an exception) or disappeared.
  - Retry required a reimport.
  - The outcome was not visible on Test Data.

**NEXT55 Performance**
- Workload: an unstructured form. No units on most fields, no inline validation and no estimate. Iterations and think time were not explained.
- Thresholds:
  - No lifecycle distinction: "configured" was the only state.
  - No validation. The backend readiness did not validate thresholds at all, so an impossible threshold (negative, an error rate above 100 %, a contradictory pair) stayed hidden until a run.
- Resources: only Resource Stability observation was shown. Runtime, image, network, certificates and container limits were missing, and whether the CPU, memory and PID limits are enforced was reported only after a run, as a run note.
- Results:
  - Partial was not labelled for timed-out runs.
  - There was no explicit execution-versus-application failure classification.
  - Dropped iterations were hidden when not reported.
  - Limitations were buried under "Advanced".
  - The scenario version was not shown.

## 2. NEXT54 implementation changes (commit `34907cca`)

- **`XmlFieldMappingPresentation`** (new, pure). Each field row carries the path, required/optional, expected XSD type, Test Data alias, mapping state, validation issue and guidance.
  - States: RequiredMissing, OptionalUnmapped, Valid, Suggested, Synthetic placeholder, **Stale** (the asset's value version changed since the saved fixture was generated), **IncompatibleAsset** (removed, not approved, or a contradicting declared type) and **Unresolved** (a mapping to an element the selected XSD does not declare).
  - Rows needing attention are listed first, and there is a new "Needs attention" filter.
- **Readiness** (`TestDataReadinessModel`). The eight evidence facts are attached to the **existing** eight dimensions, with no new dimension and no competing model:
  - identities ← approved Test Data
  - contract ← source available, XSD resolved
  - mapping ← required mappings
  - fixture ← XML generated, saved, approved
  - xsd ← schema-valid

  No evidence means Not assessed. A draft is never treated as approved. A changed mapped asset makes Mapping Needs attention, and a removed one makes it Blocked. Runtime stays Not assessed.
- **Lifecycle** (`XmlFixturePanel`):
  - Generate asks before replacing manual edits ("Replace my edits" / "Keep editing").
  - An identity change after generation marks the XML **Outdated**. Validate and Save are refused until the XML is generated again.
  - A failed save or approval keeps the XML and shows **Retry save** / **Retry approval**.
  - Persistence is still confirmed only by the reloaded catalog.
  - Schema validation is still required before save, and it is never bypassed.
- **Auto-draft** (`XmlFixtureAutoDraft`, Project Import, Test Data page):
  - Outcome kinds: Created, Already generated, Needs a decision, Not applicable, Failed.
  - An unreadable catalog generates nothing ("no duplicate draft").
  - Failures are kept (no more `[]`).
  - **Retry without reimport** is available on Project Import and on Test Data → XML fixtures. The Test Data retry reloads the catalog afterwards.
  - Repeating a run on the same snapshot gives "Already generated" (idempotent).
- **Tiltak 2**: not manufactured. A missing contract XSD yields Not applicable, never Created.

## 3. NEXT55 implementation changes (commit `ee9964bc`)

- **Workload**
  - Groups: Execution profile, Load, Duration, Iterations and think time.
  - Units on every field.
  - Inline per-field errors (`aria-invalid`, `aria-describedby`); safety-limit errors are flagged separately.
  - A problem list whose buttons move focus to the field.
  - An **Estimated load** panel: duration, peak load, the request upper bound against the limit, and an impact sentence.
  - The estimate now comes from **one shared formula**: `PerformanceTestRules.EstimatedMaxRequests`. The backend `PerformanceTestSafety` delegates to it.
  - No new capability. Iterations and think time are explained, and think time stays per step in the Scenario.
- **Thresholds**
  - Columns: Metric, Condition, Target value, Unit, Policy, Validation, Readiness impact, Latest evaluation.
  - A lifecycle strip counts **configured / valid / evaluated / passed / failed** separately, only from a finished run of the **same saved version**. Unsaved edits are never evaluated, and a partial run reads "Not assessed".
  - **One shared rule**, `PerformanceTestRules.ThresholdProblems` (not a number, negative, an error rate above 100 %, a "< 0" bound, a duplicate, a contradiction), now also **blocks backend readiness** (`thresholds` item: InvalidScenario, blocking). This strengthens validation; nothing is relaxed.
- **Resources**
  - Runtime, image, network/reachability, certificate trust and CPU/memory/process/tmp limits, each shown as **Configured → Validated → Runtime available → Execution verified**.
  - New non-blocking backend readiness item `container-limits`. It reports the requested limits and whether the runtime's delegated cgroup controllers can enforce them (`podman info`, cached, local).
  - Unenforced limits are "Limited", never Ready, and never blocking.
  - Execution verified comes from the latest run (for limits: no unapplied-limit note versus "ran without … limits").
  - A list of runtime capability warnings.
  - Resource Stability observation is kept below.
- **Results**
  - Completeness: Complete, Partial, No metrics or In progress.
  - **Failure type**: Execution failure (the tooling; the application was not assessed), Application responses failed, Cancelled (not a completed run) or No failed requests.
  - Visible execution and resource limitations.
  - Dropped iterations show "Not reported by this run" when absent.
  - Scenario version (definition version + fingerprint) and finished time.
  - The baseline snapshot names **both** runs.
  - Graphs still render only from executed evidence.
- **Cross-tab**: the Workload and Thresholds tab badges use the same models. The unsaved banner and status strip appear on every tab (already true, now covered by a test).
- **Preserved**: the explicit v1 save, version flow, unsaved banner, scenario status, the Executable rule (credential + definition + runtime values) and the authentication blockers. Backend readiness remains the authority.

## 4. Cross-module consistency findings

- No NEXT53/56/57 source file changed. The shared changes are `PerformanceTestContracts` (Performance only) and `TestDataReadinessModel`, whose consumers are the `TestDataTesterReadiness` surfaces.
- Release Readiness reads Performance verdicts from runs, and the verdict computation is unchanged.
- The new threshold-validity blocker affects only definitions with impossible thresholds.
- Terminology is kept domain-specific:

| Term | Meaning (Test Data) | Meaning (Performance) |
|---|---|---|
| Not assessed | no evidence | metric unavailable / partial run |
| Partial | — | metrics cover part of the workload |
| Stale | the asset changed since generation | — (Outdated = the XML no longer matches the mapping) |
| Approved | a person approved the fixture version | — |
| Executable | — | definition + credential + runtime values |
| Executed / Passed | — | evaluated in a finished run / met |
| Limited | — | a known runtime limitation (never Ready) |

## 5. Readiness and evidence rules verified

All of the following are automated-test verified, and the browser checks confirm them on the throwaway stack:
- No evidence is never Ready.
- Runtime is Not assessed by Test Data.
- A draft is not approved.
- A changed or removed asset makes Mapping stale or blocked.
- A configured threshold is not passed; unsaved or other-version thresholds are not evaluated.
- A partial or cancelled run is never a pass or a completed run.
- An execution failure is not an application failure.
- Unenforced container limits are never Ready.
- Validation is not claimed for unsaved changes.

## 6. Tests added and updated

- **Frontend (+38)**
  - `XmlFieldMappingPresentationTests`: field states, type compatibility (theory), evidence-to-dimension mapping, draft ≠ approved, stale/removed.
  - `XmlFixtureAutoDraftTests` (+4): skip kinds, a missing package never yields Created, catalog failure → no draft + idempotent retry, refused save / unreadable test packs.
  - `XmlFixturePanelTests` (+4): table columns, Retry after a failed save (no regeneration, catalog reload), Outdated after an identity change, no silent overwrite.
  - `TestDataPageTests` (+1): post-import notice and Retry.
  - `PerformanceTabPresentationTests`: workload issues and estimate, shared threshold rule, lifecycle, resource stages, result interpretation.
  - `PerformanceTestReviewPageTests` (+7): Workload, Thresholds, Resources, Results (cancelled / partial, execution failure, baseline identity), cross-tab unsaved state.
- **Backend (+1)**: `Container_limits_report_what_the_runtime_can_enforce_and_never_block_or_claim_ready_without_controllers`.

No existing assertion was weakened.

## 7. Backend test results

Six partitions, each with `--no-build`, `MaxCpuCount=1` and `xUnit.MaxParallelThreads=2`, run sequentially. All six completed, and the partitions are complete and non-overlapping: 970 + 1059 + 806 + 644 + 511 + 1941 = **5931** (baseline 5930 + 1 new).

| Run | Total | Passed | Skipped | Failed |
|---|---|---|---|---|
| First pass | 5931 | 5917 | 13 | **1** |
| Partition 4 re-run in full | 644 | 644 | 0 | 0 |
| **Combined** | **5931** | **5918** | **13** | **0** |

The single first-pass failure was `FrontendQualityEngines.ReadinessStatusIntegrityTests.AccessibilityPreferenceTrue_OverridesLegacyFalse_AndReachesDependencyProbe`, which failed with a `TaskCanceledException` (a dependency-probe timeout). NEXT58 did not touch it. It passed 3 times out of 3 in isolation and in the complete partition-4 re-run. It is recorded as a timing flake, not hidden. The targeted Performance subset passed 270 of 270.

## 8. Frontend test results

Full suite: **8327 passed, 0 failed, 0 skipped** (baseline 8289 + 38 new).

## 9. Browser acceptance results

Local stack: API on `localhost:5099` with the throwaway database `birknext_gaps`, and the published web app on `localhost:5199`. Edge via Playwright, localhost-only routing, no state-changing request.

| Script | Result |
|---|---|
| `next58-e2e` (new: Test Data evidence, Workload/Thresholds/Resources/Results, labels, inline error + focus to the blocking field, unsaved state, 390 px for all four tabs, no writes, no external host, no page errors) | **25/25** |
| next53 / next54 / next55 / next56 / next57 | 18/18 · 13/13 · 13/13 · 14/14 · 14/14 |
| phase3 | 25/25 |

Afterwards the stack was stopped by the PIDs that own ports 5099 and 5199.

## 10. Memory usage and guard behavior

The guard floor was 1.5 GB. **No operation was aborted.**

| Operation | Peak used | Minimum free |
|---|---|---|
| Frontend full suite | 3.4 GB | 5.9 GB |
| Backend partitions 1–5 | ≤ 1.3 GB | — |
| Backend partition 6 | 4.5 GB | 3.6 GB |
| API publish | 2.2 GB | — |
| Web publish | 2.7 GB | 4.4 GB |

Build servers were disabled (`--disable-build-servers`) and shut down after the publish. No backend and frontend suites ran concurrently. No browser automation ran during a publish.

## 11. Remaining unverified workflows

- **Blocked by unavailable external test package**: the real Tiltak 2 import → automatic draft → save → reload. In `birknext_gaps` the contract XSD is absent (the browser confirmed "No matching XSD found"). The field table, save/Retry and auto-draft are therefore automated-test verified only.
- **Runtime unverified**:
  - Real k6 execution.
  - Container-limit enforcement on a real Podman host (the readiness item reads `podman info` controllers; no run was executed).
  - Results for real runs (the browser saw no run in `birknext_gaps`, so the empty state was verified).

## 12. Security regression observations

This was a read-only review of the NEXT58 diff.
- No controller, authorization, appsettings, TLS, XML-parser or proxy change.
- `container-limits` calls the existing cached `podman info` controller probe: local, no user input, no network.
- Threshold validation is stricter; nothing is relaxed. Performance target and production guards are unchanged.
- The XML fixture save still requires a valid XSD result. Retry calls the same guarded save. Approval is still an explicit click. No destructive action was added.
- No values or credentials are newly displayed: validation-issue text was already shown in the issue list.
- The broader API/GraphQL authorization gaps remain a **separate open workstream and are not resolved here**.

## 13. Commits created

- `34907cca` NEXT58A: Test Data completion — field states, evidence-mapped readiness, auto-draft retry
- `ee9964bc` NEXT58B: Performance Test Review — Workload, Thresholds, Resources and Results layouts
- This report (docs commit)

No NEXT58C was needed.

## 14. Changed files

- **Backend**: `ContainerRuntime/ContainerExecutionRuntime.cs` (PIDs and tmpfs constants), `PerformanceTests/PerformanceTestExecutionService.cs` (container-limits item, threshold validation), `PerformanceTests/PerformanceTestSafety.cs` (delegates the estimate), and a test in `PerformanceTestReviewTests.cs`.
- **Shared**: `PerformanceTestContracts.cs` (`EstimatedMaxRequests`, `ThresholdProblems`).
- **Frontend services**: `XmlFieldMappingPresentation.cs` (new), `PerformanceTabPresentation.cs` (new), `TestDataReadinessModel.cs`, `XmlFixtureAutoDraft.cs`.
- **Frontend UI**: `XmlFixturePanel.razor(.css)`, `TestDataTesterReadiness.razor(.css)`, `TestDataPage.razor(.css)`, `ProjectImport.razor`, `PerformanceTestReview.razor(.css)`, `PerformanceRunResults.razor(.css)`.
- **Tests**: the six frontend test files listed in §6.

## 15. Remaining risks and open decisions

- The Tiltak 2 package is needed to close NEXT54 end to end. The Utdanning ↔ SkoleAdapter relationship conflict is untouched and still open.
- The frontend workload validation mirrors the backend `WorkloadIssues` messages (preview only; the backend is the authority). If backend messages change, the two can drift. The request estimate and the threshold rule are already shared.
- Definitions that already store an impossible threshold will now show as blocked in readiness until fixed. This is intended, but it is visible to users.
- The `ReadinessStatusIntegrityTests` dependency-probe timeout can flake under load; consider a longer probe timeout in that test.
