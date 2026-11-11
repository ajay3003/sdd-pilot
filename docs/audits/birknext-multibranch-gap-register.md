# Multi-branch source context — canonical gap register (tasks 14 + 15)

This is the one register for tasks 14 and 15. `birknext-m2lb-multibranch-gap-register.md` points here.

**Columns:**
- **Priority:** P0–P3.
- **Kind:** D = BirkNext defect, A = source ambiguity, U = deployment uncertainty, M = missing metadata, X = intentional user decision.

Root causes are in `birknext-multibranch-source-context-audit.md`.

> **Verification status (2026-11-12):**
> - FIXED rows are covered by focused tests: backend 271/271, frontend 589/589.
> - The real five-package browser acceptance was **interrupted**. Four of the five archives were imported through the UI into an isolated install before
>   Claude Code stopped the background services because the machine ran low on memory.
> - Browser check ids named in this table (BL-*, CMP-*, DUP-*, UI-*, VIS-*) are therefore **NOT RUN** unless a later acceptance report says otherwise.

| Id | Pri | Category | Module | Kind | Expected | Actual (before) | Fix | Regression test | Status |
|---|---|---|---|---|---|---|---|---|---|
| MB-1 | P0 | TOPOLOGY | Integration Topology | D | One version per logical source | Newest per ZIP name, so branches were merged | Shared resolver | `SourceBaselineServiceTests.Resolve_*`; browser BL-0/2/4 | FIXED |
| MB-2 | P1 | CONTRACT_COMPATIBILITY | Contract Testing source candidates | D | Contracts of the analysed version only | Newest per contract across branches | Shared resolver | resolver tests; focused contract-registry suite | FIXED |
| MB-3 | P1 | BASELINE_RESOLUTION | System Understanding basis | D | Cites the analysed version; a switch marks claims | All versions cited | Shared resolver | focused SystemGuide suite | FIXED |
| MB-4 | P1 | PROJECT_QUALITY / RECOMMENDED_WORKFLOW | Technology coverage → Dashboard/Workflow | D | Analysed version | Newest overall | Shared resolver | browser BL-3/5 | FIXED |
| MB-5 | P1 | BASELINE_RESOLUTION | Composite discovery | D | Baseline or newest, one per project | Newest only | `Discover(..., baselines)` | focused Composite suite | FIXED |
| MB-6 | P1 | TARGET_ENVIRONMENT / SOURCE_ROLE | Project Evidence | D | One decision ("choose the baseline"); baseline = application source | "Conflict", N reviews, no application source | Version-aware association; main package follows an automatic choice only | `ProjectEvidenceVersionsTests` (4) | FIXED |
| MB-7 | P1 | BASELINE_RESOLUTION | Analysis baseline | D | Explicit, persisted, history, no implicit "latest" | None | `source_decisions` + `SourceBaselineService` + API + UI | service tests; browser BL-1/6/7 | FIXED |
| MB-8 | P2 | SNAPSHOT_INTEGRITY | Fingerprints/duplicates | D | Archive vs content identity distinguished | Archive hash only; misleading duplicate text | Canonical content fingerprint; content duplicate | `SourceContentFingerprintTests`, `ProjectImportContentDuplicateTests`; browser DUP-1/2 | FIXED |
| MB-9 | P2 | SOURCE_IDENTITY | Branch identity | M | Verified / Declared / Unknown | No declared field | `DeclaredBranch` decisions; Unknown shown | service test; browser UI-2 | FIXED |
| MB-10 | P1 | UI_UX / SOURCE COMPARISON | Source Change Impact | D | Compare any two versions of one source | Refused across imports; panel not hosted | Rule relaxed; panel hosted in Source versions | `SourceChangeImpactServiceTests`; browser CMP-1..3 | FIXED |
| MB-11 | P3 | SNAPSHOT_INTEGRITY | Import again | D (minor) | New record per import | Same bytes update the record in place (snapshots stay immutable) | — | — | OPEN (documented) |
| MB-12 | P2 | SOURCE_IDENTITY | Grouping basis | A | Verified repository identity | Archive-name basis (no root .sln, no Git metadata) | Basis shown ("not verified repository identity") | browser ID-1 | ACCEPTED (honest presentation) |
| MB-13 | — | DEPLOYMENT | Deployment baseline | U | Evidence-backed only | None | Nothing inferred; UI says "not a deployment claim" | — | NOT APPLICABLE (no deployment evidence; `DEPLOYMENT_BASELINE_NOT_VERIFIED`) |
| MB-14 | P2 | SNAPSHOT_INTEGRITY | DB guard | D (hardening) | Append-only enforced in DB | Code is insert-only; no trigger (local data reset deletes rows) | — | — | OPEN |
| MB-15 | P2 | COMPONENT_OWNERSHIP | Component-level composite baseline | — | Explicit mixing of components from different snapshots | Not implemented (Composite contexts cover explicit multi-source runs) | — | — | OPEN (deliberately not built: no confirmed need; avoids accidental stitching) |
| MB-16 | P2 | TEST_DATA / INTEGRATION_SUITE / PROVENANCE | Pins | — | Revisions pinned | Already pinned by existing revision/manifest infrastructure (immutable manifests, DB trigger) | — | existing suites | VERIFIED (no change) |
| MB-17 | P2 | SPECIFICATION / TRACEABILITY | Per-snapshot review decisions | — | Decisions tied to exact revisions | Existing revision model; not re-tested against the five archives (they contain no `specs/` requirements except in (7)) | — | — | NOT RUN |
| MB-18 | P2 | BACKUP_TRANSFER | Export of decisions | D | Baselines travel with packages | `source_decisions` are in full backups (database dump), but not in `.birknext` project packages | — | — | OPEN |
| MB-19 | P3 | UI_UX | Import duration | — | Responsive | Real imports take 1.5–4 min (full analysis) | — | measured | OBSERVED |
