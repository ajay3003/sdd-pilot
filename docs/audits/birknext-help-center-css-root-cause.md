# BirkNext — Help Center "unstyled" appearance: root cause (2026-11-11)

**Baseline:** `8b6024ea`. Checked in a real browser: Microsoft Edge, the Release frontend (`dotnet publish -c Release`) served on `:5497`, an isolated API, and probe `css-probe.cjs`.

## Asset loading (all 15 checks from the brief)

| Check | Result |
|---|---|
| Stylesheets requested | 24 stylesheets, including `css/user-guide.css` and `BirkNext.Web.styles.css` (isolation bundle) |
| Status and MIME type | All `200 text/css` |
| CSS isolation | Bundle loaded (13,509 rules). The Help Center uses no isolated CSS: `user-guide.css` is global because the articles are child components |
| Rules applied | `user-guide.css` exposes 141 rules via `document.styleSheets`. Computed styles match the stylesheet: `.ug-shell` is a grid (248 px + 776 px), and the category grid has 3 columns |
| Classes vs selectors | 91 classes in the markup, all defined. The only exceptions were modifier classes (`ug-nav`, `ug-topic`, `ug-meta-verified/unverified`, `ug-chip-list`) that need no rule of their own |
| Base path | `<base href="/">`; all assets resolve |
| CSP / blocked stylesheet | None blocked; no console errors |
| Global reset conflict | None found (Bootstrap reboot only sets defaults) |
| Stale cache / publish omission | Not reproduced in a fresh profile against a fresh Release publish |

**Conclusion: no CSS loading, isolation, base-path or publish defect at HEAD.** In the isolated Release build the stylesheet loads and applies.

The supplied screenshot may come from another environment, such as the developer instance on 5000/5173, which was not touched. That could not be inspected.

## The actual cause of the "basic HTML" look

The page was styled as designed, but the design and information architecture produced exactly what the screenshot shows:

| Observation | Cause in code |
|---|---|
| Blue underlined link lists, small text | Category cards rendered topic links as bulleted `<a>` lists at 13.6 px with the browser's underline; the sidebar links were 13.4 px; article body text was 14.4 px |
| Two competing navigations | A sidebar listing **every** topic (all groups open) **and** a category grid listing the same topics |
| Long vertical scrolling | Every article of the corpus rendered on one page: **47,057 px** tall at 1440 px |
| Narrow content, unused width | `.ug-page` capped at 1280 px; the content column was 776 px beside the 248 px sidebar |
| Default-looking buttons | The audience buttons and the narrow-screen toggle used minimal styling |

This was fixed by redesigning the information architecture and the stylesheet. There are no inline styles; the only generated rule is the "show only the current article" selector, built from a registry topic id. See `../hardening/birknext-help-center-ux-implementation.md`.
