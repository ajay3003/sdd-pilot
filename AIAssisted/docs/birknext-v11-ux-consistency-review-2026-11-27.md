# BirkNext V11 — UX consistency review (2026-11-27)

V11 Phase 2 overlaps V12, which ran first. This review records the state of each V11 UX principle after V10 and V12, plus V11's own change, so nothing is claimed twice.

| V11 principle | State | Where |
|---|---|---|
| 1. Scenario-first | PASS | Finn og forstå rows open the scenario; Testgjennomføring is per scenario occurrence (V10/V12) |
| 2. No internal work-item schemas | PARTIAL | Technical evidence is collapsed by default in the detail panel; some technical labels remain («Kildegrunnlag n/6») |
| 3. Essential information first | PASS | Detail panel: title, expected result, readiness, plans, one primary action (V12) |
| 4. Technical evidence behind progressive disclosure | PASS | «Klassifisering, kilde og sporbarhet», «Vis teknisk analyse» collapsed |
| 5. No repeated actions or duplicated information | PARTIAL | Duplicate «Forbered testtilfelle» removed (V12). Source context is still repeated in Planlegg og klargjør (V12 MT-8 partly) |
| 6. One clearly identifiable primary action per task | PASS (Finn og forstå), PARTIAL (Planlegg: «Godkjenn» and «Overfør testplan» can both be primary) | V12 |
| 7. Readable titles over identifiers | PASS | Rows show the title; ids as metadata |
| 8. Canonical identities and traceability | PASS | V10 identity rules; Data Quality dry run |
| 9. Consistent Norwegian terminology | PARTIAL | «Vurdert av tester» fixed (V12). Scenario / AC / forslag / kandidat are still mixed (V12 MT-11) |
| 10. Fewer clicks | PASS (row opens; small results open their groups: V11), PARTIAL (no direct «Legg til i testplan»: V12 MT-9) | V11/V12 |
| 11. Never hide an opened item without explanation | PASS | «Dette scenarioet vises ikke med gjeldende … filter» (earlier), V10 selection banner («skjules av andre aktive filtre») |
| 12. Preserve unfinished edits and context | PASS | NavigationLock, create/edit separation keeps values (V12), tab state (V10) |

**Finn og forstå's «Ende til ende» experience** (V11 special attention):
- 11 results behind nested groups → shown directly (V11);
- repeated «Vis detaljer» → a row opens (V12);
- narrow detail panel → the panel opens at every width (V12);
- duplicated «Forbered testtilfelle» → one (V12);
- documented vs candidate → FUN-014 (V10).

**Open (P2):** one shared filter toolbar, one grouping system, direct «Legg til i testplan», terminology pass (V12 MT-5, MT-6, MT-9, MT-11).
