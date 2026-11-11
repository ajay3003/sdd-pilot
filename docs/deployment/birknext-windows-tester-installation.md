# BirkNext Tester for Windows — installation and daily use

## What you need

- Windows 10 (1809 or later) or Windows 11, 64-bit (x64).
- **Microsoft Visual C++ 2015–2022 Redistributable (x64).** It is present on most managed PCs. If it is missing, BirkNext says so and IT must install it; BirkNext never installs it itself.
- About 1 GB of free disk space for your data, plus about 600 MB for the extracted package.
- Microsoft Edge (or another browser).
- **Not needed:** administrator rights, Podman, Docker, WSL2, Hyper-V, a separate .NET, PostgreSQL or k6 installation, or an internet connection for starting BirkNext.

## Install

1. Download `BirkNext-Tester-Windows-x64-<version>.zip` and its `.sha256` file.
2. Optional: check the archive.
   - In PowerShell, run `Get-FileHash .\BirkNext-Tester-Windows-x64-<version>.zip -Algorithm SHA256`.
   - Compare the result with the `.sha256` file. A checksum proves the file is unchanged; it does not prove who published it.
3. Extract the **whole** archive into a folder of your own, for example `C:\BirkNext\1.0.0`. Use a new folder for every version.
   - Keep the folder path short (under about 130 characters). The package contains paths of up to 121 characters, and Windows Explorer cannot extract beyond its 260-character path limit.
4. Double-click **`Start-BirkNext.exe`**.

## First start

The window shows:

```text
BirkNext is preparing your local environment.
  • Checking application files…
  • Preparing local database…
  • Starting local database…
  • Creating BirkNext database…
  • Applying database updates and starting BirkNext…
  Ready. BirkNext is running at http://localhost:5481/
  Connection code: XXXX-XXXX-XXXX
```

- **Duration:** the first start takes about a minute: it checks every package file once and creates your database.
- **Connection code:** Edge opens. Enter the connection code once per browser, and again after BirkNext restarts.
- **Your data:** stored in `%LOCALAPPDATA%\BirkNext\TesterNative`, only for your Windows user, outside the package folder.

## Every day

- **Start:** double-click `Start-BirkNext.exe` and keep its window open while you work.
- **Stop:** press **Ctrl+C**, close the window, or run `Start-BirkNext.exe --stop`. BirkNext stops the web application, its service and the local database in order. Your data stays.
- **Second start:** starting BirkNext a second time opens the running BirkNext instead of starting another one.

| Command | What it does |
|---|---|
| `Start-BirkNext.exe --status` | Shows the runtime profile, versions and database state |
| `Start-BirkNext.exe --backup` | Writes a verified backup to `%LOCALAPPDATA%\BirkNext\TesterNative\backups` (BirkNext must be stopped) |
| `Start-BirkNext.exe --diagnostics` | Writes a diagnostics archive (logs and state, no passwords) to send to the BirkNext owner |
| `Start-BirkNext.exe --validate` | Checks the package and this PC without starting anything |

## Performance tests

The package contains k6 1.0.0. In **Performance Test Review**, runs start only when you start them.

On this PC, k6 runs as a normal Windows program:
- it uses the PC's own network, proxy and Windows certificate store;
- it runs without container resource limits;
- a cancelled run has no partial results.

A "CA bundle" setting is refused here: trust the corporate CA in Windows instead.

## When something goes wrong

| Message | What to do |
|---|---|
| The Microsoft Visual C++ 2015–2022 Redistributable (x64) is not installed | Ask IT to install it |
| This BirkNext package is incomplete / has been changed after it was built | Extract the downloaded archive again into a new folder |
| Unable to start the local database | Start again; if it repeats, send `--diagnostics` |
| Database files are present but cannot be opened | Do not delete anything; send `--diagnostics` |
| The database requires an upgrade before BirkNext can start / requires a newer BirkNext | See the upgrade and recovery guide |
| Windows did not allow … to start | An application-control or antivirus policy blocks a bundled program. Ask IT, and send `--diagnostics` |

## Managed PCs (IT notes)

- **Unsigned binaries:** the package executables (`Start-BirkNext.exe`, `BirkNext.Api.exe`, `k6.exe`, the PostgreSQL programs) are not Authenticode-signed. AppLocker/WDAC may require an allow rule, and SmartScreen may warn on first start. BirkNext does not bypass these controls.
- **Network:** BirkNext listens on loopback only:
  - PostgreSQL on 127.0.0.1, port 55432 or the next free one;
  - BirkNext's service on localhost:5480;
  - the web application on localhost:5481.
  
  No firewall rule is needed.
- **Process identity:** everything runs as the signed-in user. Nothing is installed as a service; nothing is written outside the extracted folder and `%LOCALAPPDATA%\BirkNext\TesterNative`.
