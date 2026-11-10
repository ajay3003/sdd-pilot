# BirkNext — Multi-source end-to-end integration testing: acceptance (2026-11-10)

Branch `008-traceability-first`. Product commits: `0ed3fe56` (backend) and `e6b00385` (UI), plus the acceptance fixes listed in the gap register (committed with these reports). No push, no deployment.

## 1. Environment

| Item | Value |
|---|---|
| Stack | Real BirkNext API (scratch build of the working tree, Debug) on `:5098`; Release-published frontend on `:5198`; Edge headless (Playwright, real browser). Every non-localhost request is aborted, and outbound proxies point at a dead port. |
| Database | Disposable `birknext_mse_20261110`, recreated at the start of each run. User databases are untouched. |
| Synthetic integration | `synth_services.py` on `:5299`: Altinn → MU → MM → Skolenærvær → Person → period lock → Bufdata. Asynchronous hops, an independent ledger (`out/ledger.jsonl`), and modes normal / mm-reject / wrong-correlation / submit-hangs. |
| Real archives | Imported read-only through the real Project Import UI: `Tiltak 2 - Sikkert digitalt økosystem.zip`, `M2LB.zip`, `M2LB-language.zip`, `M2LB.Common.zip`, `Terraform-iac-meldingsutveksler.zip`. No real system was contacted. |
| Synthetic packages | `SynthPackageA` (Altinn), `B` (MU + MM), `B-legacy` (older MM schema), `C` (Skolenærvær, Person, period lock), `D` / `D-v2` (Bufdata delivery; v2 secured). |
| Oracles | `real-oracle.json`: a stdlib zip reader plus the documented word rule; it computes project keys, stage folders and Skolenærvær XSD namespaces independently of BirkNext. `synthetic-oracle.json`: the package definitions. The ledger is the oracle for every run. |
| Widths | 1440 / 1280 / 1024 / 768 / 390 px |

## 2. Results — final run (third full run)

**Main phase: 62/62 PASS. After API restart: 6/6 PASS.**
- No external request.
- No page error.
- No unexpected console error. One expected 401 came from SS-33, the anonymous refusal.

### Real sources (RS)

| ID | Check | Result |
|---|---|---|
| RS-01 | MU stage: only Tiltak 2 (`buf.meldingsutveksler`); M2LB's MM folder named after MU is excluded | PASS |
| RS-02 (×4) | mm, skole-intake, person-tjeneste, period-lock: M2LB and M2LB-language are separate projects → AMBIGUOUS | PASS |
| RS-03 | Bufdata recognised only through the producer's dataset contract; no consumer source | PASS |
| RS-04 | Altinn: no imported source; covered only by the built-in Altinn app reference | PASS |
| RS-05 | M2LB.Common (library) and the Terraform archive (analysis Failed: no application code) are supporting evidence only | PASS |
| RS-06 | An ambiguous stage is never preselected | PASS |
| RS-07 | MU → MM XML INCOMPATIBLE, as the independent inventory says: sender v0.8.2 / v2.0.0 (flat, altinn); receiver v2.0.1.flat | PASS |
| RS-08 | Skolenærvær → Bufdata: dataset contract SPECIFIED_BUT_NOT_RUNTIME_VERIFIED | PASS |
| RS-09 | Source coverage BLOCKED by the incompatible transition, kept separate from the snapshot analysis status | PASS |
| RS-10 | Generation refused while blocked unless a partial draft is chosen explicitly | PASS |
| RS-11 | The real composite draft is labelled PARTIAL and Draft | PASS |
| RS-12 | Three readiness levels: Blocked / Partial / Blocked; Complete E2E is never Ready | PASS |
| RS-13 | MU, MM, Skolenærvær, Person and Bufdata have no BirkNext executor or observer for the real flow | PASS |
| RS-14 | Actionable blockers: MU → MM (test lead), period lock (operator), Kode 6/7 domain decisions, Bufdata delivery | PASS |
| RS-15 | Bufdata SPECIFIED_BUT_NOT_RUNTIME_VERIFIED; no full E2E claimed | PASS |

Real facts recorded by the run:
- **Discovery:**
  - altinn: Confirmed (built-in reference)
  - mu: Candidate (`buf.meldingsutveksler`)
  - mm, skole-intake, person-tjeneste, period-lock, bufdata: Ambiguous (`m2lb`, `m2lb-language`)
- **MU → MM dimensions:**
  - Source version: Compatible
  - Message schema (XML): **Incompatible**
  - Messaging channel: Missing evidence
  - Correlation fields: Missing evidence
  - Authentication and Environment: Not assessed

### Synthetic sources (SS)

| ID | Check | Result |
|---|---|---|
| SS-01–05 | Every stage offers the oracle's project; real + synthetic claims of one stage are AMBIGUOUS; MU → MM XML and channel compatible; period lock → Bufdata topic declared on both sides; source coverage READY after approval | PASS |
| SS-06–07 | One composite suite over four separately imported sources; each contract keeps project and stage; Test Data bound from the contract (synthetic enums) | PASS |
| SS-08–12 | Plan with executor, observers and lock action and the documented identifier mappings; the plan stays a Draft until approved; readiness Ready / **Ready** / Needs review; explicit approval; nothing reached the target before the authorised run | PASS |
| SS-13–19 | Preflight names FULL_E2E and the two data-changing operations; verdict FULL_E2E_VERIFIED with 7/7 stages Succeeded; 6 boundaries CONFIRMED by echoed identifiers equal to the ledger's (submission, message, report, period); one transaction in the ledger with the exact transitions; each data-changing call sent once; bounded observation; correlation header on every request | PASS |
| SS-20 | Altinn → MU subflow = SUBFLOW_TEST, verdict SUBFLOW_VERIFIED (never full E2E) | PASS |
| SS-21 | MM never shows the message → TIMED_OUT (not proof of failure), later stages BLOCKED, verdict INCOMPLETE | PASS |
| SS-22 | MM echoes another message id → WRONG_CORRELATION although the calls passed | PASS |
| SS-23 | Submission never answered → UNKNOWN_OUTCOME, sent exactly once | PASS |
| SS-24 | Suite outcome Verified only from the one continuous run | PASS |
| SS-25–26 | Older MM schema → INCOMPATIBLE and BLOCKED; accepted with a reason, the state stays INCOMPATIBLE and coverage becomes Ready | PASS |
| SS-27–30 | Newer synth.delivery snapshot proposed with its stage and never applied automatically; new context revision with the change recorded; suite revision 2 with drift "v1 → v2"; no duplicate contracts | PASS |
| SS-31 | The secured v2 delivery API blocks the Bufdata stage with an actionable Authentication blocker | PASS |
| SS-32–34 | Unchanged inputs reuse the revision; without a session nothing can be associated or run (401); the context stores references only (no contract text, no secrets) | PASS |

### UX, layout, keyboard

| ID | Check | Result |
|---|---|---|
| MS-U1 / U2 | Opens on Integration & Sources with four work areas; no giant dropdowns | PASS |
| MS-U3 / U4 | A link to revision 1 says revision 3 is current and opens it on request; the address bar follows the opened revision | PASS |
| MS-L1–L4 | Sources, generate, readiness, execute: no page-level horizontal scroll at the five widths (screenshots in `out/`) | PASS |
| MS-K1 | Work areas reachable with Tab, opened with Enter | PASS |
| MS-Z | No external request, page error or console error | PASS |

### After API restart (PR)

| ID | Check | Result |
|---|---|---|
| PR-01 | Composite runs still listed | PASS |
| PR-02 | The continuous run reopens as FULL_E2E_VERIFIED with its four exact source versions (SHA-256) | PASS |
| PR-03 | The context keeps its revisions and approval decisions (who, when) | PASS |
| PR-04 | The real context reopens with MU → MM still INCOMPATIBLE | PASS |
| PR-05 | The real partial draft reopens as PARTIAL | PASS |
| MS-Z | Clean console and network | PASS |

## 3. Run history (nothing hidden)

| Run | Main | After restart | What failed and why |
|---|---|---|---|
| 1 | 53/60 | 5/6 | SS-10, SS-17, RS-05 and the stale-revision 409 were product defects MS-D1…D4. A harness `settle()` wait on a by-design disabled button caused 10-minute stalls, so the session expired and SS-28/29/31, MS-K1 and PR-03 failed. |
| 2 | 61/62 | 6/6 | SS-31: the regenerated v2 contract's positive test is still bound to v1 (contract evolution never rebinds it), so the planner found "no positive test" instead of the real blockers. This was product defect MS-D5. |
| 3 | **62/62** | **6/6** | none |

No timeout was increased and no check was weakened. The harness changes were:
- waiting on the page's own `aria-busy`;
- a Tab bound large enough for the sidebar, with an honest failure if focus never reaches the area tab;
- new checks MS-U3/U4.

## 4. Automated tests

| Suite | Result |
|---|---|
| Focused backend: IntegrationSuites, ContractTest, IntegrationJourney, SkoleNaervaer | 549/549 PASS, including `CompositeSuiteTests` 9/9 with the new assertions for MS-D1/D3/D5 |
| Focused frontend: IntegrationSuites, Navigation, Documentation, Guide | 412/412 PASS, including `IntegrationSuitesCompositePageTests` 6 |
| Release frontend publish | PASS |
| Full regression (six memory-guarded backend partitions, then the full frontend suite, then the Release build) | Run once, sequentially, on the combined tree after the next task (Test Execution & Evidence). Results are recorded in `birknext-unified-execution-evidence-acceptance.md` §Regression. |
