# Help Center — oversized black shapes (repair, 2026-11-12)

Baseline: `437c455f`. Related earlier audit: `birknext-help-center-css-root-cause.md` (`da5f4a72`).

## Symptom (reported)

`/user-guide` showed:
- very large black circles and triangles;
- very small text and controls;
- a lot of empty space.

## Root cause (reproduced in a real browser, not assumed)

| Finding | Evidence |
|---|---|
| The current build renders correctly | Isolated Release install, Edge, 1440 / 390: search icon 18 px, category icons 20 px, body 16 px, `user-guide.css` 196 rules, no failed stylesheet |
| `da5f4a72` shipped the new icon markup (`<svg class="ug-search-icon">`, category icons) **and** the only rules that size and paint them in the same change | `git show da5f4a72~1:…/user-guide.css` has no `ug-search-icon` / `ug-category-icon` rule |
| `css/user-guide.css` is an unversioned static file served without `Cache-Control`; the previous copy had been unchanged for two weeks | `index.html` linked `css/user-guide.css`; .NET 8 Blazor does not fingerprint static assets. A browser may therefore reuse the old copy (heuristic freshness), while the application itself is new. |
| New markup + previous stylesheet reproduces the report exactly | The probe served the pre-`da5f4a72` stylesheet to the current build. Result: search glass **832 × 832 px**, category icons **224 × 224 px**, default **black fill** (the circle of the magnifier, the triangles of the icons), plus the old small, underlined list typography with wide gaps. Screenshot `scratchpad/mb/out/ug-stale-1440.png`. |

**Root cause:** markup and stylesheet skew. The icons depended entirely on an external, cacheable, unversioned stylesheet for their size and paint.
An SVG without a size fills its container, and an unpainted shape is filled black.

## Fix (local to the Help Center, no global SVG rule)

1. **The icons carry their own size and paint** as SVG presentation attributes in `Pages/UserGuide.razor`:
   - search icon `width="18" height="18"`;
   - category icons `width="20" height="20"`;
   - both `fill="none" stroke="currentColor"`, plus stroke width and caps.

   The stylesheet still overrides these (CSS beats presentation attributes), so the designed look is unchanged. Without it, the icons stay icon-sized.
2. **The stylesheet link is versioned:** `css/user-guide.css?v=20261112`. A browser that holds the previous copy fetches the new one.

No article, search, deep link or the Manual Testing article was changed.

## Tests

`HelpCenterTests` (frontend), both new:
- `Every_help_center_icon_carries_its_own_size_and_stroke_paint…`: every SVG on the home and on the Manual Testing article has width and height of 12–24, `fill="none"` and `stroke="currentColor"`.
- `The_help_center_stylesheet_link_is_versioned…`

Focused run on a fresh build (gated `exit=0 aborted=False`), covering Help Center, user guide, documentation catalog, guide and index.html tests: **82/82**. The web app's Release publish succeeded (one existing CS0414 warning).

## Real browser acceptance

Isolated Release install, Edge, harness `scratchpad/mb/ug-accept.cjs`. **34/34 PASS.**

| Check | Result |
|---|---|
| Home and the `#manual-testing` article at 1440 / 1280 / 1024 / 768 / 390, current stylesheet: icons ≤ 24 px, no black fill, no horizontal scroll | PASS (20 checks) |
| Same 10 views with the **previous stylesheet forced** (the reported situation): icons stay 18–20 px, no black fill | PASS (10 checks) |
| Search "manual testing" finds the article | PASS |
| The stylesheet is requested with `?v=` | PASS |
| No page error; nothing left localhost | PASS (both modes) |

Screenshots: `scratchpad/mb/out/ug-fix-{current,stale}-{home,article}-{1440,1280,1024,768,390}.png`.

## What the person should do

Reload once after updating. The versioned link fetches the new stylesheet. A hard refresh (Ctrl+F5) also clears it in an older build.

## Gaps

| Id | Pri | Gap |
|---|---|---|
| HC-1 | P3 | The other global stylesheets in `index.html` are still unversioned, so the same skew can affect any page whose markup and global CSS change together. A build-time content hash (for example .NET 9 `MapStaticAssets` fingerprinting) would cover all of them. The version token is manual and must be raised when `user-guide.css` changes. |
