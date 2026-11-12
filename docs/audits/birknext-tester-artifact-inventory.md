# Tester distribution — artifact inventory (pipeline `qa-review-studio-ci`, reference build 415003)

Source: `azure-pipelines.yml` (read locally; build 415003 itself was **not downloaded**: no Azure DevOps access in this session). Sizes are those the user reported from the build page.

| Artifact (before) | Job | What it is | Needs on the PC | Consumer | Size |
|---|---|---|---|---|---|
| `birknext-tester-package-candidate` | BuildAndPackage (ubuntu) | Folder: framework-dependent backend + frontend, `scripts/start-local.bat/.ps1`, docker-compose, `.env`, manifests | — (internal) | The SmokeTestTesterPackage job (download) | ~261 MB |
| `birknext-tester-package` | SmokeTestTesterPackage (windows) | **Byte-identical** copy of the candidate, re-published after `validate-package` + `start-local -ValidateOnly -SkipContainers` | .NET 8 ASP.NET Core runtime, Podman/Docker for PostgreSQL, internet for `dotnet-serve`, PowerShell; **manual connection code** | Testers (by mistake), `.github` workflow (separate CI), docs | ~261 MB |
| `birknext-tester-package-archive` | SmokeTestTesterPackage | `BirkNext-<version>.zip` + `.sha256` of the same folder | as above | Docs (release provenance) | ~103 MB |
| `birknext-windows-tester-package` | BuildWindowsTesterPackage (windows) | `BirkNext-Tester-Windows-x64-<version>.zip` + `.sha256`: **self-contained** `Start-BirkNext.exe` launcher and backend (win-x64), frontend, pinned native PostgreSQL and k6 (SHA-256-verified), licences, manifests, `README.txt`, docs. CI start check: `--validate`, first-run database, health, clean stop | Windows x64 only | The intended tester download | ~248 MB |

**Findings**
- Four artifacts, but only **two distinct packages**:
  - the framework-dependent *developer* package, published three times (candidate, identical validated copy, archive);
  - the self-contained *tester* package.
- The names did not say which was for testers. "birknext-tester-package" — the one that needs .NET, containers and a typed code — looked like the obvious choice.
- **Reported symptom → cause:** a tester using `birknext-tester-package` must install the .NET runtime and Podman/Docker, needs internet for `dotnet-serve`, and must type the connection code from the start window. That matches "cannot see a key", and the dependencies explain stalls in `start-local.ps1` (container or tool installation).

**After (local YAML change, not run):**

| Artifact | Purpose |
|---|---|
| `BirkNext-Tester-Windows-x64` | **The only tester download**: one self-contained ZIP + SHA-256 |
| `birknext-tester-package-candidate` | Internal intermediate (the smoke job's input), labelled as the developer package |
| `birknext-developer-package-archive` | Developer package (needs .NET 8 + Podman/Docker), versioned ZIP + SHA-256 |
| *(removed)* `birknext-tester-package` | Byte-identical duplicate of the candidate |

**Migration required (not done here; needs approval to change Azure DevOps or external consumers):**
- anyone who downloads by the old names `birknext-windows-tester-package` / `birknext-tester-package-archive` / `birknext-tester-package`;
- the separate GitHub workflow `.github/workflows/qa-review-studio-ci.yml`, which still publishes the old names in its own CI.

**Size:** ~248 MB unpacked for the tester package is mostly the self-contained .NET runtime (backend + launcher), PostgreSQL binaries and k6. Nothing was trimmed, because reliability comes first. Trimming PostgreSQL (e.g. pgAdmin/doc folders, if present) is a follow-up after an inventory of the unpacked archive, which was **not available here**.
