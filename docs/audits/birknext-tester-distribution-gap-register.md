# Tester distribution — gap register

| Id | Pri | Gap | What closes it |
|---|---|---|---|
| TD-G1 | P1 | The packaged app was not built or started in this session (pinned downloads not approved) | Run `scripts/build-tester-package.ps1 -AllowDownload`, then the package acceptance (`--validate`, start, automatic connection, restart, stop) |
| TD-G2 | P1 | No clean Windows / AVD acceptance | An approved clean VM or AVD session; checklist in `birknext-tester-package-acceptance.md` |
| TD-G3 | P1 | The pipeline change is local only; renamed artifacts need consumers updated (download habits, docs, the GitHub workflow) | Review and approve the YAML; announce the new download name `BirkNext-Tester-Windows-x64` |
| TD-G4 | P2 | `Start-BirkNext.exe` is unsigned. AppLocker/WDAC/SmartScreen may block it on managed PCs | Code signing or an IT allow-list. **Never** bypass |
| TD-G5 | P2 | QuickEdit freeze: mechanism verified in code, not reproduced interactively | A manual check on Windows: click into the window during start-up |
| TD-G6 | P3 | Launcher UI is a console window (no graphical progress, Retry or "copy diagnostics" button); diagnostics run through `--diagnostics` | A small GUI or tray launcher, if wanted |
| TD-G7 | P3 | Package size (~248 MB) not reduced | An inventory of the unpacked archive (PostgreSQL extras), then a safe trim |
| TD-G8 | P3 | The bootstrap token appears in the browser's command line for one launch (same-user readable, single use, 2 min) | Accepted per the design; an alternative would be a launcher-hosted handoff page |
