# Active Target Environment resolution

Verified on 2026-09-16 against base commit `0f48c35` (`008-traceability-first`).

## Diagnosis and model

The existing browser's original active ID was not inspected. At the user's direction,
verification used deterministic persisted profiles instead. No claim is made that DEV
was active in the original browser session.

Storage already has one authoritative `activeProfileId` in browser localStorage key
`birknext:frontend-analysis-settings`. Settings UI selection is the separate, transient
`_selectedProfileId`. The following incorrect behaviors were confirmed in source:

- The context factory substituted the first profile when active lookup failed.
- The settings active summary/badge made the same substitution.
- Deleting an active profile activated the first remaining profile.
- Startup seeded an active Local profile, including after loading an empty store.
- Sample-project hint application could choose the first profile or activate a new one.
- Review execution reused its landing context and retained references to mutable settings.

All activity substitutions were removed. Exact stable-ID lookup accepts exactly one
matching profile; missing IDs and duplicate IDs never resolve by order. Conflicting
legacy `IsActive` markers produce an explicit persisted configuration error, retained
even across unrelated saves, until the user explicitly sets one target active. Single
legacy flags do not override the authoritative ID or infer activity when it is absent.

Set as Active persists a valid saved target without requiring detection to succeed.
This action does not grant authentication, assert reachability, or change engine
readiness. Detection/manual-verification evidence and execution checks remain separate.
Storage failures restore the previous active ID and show an error. Duplicate/reset
preserve activity; deleting active clears it. Seed profiles remain inactive.

The context factory detaches configuration before asynchronous work. Review start
resolves the active target again. Completed reports retain an immutable, serializable
identity (ID/name/type/URL/start time); HTML export and the printable PDF view use it.

## Automated verification

- Focused frontend: **138 discovered, 138 passed, 0 failed, 0 skipped**.
- Full `BirkNext.Web.Tests`: **2,790 discovered, 2,790 passed, 0 failed, 0 skipped**.
- Release `BirkNext.Web`: **0 errors, 17 existing warnings, 0 new warnings**.
- Backend/shared source was not changed. No backend/shared test suite was required.
- Static Security, Passive Performance, preflight HTTP/DNS handling, engine capability
  rules, and release policy implementation were not changed.

TRX and build logs are in `BirkNext.Web.Tests/TestResults/active-target-*.trx` and
`TestResults/active-target-*.log`. The first attempted baseline was blocked by an
untracked stale scoped CSS bundle. That file was preserved outside the repository at
`../active-target-stale-styles.css` relative to the repository root. Initial sandboxed
NuGet/test-host failures were resolved by running restore/tests outside the sandbox.

## Real browser acceptance

`scripts/active-target-acceptance.cjs` starts isolated frontend/backend processes on
5174/5002. It uses real backend responses and the real M2LB DEV URL. Browser routing
only directs application configuration and hard-coded localhost admin requests to
the isolated backend; review responses and target reachability are not mocked.
The original user's browser profile and running app processes are untouched.

Persisted fixture order and initial activity:

| Order | Stable ID | Name | Type | URL | Initially active |
| --- | --- | --- | --- | --- | --- |
| 0 | qa | QA | QA | https://example-qa.local | Yes |
| 1 | dev | M2LB DEV | Development | https://m2lbdev.bufetat.no/ | No |

Passed sequence:

1. Select DEV, click Set as Active, verify active state and FQR target.
2. Run real review: reachability, security scan, and asset discovery requests all use
   M2LB DEV. No request uses the QA placeholder; no QA hostname failure is displayed.
3. Select QA without activation: FQR remains on DEV.
4. Explicitly activate QA: FQR resolves QA.
5. Restore DEV, persist browser storage, stop both isolated servers and browser context.
6. Start new server processes and a new browser context from persisted storage:
   FQR still resolves DEV.

Evidence: `TestResults/active-target-runtime/acceptance.json`, `dev-active.png`,
`dev-review.png`, and `dev-after-restart.png`.

The separate component tests suspend an actual FQR run at its orchestration boundary,
change active target and edit DEV, then verify completion/export stays bound to the
original DEV snapshot and the next run uses QA. They also cover selected DEV/active
QA, no active, invalid active, failed activation persistence, and active badges.
