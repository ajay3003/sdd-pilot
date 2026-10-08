# Project Compatibility diagnostics

Project Compatibility is available in **System Settings → Developer → Project Compatibility**. It checks whether BirkNext can discover supported document roles and source evidence across generated archive layouts and names.

The diagnostic invokes the same bounded archive reader, Project Import source detector, and shared artifact-role classifier used by production. It creates ZIP data in memory and does not enter the Project Import staging or commit path. It cannot change the current workspace, artifact selections, Source Analysis snapshot, Target Environment, or saved review results. No temporary archive is written to disk.

Results describe discovery portability only. A **Pass** means BirkNext handled that scenario; it is not a project quality result. Unsupported technologies are shown as **Partial**, and missing optional inputs are neutral.

Generated scenarios run in ordinary automated tests and are the authoritative CI checks. A local acceptance archive can optionally be configured at `ProjectCompatibility:AcceptanceArchivePath` (environment variable `ProjectCompatibility__AcceptanceArchivePath`). When present, the diagnostic validates the archive and tests deterministic Markdown rename/nesting against its original semantic role and content fingerprints. The local path and file contents are not returned in the result. No real project archive is required for CI or normal use.
