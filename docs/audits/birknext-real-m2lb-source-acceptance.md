# BirkNext — real M2LB source acceptance (2026-11-11)

**Verdict: REAL-SOURCE ACCEPTANCE.**
- All six named packages were available and were imported unchanged.
- No synthetic fixture stood in for a real package. Synthetic fixtures were used only in unit tests.
- Nothing was runtime-verified against the real systems. No external traffic was sent.

## 1. Source availability and handling

| Item | Result |
|---|---|
| `C:\Downloads` | **Does not exist** (`ls /c/Downloads`: No such file or directory) |
| Located instead | All six packages, with the exact names given, are in `C:\Users\ajaan\Downloads`. Used as the source location; recorded here as a deviation from the brief |
| Originals | Not modified. SHA-256 recorded before use (below). Read via copies in a session scratch folder; each extracted into its own empty directory; nothing inside a package was executed |
| Upload names | Copies were uploaded as `p1.zip`…`p6.zip`. BirkNext shows these names; the SHA-256 recorded on every snapshot identifies the original |

| Upload | Original | SHA-256 | Files | Import | Snapshot | Analysis |
|---|---|---|---|---|---|---|
| p1.zip | `M2LB (7).zip` | c7bb9f125826846355aa0da5459b7bcf262c043d598712d2cb1e6be476f654d5 | 3003 | 200 (196 s) | dd36c623… | Partial (2099 files) |
| p2.zip | `Tiltak 2 - Sikkert digitalt økosystem (1).zip` | 1293b1928ffed29229807013fa3a70b486cfb985d6e3d0b602aba7eb9a0a5739 | 503 | 200 (5 s) | 5c1a2906… | Partial (419) |
| p3.zip | `M2LB (6).zip` | a306ee4733ec3bc9bc344f5aaad119d703d3173293651d90c22f66c01c0494cf | 1975 | 200 (106 s) | d774940d… | Partial (1352) |
| p4.zip | `M2LB (5).zip` | 0af7dcf7f1eb40b2ee30ed338799f57d417d561987c3171d86bb1b1645edc2e9 | 2098 | 200 (161 s) | 52fc49d5… | Partial |
| p5.zip | `M2LB (3).zip` | bc780b6b6b5dcbf8043c686b23395e4394f9d68d2dd9e8f47469ad13c6cf1fad | 2361 | 200 (232 s) | e1852e33… | Partial |
| p6.zip | `M2LB (4).zip` | 049031372eacde844f2b80a173c6768afd8b25ec101fcd417b85f47ada230506 | 1839 | 200 (106 s) | 68bb177b… | Partial |

- **Import path:** through the public Project Import API (`preview` → `commit`), one package at a time, into an isolated API (`localhost:5496`, Development, its own content root) with the disposable database `birknext_sn_20261111`.
- **Why "Partial":** every snapshot is Partial only because BirkNext reports tool limitations (JavaScript and Python are detected but not analysed). It is not a source failure.
- **Deployment order:** neither deployment order nor the deployed version was inferred from the ZIP names or the import order.

## 2. BirkNext versus the independent oracle

| Area | Oracle (real source) | BirkNext before | BirkNext after |
|---|---|---|---|
| MU source role | Only `Tiltak 2 (1)` is MU | p2.zip, AvailableNotBound | same (correct) |
| M2LB source role | Code in `M2LB (7)`, `(3)`, `(5)`, `(6)`; specs only in `(4)` | Candidates p1, p3, p4, p5; p6 not; ReviewRequired; none chosen | same (correct). Candidate capability coverage: BN-8 |
| Contract evidence | XSD v2.0.1 in `(7)`/`(3)`, v2.0.0 in `(5)`; MU v2.0.0/v0.8.x; intake OpenAPI in all M2LB packages | Matches per snapshot | same. Spec-only contract usage label: BN-9 |
| Messaging channels | `skole.perioder` / `skole.rapporter` / `skole.utdanning` produced by SkoleService/Skole; `person.barn` / `person.person` consumed | Found (Confirmed, with file:line) | same |
| Period-closed channel | `skole.perioder` | Pack expected `skole.reports`: never found | **`skole.perioder` found** in p5.zip ("not bound") (BN-1) |
| Period-closed event | `SkolenaervaersperiodeLukket` / `SkolenaervaerPeriodClosed` | Old name only | Both (BN-2) |
| MM / intake versions | Generation-dependent | Pinned "2.0.0" | Not guessed; both described (BN-3) |
| Bufdata delivery | Implemented in the English generation | "No delivery exists (ADR-028)" | Generation-dependent (BN-3) |
| System Understanding change awareness | The guide was never checked against these packages | `sourceChanges: []` | "Sources changed — may need review (5)": p1, p2, p3, p4, p5 (BN-4) |
| System Understanding content | See the evidence matrix | Out of date in 9 areas | Rewritten from the oracle (BN-5) |
| XML → JSON mapping | Oracle table §3.2 (v2.0.1) and the v2.0.0 differences | Not represented as data (contract profile says so) | Guide steps 10–11 describe it per generation; still not machine-checkable (BN-3 note) |

## 3. Browser acceptance (Microsoft Edge, real snapshots)

Setup:
- Release frontend on `:5497`; isolated API on `:5496`.
- Every non-localhost request was aborted.
- Expected values were taken from the oracle, not from BirkNext.

| ID | Check | main | after-restart |
|---|---|---|---|
| SN-01 | Six separate snapshots in the Snapshot Registry | PASS | PASS |
| SN-02 | MU recognised only in `Tiltak 2 (1)`; the page says "1 of 3 required sources bound", MU "available but not bound" | PASS | PASS |
| SN-03 | M2LB candidates are exactly p1, p3, p4, p5 (specs-only `M2LB (4)` excluded) | PASS | PASS |
| SN-04 | No snapshot bound or chosen automatically | PASS | PASS |
| SN-05 | Guide flagged "may need review" | PASS | PASS |
| SN-06 | Five recognised snapshots named; p6 not | PASS | PASS |
| SN-07…15 | Guide content: both routes; Parquet in source and never runtime-verified; MU polling; positive-only receipt; Tjeneste at lock; MU/MM version conflict; Person events; `skole.perioder`; Data & contracts tab renders | PASS ×9 | PASS ×9 |
| SN-Z | No external request, page error or console error | PASS | PASS |

- **Totals:** main 16/16, after-restart 16/16 (a new API process on the same database), visual 7/7.
- **Responsive:** no page overflow at 1440, 1024 and 390 px on the System Understanding Data & contracts tab and on Skolenærvær Testing.
- **Screenshots inspected:** at 1440 px the generation facts read as separate rows, and the Conflicting badge is visible but restrained. At 390 px the text is in one readable column.

**Harness problems, reported as found:**
1. **Source Management.** The first check used Source Management, which showed "No sources yet". The page lists only the workspace's import records, which the Project Import UI creates; the API-driven import created none. This is recorded as BN-13. The check moved to the Snapshot Registry, which reads the backend.
2. **Skolenærvær Testing.** The page was still loading when the first SN-02 check ran, because a fresh browser profile was loading. The harness now waits for the settled readiness text.

## 4. Not run / blocked

- **Runtime verification of MU, MM, SkoleService, Person, Tjeneste or Bufdata:** NOT RUN (forbidden; no external traffic).
- **Binding snapshots in a Target Environment** was not exercised. Binding is a tester decision, and it is never automatic.
- **Executing the packages' own test suites:** NOT RUN. Packages are evidence only, and their scripts must not run.
