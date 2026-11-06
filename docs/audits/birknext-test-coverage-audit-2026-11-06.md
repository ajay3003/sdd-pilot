# BirkNext Independent Audit â€” Test Coverage and Execution

## Test project inventory

| Project | Framework / role | Source evidence | Audit execution |
|---|---|---|---|
| `AIAssisted/backend/BirkNext.Api.Tests` | .NET 8 xUnit; API/controller/service tests; MVC test host; EF Core InMemory and Testcontainers PostgreSQL package. | `BirkNext.Api.Tests.csproj` | Focused LocalSession filter attempted against existing Debug and Release assemblies; **no matching tests**. No suite pass/fail result. |
| `AIAssisted/frontend/BirkNext.Web.Tests` | .NET 8 xUnit + bUnit component and service tests. | `BirkNext.Web.Tests.csproj` | Not run. |
| `AIAssisted/frontend/BirkNext.Web.PlaywrightTests` | Playwright-based browser tests; includes/excludes selected tests based on MSBuild properties. | `BirkNext.Web.PlaywrightTests.csproj` | Not run; no browser/app launched. |
| `AIAssisted/frontend/BirkNext.RealProjectAcceptance` | Acceptance data/results referenced by web and Playwright tests. | Project referenced from both test csprojs. | Not run; no real target data accessed. |

## Attempted command and result

Commands attempted sequentially:

```text
dotnet test AIAssisted/backend/BirkNext.Api.Tests/BirkNext.Api.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~LocalSessionSecurityTests --verbosity minimal
dotnet test AIAssisted/backend/BirkNext.Api.Tests/BirkNext.Api.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~LocalSessionSecurityTests --verbosity minimal
```

Both test runners returned **no matching test cases**. This is not a passing result and indicates the prebuilt assemblies do not contain the current source test class. The current source class is present at `AIAssisted/backend/BirkNext.Api.Tests/Controllers/LocalSessionSecurityTests.cs`.

No test/build command was run after the no-match result. Rebuilding would generate outputs and consume additional resources; the checkout has pre-existing in-flight work and historical reports document memory-partitioned full backend runs. The user requested exact results, so current audit results are recorded as **NOT RUN**, not inferred from historical report counts.

## Coverage observations

- Test source includes direct local-session and authorization cases, import idempotency cases, many bUnit tests and Playwright scenarios. This establishes that tests are authored, not that they currently compile or pass.
- Backend test project references `Microsoft.AspNetCore.Mvc.Testing`, EF Core InMemory and Testcontainers PostgreSQL. Presence of Testcontainers does not mean all integration tests use a real PostgreSQL instance.
- Playwright tests conditionally remove pre-started and real-project acceptance tests unless specific MSBuild properties are set. Default test execution therefore is not evidence that those acceptance flows ran.
- Historical reports list large successful partitions and browser runs. They are historical evidence and were not repeated in this audit.
- The checkout changed during the audit through commits made outside this audit; prebuilt assemblies may not represent current source.

## Coverage gaps requiring a reproducible run

1. Build current source and run the LocalSession security tests.
2. Run backend tests in memory-safe partitions, capturing per-partition pass/fail/skip and build identity.
3. Run frontend unit/component suite against the current source.
4. Run Playwright only against a controlled local environment and explicitly document excluded test categories.
5. Add/verify producer-consumer tests for import â†’ current snapshot â†’ canonical relation/review â†’ release/report freshness.
6. Verify project/environment scope, partial failures, cancellation and stale async response cases across high-impact workflows.

No test coverage percentage is claimed. See [BQA-003](birknext-gap-register-2026-11-06.md#gap-register), [BQA-004](birknext-gap-register-2026-11-06.md#gap-register), and [BQA-006](birknext-gap-register-2026-11-06.md#gap-register).
