# User Guide — content audit (2026-11-13)

Starting HEAD: `e757d5ca`. The structural audit of the guide is still `birknext-user-documentation-audit.md`; this pass checks content against the current code.

## Where the content lives (no parallel guide created)

| Source | Role |
|---|---|
| `Components/Guide/Guide*.razor` (6 files) | Article prose. Anchors are literal; the guide tests read these files |
| `Services/UserGuideContent.cs` | Topics, search keywords, anchors, troubleshooting, glossary, status terms, verification records |
| `Services/UserGuideSearch.cs`, `Pages/UserGuide.razor`, `GuideArticleMeta`, `GuideCallout` | Search and rendering, **not changed** |

## Findings and corrections

| Area | Before | Action |
|---|---|---|
| Manual testing workflow | INCORRECT: "Understand the system" listed twice, out of order | Merged into one step |
| Workspace Views text | OUTDATED: referred to "Show other modules", removed in `e757d5ca` | Search ("Outside current view"), All Modules and links |
| Windows Tester Package | MISSING: only one runtime-profile line | New `start-here#tester-package` plus 5 troubleshooting entries, checked against the launcher source and the acceptance on the real ZIP |
| Relationship Review | MISSING | New `traceability#relationship-review` |
| Reviewed requirement coverage (MT-7) | MISSING | New `traceability#reviewed-coverage`, from `RequirementCoverageService` and its presentation labels |
| Source Management | INCOMPLETE | New `source-analysis#source-management`: analysis vs review, baseline vs newest, baseline vs deployed, inferred ZIP identity |
| Product credit | MISSING | "The Story Behind BirkNext" in `about#story`, with the requested text |
| System Understanding, Test Plan Suggestions, Project Quality, Recommended Workflow | CURRENT: verified 2026-11-11/12; What to test vs Test Plan Suggestions already distinguished | None |
| Remaining ~40 topics | UNVERIFIED in this pass | See gaps |

Module coverage: `birknext-user-guide-coverage-matrix.md`.

## Remaining gaps
- About 40 topics were not re-verified line by line. Their verification records are unchanged, and the page says when an article has none.
- Release Readiness and Snapshots have no depth beyond the readiness topic.
- Specification, Constitution, Plan, Task Explorer and Document Quality Review are being redesigned in queued tasks; their articles are updated with those tasks.
- Delivery Overview, Delivery Reports and Plan/Task Explorer are not compared side by side.
