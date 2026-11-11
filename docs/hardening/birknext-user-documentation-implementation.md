# BirkNext — User documentation & Help Center: implementation (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `4c424ca6`.
- **Related documents:** audit `../audits/birknext-user-documentation-audit.md`; information architecture `../architecture/birknext-help-center-information-architecture.md`.
- **Scope:** no push, no deployment.

## Content

| Item | Change |
|---|---|
| New topics | `integration-suites` and `parquet-viewer` (articles that existed but were not in navigation or search); `suggested-e2e` (new); `versioning` (new); `whats-new` (new, honest "not available") |
| New articles | **Suggested E2E tests and the Edge Companion:** generate → readiness → Verify DOM in Edge (Accept locator / Mark unresolved / Reject mapping) → Approve test (Diagnostic) → run → results; pairing in your attended session only; locator staleness. **Versions:** artifact versions, pinned runs, Historical result, suite pinning; BirkNext build (application version, CI / Local / Unknown build, Application information, versioned archive + checksum; a checksum is not a signature). **What's new:** not available, and commit messages are not used. |
| Updated articles | **Integration Test Suites:** suite revisions run their pinned test revisions; subflows ≠ full E2E; **MU → MM schema-version risk** (v2.0.0 vs v2.0.1.flat; MU forwards unchanged; the sender's version and the routing channel are unresolved from source; not a confirmed production incompatibility). **Status meanings:** execution outcomes and result currency tables. |
| Troubleshooting | 4 new entries: build provenance missing or Unknown build; frontend and backend are different builds; a result labelled Historical; import conflicts and revalidation |
| Glossary | 5 new terms: Execution manifest, Historical result, Composite source context, Attended session, Build version |
| Groups and areas | New groups "Integration & E2E" and "Versions & workspace"; three audience areas |
| Verification records | 9 articles verified against BirkNext 1.0.0 on 2026-11-11, each with its evidence; all other articles state that no verification is recorded |

## Code

| File | What |
|---|---|
| `Services/UserGuideContent.cs` | Topics, groups, `GuideArea` + `AreaOf`, `Verified`, `RelatedTo`, `ExecutionOutcomes`, `ResultCurrency`, glossary and troubleshooting entries; `Search` delegates to `UserGuideSearch` |
| `Services/UserGuideSearch.cs` (new) | Local index from the embedded guide sources and structured lists; synonyms; weighted ranking; phrase bonus; bounded input; plain-text excerpts |
| `Pages/UserGuide.razor` | Help Center landing: hero search with excerpts and Enter-to-open, quick links, audience switch with category cards, common tasks (user guides), sidebar contents (unchanged behaviour) |
| `Components/Guide/GuideArticleMeta.razor` (new) | Breadcrumb, Open feature links (catalog routes), verification, related guides; inserted under all 45 article headings |
| `Components/Guide/GuideTesting.razor`, `GuideReference.razor` | The new and updated articles |
| `Components/HelpLink.razor(.css)` (new) | Contextual help link; placed on 6 pages |
| `wwwroot/css/user-guide.css` | Landing, areas, categories, excerpts, article facts |

## Tests

- `HelpCenterTests` (9): body search and excerpts; synonyms and ranking; hostile and oversized input; results with area, highlight and Enter; areas (one area per topic, default user guides); article facts on every article, only recorded verification, Open links from real routes; every `HelpLink` in the app resolves (source scan); an unknown help link renders nothing; claims of the new articles.
- `UserGuidePageTests`: the heading assertion was updated deliberately to "Help Center".
