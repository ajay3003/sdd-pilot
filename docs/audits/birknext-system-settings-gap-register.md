# System Settings — gap register (task 12)

| Id | Gap | State |
|---|---|---|
| G1 | Same facts shown in up to five places (ribbon, metric cards, tables, cards) | CLOSED: Overview + one authority per fact |
| G2 | Two migration checks ("Up to date" vs per-context schema) | CLOSED: only the per-context schema check remains |
| G3 | No at-a-glance health | CLOSED: Overview tiles with real states |
| G4 | Each card re-read build-info/runtime | CLOSED: one shared read per source (measured 1/1) |
| G5 | Dense tables; endpoints always visible | CLOSED: fact list + Technical details |
| G6 | No freshness wording | CLOSED: "read when the page opened, not monitored live" + explicit Refresh |
| SS-G1 | TesterNative runtime not verified in a real package during this task | OPEN: NOT RUN; needs an installed Tester Package |
| SS-G2 | PostgreSQL-unavailable / k6-missing states verified by component tests only | OPEN: by design (no Podman changes allowed) |
| SS-G3 | Unauthorized-access probe not repeated | OPEN: backend guard unchanged |
| SS-G4 | Admin APIs rely on the localhost guard + local session; no role model | OPEN: pre-existing; carried into task 13 (Feature Visibility governance) |
| SS-G6 | No test for a backend that reports an unrecognised runtime profile value (fixture C only partly covered) | OPEN |
| SS-G5 | Overview freshness is per page load; values are not refreshed while the page stays open | ACCEPTED: documented; Refresh is explicit |
