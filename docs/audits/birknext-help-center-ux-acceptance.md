# BirkNext — Help Center UX acceptance (2026-11-11)

- **Environment:** Microsoft Edge, with every non-localhost request aborted. The Release frontend on `:5497`; an isolated Development API on `:5496`.
- **Expected values:** the topic inventory is parsed independently from `UserGuideContent.cs` (47 topics), not read from the page.

## Browser acceptance (`hc/accept.cjs`)

| ID | Check | main | after API restart |
|---|---|---|---|
| HC-01 | Opens on the home view, h1 "Help Center" | PASS | PASS |
| HC-02 | `user-guide.css` and the isolation bundle load, `200 text/css` | PASS | PASS |
| HC-03 | Home: 8 category cards, 10-step journey, **no** directory sidebar, **no** article text, 14.9 px non-underlined topic rows, page 2,965 px tall (was 47,057) | PASS | PASS |
| HC-04 | Search "historical result" returns status-reference, versioning, … | PASS | PASS |
| HC-05/06 | The first result opens exactly one article with a breadcrumb; the breadcrumb returns home | PASS | PASS |
| HC-07 | Getting Started → System Understanding guide (how to use it; the system guide stays in System Understanding) | PASS | PASS |
| HC-08…10 | Project Quality, Integration Suites and Backup & Transfer guides open from the guide navigation; exactly that article is shown | PASS | PASS |
| HC-11/12 | Technical reference and Administration really filter the categories (`aria-pressed`) | PASS | PASS |
| HC-13 | `#runtime-profiles` (an anchor inside System Settings) opens System Settings | PASS | PASS |
| HC-14 | "On this page" lists the headings; a heading link stays in the article | PASS | PASS |
| HC-15/16 | Back/Forward between articles; refresh keeps the article | PASS | PASS |
| HC-17 | `#no-such-article` shows "not found", never another article | PASS | PASS |
| HC-18 | No results: "No guide sections match. Try fewer or other words, or browse the categories." | PASS | PASS |
| HC-19 | Keyboard: Enter in search opens the best match; focus lands in the article | PASS | PASS |
| HC-20/21 | The contextual help link from Backup & Transfer opens `workspace-transfer`; Back returns to the module | PASS | PASS |
| HC-22 | **Real corpus:** all 47 registry articles open by their deep link, with 0 misses | PASS | PASS |
| HC-Z | No external request, page error or console error | PASS | PASS |
| HC-L | No page overflow, and article body ≥ 15 px, at 1440, 1280, 1024, 768 and 390 px (home, article, table article) | 16/16 PASS | |

**Totals:** main 23/23, after-restart 23/23, visual 16/16.

## Run history (failures as found)

| Run | Failure | Cause | Resolution |
|---|---|---|---|
| main 1 | HC-09 | The harness clicked a link inside a collapsed navigation group | The harness expands the group like a user does; 23/23 |
| visual 1 | HC-L article ×10 "body 13.76 px" | The harness measured the article's small facts line (`.ug-meta`), not body text | Measures the first visible body paragraph; 16/16 |
| Screenshot inspection | **Product defect**: opening an article scrolled its title under the app's top bar | `scrollIntoView` on the section | A whole article scrolls to the page top; anchors have `scroll-margin-top: 4.5rem`; fixed and re-run |

## Screenshot inspection

- **1440 home:** compact header, a prominent search, journey tiles, balanced category cards with icons, descriptions and counts, readable rows. There is no wall of links and no huge blank area.
- **1280 article (Status meanings):** breadcrumb, compact navigation with one group open, readable column, collapsible "On this page", and the table scrolling inside its frame.
- **390 article:** one column, a "Guide contents" toggle, readable text, no overflow.
- **Rejected items checked:** no browser-default buttons, no blue-link directory, no tiny body text, no narrow page with large margins, no nested cramped scrolling.

## Automated tests

| Suite | Result |
|---|---|
| Focused frontend (UserGuide, HelpCenter, Documentation*, HelpLink, Guide*) | **240/240 PASS** |
| Full frontend at `da5f4a72` | **8636/8636 PASS** |
| Full backend at `da5f4a72`, six sequential partitions | **6526 passed, 1 failed, 19 skipped (6546)**. Partitions 1–5 = 1049/1098/840/652/520; partition 6 = 2367 passed, 1 failed. The failure is a timing test, `ScenariosQueryTests…WarmedP95CompletesWithinTwoSeconds` (p95 4.3 s; samples of 4.3 s and 9.1 s while the partition ran 5 min 40 s instead of 3 min 22 s). Re-run alone on the same build: **5/5 PASS**. This change touches no backend code; the threshold was not changed |

## Synthetic fixtures (§41)

| Fixture | Result |
|---|---|
| A, D, F, H/I (registry topics with tables and code), J (hostile anchor), L (390 px), M (unknown anchor), O (history) | Covered by tests and browser checks |
| B (empty category) | Handled: categories without topics for the selected audience are not rendered |
| C (duplicate title), K (long localized title), N (registry load failure) | **NOT RUN**: the registry is compiled in, so a load failure cannot happen at runtime; there is no localized corpus |
