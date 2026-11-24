# Manual Testing — User Story–aware, scenario-first workspace (2026-11-24)

Branch `008-traceability-first`, on top of 072c6e17. **Not committed.** Scope: Finn og forstå redesigned around user stories and scenarios;
Planlegg og klargjør speaks the same language. No backend change; everything reads data BirkNext already stores.

## What a tester sees now (Finn og forstå)

| Proposal rule | Behaviour | Evidence |
|---|---|---|
| Select US1 → its scenarios and business context | Story list (rail) with US id, priority, title, scenario count and how many have cases. Choosing a story lists **all** its scenarios as "Scenario 1…N" (a remembered work queue no longer hides some) and shows a story card. | Real SkoleService: US1 lists Scenario 1–11, "11 akseptansescenarioer i spesifikasjonen · 2 med testtilfeller" |
| User stories group, never gate | «Alle scenarioer» is always first; search reaches every story ("N treff i andre brukerhistorier"); scenarios without a story have their own group. | `DiscoverWorkspaceTests`, real no-story case (524 scenarios of avhengigheter-bestilling) |
| Manual testing in focus | Counts lead with what matters for manual testing: «Alle scenarioer: 217 for manuell testing · 433 tekniske skjult». Stories about the code base (e.g. AutorisasjonService 001-translate-source-english) and an empty no-story group wait behind «Vis teknisk innhold». | Real data; `ManualFocusRealScenarioTests` on the 57 verbatim benchmark scenarios |
| Story details on demand | «Vis US-detaljer»: description, why this priority, Independent Test with each objective marked as appearing covered by named scenarios or "mulig manglende dekning", requirements the story/tasks name. | Real US1: objective "valid reports for children known to Person" → Scenario 1; "the rejection reasons" → possible gap |
| Select scenario 2 → Given/When/Then and FR candidates | Breadcrumb `US1 › Scenario 2 › 1 testtilfelle`; «Fra kilden (ordrett)» shows the source's own Gitt/Når/Så; «Krav» lists requirements by relation: **Nevnt i kilden**, **Bekreftet av tester** (from saved cases), **Foreslått · A–D** (with the rule), never a suggestion shown as a link. | Real scenario 2 (actual > planned hours): only FR-015a suggested, "C · Samme regel (samme utfall)"; FR-015 not suggested |
| Click FR-015a → requirement and relationship | Panel: requirement text from the scenario's own specification, why it is linked, other scenarios naming it (across stories), «Vis alle scenarioer for …», link to Specification Explorer. | Real: FR-015a text and "Same rule: both state the outcome «attendance above plan»" |
| Readiness as simple checks | «Klar til kjøring? n / 6»: source & expected confirmed, test data, trigger, verification method, environment & access, reviewed & approved — from `TestDesignAdvisor.Readiness` (stored evidence); unknown is never done; each open point names who usually resolves it. | Real scenario 2: 1/6 (trigger from the source's own step) |
| Open test case / create another | Saved cases of the scenario listed with stage and execution in Norwegian and a direct link (`manual-testing/plan?draft=…&case=…`); «Lag enda et testtilfelle» keeps the scenario and story. | Real: your case "Forberedt · Ikke kjørt" |
| Tabs | Scenarioer / Testtilfeller / Testdekning over the same scope. Testdekning: requirements named by the story, its tasks, its scenarios or confirmed by a tester, with case counts from the trace ("ukjent" when unreadable, never 0); suggestions listed apart (B/C shown, D behind a toggle), never counted. | Real US1: FR-018 (tasks); 14 B/C suggestions, 11 D hidden |
| Change specification | The open scenario is never silently replaced: a scenario that no longer exists is reported by name; a filter that hides it keeps it open with a note. | `DiscoverWorkspaceTests` |
| Return to the workspace | Story, open scenario and tab restored per browser tab. | Real reload restored US1 / scenario 2 / tab |

**Planlegg og klargjør**: breadcrumb `US › Scenario «…» › Testtilfelle`; «Vis scenarioet i Finn og forstå» opens exactly that scenario with its story chosen; the readiness block uses the same six questions. The Norsk / Original / Sammenlign editor, translation, saving and review are unchanged.

**Layout**: laid out by the page's own width (container queries), not the viewport: narrow = stacked with a story dropdown; medium = scenarios + open scenario with stories as a row; wide (≥ 80rem) = three columns. Verified at 390, 1280, 1600 and 1920 px: no overlap, no horizontal scroll.

## Real-data findings fixed during the walkthrough

1. Story titles showed the raw heading "User Story 1 - … (Priority: P1)" → title only (chips show id and priority).
2. A remembered work queue hid 2 of US1's 11 scenarios → choosing a story shows all its scenarios.
3. Every Independent Test objective showed as a gap ("children" vs "child"; scenario Given/When/Then not read) → singular/plural matching, source text included.
4. The detail panel overlapped the list at 1600 px (inner grid sized to its widest child; viewport media queries with a 72rem page) → container queries, 96rem page, `minmax(0, 1fr)` inner grids.
5. Server labels "Prepared / Not executed" → «Forberedt / Ikke kjørt».
6. 25 full-text suggestions overwhelmed Testdekning → B/C only, one line each, D behind a toggle.
7. A full-suite timing flake: rebuild yields (`Task.Delay(1)`, meant for the browser's UI thread) now run only in the browser.

## Tests

- New: `ScenarioWorkspacePresentationTests` (stories, numbering, relations, coverage, suggestions, objectives, checklist, titles), `DiscoverWorkspaceTests` (11 page rules), `ManualTestingPlanWorkspaceTests` (breadcrumb, way back, six questions), `ManualFocusRealScenarioTests` (57 verbatim M2LB scenarios: Person, SkoleAdapter, SkoleService manual; translation stories technical; numbering; no scenario ready before approval).
- `M08` now asserts the per-scenario classification is reused (reference equality) instead of a 1.5 s wall-clock budget that failed only under full-suite load (≈0.1 s alone).
- **Full frontend suite: 9,603 passed, 0 failed.**

## Data safety

Read-only for your data: no plan saved or changed («Testplan 2026-11-24» unchanged, 08:32). The walkthrough switched the specification to
SkoleService and ran discovery (established procedure), then restored `avhengigheter-bestilling.md` with a fresh discovery.

## Limitations

- Person and SkoleAdapter were verified on their verbatim scenarios (benchmark fixture), not in the live app: that needs switching your module selection.
- Suggested requirements need the specification's requirement catalog; when it cannot be read, only what the source names is shown.
- FR ids display upper-cased (FR-015A) — the known casing limitation, now part of the queued FR-grouping task.
- Testtilfeller lists cases by the scenario's specification id; cases of contract/code scenarios are counted but not listed (the page says so).
- Objective coverage is a word-match hint, labelled as such.
- Validation with manual testers is still to be done (the 20-scenario review sheet is ready).
