# BirkNext — self-contained Windows tester distribution: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `2a8291c2`.
- **Method:** read-only inspection of HEAD, plus binary inspection of the candidate runtime archives (PE import tables, without running anything).
- **Labels:** CONFIRMED (verified in code or by a run); INFERRED (reasoned, not run); NOT VERIFIED; BLOCKED.

## 1. Existing tester package (before this work)

| Component | Location | Behaviour | Container dependency | Status |
|---|---|---|---|---|
| Launcher | `scripts/start-local.ps1` (1,405 lines) + `start-local.bat` | Source and packaged modes; port preflight; migration integrity preflight; starts the backend DLL with `dotnet`; serves wwwroot with `dotnet-serve` on 5173 | **Mandatory**: PostgreSQL via `podman compose` / `docker compose` (`AIAssisted/docker-compose.yml`, `postgres:16`). Starts or restarts the Podman machine itself | CONFIRMED |
| .NET | packaged backend is framework-dependent | Requires an installed .NET 8 runtime (`Test-DotNetRuntime`) | — | CONFIRMED |
| Frontend host | `dotnet tool install --global dotnet-serve` | **Downloads a global tool from NuGet at first start** | — | CONFIRMED |
| Package build | `azure-pipelines.yml` (`BuildAndPackage` on ubuntu, `SmokeTestTesterPackage` on windows) | Publishes framework-dependent output; `package-manifest.json` (format 2 hashes); `build-manifest.json`; `BirkNext-<version>.zip` + `.sha256` | — | CONFIRMED |
| Mutable data | `<backend>/App_Data/*` (Test Data, Critical E2E, local session, credentials, backups, transfer, parquet, sign-in settings) | **Inside the package folder**; an upgrade into a new folder loses it | — | CONFIRMED (15 call sites) |

## 2. Container and runtime dependencies (all mandatory runtime dependencies examined)

| Dependency | Where | Class | Tester package before | After |
|---|---|---|---|---|
| PostgreSQL 16 container | compose, `start-local.ps1` | Database, mandatory | Podman/Docker required | Bundled native PostgreSQL 16.13 |
| k6 container `docker.io/grafana/k6:1.0.0` | `K6PerformanceTestProvider` via `PodmanContainerExecutionRuntime` | Performance tests, optional at startup | Podman required for any run | Bundled `k6.exe` via `NativeProcessExecutionRuntime` |
| Podman CLI for resource stats | `PodmanResourceObservationProvider` | Optional (Resource Stability) | Podman | Not available natively: stats of approved target containers report an error; reported as unavailable, not zero |
| ZAP passive scan, Lighthouse, Playwright | Frontend Quality engines | Optional modules, off by default | Unchanged | Unchanged (not part of TesterNative startup) |
| Testcontainers / test-only PostgreSQL | test projects | Test-only | — | — |
| `dotnet` runtime | packaged backend | Mandatory | Installed .NET 8 | Self-contained win-x64 |
| `dotnet-serve` | frontend host | Mandatory, downloaded | NuGet download | The launcher's own static host |

## 3. PostgreSQL compatibility (CONFIRMED)

- **Developer container:** PostgreSQL 16.13 (Debian 16.13-1.pgdg13+1), UTF8, `en_US.utf8`. The only extension is `plpgsql`.
- **Schema:**
  - 4 DbContexts (Application, Delivery reporting, Canonical interpretation, Snapshot registry);
  - 78 migrations (68 / 6 / 2 / 2), with separate history tables per schema;
  - applied at startup under `pg_advisory_lock` (`DatabaseMigrationLock`).
- **Code:** no `CREATE EXTENSION` and no `HasPostgresExtension`. Npgsql EF Core 8.
- **Native build:** EDB PostgreSQL 16.13 Windows x64.
  - It contains `postgres`, `initdb`, `pg_ctl`, `pg_dump`, `pg_restore`, `pg_controldata`, `psql`, `pg_isready` and `pg_upgrade`, plus ICU 67 and OpenSSL 3.
  - PE imports: `VCRUNTIME140.dll` and `MSVCP140.dll` (Visual C++ 2015–2022), and the UCRT (`api-ms-win-crt-*`), which is part of Windows 10+.
  - The VC++ runtime is **not** in the archive, so it is a stated prerequisite.

## 4. k6 compatibility (CONFIRMED unless marked)

- **Version and binary:** the image is k6 1.0.0; the native binary is k6 v1.0.0 (go1.24.2, windows/amd64). Its only PE import is `kernel32`.
- **Usage:**
  - BirkNext generates the script (no user scripts) and uses `handleSummary` JSON, the `--out json` NDJSON stream, `k6/http`, `k6/metrics` and `k6/execution`;
  - executors are `ramping-vus` and `ramping-arrival-rate`;
  - **no extensions (xk6) and no browser module**.
- **Container-specific assumptions:**
  - mount paths `/birknext/in`, `/birknext/out` and `/birknext/ca/ca.pem`;
  - the host-gateway alias `host.containers.internal` for loopback targets;
  - Podman networks for container targets;
  - `SSL_CERT_FILE` for CA bundles;
  - the image's own CA store (`/etc/ssl/certs`);
  - SIGTERM graceful stop;
  - cgroup limits.
- **Native equivalents:**
  - host paths;
  - the target as configured;
  - no networks (not applicable);
  - **`SSL_CERT_FILE` is ignored by Go on Windows (INFERRED from Go's `crypto/x509` Windows verifier)**, so a configured CA bundle is refused rather than silently ignored;
  - no image CA store;
  - no graceful stop;
  - no cgroups (reported).

## 5. Security constraints found

- The local-session gate (launch code then an HttpOnly cookie) and `LocalRequestGuard` (loopback Host, CSRF, Origin) apply to every API. The TesterNative backend keeps them.
- **Active tests are not authorized in a Production backend:**
  - `LocalTrustedExecution` was Development-only, so the existing tester package (Production) could not start a performance run without Entra (CONFIRMED by a 403 in acceptance);
  - resolved by product-owner decision (TesterNative honours the setting under the same checks);
  - `Authorization:LocalConfigurationWrites` remains Development-only (gap SC-G7).
- The existing package shipped `AIAssisted/.env` with development credentials (`birknext`/`birknext`). The TesterNative package has no `.env` and generates per-user credentials.

## 6. Pipeline gaps (CONFIRMED)

- There was no Windows self-contained build, no native runtime acquisition and no TesterNative archive.
- The pipeline's Windows smoke job validates the existing package only.
- The pipeline is not triggered in this work.

## 7. Clean-PC prerequisites

- Windows 10 1809+/11 x64 and the VC++ 2015–2022 x64 redistributable.
- No Podman, Docker, WSL2, Hyper-V, .NET, PostgreSQL or k6.
- A clean VM is **not available** in this environment (BLOCKED; see acceptance).

## 8. Confirmed implementation gaps (all addressed in this work unless listed in the gap register)

1. No native PostgreSQL lifecycle.
2. No native k6 runner.
3. A framework-dependent backend.
4. A runtime download of `dotnet-serve`.
5. Mutable data inside the package.
6. No runtime profile.
7. No upgrade backup.
8. No package for TesterNative.
9. No runtime diagnostics in System Settings.
