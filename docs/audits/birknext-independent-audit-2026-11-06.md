# BirkNext Independent Quality Audit â€” 2026-11-06

## Scope and evidence rules

This is an independent, source-first audit of the checkout at `008-traceability-first`. It uses current executable source and test source as evidence. Historical hardening reports were read as change history and leads, not as proof that current behavior works.

The checkout had pre-existing modifications and untracked work at audit start. These were preserved. In particular, no contents of `AIAssisted/backend/BirkNext.Api/appsettings.Local.json` or `AIAssisted/testdata/` were read. At initial inspection, Security Classification files were modified and Manual Review files were untracked. During the audit, the branch advanced and those changes became committed by work outside this audit. The affected feature set was not exhaustively re-audited after those commits, so this report does not claim those changes are fully verified.

No production, test, configuration or database files were changed. Only the six requested audit Markdown reports were created. No services, containers, browser automation or external integrations were started.

## Baseline

- Branch: `008-traceability-first`, tracking `ado/008-traceability-first`.
- Initial HEAD: `91c1a101`. During the audit, the branch advanced through commits made outside this audit to current HEAD `546584f1` (`Master Hardening: gap register and final report`). The current source security and Project Import anchors were rechecked after that advance.
- Worktree was not clean. It contained modified API/frontend/shared files, an untracked Manual Review API/UI/shared implementation, and the excluded local settings/testdata paths. See the session baseline; the audit did not stage or alter them.
- Main implementation: Blazor WebAssembly (`AIAssisted/frontend/BirkNext.Web`), ASP.NET Core API (`AIAssisted/backend/BirkNext.Api`), shared contracts, PostgreSQL via four EF Core contexts, plus a separate `ManualLearning` solution.
- Discovered projects include API tests, web tests, Playwright tests, and Real Project Acceptance. The web contains 62 `@page` directives; the API has 78 controller files and 98 migration source files. These are inventory counts, not proof every route or migration was behaviorally tested.

## Executive assessment

**Quality assessment: substantial and recently hardened local QA application; broad functional test source exists, but this audit did not establish current full-suite health or end-to-end correctness.** The most important status update is that the previously reported anonymous REST/GraphQL boundary is no longer the current source state: the API now registers a local session scheme and applies `LocalRequestGuardMiddleware` to `/api` and `/graphql`. Loopback binding is also checked at startup. Source and test code support these controls; current binaries did not contain the named focused security test, so the controls are **not test-run-confirmed in this audit**.

No new confirmed product defect was established within the bounded source pass. This is not a claim that the application has no defects: complete visual, route-by-route, all-module call-chain and runtime verification did not fit this pass. The gap register records those limits separately from product findings.

| Category | Audit result |
|---|---|
| New confirmed defects | 0 established in this bounded pass |
| Regressions | 0 established; full regression suites were not run |
| Previously reported gaps | Import retry/idempotency concern is now addressed in source and has test source (execution not verified here); other historical issues are not reclassified as fixed without current source review |
| Unverified risks / conditional architecture risks / coverage limits | 7 tracked in the central gap register, including launch binding configuration coverage, credential file ACL assumptions, workspace identity scope, incomplete module/runtime review, and stale test binaries |
| Severity distribution | No confirmed product finding assigned P0â€“P3; unverified/coverage items are tracked separately |

## What is verified in current source

### Local API security boundary

`Program.cs` validates configured `urls` and `Kestrel:Endpoints:*:Url` through `LoopbackBinding`, registers a local session service and authentication scheme, then places `LocalRequestGuardMiddleware` between authentication and authorization. Controllers and GraphQL are mapped after the guard. See [Program.cs:76â€“95, 108â€“117, 919â€“927](../../AIAssisted/backend/BirkNext.Api/Program.cs) and [LocalRequestGuard.cs:30â€“96](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs).

The guard requires a loopback Host for `/api` and `/graphql`, authenticates either the local session cookie or a bearer token, requires exact frontend Origin plus a client header for cookie-authenticated unsafe calls, and confines browser extension origins to the companion extension route. The extension route is exempted from the central session requirement because it has its own loopback/origin/pairing validation; this exception is visible in source and must remain covered by dedicated tests.

The session exchange is explicit: `POST /api/local-session` checks frontend Origin and the custom client header, exchanges the launch code, and sets an HttpOnly, SameSite=Strict cookie. The session service stores only session-token hashes in memory and enforces idle and absolute lifetime; the launch code itself is persisted under the API content root. See [LocalSessionController.cs:14â€“56](../../AIAssisted/backend/BirkNext.Api/Controllers/LocalSessionController.cs) and [LocalSessionService.cs:14â€“130](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs).

### Project import idempotency

The current Project Import service computes import identity from archive SHA-256, serializes commits per staged import, returns a settled prior result on repeat commit, and reuses a current source snapshot when the same archive remains current. Test source asserts same-archive reuse and preservation of prior history for a new version. See [ProjectImportService.cs:29â€“45, 95â€“105, 127â€“143](../../AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs) and [ProjectImportServiceTests.cs:212â€“240](../../AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs). This is **source-confirmed**; its tests were not executed in this audit.

### Broad module/test inventory

The frontend route inventory includes import, source management/analysis, explorers, traceability, relationship review, delivery, API/integration/frontend/performance reviews, Schema Studio, Test Data, M01, and SkolenÃ¦rvÃ¦r workflows. The list of route directives is discoverable in `AIAssisted/frontend/BirkNext.Web/Pages`; it is not a user journey coverage guarantee.

Test projects are:

- `AIAssisted/backend/BirkNext.Api.Tests` â€” .NET 8, xUnit, MVC test host, EF Core InMemory, Testcontainers PostgreSQL package.
- `AIAssisted/frontend/BirkNext.Web.Tests` â€” .NET 8, bUnit, xUnit.
- `AIAssisted/frontend/BirkNext.Web.PlaywrightTests` â€” Playwright tests; compile inclusions are conditional on MSBuild properties.
- `AIAssisted/frontend/BirkNext.RealProjectAcceptance` â€” acceptance dataset and result models.

## Test execution record

The audit attempted the focused API test filter `FullyQualifiedName~LocalSessionSecurityTests` with `dotnet test --no-build --no-restore` in both Debug and Release. Both commands exited successfully at the test-runner level but reported **â€œNo test matches the given testcase filterâ€**. The prebuilt assemblies therefore do not provide evidence about the current test source. No tests are marked passed by this audit. No build was run to refresh those binaries; broad suites have a documented history of memory partitioning, and the checkout contains active local changes.

Status: focused local-session tests **NOT RUN**; all broader suites **NOT RUN**; current browser, accessibility, import, external-target and persistence runtime behavior **NOT VERIFIED**.

## Working well (evidence-qualified)

- The current source has a centralized API/GraphQL caller boundary rather than relying solely on controller annotations.
- Local session proof is separated from Entra identity and named capability policies; a local session does not mint those permission claims.
- Source code distinguishes source analysis from runtime evidence in the Project Import workflow and records import provenance.
- Test source is organized across backend, component, Playwright and acceptance projects; the test architecture is broader than a single unit-test layer.
- Historical reports document prior targeted test/browser runs, but those results are historical and were not independently repeated here.

## Main limitations

This pass did not visually inspect the 62 routes, run the application, execute the full automated test matrix, inspect every one of the 78 controllers or trace every module to storage. It did not exhaustively assess the Manual Review and Security Classification changes that landed during this audit. No claim about deployment, runtime readiness, external target safety, accessibility conformance or overall defect-free status follows from this report.

## Finding references

See [birknext-gap-register-2026-11-06.md](birknext-gap-register-2026-11-06.md) for stable IDs and severity/confidence. See [cross-module audit](birknext-cross-module-audit-2026-11-06.md), [security/readiness audit](birknext-security-readiness-audit-2026-11-06.md), [UI/UX audit](birknext-ui-ux-audit-2026-11-06.md), and [test coverage audit](birknext-test-coverage-audit-2026-11-06.md) for bounded evidence and unverified areas. The branch advanced during this audit through commits made outside the audit. Final HEAD is `546584f1`; local-session security and Project Import idempotency anchors were rechecked after the advance. Other changes committed during the session were not exhaustively re-audited.

## Attention order

1. Rebuild/run current focused security tests (BQA-003); existing binaries had no matching tests.
2. Verify all supported host binding inputs and the actual socket address (BQA-001).
3. Decide whether BirkNext supports multiple authenticated principals; the legacy workspace store is hard-coded to `default-user` (BQA-008).
4. Verify launch-code ACLs in the actual distribution (BQA-002).
5. Add cross-module evidence freshness/partial-failure scenarios, then complete route-to-persistence review (BQA-004).
6. Perform browser/keyboard/accessibility review after the final commit set (BQA-005, BQA-006).
