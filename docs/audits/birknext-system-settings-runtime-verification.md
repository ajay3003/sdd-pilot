# System Settings — runtime verification (task 12)

## DeveloperContainer: VERIFIED (read-only, isolated)

Setup:
- An isolated Development API on :5496 serving a published Release frontend on :5497.
- Disposable database `birknext_bt_20261111` on the existing Podman PostgreSQL. No container was restarted or changed.
- Every expected value was read independently from `api/system/runtime` and `api/system/build-info`, then compared with the page.

| Fact | API | Page (Overview / owning card) | Result |
|---|---|---|---|
| Profile | `Developer (containers)`, source = source-checkout default | Runtime tile "Developer (containers) · Source checkout / developer build"; card "cannot be changed while BirkNext runs" | match |
| PostgreSQL | `Connected`, `16.13 (Debian 16.13-1.pgdg13+1)` | "PostgreSQL 16.13 connected" | match |
| Schema | 4 contexts, all Current | "schema: 4 of 4 contexts current" | match |
| k6 | `Available`, 1.0.0, k6 container (Podman) | "k6 1.0.0 available · k6 container (Podman)" | match |
| Build | backend channel Local, source dirty | "BirkNext 1.0.0 · Local development build · frontend/backend compatibility not assessed" | match (a dirty build is never reported as compatible) |
| Configuration | page evaluation: 0 failures, Git Sources prerequisites open | "No failures · Git Sources not set up" (amber, not green) | match |

After the API process was restarted (verified PID, our scratch `BirkNext.Api.dll` only), all 23 checks passed again. The profile, database and
k6 facts stayed the same. Requests per page load: `build-info` 1, `runtime` 1.

## TesterNative: NOT RUN in this task

- No genuine Tester Package was installed and running for this task, so the native PostgreSQL, native `k6.exe` and no-Podman checks were not
  repeated here.
- Coverage:
  - The tester tile wording ("Tester (native Windows)", "Windows Tester Package", bundled `k6.exe`) is covered by the unit test
    `SettingsOverviewTests.A_tester_installation_names_its_profile_and_bundled_runner`.
  - Profile detection is covered by the runtime-profile tests from the Windows-package task.
- No claim of tester runtime acceptance is made.

## Not verified

- An unreachable PostgreSQL and a missing k6 image were covered by component tests only. The real Podman services were not stopped to produce
  these states (no Podman changes are allowed).
