# Manual Testing — Azure DevOps export and transfer v4 (2026-11-26)

Commit: `de2adce8`.

## A–B. File export contract (`AdoTestPlanExporter`)
- **Columns:** ID, Work Item Type, Title, Test Step, Step Action, Step Expected, Area Path, Assigned To, State, plus optional Priority and Description.
- **Excel:** inline-string cells only, no formulas, Test Step as a number.
- **CSV:** UTF-8 with BOM, RFC 4180, optional apostrophe guard against formula injection.
- **Language:** chosen per case and never mixed; a fallback is a warning.
- **Blocking issues:** no title, no steps, a step without action or expected result.
- **Mapping report:** per case, the source specification and AC id, requirements, scenario key and title, case key and plan revision, and test data references.

## Your priority — will the file import as correct test cases?
- **Defect found:** every step row repeated Work Item Type and Title (`AdoTestPlanExporterTests` pinned that layout).
- In Azure DevOps' own test-case export layout, a test case's fields appear on its first step row and the following step rows carry only Test Step, Step Action and Step Expected.
- A row with a Title starts a new test case, so the old layout risked one test case per step.
- **Fix:** `Values()` writes the case fields on the first step only; both writers share it. The read-back tests were updated to assert the new layout (CSV and XLSX, independent readers). 35/35.
- **NOT VERIFIED:** an actual import into an Azure DevOps organization. None was used, by design.

## Traceability
The mapping report is unchanged and complete for supported links. The panel states that the file does not create requirement links, the plan or its suites.

## H. Area Path — FIXED
- **Root cause:** the placeholder `Project\Area` looked like a configured value («ProjectArea» in the screenshot) while the field was empty, so validation correctly said «mangler».
- **Fix:**
  - placeholder «Ikke angitt — f.eks. MittProsjekt\MittOmråde»;
  - a state line under the field: missing (with what to do) or «Brukes slik det er skrevet. BirkNext kontrollerer ikke at området finnes i Azure DevOps»;
  - `aria-invalid` and `aria-describedby`; the value is trimmed.

## G/7. Empty and scenario-only plans — FIXED
- **Empty plan:** «Denne testplanen inneholder ennå ingen scenarioer eller testtilfeller» with «Legg til scenarioer / Åpne testplan / Finn scenarioer». No wizard.
- **Scenarios but no cases:** «… men ingen eksporterbare testtilfeller. Scenarioer kan planlegges og gjennomføres i BirkNext, men kan ikke eksporteres som Azure DevOps-testtilfeller uten et faktisk testtilfelle», with «Forbered testtilfeller».
- **Mixed plans:** «N planlagte scenarioer uten testtilfelle eksporteres ikke».
- The same states apply in direct mode. No case is ever invented. Test `X01`.

## 22. Approval wording
- BirkNext has no plan-approval workflow.
- The reminder now reads: «Anbefaling: la testleder se over testtilfellene før eksport. BirkNext har ingen egen godkjenning av testplaner; hvert testtilfelle godkjennes for seg».
- File mode says it exports test cases; the plan and suites must be created or chosen in Azure DevOps.

## N. Direct transfer
- Unchanged, and still behind explicit confirmation (existing four-stage flow and tests).
- No live writes were made, and none were tested against a real organization.

## 14. Staged wizard and history placement
**PARTIAL:** the existing wizard is already staged (select → fields → check → download) and the history is collapsed when empty. No further restructuring.

## Tests
Backend exporter/transfer 35/35. Frontend Ado suites 102/102, plus X01 (13/13 in `AdoTransferTests`). Full frontend 9,833/9,833.

## Browser
v1env18 (`X-scenario-only`), see the walkthrough report.
