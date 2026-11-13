# Import Project — UX implementation (2026-11-13)

Files: `Pages/ProjectImport.razor`, `wwwroot/css/project-import.css` (intent styles only), `Components/Guide/GuideGettingStarted.razor` (Import a project text), tests in `Pages/ProjectImportAnotherTests.cs`. Audit: `docs/audits/birknext-import-project-ux-audit.md`.

- **Header action "Import ZIP"** (`pi-header-import`, `btn-primary`): present before and after the first import and on the import result.
  - It opens the existing upload form and moves focus to its file chooser. On the result page it first returns to the import view.
  - It is disabled while an import is running, and never starts an import by itself.
- **The form moved to the top** of the page, under the header, so the action and its form are together. The current import and its history below are unchanged until a new import succeeds.
- **Explicit intent**, only when an application exists: a native radio `fieldset` "What is this ZIP?".
  - *Add evidence to current application* (the default; nothing is replaced) or *Import another application* (the current one stays).
  - It maps to the existing `ImportTarget` and is disabled while an import runs.
- **Cancel** returns focus to the control that opened the form (Import ZIP, Import more evidence or Import another application).
- **Unchanged:** the backend, the import engine, preview/commit, duplicate detection, Clear current import, source snapshots, baselines, review decisions, authorization, and the contextual `?import=` links.

## Verification
- **Tests:** 3 new (Import ZIP before an import; after an import with the form above the current import and focus on the chooser; explicit intent and cancel back to Import ZIP). All 133 existing Import Project tests pass; full frontend **8 741/8 741**; guide and import 183/183.
- **Browser:** `docs/audits/birknext-import-project-browser-acceptance.md`.
