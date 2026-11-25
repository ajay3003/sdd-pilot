# Manual Testing — Testoversikt dashboard, shared scope and the workspace UX round (2026-11-25)

**One combined change, as recommended.** It covers these briefs: the configurable dashboard v2, the shared scope, Finn og forstå (two columns, drawer, wording), the Planlegg left list with the purple AC, Planlegg Versjoner, and Overfør testplan. The false «Kilde mangler» fix and automatic translation have their own reports, both committed earlier in this round:
- `manual-testing-false-missing-source-fix-2026-11-25.md`;
- `manual-testing-automatic-translation-2026-11-25.md`.

Branch `008-traceability-first`. Every part is committed locally as you asked (commits below); nothing is pushed, there was no reset or database cleanup, and both stashes are kept.

**Status words used:**
- **IMPLEMENTED** — built in this round.
- **VERIFIED** — checked by a test run or the browser walkthrough.
- **PARTIAL** — some of it is built or checked.
- **NOT VERIFIED** — not checked.

## A. Repository state and commits

| Commit | Content |
|---|---|
| `5ee0fc3e` | Per-case source resolution (no false «Kilde mangler»); automatic translation |
| `80c11785` | Finn og forstå: two columns + on-demand drawer; no bare «?»; «Krav og dekning» |
| `f46ffa3d` | Planlegg: scenario-first left list with a prominent purple AC |
| `a6f64013` | Testoversikt dashboard, shared scope, System Settings → Manual Testing |
| `f7914edd` | Overfør testplan: Norwegian check messages, folded empty panels |
| `eb9241ae` | Versjoner: «Se endringer», restore confirmation |

## B. Shared scope (IMPLEMENTED)

**One selection.** The module → specification choice is the existing shared one (`IArtifactExplorerContext`, saved like the explorers' choice). Testoversikt and Finn og forstå use the same picker component, so there is no second selection state.

**Scope levels on Testoversikt** (its figures follow the chosen level):
- «Hele prosjektet» (default);
- «Valgt modul»;
- «Valgt spesifikasjon».

**Module row:** clicking a row in the module table selects that module in the shared choice and opens Finn og forstå.

**The other tabs:**
- Planlegg og klargjør keeps the active saved test plan as its scope. Each case's source status comes from its own specification (`5ee0fc3e`).
- Testplaner lists all the project's plans.

**Finn og forstå:** the picker is one line («Krav og scenarioer hentes fra …») with «Endre modul eller spesifikasjon» collapsed. It is not a second full selector panel.

## C. Testoversikt (IMPLEMENTED)

### Data

- **Endpoint:** new read-only `GET api/test-plan-suggestions/project/overview`.
- **Scenarios of every specification** come from the evidence stored at import. They pass through the discovery engine's own `FromSpecification`, so their identities are the ones saved cases carry.
- No discovery is run, nothing is written, and no revision is created (tested).

**Cases** (active plans; excluded and superseded are left out):
- each is attributed by its **own** specification;
- a hand-written case counts for its parent scenario;
- a case without a source goes under «Uten modultilknytning».

### Definitions

All are in `ManualTestingOverviewPresentation`, shared by every card, chart and table:
- **Scenarioforslag:** unique scenarios. It is «—» with an explanation when no specification is stored; never 0.
- **Dekket:** a scenario with at least one saved case. Five cases for one scenario is one covered scenario.
- **Godkjent and Klar til testing:** kept apart and never added together.
- **Blocker reasons:** they overlap; the distinct number of cases that are not ready is shown.
- **Never shown:** Gjennomført, Bestått or Feilet. The registry has no such widget (tested).

### Default parts

| Group | Parts |
|---|---|
| Key figures | Scenarioforslag, Testtilfeller, Godkjente, Klar til testing |
| Charts | Testforberedelse; Testdekning per modul (per specification for one module, per user story for one specification); Scenarioer etter prioritet; Manglende testdekning; Testbarhet og blokkeringer; Kildeendringer |
| Follow-up | Viktig å følge opp (P1); Testplaner |
| Display | Oversikt per modul (per specification / per user story when narrower) |

### Optional parts (off by default)

- Testplaner (key figure);
- manual/technical cases (by the case's chosen test type);
- Utvikling i testforberedelse: honest «Historiske tall er ikke tilgjengelige». The state at earlier times is not stored, and figures are never computed backwards;
- Godkjenning per modul;
- Siste endringer (the stored plan versions);
- Oversettelser til gjennomgang (from the stored translation state only).

**Charts** reuse BirkNext's own `DeliveryBarChart`, which prints every value so it never relies on bar length or colour. There is no new chart library.

**Navigation:** key figures and panels link to Finn og forstå, Planlegg or Testplaner. Chart bars are **not** clickable, because no exact matching-record filter exists for them yet (see L).

## D. System Settings → Manual Testing (IMPLEMENTED)

- **The card:** grouped switches (Nøkkeltall, Grafer, Oppfølging, Visning), each with the dashboard's own name and a one-line explanation.
- **Actions:** «Save changes» and «Restore defaults…» (confirmed).
- **Language:** the section chrome is English, per your standing rule for System Settings. The part names are the Norwegian ones testers see.
- **Storage:** installation-wide in App_Data (`ManualTestingDashboardSettingsStore`), the same pattern as Translation and Azure DevOps – Test Plans. Only explicit choices are written, so defaults apply until changed.
- **Server rules:** saving needs the existing write permission, unknown widget ids are refused, and plans, cases and revisions are never touched.

## E. Finn og forstå (IMPLEMENTED)

- **Two columns** (user stories + scenarios). The permanent third column is gone.
- **The drawer** opens only on «Vis detaljer»:
  - selecting a scenario never opens it;
  - while open it follows the selection; while closed it stays closed;
  - Escape or «Lukk» closes it and keeps the selection.
- **Wording:** unknown readiness is «Ikke vurdert» with a neutral mark (no bare «?»). Story goals say «Dekket» / «Ikke dekket ennå». The headings are «Begrunnelse for prioriteten» and «Slik testes brukerhistorien alene». The requirement tab is «Krav og dekning».

## F. Planlegg og klargjør (IMPLEMENTED)

**Left list:**
- the scenario title appears once;
- a case whose title only repeats it is shown as «Testtilfellet», with the full title in `aria-label`;
- distinct case titles are kept;
- «N testtilfeller · X av N godkjent» (never «Godkjent» because one case is);
- three or more cases fold;
- the AC badge is purple, and the selected group is softly purple;
- «Flere AC» replaces «AC ?».

**Versjoner:**
- «Se endringer» compares two stored versions per case and field;
- «Gjenopprett» asks first: «Gjenoppretting lager en ny gjeldende versjon … Ingen versjoner slettes».

**Already in this round:**
- «Før du starter» and the secondary design sections are collapsed;
- the automatic translation;
- the per-case source status.

## G. Overfør testplan (PARTIAL: audit plus the gaps found)

**Already present** (earlier today, `654fff9f`):
- file export is the default every time, and direct transfer is opt-in;
- field defaults live in System Settings, shown as one line with «Endre felter for denne eksporten» collapsed;
- the check summary is compact;
- the import guide is collapsed;
- the approval warning and declaration are always visible, with the reasons collapsed and server-side enforcement.

**This round:**
- blocking messages in plain Norwegian with «Åpne Azure DevOps-innstillinger», for example «Område (Area Path) mangler. Velg standardområde …». The original message is kept as «teknisk melding», and validation is unchanged;
- empty result and history panels are folded.

**Not changed:**
- the page width;
- the per-row case list layout;
- a separate «vanlig Excel» without Area Path. The exporter's format requires it for Azure DevOps Services, so it was not bypassed.

## H. Tests

- **Frontend:** **9,734 passed / 0 failed** (full suite, after the last commit).
- **Backend:** TestPlanning + TestEvidence **578 passed / 3 skipped / 0 failed** after the dashboard. The earlier source-status round ran 633 / 3 / 0 with the reset suites.
- **New tests:**
  - dashboard settings store (defaults, merge, unknown refused, reset, restart);
  - overview attribution (own specification, hand-written case, «Uten modultilknytning», no write);
  - aggregation (unique counting, five cases = one covered scenario, scope levels, overlapping milestones, P1 attention);
  - the Testoversikt page (no «Neste steg», no execution words, module table, switch off, history unavailable, no project);
  - the settings card (only changes saved, failure said, reset confirmed);
  - the drawer and wording;
  - the left list;
  - version comparison;
  - Norwegian issue texts.

## I. Browser walkthrough (isolated: fresh database, own App_Data, offline translator, M2LB imported)

*Filled in from the walkthrough's results below.*

## J. Data safety

- **Your database** was not used by any part of this round: every browser check ran in an isolated instance.
- **Read-only reads:** the overview and settings reads change nothing (tested).
- **Settings writes** touch only `App_Data/manual-testing/dashboard-settings.json`.

## L. Remaining limitations

- **Chart drill-down:** clicking a chart bar does not yet open the exact matching records. Bars are information only; key figures and panels link to the right tab.
- **«Valgt modul»/«Valgt spesifikasjon»** follow the shared choice. There is no separate «Alle spesifikasjoner i modulen» selector apart from the module level.
- **History chart:** unavailable by design until point-in-time figures are stored.
- **Planlegg editor hierarchy** (Testbeskrivelse / Teststeg / Før du starter / Ekstra) was not restructured beyond the collapsing. «Samlet forventet resultat» is kept: it is an existing field in the export and saved cases.
- **Overfør testplan:** width and row layout are unchanged (G).
