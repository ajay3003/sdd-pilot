# BirkNext — System Understanding: acceptance (2026-11-11)

- **Environment:**
  - Microsoft Edge, with every non-localhost request aborted;
  - the Release frontend on `localhost:5497`;
  - an isolated Development API on `localhost:5496`, with its own content root and the disposable database `birknext_su_20261111`.
- **Not touched:** the developer instance (5000/5173) and its database.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN.

## 1. Automated tests

| Suite | Result |
|---|---|
| Backend `SystemGuideTests` on a fresh migrated temporary PostgreSQL | **15/15 PASS**: seed consistency and honesty; seed once as an unreviewed draft; submit → approve → publish; published content never rewritten (database trigger) and supersession kept; one published revision per guide (unique index); identical content creates nothing; concurrent edit gives 409; source import gives "may need review" without changing content; generic second system; unsafe links (6 cases) refused; inconsistent drafts refused |
| Focused frontend (System Understanding page and presentation, navigation, Help Center, user guide, documentation catalog, Recommended Workflow, feature visibility) | **292/292 PASS** (run before the final label change; re-run in the follow-up regression) |
| First runs | Failed: page/namespace name clash in the test, a wrong bUnit keyboard API, raw-SQL `{}` placeholder. All were test-code errors, fixed |
| Full frontend regression at `e8615434` | **8630/8630 PASS** (0 failed, 0 skipped) |
| Full backend regression at `e8615434`, six sequential memory-guarded partitions | **6510 passed, 0 failed, 19 skipped (6529)**: partitions 1–5 = 1049/1098/840/652/520 passed (2/5/0/1/3 skipped). Partition 6 was stopped by the memory guard on the first run because another workload (an isolated import for the next task) ran at the same time; this was not a test failure. A re-run of partition 6 alone gave 2351 passed, 8 skipped, 0 failed |
| Build | Api.Tests and Web.Tests build succeeded (294 / 147 warnings, all pre-existing kinds) |

## 2. Browser acceptance

| ID | Check | Result |
|---|---|---|
| SU-01/02 | The sidebar item is visible and navigates to the page | PASS |
| SU-04/05 | The Skolenærvær guide opens as "Draft only — not reviewed"; the one-minute overview is readable | PASS |
| SU-06…08 | 11 components render; highlighting the main flow dims side flows; connection kinds differ by line pattern, not only colour | PASS |
| SU-09/10/44 | Node details with evidence; edge details with transport (`HTTP POST /api/skole/v1/skolenaervaer/rapporter`) and format (JSON); a node is selectable by keyboard | PASS |
| SU-11/40 | Step-by-step moves to the next step (`?step=09`); Back restores step 08 | PASS |
| SU-12…14 | Rules show implementation vs verification; Data & contracts flags the version mismatch with "Deployment compatibility: not verified"; test guidance says it is not coverage | PASS |
| SU-15/16/17 | An evidence link opens Skolenærvær Testing; a contract link opens Contract Testing; Recommended Workflow's "Understand the system flow" opens the guide | PASS |
| SU-18/19 | Authorized edit → save → submit → approve → publish (revision 1). Revision 2 opens as a draft while readers still see revision 1. Republishing revision 1 gives 409 | PASS (SU-18/19b on the second run, see §3) |
| SU-20 | Importing a synthetic source whose project is `Skoletjenesten.Api` marks the guide "Sources changed — guide may need review (1)", names the snapshot and the number of affected claims, and leaves the published text unchanged | PASS |
| SU-22 | Without the local session the guide API answers 401 | PASS |
| SU-23 | After a real API restart (new process): published revision 1, draft revision 2 and the history persist | PASS (on the second run, see §3) |
| SU-L | No page-level horizontal scroll at 1440/1280/1024/768/390 px (overview and steps); on a phone the flow is a readable list | PASS (12/12) |
| SU-Z | No request left localhost; no page or console error (every phase) | PASS |

**Totals (final):**
- main: 24 of 26 on the first run;
- draft2: 3/3;
- after-restart: 3/3;
- visual: 12/12.

## 3. Run history and visual review (failures reported as found)

| Run | Failure | Cause | Resolution |
|---|---|---|---|
| main SU-18, SU-19b | The harness took the "Editing a draft" notice for the save confirmation and navigated away before the second save finished; revision 2 was not stored (confirmed in the database) | Harness | Waits for "saved"; the `draft2` phase passes 3/3 |
| after-restart (first) | PASS, but **invalid**: stopping the task only killed the shell, so the old API (pid 33248) still served requests; the "restarted" API failed to bind | Harness | The old process was stopped after its command line was verified as this acceptance's API; re-run against a new process: 3/3 PASS |
| Visual review of the 1440 px screenshot | **Product defects:** connection curves were filled (the CSS set `fill` for lines and arrowheads together); labels were clipped between columns; upward side-flow curves crossed the Person node | Product | Separate arrowhead fills, wider columns (gap 82 px), labels shortened to the format's essence (≤ 14 characters, full text in details), upward curves routed through the gap between rows; a test was added |
| visual (after republish) | Console error: `BirkNext.Web.wasm` fetch failed once | The frontend was republished while the browser profile held the previous boot assets | Re-run: 12/12 PASS; reported here |

The final 1440 px overview screenshot was inspected:
- the main journey runs on top, with clearly styled side flows below;
- the labels are readable;
- there is no overlap.

## 4. Not run / partial

See the gap register:
- SU-G1: real-source verification of the content is the next task;
- SU-G9: synthetic fixtures B, D and J;
- project switching does not apply (guides are installation-wide, SU-G4).
