# BirkNext — Multi-source end-to-end integration testing: independent audit (2026-11-10)

Branch `008-traceability-first`. **Baseline `54db534d`**. Working tree at start: the user's `appsettings.Local.json` (modified) and other sessions'
untracked files (`AIAssisted/testdata/`, `docs/audits/birknext-final-*-2026-11-07.md`, `birknext-release-assurance-decision-2026-11-07.md`,
`birknext-automatic-integration-test-generation-audit.md`). None overlaps this work; none is staged by it. No other agent's work is in progress.

## 1. Existing architecture (read from code)

| Capability | Existing implementation | Partial / missing | Owning module | Required change | Verification |
|---|---|---|---|---|---|
| Suite generation | `IntegrationSuiteService.GenerateAsync` + `IntegrationSuiteComposer` (`/integration-suites`, tables `integration_suites`, `integration_suite_runs`) | **One snapshot per suite** (`IntegrationSuiteGenerateRequest.SnapshotId`); `InModules` keeps only top folders named in the scope (`SkoleService`, `MeldingsutvekslerReceiver`, …) so another project's folders (e.g. `Buf.Meldingsutveksler.Service`) can never contribute | Integration Suites | composite source input; per-source module resolution; per-stage provenance | unit + browser |
| Stage model | scope `skolenaervaer`: 7 stages (`altinn`, `mu`, `mm`, `skole-intake`, `person-tjeneste`, `period-lock`, `bufdata`) mapped onto the Skolenærvær journey pack's step ids | stages carry no source requirement; stage → contract tests matched by **component name inside the contract key** (`ContractRef.Contains(Component)`) | Integration Suites + journey pack | keep stage ids; add per-stage source requirement (tokens, exclusions, kind of transition) | unit |
| Source recognition | `TestPackSources.Recognizes/MatchedOn` — component/project/configuration tokens, exclusion tokens, API-document keys, IaC; never archive names | pack-level roles only (MU, M2LB application, Altinn app reference, IaC); no stage granularity; no ambiguity class across projects | Journey packs | reuse the same word matcher (`ArchitectureRuleEvaluator.MatchesAny`) per stage | unit |
| Project identity | `ReviewSourceEvidenceProvider.Identity(snapshot).Key` + human-declared `source_repository_associations` (same project) | no association of **different** projects into one integration | Source Analysis | composite context references snapshots of separate projects without merging them | unit |
| Project access control | global policies only (`IntegrationConfigurationWrite`, `ActiveEventExecute`) | **no per-project ACL exists** (single local workspace) | Platform | composite never grants access: writes need the write policy, every association needs a recorded human approval, contents are referenced not copied | unit (401/403 + refusal of unapproved sources) |
| Contracts | `ContractRegistryService.ImportFromSourceAsync` (project-scoped, continuation, fingerprint) | per snapshot only | Contract Registry | call once per associated snapshot (no merge, no duplicate) | unit |
| XSD | `SourceContract.XmlSchema` (target namespace, version evidence, dependencies), `CodeReferences` kind `Validator`; shared XSD engine | no cross-snapshot comparison | Contract & Schema evidence | namespace comparison per transition | unit + real |
| Messaging | `ArchitectureSnapshot` dependencies `ServiceBusPublish/Consume`, `EventHubProduce/Consume` with channel ids/references | no cross-snapshot channel match | Source architecture | channel match per transition (declared, never runtime) | unit |
| Test Data | `TestDataCatalog.Resolve` (journey roles, classification levels 2/3 for Kode 7/6) | — | Test Data | reused unchanged; one composite role set | unit |
| Kode 6/7 | M01 Security Classification consumer + K67-01…12 (`IntegrationSuiteComposer.Security`), E2E-05, DATA-03 | — | Security Classification | reused unchanged | unit |
| Executors / observers | `IntegrationJourneyRunner` with registered `SkoleNaervaerAltinnExecutor` (Altinn TT02 steps), `SkoleNaervaerAltinnInstanceObserver`, `SkoleNaervaerMessageVerificationObserver` | MU, MM, Skolenærværsrapport, Person, period lock, Bufdata steps: **no executor or observer** | Journey runner | none invented; shown as MISSING_EXECUTOR / NOT_OBSERVABLE | unit |
| Chained execution | Contract Testing chains (`ContractTestChain`, Draft/Approved, `RunChainAsync`) → one Execution Evidence run; `StepId`/`DependsOn`/`Extract`; outputs `{{steps.x.y}}`; `RetryPolicy` (bounded, idempotent calls only, ≤ 10 s delay); "Outcome unknown" for unanswered mutating calls; `StopOnFirstFailure` | chains are authored per contract; no per-step bounded observation; no stage meaning | Contract Testing / Execution Evidence | composite transaction plan = a Draft chain across the stages' contracts; additive per-step observation policy (GET only) | unit + synthetic E2E |
| Correlation | `X-BirkNext-Execution-Id` per call; extracted outputs + resolved values per call | no boundary-by-boundary identifier record | Execution Evidence | read-side correlation from recorded outputs/inputs (explicit field mapping, never time) | unit + synthetic |
| Bufdata delivery | `bufdata-leveranse.md` (`1.0.0-draft`, retained by Parquet Quality Review), `ParquetWriters.cs` in source | no runtime delivery evidence | Parquet Quality Review | SPECIFIED_BUT_NOT_RUNTIME_VERIFIED for real M2LB; Parquet engine reused only for files | real |
| Period lock | pack scenario `period-locked` / `close-valid-period` Not assessed — operator-only lock (Skole source review) | no lock executor | Journey pack | named prerequisite with owner; never executed against a real system | unit |
| UI | `IntegrationSuitesPage.razor` (450 lines): one large snapshot `<select>`, scope select, options, flat result sections | single-source; no stage strip, no discovery, no three-level readiness, no stage timeline | Web | four areas (Integration & Sources / Generate & Review / Resolve Readiness / Execute & Evidence) | bUnit + browser |

## 2. Source package inventory (independent, `AIAssisted/testdata/`, read-only; Python zip reader, not BirkNext)

| Archive | SHA-256 (prefix) | Files | Role evidence (projects / folders) | Contracts | XSD (Skolenærvær family) |
|---|---|---|---|---|---|
| `Tiltak 2 - Sikkert digitalt økosystem.zip` | `283154f4b710` | 516 | `Buf.Meldingsutveksler.*` (Service, BusIntegration, FagKlient, KommuneKlient, SkjemaVerktoy, TestData, Demo) → **MU** | none (no OpenAPI) | `…skolenaervaer.v0.8.2.flat`, `v2.0.0.flat`, `v2.0.0`, `v0.8.2.altinn`, `v2.0.0.altinn` (test-data folder) |
| `M2LB.zip` | `22339207cd2e` | 2,361 | `MeldingsutvekslerReceiver` (MM), `SkoleService`, `Person`, `Tjeneste`, `SkoleAdapter`, … | 10 OpenAPI incl. `SkoleService/specs/001-skoleservice/contracts/rapportinntak.openapi.yaml`; `bufdata-leveranse.md` | `…skolenaervaer.v2.0.1.flat` (+ `.m2lb`) in `M2LB.MeldingsutvekslerReceiver.Skolenaervaer/Skjema` |
| `M2LB-language.zip` | `a5c083c81e63` | 2,612 | same components, translated (`AutorisasjonService`, `HendelseService`, `TjenesteService`) | same files | same `v2.0.1.flat` |
| `M2LB.Common.zip` | `6015c521dbfe` | 67 | shared library (`M2LB.Common`) — no stage | none | none |
| `Terraform-iac-meldingsutveksler.zip` | `1f956ee62ca2` | 72 | IaC (MU infrastructure) | none | `bufdir.barnevern.*` v1 (no Skolenærvær) |
| `Terraform-iac-m2lb.zip`, `Terraform-iac-m2lbm01.zip`, `Terraform-BasicOpsInfra_*.zip` | — | 14–249 | IaC only — supporting evidence, never an application stage source | none | none |

**Independent finding (real):** MU's source (Tiltak 2) knows the Skolenærvær report up to namespace `https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0(.flat)`; MM in M2LB
validates `…skolenaervaer.v2.0.1.flat`. The namespaces are disjoint: as imported, the MU → MM schema evidence is **incompatible by namespace** (MU may transport the
payload opaquely — that is a review question, not a compatibility proof). M2LB and M2LB-language are two separate projects (`m2lb`, `m2lb-language`) that both
claim every M2LB stage → **AMBIGUOUS** until a person chooses.

Stage hypotheses of the brief, checked against the inventory: Altinn → MU = Tiltak 2 (MU projects; the Altinn side is the pack's built-in Altinn app reference, no
imported Altinn source exists); MU → MM = Tiltak 2 + M2LB (namespace mismatch above); MM → Skolenærvær and processing = M2LB; Skolenærvær → Bufdata = M2LB
(draft dataset contract, writer in source, **no Bufdata consumer source**).

## 3. Gap matrix

| ID | Observed behaviour | Confirmed gap | Root cause | Risk | Required improvement | Verification |
|---|---|---|---|---|---|---|
| MS-G1 | Suite needs one ZIP with "the whole integration" | yes | `SnapshotId` single; module filter by top folder name of the scope | E2E suite silently misses MU (other repository) | versioned **Composite Source Context** referencing several snapshots (no copy, no new store) | unit + browser |
| MS-G2 | No cross-project association | yes | project = repository identity; only same-project associations exist | merging by name; leakage | per-snapshot association with project key, role, stages, evidence, confidence, approval (by, at, reason) | unit |
| MS-G3 | No stage → source discovery | yes | — | wrong sources | per-stage discovery: CONFIRMED (human approval) / CANDIDATE (one project) / AMBIGUOUS (several projects) / MISSING; newest snapshot per project, older ones history; IaC and libraries grouped separately | unit + real + synthetic |
| MS-G4 | No cross-source compatibility | yes | — | incompatible versions combined silently | per transition: XML namespace, REST contract, messaging channel, correlation fields, source version, authentication, environment → COMPATIBLE / POTENTIALLY_COMPATIBLE / INCOMPATIBLE / NOT_ASSESSED / MISSING_EVIDENCE | unit + real |
| MS-G5 | Readiness single level | yes | — | "Ready" with missing stages | A source coverage · B stage execution · C complete E2E; snapshot analysis status kept apart from integration source coverage; partial draft labelled | unit |
| MS-G6 | Stage contract tests by name substring | yes | `ContractRef.Contains(Component)` | wrong tests on a stage | stage contracts = contracts of the stage's associated snapshot + matched modules | unit |
| MS-G7 | No composite execution | yes | runner executors are Altinn-only; suite execution = flat contract test run | stitched runs claimed as E2E | composite transaction plan as a Draft Contract Testing chain (cross-contract identifier flow from documented response fields → path parameters), approved by a person; one Execution Evidence run; full vs subflow | unit + synthetic |
| MS-G8 | No asynchronous handling | yes | — | false failures / false passes | bounded observation per observer step (GET only, attempts × delay, 404/409 = not yet observed); WAITING / OBSERVING / SUCCEEDED / FAILED / TIMED_OUT / UNKNOWN_OUTCOME / NOT_OBSERVABLE | unit + synthetic |
| MS-G9 | No boundary correlation | yes | — | time-coincidence "proof" | per boundary: upstream output → downstream input (explicit field), echo check when the downstream response returns the upstream id; mismatch = WRONG_CORRELATION | unit + synthetic |
| MS-G10 | Composite evidence | yes | `integration_suite_runs` has no plan | run cannot be reopened with stage meaning | plan + kind + source versions stored with the run link | unit + browser (restart) |
| MS-G11 | Regeneration impact | partial | drift exists for contracts/tests/snapshot | approved mappings lost on new snapshot | context revisions immutable; newer snapshot of an associated project proposed (affected stages listed), never applied automatically; suite revision continues contracts | unit + synthetic |
| MS-G12 | Coverage | partial | suite lists tests | partial runs counted as E2E | per stage / scenario: GENERATED / CONFIGURED / EXECUTABLE / EXECUTED / VERIFIED + why not verified | unit |
| MS-G13 | Blockers not actionable | partial | flat prerequisites | — | blocker: reason, stage, missing prerequisite, evidence, action, owner category, severity, review status; never an action that writes externally | unit + browser |
| MS-G14 | UI single giant dropdown | yes | page design | wrong source chosen | stage strip + per-stage candidates grouped by role; IaC separate; SHA shortened with a technical view | bUnit + browser |
| MS-G15 | Period lock | NOT SUPPORTED (real) | operator-only | — | named prerequisite (owner: operator, environment scope, expected result, downstream trigger); synthetic lock only against the local synthetic service after explicit confirmation | unit + synthetic |
| MS-G16 | Bufdata delivery | NOT SUPPORTED (real runtime) | no consumer source, draft contract | — | SPECIFIED_BUT_NOT_RUNTIME_VERIFIED | real |

Implementation follows the brief's order A…V; see the implementation report.
