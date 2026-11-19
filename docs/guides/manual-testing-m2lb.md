# Manual testing in BirkNext — a guide for M2LB testers

This guide takes a manual tester from an M2LB archive to a file of test cases that can be imported into Azure DevOps Test Plans. Every step was
run end to end on the real M2LB archive (`M2LB (8).zip`, 18 Nov 2026, 3 008 files) on a clean BirkNext installation; the numbers below are what
that run showed. Your numbers will differ with another archive version.

**What BirkNext does and does not do**

- BirkNext helps you find what to test, prepare test cases, review them and export them as a file.
- Manual tests are **executed in Azure DevOps Test Plans**, not in BirkNext. BirkNext does not write to Azure DevOps and does not read results back.
- A suggestion is not a test case. A prepared or approved test case is not an executed test.
- Nothing in this flow contacts M2LB, MU, MM, Altinn or any test environment. It reads only the archive you import.

## The flow at a glance

| # | Step | Where | Result |
|---|------|-------|--------|
| 1 | Import the project | Import Project | M2LB is the current project; its documents and source are analysed |
| 2 | Choose the specification | Specification Explorer | One module's specification is the basis for requirements and business scenarios |
| 3 | Check what needs testing | Manual Testing → Discover & understand | Requirements with their test design state |
| 4 | Discover scenarios | Manual Testing → Plan & prepare → Choose | Scenario suggestions from the project's evidence |
| 5 | Prepare test cases | Plan & prepare → Choose → Prepare | Draft test cases in a plan |
| 6 | Edit, save and approve | Plan & prepare → Prepare | Reviewed, approved cases |
| 7 | Export | Plan & prepare → Export to Azure DevOps | An XLSX or CSV file and a mapping report |
| 8 | Import into Azure DevOps | Azure DevOps Test Plans | Test cases you execute there |

The **Manual Testing** home (sidebar → Manual Testing) always shows one *Next step* button based on what is saved. If you are unsure what to do,
open the home and follow that button.

## 1. Import the project

1. Open **Import Project** and choose **Import ZIP**.
2. Select the M2LB archive. The preview shows the project name (taken from the file name), the size and the number of entries. Nothing is
   imported yet.
3. Choose **Import project**. A 10 MB archive with about 3 000 files takes about two minutes.
4. Read the result. For M2LB it says *Imported with notes*:
   - **Specification: 63 documents, Data Model: 44, Task: 52** were found and kept. They are not reviewed or approved by importing them.
   - **Research: 99 documents, none selected**, and **11 documents could fit more than one role**. These do not block manual testing.
   - **Test Data XML fixtures — not applicable** for the Skolenærvær Altinn report: this archive contains no XSD with that contract name.
     This only matters if you plan to use that fixture.
   - **The source snapshot is partial**: 16 files are in a language Source Analysis does not analyse. Technology Coverage lists them.
5. Under **Next steps**, choose **Plan manual tests** to go to the Manual Testing home.

## 2. Choose the specification (important for M2LB)

M2LB is a *monorepo*: each service has its own `specs` folder. BirkNext counts 63 specification documents in 14 modules (for example
`SkoleService/specs/001-skoleservice`, `PersonService/specs/001-person-module` and the project-level `specs/` folder). BirkNext never picks
one of them for you.

Until you choose, the Manual Testing home says **Choose the specification**, and Discover & understand says *This project contains 63
specifications in 14 modules and none is chosen yet*. Discovery then finds no business scenarios from a specification (only UI pages, API
operations, journeys and guide scenarios).

You choose it inside Manual Testing, without leaving it:

1. On the Manual Testing home choose **Choose the specification**. It opens **Discover & understand**, which (like **Plan & prepare →
   Choose**) has the lists **Module**, **Feature** and, when a feature has several, **Specification**.
2. In **Module**, choose the service you are testing — for school attendance (Skolenærvær), **SkoleService**.
3. If the module has several features, choose one in **Feature**; if it has several specifications, choose one in **Specification**.
4. The line under the lists names the specification in use — for SkoleService, *Requirements and scenarios come from SkoleService — school
   attendance reporting from private institutions and foster homes* (`SkoleService/specs/001-skoleservice/spec.md`). Discover & understand
   then lists its **65 requirements**.

The choice is the same one the explorers use: the Specification, Plan, Task, Data Model and Constitution Explorers then show this module and
feature too (and choosing in an explorer changes it here). To test another service or feature, change the lists; after a change the page
says that scenarios found earlier came from the previous specification and offers **Discover now**, which runs discovery for the new one.

### A module with several specifications

When the module you choose has more than one specification, BirkNext again does not pick one:

- **Feature packs** (for example `specs/001-…` and `specs/002-…` in the same service): the explorer shows **Choose a feature to explore**, one
  button per feature with its number of documents. Documents shared by the whole project apply to every feature.
- **Several specification files that nothing tells apart**: the explorer shows **Choose a Specification artifact** with the file names.
- **A feature without a `spec.md` of its own** (in M2LB: AutorisasjonService features 001 and 002): the explorer says *No specification is
  available for this feature*. Notes and analyses elsewhere in the project are never used as that feature's specification automatically;
  they are listed under *shared documents … open one for this feature*, and opening one applies to that feature only. The same holds for
  plans, task lists and data models.

Once chosen, switch with the **Module** and **Feature** lists at the top of any explorer; every explorer and manual testing follow the
same choice. Long specifications are analysed in full (SkoleService has about 92 000 characters).

Until you choose, Discover & understand names the module, for example *Module SkoleService has 3 specifications and none is chosen yet*.

Manual testing works on **one specification at a time**: requirements and business scenarios come from the chosen one only (UI, API, journeys
and existing tests do not depend on the choice). To cover the others, choose each in turn and choose **Discover again**. Test cases you
already prepared stay in the plan, and each keeps the specification file it came from: the export's *Description* column and the mapping
report name it, so cases from several specifications can share one plan and one export.

## 3. Check what needs testing — Discover & understand

Open **Manual Testing → Discover & understand**. It shows, for the chosen specification:

- **Requirements** — each requirement id (such as `FR-001`) with its test design: *No test design yet*, *Prepared* or *Approved*.
- **Scenario suggestions** — after discovery, how many were found, by type, and how many need clarification.
- **What the evidence contains** — which kinds of evidence BirkNext found (specification, UI source, API contracts, integration journeys,
  System Understanding guide, existing tests).

This page only reads. Choose **Discover scenarios** to continue.

## 4. Discover scenarios

**Discover scenarios** opens **Plan & prepare → Choose**. Choose **Discover test scenarios**. On M2LB discovery takes a few seconds.

What the M2LB run found with SkoleService chosen:

| Evidence | Found |
|----------|-------|
| Business scenarios from the specification | 130 (65 acceptance scenarios and 65 requirements) |
| UI pages in the frontend source | 59 candidates (61 pages analysed, 13 with a route) |
| API operations in contracts | 439 entries (388 operations in 40 contracts) |
| Integration journeys | 19 (the Skolenærvær test pack recognises this source) |
| System Understanding guide | 21 Skolenærvær scenarios |
| Existing tests | 2 563 tests, 1 206 naming a requirement (shown on the candidates they reference, not as candidates) |

Of 668 candidates, **199 are for manual testing**; the 469 others are technical (API operations) and belong to Technical Testing. Without a
chosen specification the same run found 538 candidates, 69 for manual testing. The **Manual testing** view is shown first, grouped by module;
the **Needs attention** view lists the 120 with an open point. Use **More filters** to filter by type, source or area, and the search box to find a scenario by name.

- *Needs attention* marks scenarios where the evidence states no expected result, or where a prerequisite or an observation is missing.
  BirkNext never invents an expected result; you add it.
- Selecting a scenario approves nothing.

## 5. Prepare test cases

1. Tick the scenarios you want to test. Your selection is kept when you change filters and when you reload the page (in the same browser
   tab only; it is never saved for others and selecting prepares nothing). A scenario whose steps are incomplete in the evidence is marked,
   for example *2 of 3 steps without an expected result* or *No steps in the source*: you will write those before you can approve it.
2. Choose **Prepare test cases**. A preview shows what will be added by kind (for example *2 acceptance scenario · 2 UI behaviour · 2 guide
   scenario*), which have no expected result in the evidence (they start as *Needs clarification*), which have no steps or steps without an
   expected result, and which overlap existing cases.
3. Confirm. Each scenario becomes a draft test case in the plan. A case from the specification carries its starting situation (Given), action
   (When) and expected outcome (Then), marked *Stated in the project's documentation — confirm by accepting*; add steps where the tester needs
   them.
4. Tests BirkNext did not find are normally added in **Azure DevOps Test Plans** after you import the exported file. Only when such a test
   must be linked to a requirement in BirkNext before the export, open **Advanced: write a test case by hand**, type a title and choose
   **Add test case**. The new case opens at its first step: write what the tester does and what should happen side by side, choose
   **Add step**, and save.

## 6. Edit, save and approve

Open a test case (click its title) to edit it:

- **Title**, **Preconditions**, **steps** (action and expected result per step) and **Priority** — what the Azure DevOps file carries.
  Norwegian text is kept as typed.
- **More fields** (collapsed): purpose, expected result of the whole case, environment, roles and access, dependencies, risk, test type,
  execution method and suite. They are kept in BirkNext but are not part of the Azure DevOps file; most testers can leave them.
- **Linked requirements** — link the case to requirements of the chosen specification.
- **Test Data** — link approved test data, or say what data is needed.

Then:

1. Choose **Save draft**. An *Unsaved changes* marker is shown until you save. Name the plan under **Save & export → Save name** (for
   example *M2LB manuell regresjon*); the name becomes the export file's name.
2. Record a decision per case: **Approve**, **Needs clarification** (write why in *Rationale*) or **Exclude** (write why).
   - The decision buttons are disabled while there are unsaved changes: **save first**.
   - **Approve** follows the export's rules: at least one step, and an action and an expected result on **every** step. The expected
     result of the whole case is not needed (Azure DevOps has no such field). Until then a line under the buttons says what is missing,
     for example *To approve: step 2 needs an expected result*. Guide scenarios (System Understanding) often list steps without an
     expected result; fill them in or ask for clarification.
3. Every saved version is kept under **Version history**; restoring an old version saves it as a new version.

The Manual Testing home now lists the plan with its number of cases and how many are approved, and its next step becomes
**Export to Azure DevOps**. In Plan & prepare the step list shows *n of m approved* and, under *Save & export*, the saved plan revision
(*Plan saved (revision 6) · no snapshot yet*). Snapshots (a frozen copy per delivery) and delivery comparison are under **Advanced**; the
export does not need them.

## 7. Export to a file for Azure DevOps

1. On the Manual Testing home, choose **Export to Azure DevOps** next to the plan (or **Plan & prepare → Export to Azure DevOps**).
2. Fill in **Area Path** (as it is in your Azure DevOps project, for example `M2LB\Manuell test`) and, if you like, **Assigned To**.
   BirkNext cannot check these against Azure DevOps; it marks them *Confirm in Azure DevOps*. Values you change are kept in this browser tab
   across a reload (a note says so); choose **Save as this project's export settings** to use them for every export.
3. Check the list of cases: each says whether it will be exported and, if not, why. Only **approved** cases are exported unless you tick
   *Include cases that are not approved yet*. A case missing information blocks the download until you fix or deselect it.
4. Choose **Excel (.xlsx)** (default) or **CSV**, and **Download**. The file is named
   `<plan>-azure-test-plans-r<version>.xlsx`. The columns are the ones Azure DevOps imports: *ID, Work Item Type, Title, Test Step, Step Action,
   Step Expected, Area Path, Assigned To, State* (plus *Description*, which says where each case came from).
5. Optionally download the **mapping report** (CSV). It lists each case's BirkNext identity, its requirements with their specification, and why
   any case was not exported.

Below the export, a collapsed **Other export: BirkNext JSON of a saved snapshot** archives a snapshot in BirkNext's own format. It is not
the Azure DevOps import file.

## 8. In Azure DevOps Test Plans

Import the file into a test plan (Test Plans → your plan → *Import test cases from CSV/Excel*), organise suites, assign testers and execute.
Record Pass, Fail or Blocked, attach evidence and raise defects there. BirkNext does not read these results back, so coverage in BirkNext shows
*test design* (prepared, approved, exported), never execution.

## Troubleshooting

| You see | Meaning | Do this |
|---------|---------|---------|
| *Choose the specification* as the next step | The project has several specifications and none is chosen | Step 2 |
| *No evidence* for business scenarios from the specification | Discovery ran before a specification was chosen | Choose it, then **Discover again** |
| Approve is disabled | Unsaved changes, no step, or a step without an action or expected result | Save, or fill in what the *To approve* line names |
| A field you expected is not shown | It is under **More fields** (not exported) or **Advanced** | Open the collapsed section |
| The download button is disabled | A selected case misses required information | Open the case from the list, or deselect it |
| A scenario says *No steps in the source* or *n of m steps without an expected result* | The evidence leaves the steps open | Prepare it and write the steps; Approve waits for them |
| *These settings were kept from before the page was reloaded* (export) | You changed export fields without saving them for the project | Save them as the project's export settings, or change them back |
| Plan & prepare is read-only | This installation does not allow changes | Ask whoever installed BirkNext |
| *The source snapshot is partial* after import | Some files are in an unsupported language | Not a blocker for manual testing |

## What was verified, and what was not

Verified on a clean Release installation with the real archive (browser run, no external network access): import, the Manual Testing home,
choosing the specification, Discover & understand, discovery, preparing, editing in Norwegian, save and reload, approving, a hand-written case,
the export (XLSX and CSV read back by an independent reader) and the mapping report, at 1440 px and 390 px.

Not verified here: importing the file into Azure DevOps (this guide's limit was *file export only*), and executing the tests.
