# BirkNext — self-contained Windows tester distribution: gap register (2026-11-11)

Statuses: CLOSED · OPEN · BLOCKED · NOT SUPPORTED.

| ID | Gap | Status | Note |
|---|---|---|---|
| SC-G1 | Clean-PC acceptance (VM without Podman, .NET, PostgreSQL or k6; with and without the VC++ runtime; AppLocker/WDAC) | BLOCKED | No clean VM in this environment; steps in acceptance §1 |
| SC-G2 | Pipeline job `BuildWindowsTesterPackage` never executed | OPEN | Not triggered (by rule); the YAML parses; the same script was run locally |
| SC-G3 | Binaries are not Authenticode-signed (launcher, backend, PostgreSQL, k6) | OPEN | Managed PCs may need allow rules. No signing with production certificates in this work |
| SC-G4 | Verbatim license texts of the libraries bundled in the PostgreSQL binaries (OpenSSL, ICU, libxml2, LGPL gettext/iconv, …) are not shipped | OPEN | Identifiers are listed in the SBOM and THIRD-PARTY-NOTICES. Add the texts before any distribution outside the organisation |
| SC-G5 | EDB publishes no checksum for the PostgreSQL archive | OPEN | Pinned SHA-256 is trust-on-first-use (HTTPS, official URL). An organisation-controlled artifact feed would remove this |
| SC-G6 | VC++ 2015–2022 runtime is a prerequisite, not bundled | OPEN | App-local deployment from the official redistributable is possible but not done; the launcher names it |
| SC-G7 | `Authorization:LocalConfigurationWrites` remains Development-only | OPEN | Integration configuration writes in the Tester Package need Entra; the owner decided only execution |
| SC-G8 | No `--restore` command; restore is a documented manual procedure | OPEN | Restore into an isolated cluster verified (BK-02) |
| SC-G9 | Package paths up to 121 characters; Explorer cannot extract under a deep folder | OPEN | Documented: extract to a short folder |
| SC-G10 | The frontend logs an aborted fetch (navigation away) at Error level (`RecommendedWorkflowApiService`) | OPEN | Cosmetic console noise; no functional effect |
| SC-G11 | Native k6 limitations: no memory/CPU limits, no graceful stop (no partial summary on cancel), CA bundle refused (Windows store instead) | NOT SUPPORTED | Reported in run limitations and readiness; refusing the CA bundle rests on Go's Windows verifier (INFERRED, not tested with a corporate CA) |
| SC-G12 | Resource Stability (container stats of approved targets) unavailable natively | NOT SUPPORTED | Reported as unavailable, never zero |
| SC-G13 | Collation: ICU `en-US` (native) vs glibc `en_US.utf8` (container) | OPEN | Ordering of non-ASCII text may differ; no migration depends on it |
| SC-G14 | Full verification of 2,739 files takes ~20–55 s on first start of a package build (antivirus) | OPEN | Later starts check runtime executables only |
| SC-G15 | Live DeveloperContainer start not re-run | NOT RUN | Ports 5000/5173 belong to the developer's running instance |
| SC-G16 | `AltinnTestingTests` "no 11-digit identifier" assertion can match random hex (pre-existing; failed once in the full run, 5/5 alone) | OPEN | Test owner: exclude generated hex/GUID fields instead of only the run id |

## Closed during this work

| ID | Gap | Fix |
|---|---|---|
| SC-C1 | Podman/Docker mandatory for the tester package | Bundled PostgreSQL 16.13 + `NativeProcessExecutionRuntime` |
| SC-C2 | .NET runtime and `dotnet-serve` download required | Self-contained backend and launcher; built-in frontend host |
| SC-C3 | Mutable data inside the package | `AppDataLocation` + per-user data folder |
| SC-C4 | Active tests impossible without Entra in a packaged backend | TesterNative local trusted execution (owner decision) |
| SC-C5 | `--backup` left PostgreSQL running | Stops after the backup |
| SC-C6 | Missing package file list crashed the launcher | Checked first; catch-all tester message |
| SC-C7 | Stale session after a launcher kill | Removed once the lock is held |
| SC-C8 | PostgreSQL inherited launcher handles | ShellExecute start |
