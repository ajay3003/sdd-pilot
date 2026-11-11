# BirkNext — Help Center UX gap register (2026-11-11)

| ID | Gap | Status | Note |
|---|---|---|---|
| HC-G1 | "Unstyled" Help Center | **FIXED**: the cause was the IA and design, not CSS loading (see the CSS root-cause report) | Home and article views, a new stylesheet |
| HC-G2 | Two competing navigations (sidebar + category grid) | **FIXED** | Home = category cards; article = group navigation |
| HC-G3 | Whole corpus on one 47,057 px page | **FIXED** | One article at a time; the home is 2,965 px |
| HC-G4 | No breadcrumb, table of contents or paging in articles | **FIXED** | Breadcrumb, "On this page", Previous/Next |
| HC-G5 | Unknown deep link silently showed the home | **FIXED** | Not-found state |
| HC-G6 | Back/Forward did not move between sections (`replaceState`) | **FIXED** | `pushState` + `popstate` |
| HC-G7 | Article titles scrolled under the app bar | **FIXED** (found in screenshots) | |
| HC-G8 | Feature coverage: newer features without their own article (Backup readiness was added in task 9; System Understanding, Project Quality, versioning and runtime profiles have articles) | OPEN (content) | The audit lists them; no content was invented in a UX task |
| HC-G9 | Code blocks have no Copy action and no syntax highlighting | OPEN | The current corpus has inline code only; no highlighting dependency was added |
| HC-G10 | Synthetic fixtures C, K, N | NOT RUN | See the acceptance report |
| HC-G11 | The screenshot's environment (developer instance) could not be inspected | NOT RUN | Ports 5000/5173 must not be touched |
