# Source Management — UX implementation (2026-11-13)

Audit: `docs/audits/birknext-source-management-ux-audit.md`. Presentation-only changes in `Pages/SourceManagement.razor` (+ `.razor.css`) and `SourceManagementPresentation.BucketTone`.

| Change | Detail |
|---|---|
| One overall analysis state per row | `Source Analysis: Partly analyzed` (the bucket label, with the same tone as the area chips). The per-area chips are behind **View analysis details (n areas)**, a native `<details>`. The inspector still lists every area |
| Single source | One summary line: *1 imported source · 1 application · Source Analysis: … · No open source-review decisions*. The tiles stay in the DOM, hidden only visually, so their test ids and screen-reader text remain. No toolbar |
| Several sources | Tiles (which filter) and toolbar unchanged, plus one sentence: *Requiring review* counts decisions a person must make; *analyzed / partly analyzed / not analyzed* say how much BirkNext could analyse; the two are independent |

**Unchanged:**
- Import source and Snapshot Registry in the header; the Source versions panel and baseline selection; the empty state; the inspector; filters and sorting for several sources.
- All source data, buckets, baselines and history.

## Verification
- Existing Source Management, Source Context and related tests: 62/62.
- New tests:
  - a single source gives one summary line, no tiles shown, no toolbar, and the header actions present;
  - several sources keep tiles and toolbar plus the review-vs-analysis note;
  - bucket tone mapping.
- Full frontend suite and browser acceptance: see `docs/audits/birknext-source-management-browser-acceptance.md`.
