# System Settings — acceptance (task 12)

## Synthetic fixtures (bUnit, `SettingsOverviewTests`, 9/9)

The expectations are written from the brief, not read from the implementation.

| Fixture | Test | Result |
|---|---|---|
| A DeveloperContainer valid | `A_developer_installation…` | PASS |
| B TesterNative valid | `A_tester_installation_names…` | PASS |
| D PostgreSQL unavailable, E k6 unavailable | `An_unreachable_database_or_a_missing_k6…` | PASS |
| F build mismatch | `Different_frontend_and_backend_builds…` (never "same build") | PASS |
| G dirty build / H manifest unknown | covered by A (local build → compatibility not assessed) | PASS |
| J schema partially migrated | `A_connected_database_with_pending_migrations…` | PASS |
| K configuration warning | `Configuration_health_shows…` (3 cases) | PASS |
| L/P backend or provider unavailable | `A_backend_that_does_not_answer…` | PASS |
| C unknown profile | only partly: `RuntimeEnvironmentCardTests.An_unreachable_backend_or_host_without_the_client_is_not_presented_as_a_profile`; a backend reporting an unrecognised profile value is not tested | PARTIAL |
| I Companion disconnected | real state in the browser: Application information shows "Edge Companion: Not connected · bundled v0.1.0 · Compatibility not assessed" (bundle version is not shown as installed) | PASS (browser, SSET-09 + screenshot) |
| M unauthorized access | unchanged backend guard; not re-tested here | NOT RUN |
| N deep links, O restart | browser SSET-14..21 and the after-restart phase | PASS |

Focused frontend run (Settings, build identity, runtime, Help Center filters): 303/303.

## Browser (Microsoft Edge, Release frontend, isolated API; only localhost requests allowed)

Harness: `scratchpad/ss12/accept.cjs`.

| Phase | Result |
|---|---|
| main | 23/23 |
| after-restart (API process restarted) | 23/23 |
| visual 1440/1280/1024/768/390 | 6/6: no page overflow; overview text ≥ 13.6 px |

The checks cover:
- open; Overview first; no ribbon;
- Application tile against `build-info` (Local, dirty → not assessed);
- Runtime, Database and k6 tiles against `api/system/runtime`;
- the Configuration state;
- one `build-info` and one `runtime` request per load;
- Application information (FE/BE compatibility, Edge Companion, schema);
- Technical details expanding to the schema table;
- Runtime & Environment with no switch control;
- General deduplicated, with endpoints on demand and no secrets;
- Configuration Health, Feature Visibility, Platform and Git Sources by navigation;
- section search; deep link; Back; the Configuration tile opening Configuration Health; Refresh;
- no console or page errors.

## Screenshot review

Screenshots: `ss-general-1440.png` and `ss-<width>.png`.

- 1440: four tiles in a row, with Configuration wrapping to the second row. There is no ribbon, and Application information follows directly.
- 390: the section navigation becomes wrapped chips, the tiles stack in a single column, and Refresh stays reachable.
- No tiny type, clipped actions or full-width technical strings. Endpoints are collapsed.

## Not run

- TesterNative runtime (see the runtime verification report).
- Unauthorized-access probe.

## Full regression (commit 16162bb1, scratch build tree, sequential)

Backend `BirkNext.Api.Tests`, run as 6 sequential partitions, with no memory-guard abort:

| Partition | Passed | Skipped | Failed |
|---|---|---|---|
| 1 | 1049 | 2 | 0 |
| 2 | 1098 | 5 | 0 |
| 3 | 840 | 0 | 0 |
| 4 | 652 | 1 | 0 |
| 5 | 520 | 3 | 0 |
| 6 | 2368 | 8 | 0 |
| **Total** | **6527** | **19** | **0** (6546 tests) |

Frontend `BirkNext.Web.Tests`: 8646 passed, 0 failed, 0 skipped.

The Release frontend was built with `dotnet publish -c Release`; that is the build the browser acceptance ran against.
