# BirkNext — runtime profiles: gap register (2026-11-11)

| ID | Gap | Status | Note |
|---|---|---|---|
| RP-G1 | Live DeveloperContainer start not re-run in this task | NOT RUN | Developer instance on 5000/5173 must not be touched; full regression covers the code |
| RP-G2 | Clean-PC TesterNative verification | BLOCKED | See SC-G1 |
| RP-G3 | Profile switch UI | NOT SUPPORTED (by design) | Separate launchers; no live database switch |
| RP-G4 | `Authorization:LocalConfigurationWrites` not extended to TesterNative | OPEN | See SC-G7; needs an owner decision like execution |
| RP-G5 | Container k6 runner not exercised live in this task | NOT RUN | Unit tests over the Podman runtime pass; earlier live runs are recorded in the performance reports |
| RP-G6 | No "last startup error" shown in System Settings | OPEN | The launcher records `runtime-state/last-startup-error.json` and shows the error before the UI exists; a successful start clears it |
