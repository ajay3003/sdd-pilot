# BirkNext — Help Center UX implementation (2026-11-11)

- **Baseline:** `8b6024ea`.
- **Scope:** Help Center only (`/user-guide`). The documentation engine, article components, topic ids, anchors, search index, contextual `HelpLink`s and verification records are unchanged.

## Information architecture

| View | URL | Content |
|---|---|---|
| **Home** | `/user-guide` | Compact header ("Help Center" · "Guides, workflows and technical documentation for BirkNext.") · search · quick links · **New to BirkNext? Start here** (10-step journey, each step an existing article) · **Browse topics** (audience switch User guides / Administration / Technical reference; one card per category with icon, one-line description, real article count and the article rows) · Common tasks (user guides only) · **When something looks wrong** (Troubleshooting, Status meanings, Glossary, What's new) · **Recently verified articles** (only articles with a verification record; never a file date) |
| **Article** | `/user-guide#<topic>` or `#<anchor inside a topic>` | Breadcrumb (Help Center › area › group › article) · compact search · guide navigation with only the current group expanded and the current article marked (`aria-current="page"`) · one article at reading width (≤ 50 rem, 16 px body) · **On this page** (collapsible, built from the article's headings with stable ids) · Previous / Next |
| **Not found** | `/user-guide#<unknown>` | "This guide section was not found", the anchor shown as text, a link to the home. Never another article |

**Routing:**
- The fragment selects the article. Anchors declared in the registry resolve in C#.
- A heading anchor inside an article is resolved once in the page (`userGuide.topicOfAnchor`).
- Anything that is not `[A-Za-z0-9_-]{1,120}` is "not found".
- Same-page fragment links become one history entry (`pushState` in the capture phase), and Back/Forward and refresh work (`popstate`, `LocationChanged`).
- A whole article opens at the top of the page; a heading anchor scrolls below the app bar (`scroll-margin-top`).

**Content preservation:**
- All six article components stay rendered, so every anchor resolves and every contextual link keeps working; the articles not being read are hidden.
- Only the current article is shown, via one generated rule whose value is a registry topic id (never user text).
- On the home, articles are not displayed.

## Files

| File | Change |
|---|---|
| `Pages/UserGuide.razor` | Two views (home/article) and the not-found state; resolution from the fragment; `ShowAnchor` (JS-invokable); TOC; pager; journey; category cards; support; recently verified |
| `Services/UserGuideContent.cs` | `GroupInfo` (icon and one-line description per category), `Journey` (10 steps → existing topics), `Neighbours` |
| `wwwroot/js/userGuide.js` | Registers the page. Capture-phase same-page link handling with `pushState`; `popstate`. `headings()` (stable ids), `topicOfAnchor()`. Article-top scrolling. No `replaceState` |
| `wwwroot/css/user-guide.css` | Rewritten on the design tokens: search box, journey, cards, segmented audience control, reading layout, typography (16 px / 1.7 body, 1.75 rem titles, readable links with underline offset in prose, non-underlined navigation rows), code/pre, tables scrolling inside their frame, pager, empty state, narrow-screen drawer, reduced motion |
| Tests | `HelpCenterTests` (home, deep link, sub-anchor, unknown/hostile anchor, navigation via URL); `UserGuidePageTests` (navigation tests open an article; empty search offers a next step) |

**Not changed:** the article texts. The feature coverage gaps are listed in the gap register and not fixed here.
