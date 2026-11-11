# BirkNext — Skolenærvær cross-module system audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `02bac857` (after `e8615434` System Understanding).
- **Pre-existing user files:** the working tree held other sessions' files, which were left untouched:
  - `appsettings.Local.json` (modified);
  - `AIAssisted/testdata/`;
  - `docs/audits/birknext-final-*-2026-11-07.md`;
  - `birknext-release-assurance-decision-2026-11-07.md`;
  - `birknext-automatic-integration-test-generation-audit.md`.

**Verdict: REAL-SOURCE ACCEPTANCE.**
- Six real packages were imported unchanged.
- An independent oracle was built from their C# code, contracts and configuration.
- Six BirkNext defects were found and fixed (BN-1…BN-6), and seven remain open (BN-7…BN-13).
- Eight system observations are reported.
- Nothing is runtime-verified.

## 1. Method

1. **Sources.**
   - `C:\Downloads` does not exist. The six packages with the exact names given were found in `C:\Users\ajaan\Downloads`.
   - They were hashed and copied, then extracted into separate empty folders. The originals were not modified, and nothing inside a package was executed.
2. **Independent oracle.**
   - Two read-only reviews covered the first half of the chain (Altinn → MU → MM, including the receipt) and the second half (SkoleService, Person, Tjeneste, Utdanning/SkoleAdapter, period lock and Bufdata).
   - Every claim is labelled V/T/S/I/NV with file:line.
   - The main session re-checked the highest-impact claims in source and added one correction: SkoleService also consumes Person events.
   - Result: `birknext-skolenaervaer-evidence-matrix.md`.
3. **Import.**
   - Each package went through the public Project Import API into an isolated API with a disposable database. This produced six separate snapshots.
4. **Comparison.**
   - BirkNext's own reads were compared with the oracle: source setup and role recognition, Source Analysis, Contract Evidence, journey readiness, the System Understanding guide and the curated pack profiles.
5. **Fix and verify.**
   - Each confirmed gap was fixed in BirkNext only; the M2LB source was never changed.
   - Then: focused tests, re-import-free re-verification against the same snapshots, browser acceptance with restart, and screenshots.

## 2. Findings summary

| Area | Finding | Classification |
|---|---|---|
| Two M2LB source generations | Norwegian-named (`Meldingsutvekslermottak`/`Skole`, v2.0.0, `/rapporter`) and English-named (`MeldingsutvekslerReceiver`/`SkoleService`, v2.0.1, `/reports`, Parquet delivery). BirkNext's curated knowledge described a mixture | BirkNext fixed (BN-3, BN-5); system fact SYS-3 |
| Period closure | Topic `skole.perioder`, not `skole.reports` | BirkNext defect fixed (BN-1, BN-2) |
| System Understanding change awareness | A seed opened after the import claimed those snapshots as its basis | BirkNext defect fixed (BN-4) |
| MU → MM versions | MU's schema tool ships ≤ v2.0.0; English MM accepts only v2.0.1 | System observation SYS-1 (Conflicting) |
| Receipts | Positive only, even for rejections | SYS-2 |
| Person | HTTP lookup plus events; only `M2LB (7)` implements the English lookup routes | SYS-4; BirkNext fixed (BN-6) |
| Tjeneste | Called at lock, not at intake | BirkNext guide fixed (BN-5); topology gap BN-7 |
| Utdanning | Adapter code in the English generation only; CDC not live; codes `unknown`; `long?`/`int?` | SYS-5, SYS-6 |
| Bufdata | Parquet and manifest written in the English generation; no consumer anywhere; blob endpoint empty | SYS-7 |
| Cross-module consistency | Source Management misses API-imported snapshots | BN-13 |

The full register is in `birknext-skolenaervaer-integration-gap-register.md`. The acceptance reports are `birknext-real-m2lb-source-acceptance.md` and `birknext-skolenaervaer-cross-module-acceptance.md`. The tester-facing reference is `../architecture/birknext-skolenaervaer-system-reference.md`.

## 3. XML → JSON mapping

- **Verified against source:** the mapping was checked field by field in `SkolenaervaerMapper.cs` and its tests (evidence matrix §3.2).
- **v2.0.1 vs v2.0.0:** v2.0.1 has the hours at the root. v2.0.0 has them under `SkolenaervaerRegistrering`; the period field names also change (`startDato`/`sluttDato` vs `startTidspunkt`/`sluttTidspunkt`).
- **Where BirkNext shows it:** the guide (steps 10–11 and Data & contracts) explains the mapping per generation.
- **Limitation:** BirkNext still has no machine-readable mapping. The contract profile states that the mapping is not imported as data, so no mapping-level impact is computed.

## 4. Safety

- **Writes:**
  - Only the disposable database `birknext_sn_20261111` was written (created for this task; still kept).
  - The ports used were 5496 and 5497; 5000 and 5173 were not touched.
- **Process stops:** the one isolated API process was stopped only after its command line was verified.
- **Network:** no request left localhost.
- **Git:** no push, no reset/clean/restore/stash.
