# BirkNext — Help Center information architecture (2026-11-11)

One documentation engine: the existing `/user-guide` page, its section components and `UserGuideContent`. No second framework, no CMS, no Markdown pipeline, no server-side search.

## 1. Audience areas (landing default: User guides)

| Area | Topics |
|---|---|
| **User guides** (testers, test leads, project leads, analysts) | Every topic not listed below: getting started, project & evidence, testing, integration & E2E, review & governance, troubleshooting, versions, what's new |
| **Administration** (installation, configuration, recovery) | Target Environment, Connected Systems, Browser authentication, Integration Catalog, Setup Library, System Settings, Backup & Transfer |
| **Technical reference** | Status meanings, Glossary, Feature matrix, Navigation badges, About and limitations, Feature availability |

`UserGuideContent.AreaOf(topic)` assigns each topic to exactly one area, and a test checks this.

## 2. Workflow groups (navigation and category cards)

Groups: Getting started · Project & evidence · Test setup · Testing · **Integration & E2E** · Review & governance · **Versions & workspace** · Reference.
- At most 10 topics per group (existing guard).
- The two new groups keep Testing and Reference compact.

## 3. Article model

Each article is a `<section id="topic" class="ug-topic">` with one `<h2>`, followed by `GuideArticleMeta`:
- **Breadcrumb:** Help Center › area › group.
- **Open links:** up to three, taken from `DocumentationCatalog.Features`, so they follow the navigation's real routes.
- **Verification:** "Verified against BirkNext 1.0.0 on 2026-11-11" only for articles in `UserGuideContent.Verified`, each of which records what it was verified against. Every other article says "Verification against the current version not recorded"; a modification date is never used.
- **Related guides:** an explicit, reviewed list (`UserGuideContent.RelatedTo`).

Articles use the existing patterns: lead paragraph, numbered steps (`ug-steps`), labelled callouts, tables in scroll regions, cross-links.

## 4. Search

`UserGuideSearch`:
- **Index:** built lazily in memory from the guide's own embedded sources (the files Documentation Health already checks) plus the structured lists (status terms, execution outcomes, result currency, glossary, troubleshooting, feature names). Nothing is fetched, stored or sent.
- **Matching:** every term, or one of its synonyms (login → authentication, e2e → end-to-end, version → revision/build, …), must appear in the topic.
- **Ranking:** title 8 > keywords 4 > headings 3 > body 1, plus 5 when the whole query appears as a phrase.
- **Bounds:** the query is cut to 200 characters and 8 terms; at most 12 results.
- **Excerpts:** plain text around the match, rendered by Razor (encoded) with a `<mark>`; never HTML.
- **Keyboard:** Enter opens the best match; results are links reachable with Tab; the result count is announced.

## 5. Contextual help

`Components/HelpLink.razor`:
- **Usage:** `<HelpLink Topic="contract-testing" />`. It renders `/user-guide#topic` with an accessible name ("Guide: Contract Testing").
- **Unknown topics** render nothing.
- **Placed on:** Contract Testing, Integration Test Suites, Critical E2E Regression, Test Execution & Evidence, Backup & Transfer, and System Settings → Application information.
- **Test:** scans every `.razor` file for `HelpLink Topic="…"` and fails on an unknown topic.
- **Earlier links:** the existing "Learn more" links remain.

## 6. Status vocabulary in the Help Center

Three separate tables, so readiness and outcome are never collapsed:
1. **Status meanings:** review, configuration and evidence words (Configured … Not applicable).
2. **Execution outcomes:** Passed, Failed, Error, Timed out, Blocked, Not assessed, Skipped, Cancelled, Interrupted (unknown outcome).
3. **Result currency:** Current definitions, Historical result, Currency unknown.

## 7. Safety

- The guide is Razor-encoded content; no `MarkupString` or Markdown is involved.
- Deep links are fragments resolved by `document.getElementById`, so hostile fragments are inert.
- Browsing makes no execution call (verified in the browser acceptance).
