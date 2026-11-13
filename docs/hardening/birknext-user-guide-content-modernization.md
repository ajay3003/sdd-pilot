# User Guide content modernization (2026-11-13)

Content-only change to the existing User Guide. Starting HEAD `e757d5ca`. Findings: `docs/audits/birknext-user-guide-content-audit.md`; module coverage: `docs/audits/birknext-user-guide-coverage-matrix.md`.

## Changed files

| File | Change |
|---|---|
| `Components/Guide/GuideGettingStarted.razor` | Manual testing step merged. New subsection "Start BirkNext on Windows (Tester Package)" (`#tester-package`) in Start here |
| `Components/Guide/GuideProject.razor` | New subsections: Source Management (`#source-management`) in Source Analysis; Relationship Review (`#relationship-review`) and Reviewed requirement coverage (`#reviewed-coverage`) in Traceability |
| `Components/Guide/GuideReference.razor` | "The Story Behind BirkNext" (`#story`) in About and limitations |
| `Services/UserGuideContent.cs` | Anchors and search keywords for the new subsections; 5 Tester Package troubleshooting entries |
| `BirkNext.Web.Tests/Pages/UserGuidePageTests.cs` | One content test for this task |

## The Story Behind BirkNext
- **Placement:** inside the existing About article as an `h3` subsection, using the existing classes (`ug-subtitle`, `ug-list`).
- **Not added:** no new topic, route, popup, startup message or component.
- **Search:** the About topic's keywords now include "story", "credits", "ajay anand", "claude" and "ai-assisted development".
- **Wording:** the credit says *Concept, vision & requirements: Ajay Anand* and *AI-assisted development: Claude*. It makes no ownership, copyright, employer or authorship claim.

## No-UI-change evidence
- The diff touches only guide prose files and the content registry.
- Every added element is a heading, paragraph, list or `GuideCallout` already used throughout the guide, with existing `ug-*` classes.
- No change to CSS, `UserGuide.razor`, `UserGuideSearch`, `GuideArticleMeta`, navigation, routes or components.

## Verification
- **Existing guide tests:** every topic anchor exists, and every internal link is a current route, covering the new links to `/correlation-review` and `/sources`. 176/176 guide and documentation tests pass.
- **New test** `Guide_content_2026_11_13_story_tester_package_relationship_review_and_reviewed_coverage`: checks the story's placement and credits, the absence of ownership wording, the tester-package facts and 5 troubleshooting entries, the Relationship Review and MT-7 anchors and wording, and that "Understand the system" appears once.
- **Full frontend suite:** 8 737/8 737 passed, on the scratch tree identical to the repository.
- **Browser verification (isolated Release web, fresh publish, integrity 86/86):** `#story`, `#tester-package`, `#relationship-review`, `#reviewed-coverage` and `#source-management` render inside the existing articles with the existing layout. The credits are present, guide search for "story" finds About and limitations, and there are 0 console errors. Screenshot: `scratchpad/mb/out/guide-story.png`.
