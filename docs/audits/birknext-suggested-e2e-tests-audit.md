# BirkNext — Intelligent suggested E2E test generation: audit (2026-11-10)

Branch `008-traceability-first`. Baseline `0fca2f6f`. No push, no deployment, no external system, no unattended browser against a real environment.

## 1. Method

Everything below was read from code at HEAD:
- the Critical E2E Regression backend (store, service, runner, step executors, controller) and contracts;
- the Critical E2E page and flow editor, with their bUnit tests;
- the Edge Companion extension (`browser-companion/`: manifest, worker, content script, `lib/automation.js`, `lib/picker.js`) and its own audits (`ELEMENT-PICKER-AUDIT.md`, `E2E-PROBE-SPIKE.md`);
- the Companion backend (`BrowserCompanionService` + `.Automation`);
- the Source Analysis archive reader and store;
- requirement parsing (`RequirementReferenceParser`, `ImplementationGapExtractor`) and Unified Impact Analysis (`ImpactAnalysisRunService`, `SourceChangeImpactService`).

## 2. Existing E2E engine

| Capability | Existing implementation | Status |
|---|---|---|
| Test definition | `CriticalE2EFlowDefinition` (module, kind Critical/Diagnostic, mode, steps in array order, version bumped on material change, final outcome, boundary, Test Data aliases/mappings, Source, RequirementRef, Tags) | SUPPORTED |
| Persistence and revisions | `CriticalE2EStore`, files under `App_Data/critical-e2e`; each run records the flow version | SUPPORTED |
| Step vocabulary | Navigate, Click, Fill, Select, WaitForVisible/Text/Route, AssertVisible/Hidden/Text/Value/Route, ReadValue (closed set; no code field) | SUPPORTED |
| Assertions | outcome kinds UrlEquals/Contains, ElementVisible/Hidden, TextEquals/Contains, ValueEquals, SuccessMessageVisible, ListContains, ProviderVerified | SUPPORTED |
| Network response assertion | not in the browser vocabulary; integration steps are a separate mode | NOT SUPPORTED (in a browser flow) |
| Locator model | `CompanionSelector` TestId / Role(+name) / Label / Text / Css; ambiguity is an error | SUPPORTED |
| Execution | one runner with step executors; a command rides back on the Companion heartbeat; one command in flight; non-production only, enforced in the backend, the worker and the page | SUPPORTED |
| Evidence | run results with step results, evidence references and release-evidence rules; listed in Test Execution & Evidence (Browser E2E) | SUPPORTED |
| Element picker | `PickElement`: one element the tester clicks; candidates are counted with the replay resolver | SUPPORTED |
| Readiness | browser engine readiness (connected, one live approved page, environment allows automation); `ConfigurationProblem()` | SUPPORTED |

## 3. Edge Companion capabilities

| Capability | Before | After this task |
|---|---|---|
| Active authorised tab | SUPPORTED | unchanged |
| Current URL / route | SUPPORTED (live page route) | unchanged |
| Accessibility tree | MISSING | MISSING (not needed: role + accessible name per selector) |
| DOM element inventory | MISSING (counts only) | **PARTIAL, by design**: `VerifySelectors` checks a bounded list of ≤ 16 named selectors. It is not a free inventory, so no page content is enumerated. |
| Roles / accessible names | PARTIAL (picked element only) | PARTIAL: also reported per verified selector; the name algorithm is still simplified (aria-label → aria-labelledby → text) |
| Labels, input types, data-testid, ids | PARTIAL (pick) | PARTIAL: verified per selector |
| Stable CSS | PARTIAL | unchanged; never positional in suggestions |
| Visibility / enabled | PARTIAL | reported per selector |
| Frames | MISSING | MISSING: not searched; reported per selector ("N frame(s) on the page are not searched") |
| Shadow DOM | MISSING | MISSING |
| Dynamic refresh / SPA navigation | PARTIAL / SUPPORTED | unchanged; verification needs the page on the suggestion's route |
| **Password-field values** | **PARTIAL: `ReadValue`/`AssertValue` read `el.value` of any input** | **FIXED**: password values are never read, compared or echoed; Fill of a password reports no value |

**Security boundaries kept.**
- No new manifest permission.
- Top frame only, on approved origins only, with every sender check unchanged.
- No code field.
- Verification is an authoring action and can never be a flow step.
- The capability string `selector-verify` gates it.
- The result is identity only (count, tag, role, a short accessible name, visible, enabled) and stays within the 8 KB command-result limit (≤ 16 selectors).

## 4. Source-based UI discovery

| Item | Finding at baseline |
|---|---|
| Razor / CSHTML / HTML text | **Never read.** `IqrSourceArchiveReader` skipped `.razor`, `.cshtml` and `.html`. Only `.razor.cs` code-behind was read. |
| UI route / form / control extractor | **MISSING** |
| Test inventory | `DotNetXunitTestDiscoveryProvider` (bUnit classified as FrontendComponent); Playwright TS specs not read |
| Per-file diff between snapshots | not available (domain-model diffs only) |

## 5. Confirmed gaps → design

| ID | Gap | Design |
|---|---|---|
| SG-G1 | No UI facts from source | The reader keeps UI source **in memory only** (≤ 600 files, ≤ 512 KB each, ≤ 16 MB in total; bin/obj/node_modules/lib/dist skipped). `SourceUiExtractor` stores **facts** on the snapshot (`UiSurface`): routes, title, authorisation, forms, controls with role, test id, static id, label, binding, required, length, range, pattern, the required and constraint messages, conditional/repeated flags, components as limitations, requirement ids from comments. Never markup. |
| SG-G2 | No suggestion engine | `SuggestionEngine` (rule set `suggest-v1`, deterministic ids): page opens, navigation, required fields, invalid input (one constraint at a time), valid submission. Expected results come only from explicit evidence: the page's own heading, a link's href plus the target route, the declared required message, or Blazor's ValidationSummary convention (stated as such). A valid submission has **no** final assertion (NEEDS_EXPECTED_RESULT). |
| SG-G3 | No locator verification | Companion `VerifySelectors`; `SuggestionEngine.Verify` maps the checks to VERIFIED_UNIQUE / AMBIGUOUS / NOT_FOUND / NOT_VISIBLE / UNSUPPORTED_FRAME / NOT_ASSESSED; verification is bound to the source snapshot, so another snapshot makes it STALE |
| SG-G4 | No human review of mappings | Accept (verified unique only), choose an alternative (unique, or manual with a written reason), reject, mark unresolved; history kept (`suggestion-reviews.json`) |
| SG-G5 | Duplicates | Coverage against existing flows: ALREADY_COVERED / PARTIALLY_COVERED ("extend that flow") / OVERLAP_REQUIRES_REVIEW / NEW_SUGGESTION |
| SG-G6 | Test Data | Synthetic values only from declared constraints. **Identifiers, credentials, patterns and files are never generated** (NEEDS_TEST_DATA). |
| SG-G7 | Approval and execution | Approval saves a **Diagnostic** flow (`Source = SuggestedFromSource`, tags: suggestion, snapshot, project, rule version) through `ICriticalE2EService.SaveFlow`. It runs only through the existing runner, after an explicit Run. |
| SG-G8 | Regression impact | Approved suggested flows checked against a newer snapshot: LOCATOR_REVALIDATION_NEEDED, TEST_DATA_REVIEW_NEEDED, ASSERTION_REVIEW_NEEDED, TEST_MAY_BE_OBSOLETE, NEW_TEST_SUGGESTED. Proposals only; approved flows are never rewritten. |
| SG-G9 | Password value exposure in the Companion | fixed (§3) |

## 6. Not built in this task, and why

| Item | Status | Reason |
|---|---|---|
| Requirement-text-derived expected results (spec.md acceptance criteria) | NOT SUPPORTED | Spec parsing lives mostly in the frontend. Requirement ids are recorded from source comments and existing flows (REQUIREMENT_DERIVED origin), but the expected result is never taken from free text without a reviewed mapping. |
| .tsx / .vue / .jsx UI source | NOT SUPPORTED | JS/TS is not read by Source Analysis ("unsupported language" limitation). |
| Frames / shadow DOM | NOT SUPPORTED | Would need `allFrames` content scripts and a new permission review. |
| Running application version | NOT ASSESSED | Verification records the observed origin, route and time. It does not prove the running build equals the imported snapshot, and the UI says so. |
