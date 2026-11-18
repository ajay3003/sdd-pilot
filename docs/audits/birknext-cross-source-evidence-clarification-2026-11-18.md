# Cross-source evidence and clarification accuracy (2026-11-18)

Branch `008-traceability-first`, starting HEAD `56d2e5e8`. Local commits only; nothing pushed. Isolated Release instance (API :5097, web
:5197), disposable PostgreSQL databases, synthetic fixtures; no external environment contacted, no imported code executed.
`appsettings.Local.json`, `AIAssisted/testdata/` and the Kode67 gap register were not touched.

## 1. Original scenario and observed status

Manual Testing → Plan & prepare → Choose scenarios: **Rapportmottak · E2E-09 · Several reporters** — "Two reporters report for the same
child and period; both reports are kept." Details: Source *Integration journey / Rapportmottak / Test pack skolenaervaer 1 / rules tsd-1*;
Confidence *Candidate association — needs review*; Expected behaviour *Needs clarification — not stated in the evidence*; Existing tests
*None associated*; Open question about two approved reporters and downstream verification.

## 2. Root cause (exact code path)

| Field | Produced by (before) | Why it said what it said |
|---|---|---|
| Scenario | `TestScenarioDiscoveryService` → `IntegrationJourneyScenarioProvider` → `TestScenarioDiscoveryEngine.FromPackJourneys` | The Skolenærvær pack (`SkoleNaervaerJourneyPack.cs`, hard-coded C# scenario descriptors) is recognised for the project through its source roles (`TestPackSources.Recognizes`) |
| Confidence | `FromPackJourneys`: `Confidence = CandidateAssociation` for every pack scenario | Correct: the pack is associated with the project by source-role recognition only |
| Expected behaviour | `ExpectedBehaviour` never assigned → `DiscoveredScenario.NeedsClarification => ExpectedBehaviour is null` → razor prints "Needs clarification — not stated in the evidence" | **Inaccurate.** The pack *does* state the outcome ("both reports are kept"); the rule never read an outcome because the descriptor had no field for one |
| Existing tests | `Finalize` matches tests by `RequirementIds`; pack scenarios carry none | Correct for the data available (no explicit requirement reference) |
| Open questions | `$"Not assessed in the pack: {NotAssessedBecause}"` | One free-text reason (and a doubled "Not assessed: Not assessed:" prefix) |

Inputs the rule read: the pack object only (snapshot used as a recognition gate). Not read: specification requirements, Source Analysis
content, contracts, test inventory (except via requirement ids), Integration Topology, run history.

**Classification of E2E-09:** the business expectation is stated by the pack; what is genuinely missing is a **test prerequisite** (two
separately approved synthetic reporters — each run authenticates one) and a **verification mechanism** (no approved way to show both reports
are kept downstream). The association to the project is a reviewable candidate. "Needs clarification" was therefore inaccurate; a missing
prerequisite and a missing observation were reported as an unknown expectation. Tiltak 2 / M2LB source does not answer either question: they
are about test access and observability, not about the implementation.

Other investigated pack scenarios (classified from the pack's own reasons, nothing added):

| Scenario | Expected outcome | Open points (kind · basis) |
|---|---|---|
| E2E-07 Correction | Corrected report replaces the earlier one; earlier version kept as history | Implementation evidence missing (form expression not in a verified app contract · pack); **Contradictory evidence** (an Oppdatering that changes the dates creates a separate report · Skole source review, not runtime verified); Runtime evidence missing (· source review) |
| E2E-09 Several reporters | Both reports are kept | Test prerequisite missing; Verification mechanism missing |
| Invalid XSD | MM's XSD validation rejects the schema-invalid report | Test prerequisite missing (reviewed invalid fixture; submission contract) |
| Period locked | A report for a locked period is refused | Test prerequisite missing (operator-only lock); Verification mechanism missing (no report read contract); Runtime evidence missing (422 UTENFOR_GYLDIGHETSVINDU from source review) |
| Duplicate report | *not stated* | **Business expectation unclear** (source review shows a repeated meldingId answers the stored outcome — confirm intended); Test prerequisite missing; Verification mechanism missing |
| Retrieval/decrypt failure | *not stated* | Business expectation unclear; Test prerequisite missing (no safe fault injection) |
| Mapping validation, Period closure events, Bufdata, PII, coverage metadata | as stated by the pack | Verification mechanism missing / Test prerequisite missing / Runtime evidence missing, per the pack's reasons |
| E2E-01/02/08 and other supported scenarios | *not stated separately* (description mixes setup and outcome) | Business expectation unclear (honest: the pack does not state an outcome separately); run history added as runtime evidence |

## 3. Module / status inventory (assessment provenance)

| Module | Label(s) | Rule / engine | Sources examined | Meaning | Assessment |
|---|---|---|---|---|---|
| Plan & prepare (scenario discovery) | Needs clarification; Candidate association; Insufficient evidence | `TestScenarioDiscoveryEngine` (spec, guide, UI source, contracts, journeys, packs, test inventory) | Per provider (§4) | Was: expectation null. Now: classified gaps | **Changed** |
| Plan & prepare (prepared cases) | Needs clarification (review state, step without expected result) | `TestPlanSuggestionEngine` | The prepared case | Step expected result missing — accurate | Unchanged; open points now arrive classified |
| Manual Testing → Discover | "N need clarification" | `ManualTestingDiscover.razor` | Last discovery | Was the inaccurate count; now only unclear expectations + gap breakdown | **Changed** |
| Execute & document | Needs clarification (case review state) | Saved case | Saved case | Accurate (human review state) | Unchanged |
| System Understanding | Needs clarification (guide evidence class / scenario state) | `SystemUnderstandingPresentation` | Guide + reviewed evidence | Guide author's review state | Unchanged |
| Functional Quality Scope | Needs clarification / Not testable yet; Candidate association — not confirmed | `FunctionalQualityScope` | Requirements, schema matches | Testability classification | Unchanged (own model) |
| Delivery reporting | Needs clarification; Needs attention | `WeeklyReportBuilder`, `DeliveryBacklog` | `[NEEDS CLARIFICATION]` markers in specs | Literal spec markers — accurate | Unchanged |
| Message Flow Review | "Needs clarification: …" prefix | `MessageFlowReviewContracts` | Flow open questions | Accurate | Unchanged |
| Test Coverage engine | Needs attention / Review recommended | `TestCoverageReviewEngine` | Topology steps + evidence | Evidence-strength based | Unchanged |
| Contract relationships | Insufficient evidence | `ContractRelationshipReviewService` | Contract resolution | Neither side resolves | Unchanged |
| Performance | Insufficient evidence | `ResourceStabilityAnalyzer`, baseline regression | Samples / runs | Statistical sufficiency | Unchanged |
| Dependency health | Missing evidence | `DependencyHealthContracts` | Observations | Accurate | Unchanged |
| Integration catalog / SCIM / Security classification / Service Bus | Not testable | respective contracts | Their own inputs | Domain-specific | Unchanged |
| Integration Testing (journey capability) | Not assessed / Operator required | `IntegrationJourneyService` | Same pack (`NotAssessedBecause`) | Same source as Plan & prepare | Unchanged — shows the pack's reason text; see §11 |

Different modules assess different things; no status calculation was merged.

## 4. Evidence ownership and what discovery can use

| Evidence | Owner | Used by scenario discovery |
|---|---|---|
| Specification requirements / acceptance scenarios | Browser specification parser → `CoverageWorkspaceEvidence` | Yes (Specification provider) |
| System Understanding guides | `SystemGuideService` | Yes (guide provider) |
| UI source, API contracts (OpenAPI) | Source Analysis snapshot evidence domains | Yes (UI source, API contract providers) |
| Integration journeys (declared) | `DeclaredIntegrationJourneyStore` | Yes |
| Test packs (Skolenærvær) | `IIntegrationJourneyPackRegistry` | Yes, gated by source-role recognition |
| **Integration journey run history** | `IntegrationJourneyRunStore` (per Target Environment) | **Yes — new** (runtime evidence) |
| Test inventory (xUnit) | Source snapshot | Yes, by explicit requirement reference only |
| XSD / contract graph | `ContractEvidenceService`, pack contract profile | No (not mapped to scenarios) |
| Integration Topology | `IntegrationTopologyService` | No |
| Journey requirement links | `JourneyRequirementLinkStore` | No — they belong to project-declared journeys (by slug), not pack journeys |
| Terraform / IaC | Infrastructure evidence domain | Recognition gate only; never treated as deployment or runtime proof |

## 5. Uncertainty classification

`EvidenceGapKind` (shared): Business expectation unclear · Implementation evidence missing · Test prerequisite missing · Verification
mechanism missing · Source association needs review · Runtime evidence missing · Contradictory evidence · Evidence not evaluated. Each
`EvidenceGap` carries the question, its basis and, where BirkNext supports one, the next action; the UI names who usually resolves it.
`NeedsClarification` keeps its meaning — the expectation itself is not stated — and is no longer used for anything else.

## 6. Architecture changes

* `shared/TestScenarioDiscoveryContracts.cs`: `EvidenceGapKind`, `EvidenceGap`, `EvidenceSourceCheck`, `EvidenceGaps` labels/owners;
  `DiscoveredScenario.Gaps`, `.SourcesChecked`.
* `shared/IntegrationJourneyContracts.cs`: `JourneyScenarioDescriptor.ExpectedOutcome`, `.Gaps` (optional; packs without them keep working).
* `SkoleNaervaerJourneyPack.cs`: 13 not-assessed scenarios declare their outcome and classified gaps, restating only their existing reasons.
* `TestScenarioDiscoveryEngine.FromPackJourneys`: stated outcome → expected behaviour (basis: the pack); gaps → classified open points;
  association always reviewable; doubled prefix removed; free text without classification stays one unclassified point (never guessed).
* `TestScenarioDiscoveryEngine.WithRunEvidence` + provider: the pack's run history in the active Target Environment — one bounded read
  per pack (≤ 500 runs), latest run per scenario; only `Completed` removes "Runtime evidence missing"; a scenario the pack cannot run is not
  told to run; without an environment the history is reported as *not examined*.

No new engine, parser, registry or store. Saved test cases, reviews and run history are not touched (discovery results only).

## 7. Manual Testing changes

* Choose scenarios row chip: "Needs clarification" only for an unstated expectation; otherwise the most pressing gap (contradiction →
  implementation → prerequisite → verification); "not run yet" and "association review" are in the details, not on every row.
* Details: Expected behaviour (with basis) · steps · existing tests · evidence (a run is named by its state/time, never by its id) ·
  **What is still open** (kind, question, basis, next action, who resolves it) · **Sources checked**.
* Discover: the clarification count is now accurate; an "Also open:" line counts scenarios per gap kind.
* Prepared cases: open points arrive as "Test prerequisite missing: …" etc. (existing missing-item mechanism).

## 8. Technical Testing and other modules

Not changed in this pass. Integration Testing reads the same pack and shows its `NotAssessedBecause` text for not-assessed scenarios
(consistent source, different purpose: runnability). Other modules' labels were audited (§3) and are accurate for what they assess.

## 9. Cross-module consistency

Discover and Plan & prepare read the same discovery result and use the same classification. Integration Testing and Plan & prepare cite the
same pack scenario; Integration Testing answers "can it run", Plan & prepare "what is open before a test case is ready" — different goals,
consistent provenance.

## 10. Performance

Enrichment is in-memory on the provider's existing output: one run-history query per recognised pack (not per scenario, not per row
render), no source reprocessing, cached with the discovery result as before. Rendering adds two short lists inside the collapsed details.

## 11. Automated tests

On final code:

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| New backend `ScenarioEvidenceGapTests` (generic synthetic pack + the real E2E-09/E2E-07/duplicate pinned) | 12 | 0 | 0 |
| Focused backend: discovery, test planning, integration journeys, Skolenærvær, test packs | 344 | 0 | 0 |
| New/updated frontend: Plan & prepare (chip, details, sources checked, warning order) | 37 (page tests) | 0 | 0 |
| Full frontend suite | 9275 | 0 | 0 |
| Backend partitions 1–6 (sequential, in-repo build) | 1052 / 1101 / 904 / 682 / 541 / 2696 = 6976 | 0 | 19 |
| Playwright VerifyPre workflows (Manual, Technical, Integration, Recommended, Navigation) | 6 | 0 | 0 |
| API + Web Release publish (tag 52) | exit 0 | — | — |
| Browser acceptance (§12) | 18 | 0 | 0 |

Not run: PostgreSQL-specific integration tests beyond the backend partitions (no schema or store changed).

## 12. Browser acceptance

Fresh Release publish (tag 52: API and web exit 0, 0 build errors), disposable database `birknext_xsev2_20261118`, API :5097 / web :5197,
repository network guard. Synthetic source archive `SkoleSynth.zip` (a specification and a `Skoletjenesten` C# project — no product code)
imported through Project Import; Source Analysis recognises it as the Skolenærvær pack's M2LB application source. Script
`acc/xsev-accept.cjs` (scratchpad).

**Result: 18 of 18 checks passed.**

| Check | Result |
|---|---|
| Synthetic source imported; E2E-09 discovered from the recognised pack | pass |
| E2E-09 row chip "Test prerequisite missing" (not "Needs clarification") | pass |
| E2E-09 expected behaviour "Both reports are kept. (Stated by the test pack Skolenærvær Testing 1)" | pass |
| E2E-09 gaps: Test prerequisite missing · Verification mechanism missing · Source association needs review; next step "Prepare a second approved synthetic reporter in Test Data." | pass |
| Sources checked: test pack (found), Integration journey runs ("No run in the active Target Environment") | pass |
| No runtime claim for a scenario the pack cannot run | pass |
| E2E-07 flags Contradictory evidence, keeps both statements and the source-review basis, chooses nothing | pass |
| Duplicate report still "Needs clarification" (no outcome stated) | pass |
| Discover "Also open: 1 contradictory evidence · 1 implementation evidence missing · 7 test prerequisite missing · 7 verification mechanism missing" | pass |
| Details readable at 1440 / 1280 / 1024 / 768 px without horizontal overflow | pass (4) |
| No console/page errors; no prohibited network destination | pass (2) |

The console check ignores "status of 404" lines (the pre-existing Integration Quality Review 404 recorded in the project-selector audit);
no other error was filtered.

Not run in the browser (honest scope): the six project types of the brief (API-only, documentation-only, empty, contradictory, two
unrelated) as separate imports — contradiction is exercised through E2E-07, unrelated projects through the project-selector acceptance; a
"new source version" scenario was not re-imported in the browser (discovery is recomputed on demand and keyed on the snapshot, unchanged by
this work).

## 13. Remaining limitations

* Only the Skolenærvær pack declares structured outcomes and gaps; other packs keep one unclassified point per not-assessed scenario.
* Supported pack scenarios state no outcome separately from their description → still "Needs clarification" (accurate until the pack states one).
* Not correlated: XSD/contract graph, Integration Topology, and journey requirement links (pack journeys have none) — so "Existing tests"
  stays empty for pack scenarios. M2LB/Tiltak 2 source content is not searched for answers; the pack's source-review statements are the
  reviewed facts.
* Integration Testing does not yet show the classified gaps (it shows the pack's reason text).
* Run evidence needs an active Target Environment; without one it is reported as not examined.

## 14. Local commits

One local commit on `008-traceability-first` (this document included); nothing pushed. Starting HEAD `56d2e5e8`.
