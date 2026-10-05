# BirkNext CI pipeline

`azure-pipelines.yml` is the required quality gate for BirkNext. It runs on
Microsoft-hosted Ubuntu and keeps the database, API, web app, and browser checks
in one sequential job because those checks share the same services and agent.
The job has a four-hour ceiling; backend tests have a 40-minute ceiling and
VSTest hang detection at five minutes.

## Required flow

1. Validate required scripts, project files, .NET, Docker, and Docker Compose.
2. Start a clean PostgreSQL Compose service, restore and validate migrations,
   build and test the backend, and verify its publish output.
3. Guard against stale Blazor scoped CSS, restore/build/test the frontend, run
   its Phase 2E aggregate coverage guard, and verify static WASM publish output.
4. Pull pinned ZAP 2.16.1 and run the passive security integration gate.
5. Start the API and web app with recorded process group IDs, check readiness,
   install Chromium once, then run Browser Runtime, axe accessibility,
   Lighthouse, Phase 2E real acceptance, and Playwright E2E gates.
6. Stop owned services, collect database logs, remove containers and volumes,
   publish test evidence and diagnostics, and create the optional tester package.

All builds, tests, migrations, security, and browser checks are mandatory. The
legacy `BUILD_BACKEND`, `BUILD_FRONTEND`, and `RUN_TESTS` variable values are
informational only; a missing or false value cannot skip quality gates. The
`PUBLISH_TESTER_PACKAGE` variable controls only the distributable package and
defaults to disabled when it is absent. The package steps require job success.

The non-real Phase 2E guard checks frontend aggregate normalization in a unit
test. The Phase 2E real acceptance check exercises the live frontend review
flow in Playwright, so both retain separate evidence. Browser Runtime,
accessibility, Lighthouse, passive ZAP, and E2E each cover distinct behavior.
Filtered gates check that at least one test is selected before execution.

## Test failure, timeout, and evidence

`dotnet test` exit codes are authoritative. Test steps do not continue on
failure. `PublishTestResults@2` runs with `always()` and publishes without
redefining test pass/fail semantics. Backend VSTest diagnostics are written to
`pipeline-diagnostics/backend-vstest.diag.log`; blame-hang detection waits five
minutes and omits large dumps. The backend step ceiling terminates a stalled
full suite and fails the job. There is no automatic diagnostic rerun that can
turn a failure into a pass.

The job ceiling is 240 minutes. Step ceilings are 40 minutes for backend tests
and passive ZAP, 30 for frontend tests, 20 for Chromium installation and
Lighthouse, 15 for Browser Runtime and axe, 10 for Phase 2E real acceptance
and Lighthouse dependency installation, and 5 for Playwright E2E. The VSTest
hang detector is independent of the backend step ceiling.

TRX files remain in per-area folders under the `test-results` artifact:
backend, frontend, frontend Playwright, browser runtime, accessibility,
Lighthouse, and security. The artifact also includes
`birknext-test-evidence-manifest.json` with build ID, commit, branch, and each
TRX path. `pipeline-diagnostics` retains service logs, VSTest logs, hang
sequence files, and PostgreSQL logs. These artifacts publish after failures.
The package artifact is published only after all required gates succeed.

The backend test project includes `coverlet.collector`, but the pipeline does
not currently request or publish coverage. Coverage is not a pass requirement.

## Containers and optional product workflows

Hosted CI explicitly requires Docker and uses the single
`CI_CONTAINER_RUNTIME=docker` setting for Compose startup/readiness, ZAP,
ZAP's test fixtures, and cleanup. Local BirkNext configuration can prefer
Podman; the CI engine choice does not couple the product to Docker. The ZAP
image is pinned to `ghcr.io/zaproxy/zaproxy:2.16.1`, and the run logs its
resolved registry digest. PostgreSQL uses the existing `postgres:16` image.
The build uses .NET 8 (`8.0.x`), Lighthouse uses Node 20 (`20.x`), browser
installation comes from the pinned Microsoft.Playwright 1.48.0 package, and
Lighthouse JavaScript dependencies install from the committed lockfile using
`npm ci --ignore-scripts`.

The normal PR pipeline does not run k6 load tests, soak/resource stability
tests, live M2LB checks, Azure Resource Graph, Event Hub, Service Bus, MFA, or
active external target scans. Their deterministic unit tests remain in the
solution suites; real environment workflows need an explicit scheduled or
manual profile, target, and limits.

## Branches and pull requests

Push triggers include `main` and the wildcard `*`, covering feature, bugfix,
numeric, and other active branch names without maintaining a temporary branch
allowlist. The YAML also declares broad PR target branches. Azure Repos Git
uses repository branch policies for PR validation rather than the YAML `pr:`
trigger; maintain a required build validation policy for protected merge
branches in Azure DevOps. That external policy cannot be verified from this
repository. Other supported providers can use the YAML PR trigger directly.

Tester packages and all test evidence are reproducible from the build's source
version. The pipeline does not run live k6 or soak workloads against shared
development or QA environments.
