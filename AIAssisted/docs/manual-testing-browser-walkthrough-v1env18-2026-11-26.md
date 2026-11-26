# Manual Testing — browser walkthrough in the isolated instance v1env18 (2026-11-26)

**Environment:**
- Release publish of the committed tree at `c18c2218` + `432556ec`.
- Ports 5197/5097, own disposable PostgreSQL database, M2LB.zip imported through Project Import.
- Headless Chromium (Playwright). Scripts: `scratchpad/v1/v1_final18.py`, `v1_xcheck.py`, `v1_tabclick.py`, `v1_apitiming.py`.
- No user database, no Azure DevOps call, no live external writes.

## Results

| Check | Result | Observation |
|---|---|---|
| D-removed-stories | PASS | Spec 004: 18 ACs, 0 without a story (US3/US5 struck through → not offered) |
| D-requirement-group | PASS | «Krav uten akseptansescenario» is its own group after «Scenarioer uten brukerhistorie» |
| T-template | PASS | «E2E-testplan» → «Forretningsflyter», «Andre forretningsflyter», 0 cases |
| T-empty-row | PASS | «Ingen scenarioer i testplanen ennå.» |
| T-add-two-suites | PASS | «1 scenario lagt til i «Forretningsflyter».» / «… «Andre forretningsflyter».»; cases = 0 |
| T-execute | PASS | «2 scenarioer · 1 bestått · 0 feilet · 0 blokkert · 1 ikke kjørt» |
| T-copy | PASS | Copy: total 2, passed 0. Source: passed 1 (unchanged) |
| K-empty-plan-state | PASS | «Ingen testtilfeller i denne testplanen. Testtilfeller er valgfrie …» |
| K-prepare | PASS | Testdata Valgfritt, Miljø Ikke relevant (reason), Verifisering Oppfylt → «1 av 2 påkrevde punkter oppfylt»; cases still 0 |
| X-scenario-only | PASS (rerun) | «Testplanen inneholder 2 scenarioer, men ingen eksporterbare testtilfeller …» in file and direct mode. The first run read the page while it was still loading. |
| C-no-tile-menus | PASS | 5 KPI cards, no ⋯ menus |
| C-local-mode | PASS | «Tilpass oversikten» stays on /manual-testing |
| C-cancel | PASS | Hidden card returns after «Avbryt» |
| C-1920 / C-1440 | PASS | KPI row = 1 row, no horizontal scroll |
| C-1024 | PASS | 3 + 2 |
| C-390 | PASS | One column |
| Testplaner tab (blocker) | PASS | Clickable from Testoversikt, Finn og forstå, Planlegg and Testplaner; nothing covers it. The real cause was the customisation mode's navigation guard (fixed in `e48da18c`). |
| Console | — | No errors except expected 401s before login |

Screenshots are in `scratchpad/v1/v1env18/shots/` (t-*, k-*, x-*, c-*).

## Measurements
See `birknext-application-wide-performance-scope-consistency-2026-11-26.md`.
