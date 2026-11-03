# Status vocabulary — Phase 0 audit map (2026-11-03)

Scope: queue item #14 ("one global status vocabulary, one badge system, metadata separated from status").
Companion documents: `status-vocabulary.md` (the generated dictionary) and `birknext-status-consistency-audit-2026-11-03.md` (the logic audit).

## 1. Inventory (frontend `BirkNext.Web`, build output excluded)

| Measure | Count | Note |
|---|---|---|
| Distinct `*-pill` / `*-badge` / `*-chip` / `*-tag` class families in Razor | 235 | Each page grew its own pill with its own tone names |
| Hard-coded hex colours in component CSS | ~6,500 | Design tokens exist (`wwwroot/css/design-tokens.css`) but most components bypass them |
| CSS files using `text-transform: uppercase` | 98 | Many apply it to status labels, which hides sentence case and harms screen-reader output |
| Test files pinning pill/badge classes | 96 | Any mass rename breaks these; migration has to go surface by surface |
| Shared status components before #14 | `ReviewStatusBadge`, `SourceStatusBadge` | Each had its own vocabulary and tones |

Tone vocabularies observed (about 20): `ok/warn/bad`, `success/warning/danger`, `good/attention/blocked`, `is-positive/is-caution`, `tone-*`, `state-*`, ad-hoc `green/amber/red`, plus per-feature enums (`SourceEvidenceState`, `ProjectActivityStatus`, `HeadlessRunStatus`, `ReviewDecision`, `TestDataXmlReadinessState`, …).

## 2. Conflicts found (same word, different meaning, or the reverse)

| Term | Conflict | Resolution in the vocabulary |
|---|---|---|
| Ready | "Ready" for both "inputs present" and "verified at runtime" | Ready = prerequisites satisfied for *this activity*; Verified is a separate family |
| Needs review | Used for unresolved references, ambiguity and pending approval | Needs review = human judgement required; Infrastructure "Needs review" count renamed to **Unresolved references** |
| Partial vs Detected | The Infrastructure header said "Partial" while the overview card said "Detected" | The header now shows the overview's canonical area state (one truth per domain) |
| Completed (fallback) | `ProjectActivityService` mapped unrecognised statuses to Completed | Now maps to **Unknown** (neutral) |
| Not configured / Missing | Both used, sometimes red | Missing (neutral: nothing configured yet is not an alarm), never red |
| Valid / Validated | Client-claimed "Valid" vs server-validated | Validated = server validation recorded; client claims map to it only through `FromLegacy` for display |
| Environment, type and confidence shown as status pills | "Development" and "OpenAPI" rendered in warning or info status colours | `MetaBadge` (`BadgeKind`) for metadata, never status colours |

## 3. Persisted values

- Statuses persist as strings or enums in several stores. `StatusVocabulary.FromLegacy` maps every known legacy label and sends unknown values to **Unknown**, so old snapshots never render as Ready or as an error.
- `FrontendReviewAccessScope` was serialised as a number; it now writes strings (`JsonStringEnumConverter`) and still reads the old numbers.
- `ProjectActivityStatus.Unknown` is appended (not inserted), so stored numeric values keep their meaning.

## 4. Decision

A full migration of 235 pill families and 96 pinned test files in one pass is not feasible without risking regressions. #14 therefore delivers:

1. Foundation: `StatusVocabulary` (one definition per term: label, family, intent, icon, meaning, use-when and do-not-use-when), `SemanticStatusBadge`, `MetaBadge`, and an intent→token mapping defined once.
2. A generated dictionary (`docs/status-vocabulary.md`) whose drift fails a test.
3. Key surfaces migrated: Source Analysis overview cards, coverage, legend and domain detail headers; ReviewStatusBadge fallback; Project Activity Unknown; environment and confidence metadata in AQR/FQR summaries and the API target picker.
4. Remaining surfaces are listed in the final report as follow-up work: migrate per page, then delete that page's pill CSS and its pinned assertions together.
