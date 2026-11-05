# QA Review Studio — Master Hardening (2026-11-05)

Branch `008-traceability-first`, not pushed. NEXT43. It builds on NEXT41 (`813f94a1`) and NEXT42 (`8627d40c`), whose shared fixes cover Generated Documentation Explorer/Health, Technology Coverage, Dependency Review and Pipeline Review. Those were re-checked here, not redone.

> **Scope decision.** The brief covers 15 modules and asks that, when scope exceeds one pass, shared correctness and high-risk bugs come first. This pass delivers:
>
> - the shared baseline selector (B2), the brief's headline defect;
> - the default-source identity rule (B1/R);
> - Specification Given/When/Then parsing (J);
> - a crash found during browser verification (Code Risk).
>
> The remaining module items are listed per module as NOT IMPLEMENTED with what was observed. None is claimed.

## 1. Executive summary

| Area | Before | After | Label |
|---|---|---|---|
| Baseline selector (Test Coverage & Overlap, Code Risk) | A baseline was offered only when the repository key matched exactly. The key is the import's repository or archive name (`m2lb` vs `m2lb-language`), so M2LB was silently unavailable as a baseline for M2LB-language, and the select was disabled with no reason | One shared rule (`BaselineEligibility`): same identity is eligible; every other snapshot is listed under "Other repositories — confirm first" with the reason. It can be compared only after an explicit confirmation, which the run sends and the stored result records as "Declared by the reviewer … Not detected". When nothing exists, the page says why | **VERIFIED** (unit, integration, browser) |
| Default current snapshot (Test Coverage, Code Risk) | The newest snapshot (M2LB.Common, imported last) | The Target Application's main package (M2LB), as a default only; explicit selection wins; it never decides the baseline | **VERIFIED** |
| Code Risk change review on real data | **HTTP 500**: `ToDictionary` on endpoint keys, while the same key (`HTTP\|GET /\|minimal`) occurs in several services | Compared per key (lookup); authorization counts as removed only when no current occurrence is authorized; each key reported once | **VERIFIED** (regression test; browser run M2LB-language vs M2LB: 1809 changed files) |
| Specification Given/When/Then (inline numbered scenarios) | Case-insensitive keywords split on a lowercase "when"/"then" inside a clause (truncated When, Then starting mid-sentence); "**Then**:" with a colon lost the Then and reported it missing | Split on bold-marked keywords when used, else capitalised keywords only; colon allowed; And/But and continuation lines stay in Then; a step absent in the source stays null (never manufactured) | **IMPLEMENTED** (unit; the explorer needs a module selection to show it, which writes the workspace, so not browser-verified) |

## 2. Modules audited

All 15 were opened on the M2LB copy (read-only probe). Test Coverage & Overlap and Code Risk were exercised end to end. The document explorers, Task Alignment, Document Quality and Documentation Drift all start with "Choose a module" (13 modules with their own documents). Choosing one changes the saved workspace, so their module-level content was not exercised in the browser.

## 3. Root causes

| RC | Cause | Fix |
|---|---|---|
| RC-B2 | Baseline eligibility hidden in each page as `RepositoryKey ==`, with identity from names; nothing explained or overridable | Shared `BaselineEligibility` (`For` / `Unavailable` / `Validate`) in `ReviewSourceContracts.cs`; both backends validate through it |
| RC-ID | "Newest snapshot wins" as the default current snapshot (same cause as NEXT41 WF-02 and NEXT42 RC-2) | `ReviewSourceDefaults.Current` (Project Evidence main package, then newest) |
| RC-AIC | Uniqueness assumed for endpoint keys | Lookup per key |
| RC-GWT | Case-insensitive keyword regexes over the whole item | Keyword positions from bold markers or capitalised keywords |

## 4. Changes

**Shared contracts**

- `BaselineRelationKind`, `BaselineRelation`, `BaselineCandidate`, `BaselineEligibility`.
- `TestCoverageReviewRequest` and `AiCodeReviewRequest` gain `BaselineSameRepositoryConfirmed`.
- `TestCoverageReviewResult` and `AiCodeReviewResult` gain `BaselineRelation`.
- All new fields are optional, so stored results stay readable.

**Backend**

- `TestCoverageReviewService` and `AiCodeReviewService` validate the baseline through the shared rule and store the relation.
- `AiCodeReviewEngine.Security` handles duplicate operation keys.

**Frontend**

- `BaselineEligibilityNote` (shared component): why no baseline, others with reasons, the confirmation.
- Both pages: grouped baseline options; the run is blocked until confirmation ("Confirm that the baseline is a version of the same repository…"); changing the baseline clears the confirmation.
- Test Coverage shows the relation in the result header; Code Risk's change provenance shows the recorded relation instead of a fixed "same repository".
- `ReviewSourceDefaults` for the default current snapshot.
- `SpecExplorerService.SplitInlineBdd` rewritten.

## 5. Shared infrastructure

- **Reused:** `ReviewSourceSnapshot` / `SourceRepositoryIdentity`, Project Evidence (main package), existing review services and stores.
- **No migration:** the declaration travels with the run request and is stored in the run's result JSON.
- **Persisted associations:** a reusable repository-association store (declare once, reuse across reviews) was **not** built, because it needs a schema change. Today the declaration is made per run and recorded per run.

## 6. Baseline comparison

The brief's scenario was reproduced on the copy: M2LB-language (two snapshots) and M2LB (one).

| Current | Baseline offered |
|---|---|
| M2LB-language | Its own earlier snapshot is eligible. M2LB is listed with "Different repository identity: M2LB (archive file name) vs M2LB-language … similar names are never treated as the same repository" |
| M2LB | No same-identity snapshot exists. The page says "No other snapshot of this repository identity (M2LB) is analyzed" instead of a silently disabled list |

The browser run compared M2LB-language (current) with M2LB (baseline, declared) in Code Risk and stored the result with the declared relation.

## 7. Evidence and provenance

- Every change result now names how its baseline relates to the current snapshot: same identity with its basis, or declared by the reviewer.
- Feature and main evidence are never merged: the comparison stays two immutable snapshots.

## 8. UI/UX

- **Baseline select:** grouped into "Same repository" and "Other repositories — confirm first".
- **Confirmation block:** warning-bordered, with the reason.
- **Disabled run:** says what to do.
- **Phone width:** no horizontal overflow at 390 px (Code Risk).

## 9. Tests added

| Suite | Test | What it covers |
|---|---|---|
| Frontend `BaselineEligibilityTests` | 3 tests | Eligibility, validation, default source |
| Frontend `AiGeneratedCodeReviewPageTests` | +1 | Confirmation required, sent, and cleared on change |
| Frontend `AiGeneratedCodeReviewPageTests` | 1 rewritten | Other repositories listed apart instead of hidden |
| Frontend `SpecExplorerServiceTests` | +6 | Inline GWT theory (5 cases) + multi-line Then |
| Backend `AiCodeReviewEngineTests` | +1 | Duplicate operation keys |
| Backend `AiCodeReviewEngineTests` | Service test extended | Same-identity relation; declared relation stored and re-read |

## 10. Builds and tests executed

| Run | Result |
|---|---|
| Full frontend suite | **8192 passed, 0 failed** |
| Full backend suite | **5909 passed, 13 skipped, 0 failed** |

**Browser** (`scratchpad/h43-e2e.cjs`): Edge, `locale nb-NO`, M2LB copy, environment `ci-live`.

- **Result: 11/11.**
- **First run:** stopped at the run step on the Code Risk HTTP 500 (RC-AIC). After the fix: 11/11.
- **Writes:** one Code Risk run to the throwaway DB; nothing else was written (Z3).

## 11. Remaining / not implemented

| Module | Observed on the copy | Status |
|---|---|---|
| C Generated Documentation Explorer | Disclaimer and document types present; structured/source/provenance views exist | NEXT42 verified; brief items (structured-item ↔ source navigation, Mermaid escaping, selection across tabs) **NOT IMPLEMENTED** |
| D Generated Documentation Health | NEXT42 RC-5 (freshness adds up, denominator named) | Remaining (filters, review-suggested reconciliation) **NOT IMPLEMENTED** |
| E Technology & Analysis Coverage | NEXT42 scope sentence | Detection vs support taxonomy, filters **NOT IMPLEMENTED** |
| F Dependency Review | Modes and "readiness is not a health result"; "version difference, not an incompatibility" already worded | Renovate semantics labelling, synthetic-scenario labelling **NOT AUDITED** |
| G Pipeline Review | NEXT42 fixes (outcome, default source, structure wording) | Gating classification (always()/continueOnError), artifact reuse vs rebuild **NOT IMPLEMENTED** |
| H Test Coverage & Overlap | Baseline fixed | Journey view, test-level classification, mocks highlighting **NOT AUDITED** (no run on the copy) |
| I Code Risk | Baseline and crash fixed; current-state vs change-risk rules shown | Grouping/priority, stale detection **NOT AUDITED** |
| J Specification Explorer | GWT fixed (unit) | Clarification classification, suggestion dedupe, test↔user-story links, layout **NOT IMPLEMENTED** |
| K–N Constitution / Data Model / Plan / Task Explorer | Each requires a module choice | **NOT AUDITED** |
| O Task Alignment | "Not run — Specification and Task need selection; nothing is picked automatically" (correct) | Technical-only re-evaluation **NOT AUDITED** |
| P Document Quality Review | "Evidence scope: Project documentation … does not assess source implementation or runtime behavior"; "Available … is not a review, an approval or a pass" (correct) | Score explanation, false positives **NOT AUDITED** |
| Q Documentation Drift | Comparison modes listed | Count reconciliation (492/91/7/18) **NOT AUDITED** |
| B1 | Repository identity remains name-derived when an import has no repository identity (the basis is shown, e.g. "archive file name") | A persisted, reusable repository association **NOT IMPLEMENTED** (needs a schema change) |
| B6 | Shared UI patterns across all modules | **NOT IMPLEMENTED** beyond the baseline block |

## 12. Files changed

**Shared:** `ReviewSourceContracts.cs`, `TestCoverageContracts.cs`, `AiCodeReviewContracts.cs`.

**Backend:** `TestCoverageReviewService.cs`, `AiCodeReviewService.cs`, `AiCodeReviewEngine.cs`.

**Frontend:**

- `Components/BaselineEligibilityNote.razor` (+ CSS)
- `Pages/TestCoverageReview.razor`, `Pages/AiGeneratedCodeReview.razor`
- `Services/ReviewSourceDefaults.cs`, `Services/SpecExplorerService.cs`

**Tests:** as in §9.

## 13. Manual verification scenarios

1. Open Code Risk Review. The current snapshot defaults to M2LB, not M2LB.Common.
2. Choose M2LB-language as current. The baseline list shows its earlier snapshot under "Same repository" and M2LB under "Other repositories — confirm first".
3. Choose M2LB as baseline. Run is blocked with "Confirm that the baseline is a version of the same repository…". Tick the confirmation and run. The result's change provenance reads "Declared by the reviewer: M2LB and M2LB-language are versions of the same repository. Not detected — their repository identities differ."
4. Open Test Coverage & Overlap with M2LB current. "No other snapshot of this repository identity (M2LB) is analyzed", with the other repositories listed and their reasons.
5. Specification Explorer → choose a module with inline scenarios that contain "and then", or "**Then**:". When and Then are split at the real step keywords.
