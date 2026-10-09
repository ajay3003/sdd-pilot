# Test Coverage & Overlap Review

Quality & Testing → **Test Coverage & Overlap Review** (`/test-coverage-review`) helps testers avoid repeating what developer automation
already checks, and focus on what is still unverified. It answers:

1. What is already tested?
2. What does that test actually prove — at which level and system boundary?
3. Which part of each real system journey is still not verified?
4. Is a QA test likely to duplicate existing developer automation?
5. What should testers still test?

It produces readable **Covered Tests & Remaining QA Scope** documentation (HTML and Markdown).

The rule is never “a developer test exists, so testers do nothing”. It is: *what does the developer test prove → what boundary or risk
remains → testers focus on the remaining risk*. BirkNext never removes or rejects a test; the test lead decides.

## Semantics that are never collapsed

| Kept apart | Why |
|---|---|
| Discovered ≠ analyzed ≠ executed ≠ passed | Execution is shown only from imported results (TRX). Without them: *Tests were found, but BirkNext does not have proof that they were executed.* |
| Unit test passed ≠ integration proven | Test level comes from setup evidence, never from a name. |
| Mocked/faked broker ≠ real broker verified | Each test records how it reaches each dependency: real, real engine in a container, in-process host, in-memory substitute, mocked, fake. |
| Repository test ≠ deployed database | An EF Core in-memory provider or SQLite in-memory is an in-memory substitute; Testcontainers is a real engine, not the deployed database. |
| Same name / same requirement ≠ same behaviour ≠ duplicate | Overlap needs several matching dimensions. |
| No test evidence found ≠ no test exists | Wording says what the evidence shows. |
| No gap detected ≠ complete coverage | The review never certifies coverage. |
| Code coverage % ≠ functional/risk coverage | No percentage is computed; a coverage report found in source is mentioned, not imported. |

## Evidence

All source comes from the Source Analysis snapshot — no second upload.

- **Per-test facts** are captured once at upload by `TestBehaviorSourceAnalyzer`, from C# syntax for xUnit, NUnit and MSTest. They are stored on the snapshot as `TestBehaviorEvidence`:
  - boundary signals: `WebApplicationFactory`/`TestServer`, `Mock<T>`/`Substitute.For<T>`/`A.Fake<T>`, `Fake*`/`InMemory*` types, EF in-memory, Testcontainers, real Event Hub/Service Bus clients, Wolverine in-process tracking, Playwright/Selenium, bUnit;
  - assertions: HTTP status, expected exception, record count/persisted, message published, mock invocation, value equality, snapshot, not-null only;
  - targets mapped to production projects through a type-name index and project references;
  - skip state, categories and explicit requirement identifiers (`FR-002`, `SC-001`, …).
  - Test files in other languages are counted (TypeScript/JavaScript/Python/Java/Go), never analyzed and never reported as clean.
- **Test inventory** comes from the existing xUnit discovery.
- **Journeys** are built from the Source Architecture model:
  - components, explicitly wired HTTP/GraphQL calls, Event Hub/Service Bus producers and consumers, and datastores;
  - unresolved call targets become a suggestion only when names match, otherwise *needs confirmation*;
  - channels whose producer is outside the source get an *upstream* node.
- **Requirements and acceptance scenarios** come from the workspace specification. Acceptance scenarios are the QA test candidates, together with QA-owned automated tests and Critical E2E flows of the active Target Environment.
- **Execution results** come from imported test results (TRX via the SDD lifecycle).

## Views (one evidence model)

- **Overview** — tests found/analyzed/with results, journeys, possible duplicates, gaps, needs-review. Also test project ownership, which a reviewer can declare.
- **By Journey** — each step shows developer coverage (Covered / Partly covered / Covered by mocks only / Covered at a lower level only / Not verified), the connection's evidence (Confirmed in source / Strongly supported / Suggested / Needs confirmation), QA and runtime evidence separately, and *Technical details* with provenance. Suggested connections can be confirmed or rejected; this is stored as *Confirmed by reviewer* and never replaces source evidence.
- **By Component** — Unit, Integration, Contract, API, Database, Messaging, Authorization, Error handling, Retry, Idempotency, E2E, Performance and Security, each marked Covered / Some evidence / Partly covered / Not verified / Not assessed.
- **By Requirement / Risk** — what is already tested, where, what is not verified yet, and what testers should do.
- **Possible Duplicate Tests** — high-confidence overlap needs the same behaviour and expected result at the same level and boundary, from an analyzed developer test, plus either the same requirement or strong behaviour overlap. A lower-level developer test is *Different level — not a duplicate*, and the same requirement alone *adds coverage*.
- **Remaining QA Scope** — what to keep testing, what probably does not need to be repeated at the same level, and what needs tester review.
- **Covered Tests Documentation** — preview of the export.

Exports:
- *Export Covered Tests* (HTML);
- *Export Remaining QA Scope* (Markdown, for wikis, Loop or Azure DevOps);
- *Export Full Review* (HTML);
- *Markdown* (full review).

Each run is stored bound to the exact snapshot ids it reviewed.

## Limitations

- What a test intends cannot always be proven; descriptions say what a test *appears* to check.
- Mocks and fakes are recognised by type names in C# test syntax only (no semantic model).
- Runtime coverage needs runtime evidence; Critical E2E results are shown as runtime evidence for a flow, never as proof of a specific step.
- Journey connections may need confirmation; a name match is never treated as a confirmed connection.
- Manual tests that are not in the specification (acceptance scenarios) or marked QA-owned are not seen.
- A code coverage percentage needs a real coverage report; none is imported.
