# BirkNext — User documentation & Help Center: gap register (2026-11-11)

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## Closed

| ID | Gap | Status | Evidence |
|---|---|---|---|
| UD-C1 | Search covered titles and keywords only | PASS | Body, headings and structured lists; synonyms; excerpts (HelpCenterTests; HC-02, HC-10, HC-21) |
| UD-C2 | No contextual help component; most features never linked to the guide | PASS | `HelpLink` on 6 pages; source-scan test; HC-06–HC-08 |
| UD-C3 | Integration Suites and Parquet were missing from navigation and search | PASS | Registered topics |
| UD-C4 | No Suggested E2E / Edge Companion DOM-verification article | PASS | `suggested-e2e` |
| UD-C5 | Execution outcomes and historical results were not in the status reference | PASS | Two new tables |
| UD-C6 | No versioning documentation (artifact or application) | PASS | `versioning` article; troubleshooting |
| UD-C7 | No per-article applicability or verification | PASS | `GuideArticleMeta`; only recorded verification is claimed |
| UD-C8 | Old-style landing (one task grid) | PASS | Search-first landing, audience areas, category cards (HC-01, HC-L1/L2) |
| UD-C9 | MU → MM risk undocumented | PASS | Worded to MM-G1; no confirmed incompatibility is claimed |

## Remaining

| ID | Gap | Status | Note |
|---|---|---|---|
| UD-G1 | `MarkdownRenderingService` (explorers, imported documents) does not call `DisableHtml()`; `<iframe>`/`<form>`/`<object>`/`<style>` can reach `MarkupString` | OPEN (security, outside the Help Center) | Needs a reviewed fix (escaping HTML changes existing document rendering) plus real tests; the current tests only assert that text exists |
| UD-G2 | 19 features are only partially documented (shared broad articles or generic catalog detail) | PARTIAL | Listed in the audit §4 |
| UD-G3 | Two status vocabularies (guide 16 + outcomes vs `StatusVocabulary` 48) are not reconciled | OPEN | — |
| UD-G4 | About 36 articles not re-verified against 1.0.0 | PARTIAL | Each says "verification not recorded"; nothing claims to be current |
| UD-G5 | No release-notes source, so What's new stays unavailable | NOT SUPPORTED | Release notes need an owner and a source; commit messages are not used |
| UD-G6 | Documentation change impact (reusing Unified Impact Analysis) | NOT RUN | The structural guard catches route, anchor and navigation drift; workflow-semantics drift is not detected |
| UD-G7 | Workflow acceptance A–H run end to end through the documentation | PARTIAL | Workflows are verified in their own feature acceptances (Backup & Transfer, Artifact and Application Versioning, Contract Testing); this task verified the guide's navigation, links and claims, not a fresh run of every workflow |
| UD-G8 | Real M2LB documentation acceptance | PARTIAL | The MU → MM wording follows the M2LB/Tiltak 2 static analysis (MM-G1); suggestion counts are kept qualitative (not re-verified) |
| UD-G9 | Field-level help (tooltips per field) | NOT RUN | Contextual links per page only |
| UD-G10 | Scratch database `birknext_hc_20261111` | OPEN (needs approval) | Kept |
