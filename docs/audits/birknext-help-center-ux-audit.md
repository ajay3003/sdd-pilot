# BirkNext — Help Center UX audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `8b6024ea`.
- **Concurrent work:** none in the Help Center files. Other sessions' files were left untouched.

## Capability inventory

| Capability | Owner | Implementation | Observed | Confirmed gap | Fix | Acceptance |
|---|---|---|---|---|---|---|
| Route | `Pages/UserGuide.razor` | `/user-guide`; deep links `#anchor` | Works; the whole corpus on one page | HC-G3 | Two views | HC-01, HC-05 |
| Registry | `UserGuideContent` | 47 topics in 8 groups; anchors; audience areas; verification records; related; task cards | Sound; tests check that anchors exist | — | `GroupInfo`, `Journey`, `Neighbours` added | HC-22 |
| Articles | `Components/Guide/*` (6) | Static Razor sections with ids | Sound | — | Unchanged | HC-22 |
| Search | `UserGuideSearch` | Titles, keywords, headings and body text; synonyms; plain-text excerpt; bounded input | Sound | Empty result had no next step | Message with a next step | HC-04, HC-18, HC-19 |
| Categories / audiences | `UserGuide.razor` | Audience switch + category grid of link lists | Small underlined lists duplicated by the sidebar | HC-G2 | Cards with icon, description, count | HC-03, HC-11/12 |
| Sidebar | `UserGuide.razor` | Every group open on every view | Second navigation | HC-G2 | Article view only, current group open | HC-08…10 |
| Deep links / history | `userGuide.js` | Scroll + `replaceState` | Back did not move between sections; unknown anchors were ignored | HC-G5, HC-G6 | `pushState`, `popstate`, not-found | HC-13, 15–17 |
| Contextual links | `HelpLink`, `ug-learn-more`, `RecommendedActionHelp` | `/user-guide#topic` | Resolve (test) | — | Unchanged | HC-20/21, `Every_contextual_help_link…` |
| Article facts | `GuideArticleMeta` | Breadcrumb, feature links, recorded verification, related | Sound | — | The page breadcrumb replaces the duplicate in-article breadcrumb in the article view | HC-05 |
| Markdown / HTML | n/a | Articles are Razor markup; no Markdown and no raw HTML rendering | Safe | — | — | Hostile anchor test |
| Authorization | Route is public within the app; no actions | — | — | — | — | — |
| Styling | `wwwroot/css/user-guide.css` (global) | Loaded and applied | Small type, narrow column | HC-G1 | Rewritten on the tokens | HC-02, HC-L |

**System Understanding separation:**
- Help Center explains **how to use** System Understanding (article `system-understanding`).
- The Skolenærvær system guide stays project-specific in `/system-understanding` and is not copied into the Help Center.

Root cause, implementation, acceptance and gaps are in their own reports (`birknext-help-center-css-root-cause.md`, `../hardening/birknext-help-center-ux-implementation.md`, `birknext-help-center-ux-acceptance.md`, `birknext-help-center-ux-gap-register.md`).
