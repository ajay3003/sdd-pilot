# BirkNext — Product branding and the story behind BirkNext (2026-11-16)

Branch `008-traceability-first`.

## 1. What changed

The product is called **BirkNext**, subtitle **Quality & Testing Workspace**, wherever a person sees its name. The former name "QA Review Studio" is no longer shown anywhere in the application.

| Where | Before | After |
|---|---|---|
| Sidebar brand (expanded) | QA Review Studio | **BirkNext** with the subtitle *Quality & Testing Workspace* on a second, smaller line |
| Sidebar brand (collapsed rail) | QA | BN |
| Brand link accessible name / tooltip | QA Review Studio — Dashboard / QA Review Studio | BirkNext — Dashboard / BirkNext — Quality & Testing Workspace |
| Browser title before the app loads (`index.html`) and on `/` | QA Review Studio | BirkNext — Quality & Testing Workspace |
| Page titles (Specification, Task, Plan, Constitution, Data Model, Generated Documentation Explorer/Health, System Settings, Recommended Workflow) | "… — QA Review Studio" | "… — BirkNext" |
| Dashboard onboarding card | New to QA Review Studio? | New to BirkNext? |
| System Settings diagnostics (runtime description, copied diagnostics text) | QA Review Studio | BirkNext |
| Backend `RuntimeSettings:ApplicationName` (`appsettings.json`) and the two code fallbacks | QA Review Studio | BirkNext |
| Help Center | already "Help Center" / "Guides, workflows and technical documentation for BirkNext." | unchanged (verified) |
| Developer notes in the web project (`standards/README.md`, `RULE-ENGINE.md`, ReviewContext summary) and the two architecture documents | QA Review Studio | BirkNext |

**Not renamed (by design):** namespaces, project and folder names, file names (e.g. `docs/architecture/qa_review_studio_architecture_flow.md`), routes, databases, stored ids, `appsettings.Local.json` (a person's local file), and dated historical audit records (e.g. the 2026-11-05 hardening report), which describe the product as it was named then.

## 2. The Story Behind BirkNext

The Help Center already had "The Story Behind BirkNext" inside the About article (`/user-guide#story`, decided in the 2026-11-13 content task: no new topic, route or component). It keeps that place and link and now holds the requested text:

> **Ajay imagined it. Claude helped build it. Testers make it better.**
>
> A tool shaped by requirements, curiosity, AI-assisted development and the belief that quality should be built in—not inspected in at the end.
>
> If you find a bug, congratulations. You're using the tool correctly.
>
> *Ajay found the requirements. Claude found the semicolons. The testers found the bugs.*

The existing tagline and credits (concept, vision & requirements: Ajay Anand; AI-assisted development: Claude; quality improvement: the testers) are kept. Help Center search finds it by its title and its words (search indexes article text). It is **not** shown on start-up, as a splash screen, a dialog or a Dashboard banner — pinned by tests (`index.html`, `Home`, `Dashboard`, `MainLayout`, `NavMenu`, `App` hold none of it; the story text exists in one component only) and by browser acceptance.

## 3. Tests

- `ProductBrandingTests` (new): sidebar brand name, subtitle, accessible name and tooltip; no "QA Review Studio" in any user-facing file of the web project (`.razor`, `.cs`, `.html`, `.json`, `.md`); `index.html` title; Help Center title/description; the story's words; search finds the story (three queries); the story only in the Help Center.
- Test fixtures that carried the configured application name now carry "BirkNext" (inputs only; no assertion changed).

## 4. Results

One source tree (this commit), Release publish from a clean copy, fresh disposable database `birknext_br_20261116` from template `birknext_pts_acc_20261115`, isolated ports 5097/5197.

| Check | Result |
|---|---|
| Frontend build (non-incremental) | 0 errors |
| Frontend suite | **9,148 / 9,148 passed** (new: `ProductBrandingTests`) |
| Backend build (in-repo `bin/Verify/net8.0`) | 0 errors |
| Backend suite, 6 partitions | 1,052 + 1,099 + 888 + 676 + 541 + 2,594 = **6,850 passed**, 19 skipped, 0 failed |
| Release publish (API + Web) | exit 0, 0 errors |
| Browser: branding and story (`brand-accept`) | **17 / 17** — title before load, sidebar brand and subtitle (expanded, collapsed `BN`, fits the header), no former name on 14 pages (titles and text), "— BirkNext" page titles, Help Center title/description, search finds the story ("semicolons", "story behind BirkNext"), `/user-guide#story`, requested words, no story on start-up or Dashboard, 390 px without overflow; no page errors, console errors, 5xx or guard hits |
| Browser regression (`nav-accept`, `nav-accept2`, `utps-accept`, `itx-accept`, `tt-accept`, `ttnav-accept`) | **Still running when this commit was made** (committed on request without waiting). Same tree and build as above; results are reported separately, not here |

## 5. Git

One local commit on `008-traceability-first`; not pushed. Earlier today: Unified Integration Testing `b0430dae`, feature portfolio audit `8e9abf1a`. Unrelated working-tree changes (`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register, other audit drafts) are left untouched and uncommitted.

## 6. Addendum — browser regression on the committed build (finished after commit `b400d304`)

Same tree and Release build as §4 (database `birknext_br_20261116`): `nav-accept` 41/41, `nav-accept2` 32/32, `utps-accept` 106/106, `itx-accept` 42/42, `tt-accept` 76/76, `ttnav-accept` 48/48 — with `brand-accept` 17/17 that is 362/362. No page errors, 5xx or guard hits; one console line (`nav-accept`: Frontend Quality Review's engine-status request cut off when the script leaves the page — the ~13 s Passive Web Security probe, addressed as R13 in `birknext-consolidation-implementation-2026-11-16.md`).
