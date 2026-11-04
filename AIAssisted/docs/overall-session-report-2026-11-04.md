# Overall report — queued tasks 2026-11-04

Branch `008-traceability-first`. Every task from the queue is implemented, tested and committed. Each task has its own report in `AIAssisted/docs/`.

## At a glance

| # | Task | Result | Commits | Report |
|---|---|---|---|---|
| — | Delivery Source + Functional scope | Done | 997df56c, 2865ba53, 96cced6a | `delivery-source-functional-scope-report-2026-11-04.md` |
| — | Specification Explorer project scope | Done | bb457369, ca1631c7 | `specification-explorer-project-scope-report-2026-11-04.md` |
| — | Tester package integrity (+ Linux `.env` pipeline fix) | Done | 154e26b3, 1bcd8c62, a6980e25, 1f950ec9 | `tester-package-integrity-report-2026-11-04.md` |
| — | Delivery Work type filter | Done | f84e0eb2, 7b5822b1 | `delivery-work-type-filter-report-2026-11-04.md` |
| — | Delivery Change Report redesign | Done (phases A + B) | 2d7cd1a7, b45506ac, 3c62027c | `delivery-change-report-redesign-report-2026-11-04.md` |
| — | Schema Studio UX | Done | da63362a, 3474ce50 | `schema-studio-ux-report-2026-11-04.md` |
| — | Project glossary | Done | b9441442, 2ec22492, 2218c85f | `project-glossary-report-2026-11-04.md` |
| 23 | Product intelligence | Core done | b24569d1, 7331edb5, 340c7687, e22a6f3b, 8db7ca52, 46b00ae3 | `product-intelligence-report-2026-11-04.md` |
| 24 | Governance & quality gates | First slice done | 0ba0ea52, 75e56155, 9ed8510e | `governance-quality-gates-report-2026-11-04.md` |
| 25 | Release readiness | Done | d24bc471 | `release-readiness-report-2026-11-04.md` |
| 26 | Git sources / Microsoft Entra | Core done | 65101faf | `git-sources-entra-report-2026-11-04.md` |
| 27 | Phase 7 hardening | Targeted pass done | 37b027f7 | `phase7-hardening-report-2026-11-04.md` |

## What you can try

| Feature | Where | What it does |
|---|---|---|
| **Impact** | Correlation Review → Explore an entity → "If this changes…" (also on every page that embeds the correlation panel) | Shows what depends on an entity: direct / downstream / potential, with relation paths; must or should retest; test gaps |
| **Scenario candidates** | Delivery → Scope (Functional Quality Scope) | Draft test scenarios for acceptance criteria with no automated test, with an explainable priority. Never counted as coverage |
| **Governance** | Correlation Review, top card | Ready / Ready with limitations / Passed with reviewed exception / Blocked / Not assessed per action, with finding cards. CI-readable: `GET api/canonical/{scope}/governance?action=ReleaseReadiness&module=…` (`exitCode`) |
| **Release Readiness** | New page under Traceability & Impact | Pick modules, read the decision and its dimensions, freeze a report, sign it off. Frozen reports never change |
| **Git Sources** | System Settings → Analysis → Git Sources, and Import Project → "Import from an Azure DevOps Git repository" | Needs `AzureEnvironment:Enabled`, `AzureEnvironment:ClientId` (public client) and the delegated Azure DevOps permission. Then sign in, run "Test Git connection", and import a branch's current commit through the normal import |

## Bugs found and fixed along the way

- **Correlation Review → Explore never loaded.** It sent the text "_scope" as the project. In Spec Changes and Compare Reviews, the row being deleted was never recognised, for the same reason (`DeletingId="_deletingId"`).
- **Impact overclaimed through containers.** Seen with real data: one service made 17 API operations and 17 classes "Downstream". Containment below the changed entity is now Potential.
- **Governance:** a system-level identity conflict blocked every module's release. It now weighs only on the module the system is named after.
- **Release Readiness acceptance:**
  - Freeze stayed disabled until the name field lost focus.
  - Module names came out lowercased.
  - A link read "Open Open review decisions".
- **Migration lock (product bug):** a second API instance waiting for migrations failed after 30 s instead of waiting. This was the long-standing "flaky" migration-lock test.

## Safety

| Constraint | Status |
|---|---|
| Real external systems | No real Azure DevOps, Microsoft Entra, Azure, TT02, Altinn or M2LB call. Git Sources was tested with fake HTTP and token brokers only. All browser checks ran on localhost and aborted any other host: 0 external requests |
| Real `birknext` database | Not modified. Acceptance used the throwaway `birknext_ci2`. Tests can no longer open the configured database at all (`TestPostgres` guard + source-scanning test) |
| `appsettings.Local.json` | Not intentionally modified. It shows as modified in your working tree from before this work, and was excluded from every commit |
| `AIAssisted/testdata/` | Untouched, never committed |
| Your Git settings for Git Sources | The browser check wrote only to a scratch folder |

**Pushes.** I never ran `git push`. The remote-tracking branch shows pushes at 17:22, 17:43, 18:10, 18:44 and 19:27 today (up to 9ed8510e). Those came from outside this session. The commits after that (d24bc471, 65101faf, 37b027f7 and this report) are local only.

## Test totals

| Run | Frontend | Backend |
|---|---|---|
| After #23 | 8015 / 8015 | 5815 passed / 13 skipped + 2 known load flakes (passed alone) |
| After #24 | 8017 / 8017 | 5825 passed / 13 skipped + 1 load flake (passed alone) |
| After #25 | 8019 / 8020, then the one test fixed (typed with an input event) | 5829 passed / 13 skipped + 1 load flake (passed alone) |
| After #26 | 8025 / 8025 | 5848 passed / 13 skipped + 2 load-sensitive tests, fixed in #27 |
| After #27 | **8025 / 8025** | **5860 passed / 13 skipped / 0 failed**: the first fully green backend run this session |

## For you to do

- Delete the leftover folders `C:\bnpkg-smoke` and `C:\bnpkg-old`. The safety check blocked me from removing them.
- Watch the next Azure pipeline run: it confirms the tester-package fix for hidden files (`.env` on Linux, a6980e25).
- To use Git Sources for real: configure the app registration as above, then sign in yourself on your PC. I deliberately made no real sign-in.
- The isolated stack I used (API :5099, web :5199, throwaway `birknext_ci2`) is stopped. Your own BirkNext was never touched.

## Largest remaining debt (details in each report)

- **Impact:** baseline vs current interpretation impact; a persisted scenario workflow (accept / reject / link).
- **Governance:** waivers and overrides persisted with a UI; auth / Test Data / glossary / pipeline policies in the shared model.
- **Release readiness:** reading actual AQR / IQR / FQR / Performance / E2E run results instead of Not assessed; risk register.
- **Git Sources:** Source Sets, update checks, automatic import, device-code sign-in, managed Git storage.
- **Hardening:** performance budgets, a full accessibility pass, dark-mode verification, legacy-engine removal.
