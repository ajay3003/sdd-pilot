# Test execution evidence

BirkNext keeps four test-evidence questions apart:

| Dimension | Question | Owner / source |
|---|---|---|
| **Designed** | Is a test described for the requirement? | Specification acceptance scenarios (`SddTestEvidence`, state `Designed`) |
| **Source-discovered** | Does a test exist in source, and does it reference the requirement? | Source Analysis — `test.discovery.dotnet.xunit` |
| **Executed** | Did a run contain the test? | An imported result artifact — `test.execution.trx` |
| **Result** | What happened? | The same execution: execution state + result |

These dimensions are never collapsed into one "Covered" value:
- A `[Fact]` that exists has not necessarily been executed.
- A `[Theory]` that exists does not mean all of its data rows passed.
- A TRX result that exists does not mean a requirement is verified.
- A requirement ID in test source does not mean the requirement is satisfied.
- A passed linked test is evidence, not proof that the whole requirement is complete.

## Architecture

```
.NET/xUnit source ──(Source Analysis upload)──> DotNetXunitTestDiscoveryProvider ──> snapshot.TestInventory (SourceTestInventory)
TRX file ──(manual import)──> TrxTestExecutionEvidenceProvider ──> TestResultArtifactPreview (normalized executions)
                                        │
                     TestEvidenceCorrelation (shared, deterministic)
                                        │
                     SddTestEvidenceService ──> workspace SDD lifecycle (TestResultArtifacts, TestRuns, TestExecutions, TestDefinitions, Links)
                                        │
                     SddEvidenceGraphService rows ──> Implementation Review · Requirements Traceability · Quality Review
```

| Concern | Where |
|---|---|
| Source test discovery (owned by Source Analysis) | `backend/.../Services/TestEvidence/DotNetXunitTestDiscoveryProvider.cs`, run in `IqrSourceStore.AnalyzeAsync` |
| Execution-result provider interface | `ITestExecutionEvidenceProvider` (`ProviderId`, `CanRead`, `Read`); not tied to XML |
| TRX provider | `TrxTestExecutionEvidenceProvider` |
| Preview / correlation (stateless) | `TestExecutionImportService`, `TestEvidenceController` (`api/test-evidence`) |
| Shared contracts, provider registry, correlation | `shared/TestEvidenceContracts.cs` |
| Requirement identifier grammar (shared with Specification) | `shared/RequirementReferenceParser.cs` |
| Workspace evidence, current result, findings | `frontend/.../Services/SddTestEvidenceService.cs`, models in `Models/SddTestEvidenceModels.cs` |
| UI | `SddTestEvidencePanel`, `SddTestEvidenceDetail`, `SddRequirementTestEvidence` |

Stable provider ids: `test.discovery.dotnet.xunit` and `test.execution.trx`. CLR type names are never persisted.

There is **no second evidence graph and no separate test score**:
- Imported executions are `SddTestExecutionEvidence` records in the existing lifecycle.
- Their requirement references feed the same graph rows.
- Quality Review findings join the existing SDD finding list.

## Source test discovery (`test.discovery.dotnet.xunit`)

**Test projects** are identified from project metadata, never from folder names:
- `IsTestProject=true`, `Microsoft.NET.Test.Sdk`, or a framework package (`xunit.v3`, `xunit`, `NUnit`, `MSTest`) marks a test project.
- `Directory.Build.props` package references are inherited.
- `IsTestProject=false` excludes a project.
- The detection result records:
  - framework and its package;
  - test-relevant packages (TRX/coverage extensions, bUnit, Testcontainers, Mvc.Testing, …);
  - whether the TRX report extension is referenced;
  - whether discovery is supported. NUnit and MSTest are detected only.

**Tests** are `[Fact]`/`[Theory]` methods read from C# syntax (Roslyn, no compilation). For each test the provider records:
- the namespace-qualified class as runners report it (`Namespace.Outer+Inner`) and the fully-qualified name;
- the file and line;
- a declared `DisplayName` and a declared `Skip` (a source skip is not a result);
- `[InlineData]` row count and `MemberData`/`ClassData` presence;
- method and class `[Trait]`s, with `Category` values as categories;
- a SHA-256 fingerprint of the method text.

Identity is stable across snapshots: `repository::project::FQN`. The definition id hashes that identity, so `HealthEndpointTests` in several services never collide.

**Test kind** is assigned conservatively:
1. A declared category (`Unit` / `Integration` / `Contract`) makes it Confirmed.
2. Otherwise a bUnit package makes it **FrontendComponent**: rendered components, not browser E2E.
3. Otherwise the last project-name token (`Unit`, `IntegrationTests`, `Contract`, …) makes it StronglySupported.
4. Otherwise it is **Unknown**.

Testcontainers or Mvc.Testing alone never classify a test. Using Testcontainers does not make a test a runtime QA-environment test.

**TRX configuration evidence** is recorded too:
- a `Microsoft.Testing.Extensions.TrxReport` package reference;
- `dotnet test --report-trx`, `--logger trx`;
- `PublishTestResults` steps that publish `*.trx`.

This is configuration evidence. A pipeline that publishes TRX is not an imported execution.

Limitations (stated on the inventory):
- custom attributes derived from `FactAttribute`, inherited test methods and dynamic theory rows are not resolved;
- the bundled C# parser predates C# 12 collection expressions. When a file fails to parse, simple single-line `[a, b]` expressions are rewritten on the same line to `new[]{a, b}` and the file is re-parsed. Line numbers are preserved. Files that still parse partially are listed.

### Requirement and acceptance-criterion references

Identifiers use the **same grammar as Specification parsing** (`RequirementReferenceParser`):
- compact Spec-Kit forms such as `FR1` → `FR-001`;
- project IDs such as `JIRA-123`, `US-A1`, `ABC-4`, without a global prefix list. `FR-` is not hard-coded.

Confidence is determined by placement:

| Placement | Confidence | Links? |
|---|---|---|
| `[Trait("Requirement" \| "Requirements" \| "RequirementId" \| "Req" \| "Specification" \| "Spec" \| "UserStory" \| "Story", …)]` on the method or its class (AC: `AcceptanceCriterion`, `AcceptanceCriteria`, `AC`, `Scenario`, `AcceptanceScenario`) | Confirmed | yes |
| Documentation comment of the test method | StronglySupported | yes |
| Comment on the method or in its body that **starts with** the identifier (`// FR-023: …`, `// Requirement: FR-023`) | StronglySupported | yes |
| Identifier mentioned mid-sentence in a method comment, in a string in the body (assertion message or test data), or in the test class's comment | Inferred | no — shown as a candidate to confirm |
| Method name, file name, word similarity, skip reason, display name, helper methods, other methods, non-test projects | — | never |

Only Confirmed/StronglySupported references whose identifier exists in the current specification create graph links. Unknown identifiers are retained on the definition but link nothing.

AC references are captured only when explicit (an AC trait, or an `AC-` identifier). They are never derived from requirement-level references.

## TRX execution import (`test.execution.trx`)

TRX is the first execution provider because it is what the analysed repositories actually produce:
- **M2LB** runs `dotnet test --report-trx` and then `PublishTestResults@2` on `**/*.trx`.
- **M2LB.Common** writes `test-results.trx` and publishes it the same way.

The parser is framework-independent. xUnit, NUnit and MSTest results all import; only source correlation is framework-specific.

**Supported writers** (both verified with real output from a generated xUnit v3 project):

| Writer | `TestMethod name` |
|---|---|
| VSTest TRX logger | bare method name |
| Microsoft.Testing.Platform TrxReport | `Namespace.Class.Method(args)`, or a declared display name |

Only a name that provably starts with the class becomes a fully-qualified name; a display name stays a display name. A theory row label (`value: 1, expected: 1`) is kept per execution.

**Outcome normalization** keeps execution state and result separate. A non-pass is not automatically a failure:

| TRX outcome | Execution state | Result |
|---|---|---|
| Passed | Completed | Passed |
| Failed | Completed | Failed |
| Inconclusive, Warning | Completed | Inconclusive |
| PassedButRunAborted | Completed | Passed |
| NotExecuted, NotRunnable (xUnit skips are written as NotExecuted) | NotExecuted | Unknown |
| Timeout | TimedOut | Unknown |
| Aborted | Aborted | Unknown |
| Error, Disconnected | ExecutionFailed | Unknown |
| InProgress / Pending | Running / Queued | Unknown |
| anything else | Unknown | Unknown |

**Run-level semantics:**
- Counts are parsed from the individual results. `Total − Failed` is never assumed to be Passed.
- The writer's own counters are kept and compared. The VSTest logger, for example, counts a skipped test as neither executed nor not-executed.
- An aborted run does not make missing tests Failed. Tests without a result row have no execution evidence in that artifact.

**Security** — TRX is untrusted XML:
- DTDs are prohibited; there is no resolver, no entity expansion, and no external file or network access.
- The file size is bounded (default 20 MB), as are the result count (default 50,000) and message, stack-trace and output lengths (`TestEvidence:*` configuration).
- Only `.trx` is accepted, one explicit file per request. There is no ZIP or path input.

**Redaction** — machine names, run users, `codeBase`/`storage` paths and deployment roots are never retained:
- Absolute paths in messages and stack traces are reduced to file names.
- Credential-like text is scrubbed by the shared `SensitiveDataRedactor`.

**Storage decision:**
- The raw TRX is not retained, because it holds machine paths, user names and unbounded output.
- The normalized evidence and the SHA-256 artifact fingerprint are retained.
- Malformed input is `InvalidArtifact`, shown as a file problem, never a test failure.

## Correlation

`TestEvidenceCorrelation` matches strongest identity first:
1. Fully-qualified test name.
2. Class + method name.
3. A display name declared in source for that class.

Additional rules:
- When several projects contain the same name, the test assembly file name decides if it names exactly one project.
- More than one candidate is **Ambiguous** and is not linked.
- No match is **Unresolved**; the execution is still kept.
- No inventory means **NotAssessed**.
- There is no fuzzy or substring matching.

Linking is inherited only through correlation:
- An execution receives requirement and AC references only from a **Confirmed** correlation, and only the Confirmed/StronglySupported references of that source test.
- Inferred references become `CandidateRequirementReferences`. They are visible for review and never link.
- A test without references imports as execution evidence without a link. That is valid.

Theories:
- One source `[Theory]` is one logical definition with one execution per data row.
- The rows are aggregated for display (`1 of 3 failed`) without discarding individual rows.

## Source snapshot binding, history and currentness

**Binding.**
- A snapshot selected at import is used to correlate identities. It is **not** a claim that the results came from that source.
- The run is bound to that source version only when the user confirms it: "These results were produced from the selected source snapshot".
- Build reference, commit and environment are recorded as Provided or Unknown.
- The TRX agent machine is never mapped to a Target Environment.
- An unbound import is valid historical evidence with source currentness `NotAssessed`.

**Immutability and dedupe.**
- Every imported execution is immutable. A later run adds evidence and never rewrites an earlier Passed into Failed.
- Dedupe happens at three levels:
  - artifact fingerprint ("Already imported");
  - provider run id (a re-serialized file of the same run);
  - provider result id per row.
- Several TRX files of one build are grouped by the explicit build reference, not by import time.

**Current relevant result** (explainable):
- Potentially stale and historical executions are excluded.
- A run bound to the currently selected source snapshot wins.
- Otherwise the latest imported run is used, and the reason says the source version is unknown.
- Earlier executions stay in the history table.
- A test absent from an artifact has "No execution evidence". It is never called NotExecuted, because the artifact does not prove the test was intended to run, and the scope of a single-project TRX is respected.

**Change handling.**

| Change | Effect on evidence |
|---|---|
| New snapshot of the same repository; test unchanged | Old definition → Historical |
| Test source fingerprint changed | Old definition → PotentiallyStale; its executions → PotentiallyStale (results kept) |
| Test no longer found | Old definition → PotentiallyStale ("no longer resolves"); executions retained as history, not current verification |
| Snapshot of another repository | Nothing changes (multi-repository workspaces keep repositories apart) |
| Requirement/AC changed (existing reconciliation) | Linked executions → PotentiallyStale |

## Consumers

- **Implementation Review** — the per-requirement table shows Test design | Source test | Latest relevant execution | Result | Currentness. Rows expand into the current-result reason, the history and candidate links. The import panel, run history and test detail live below it. The generic structured JSON import remains available.
- **Requirements Traceability** — the same dimensions from the same graph. Result rows of tests without references are counted rather than listed one by one as orphans.
- **Quality Review** — deterministic findings, under shared policy:

  | Finding | Severity | Meaning |
  |---|---|---|
  | `CurrentLinkedTestFailed` | Failed (quality evidence) | A linked test's current run, bound to the selected source, failed |
  | `LinkedTestFailedSourceUnknown` | NeedsReview | The failure comes from a run whose source version is unknown |
  | `TestResultSourceUnknown` | Information | — |
  | `LinkedSourceTestWithoutExecutionEvidence` | NotAssessed | A coverage gap, not a failure |
  | `RequirementTestLinkUnresolved` | NeedsReview | Only an informal mention exists |
  | `StaleSourceTestEvidence` | NeedsReview | — |

  Each finding links to the exact execution (run, artifact, source provenance) or source definition. Import and provider limitations never become project failures.

"Test evidence coverage" means requirement-to-test linking. It is not code coverage, which is not imported.

## API

| Endpoint | Purpose |
|---|---|
| `GET api/test-evidence/providers` | Provider capabilities (per dimension) |
| `GET api/test-evidence/source-tests?environmentId&snapshotId` | A snapshot's source test inventory |
| `POST api/test-evidence/results/preview` (multipart `file` + optional `environmentId`, `sourceSnapshotId`, `sourceBindingConfirmed`, `buildReference`, `commitReference`, `environmentReference`) | Validate, normalize and correlate; stores nothing |

Import, run listing, run detail and execution detail are workspace-lifecycle operations. They are persisted by the existing workspace autosave (`sdd_lifecycle_json`) and survive navigation and reload.

Older workspaces load unchanged with empty test-evidence lists. Snapshots analyzed before discovery existed report "analyzed before source test discovery existed".

## Provider support (truthful)

| Capability | Status |
|---|---|
| TRX execution import | **Supported** — manual `.trx` import (ArtifactImport Full; SourceCorrelation Partial; RequirementCorrelation Partial; LiveRetrieval Unsupported; PipelineRetrieval Planned) |
| xUnit source test discovery | **Partial** — Fact/Theory/Trait/references from syntax; see limitations |
| NUnit / MSTest source discovery | Unsupported (detected; their TRX results import uncorrelated) |
| JUnit XML import | Unsupported |
| Playwright | Unsupported (none found in the analysed sources) |
| Azure DevOps automatic retrieval | Not implemented |
| Code coverage import | Not implemented |

The technology registry has a *Test frameworks and results* area and a *Result import* dimension (`test.xunit`, `test.nunit`, `test.mstest`, `test.trx`, `test.junit`, `test.playwright`). Source Analysis detects these from project and pipeline content.

## M2LB pilot findings (generic providers, no M2LB-specific code)

**M2LB (`M2LB (2).zip`):**
- **Test projects:** 23, across Autorisasjon, CdcReplay, Frontend, Hendelse, HendelseAdapter, Person, PersonAdapter, Proxy, Revisjon and Tjeneste. All are xUnit v3 with the TrxReport extension; many add Testcontainers.
- **Tests:** 967 discovered (919 `[Fact]`, 48 `[Theory]`). This equals every line-start `[Fact]`/`[Theory]` in the archive.
- **Kinds:**
  - Contract: `*.ContractTests`, `*.Contract.Tests`.
  - Integration / Unit: from project names.
  - FrontendComponent: `M2LB.Frontend.Tests` (bUnit).
  - Unknown, because their names state no kind: `CdcReplay.Tests`, `Person.Api.Tests`, `Person.Application.Tests`, `Person.Domain.Tests`.
- **Traits:** only `Category` (Unit, Integration, Contract, Security, SlowIntegration, WCAG). There are no requirement traits, so no reference is Confirmed.
- **References:**
  - 21 are StronglySupported: structured comments such as `// FR-023: …` in `InnmatingServiceTests`, `AC-00x`/`US-002` comments, and `SC-006`/`SC-005` in body comments.
  - 459 are Inferred: class doc comments ("Rules enforced (FR-026, PP-03)"), prose comments and assertion messages, plus test-data strings such as `BIRK-0001`. These link only if they equal a current requirement ID.
- **Pipeline:** `.pipeline/runtests.yml` generates TRX per project and has 10 `PublishTestResults@2` TRX publications.
- **Playwright:** none.

**M2LB.Common (`M2LB.Common (1).zip`):**
- 1 xUnit v3 test project with 113 tests.
- `runtests.yml` writes `test-results.trx` and publishes it.
- No requirement identifiers in its tests.
- Discovery is Partial only because two files use newer syntax; all tests are still found.
- The same providers apply, with zero Common-specific branches.

Multi-repository: each snapshot's inventory carries its repository name, and test identities are prefixed with it.

The real M2LB tests cannot run on this machine: they target net10 and only .NET 9 SDKs are installed. The pilot TRX for the end-to-end smoke was therefore written in the Microsoft.Testing.Platform TRX shape, using real M2LB test identities. Its structure matches TRX produced by real runs of a generated xUnit v3 project.

## Future providers

- **`AzureDevOpsTestExecutionProvider`** — build/run → test run → TRX attachment or test results → the same `NormalizedTestExecution` records. Build id, test run id, result id, source commit and environment map onto existing fields, without a schema rewrite.
- **Playwright** (JSON/JUnit) and **JUnit XML** — when a project produces them, add a concrete `ITestExecutionEvidenceProvider`.
- **NUnit/MSTest source discovery** — a second discovery provider next to the xUnit one. Correlation is already framework-neutral.
- **Code coverage** — a separate evidence type. It is not test evidence coverage.
