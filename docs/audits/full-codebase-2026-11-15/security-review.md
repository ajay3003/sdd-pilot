# Security review

Static inspection only. No services or external targets were probed.

## Controls observed

- LocalRequestGuard.cs lines 16–48 gates API/GraphQL on loopback Host, local session or bearer authentication, restricts extension origins, and enforces Origin plus a custom header for cookie-authenticated writes.
- Program.cs lines 114–141 configures JWT and named permission policies; ActiveEventAuthorization.cs validates scopes/roles/permission claims or a loopback-bounded Development write setting.
- ProjectImportController limits request size. SourceArchiveUpload reads one multipart file, bounds bytes during streaming, strips client path from filename, and never opens a client-supplied path.
- IqrSourceArchiveReader caps archive, entry, and expanded reads and normalizes archive paths.
- ManualTestRunService caps evidence/defect counts and text and permits only HTTP(S) reference links.

## SEC-01 — No visible identity-to-project authorization (P1 conditional)

Affected: project/scope/guide/run API families. Examples: ManualTestRunsController.cs lines 21–44, TestPlanSuggestionsController.cs lines 26–98, ManualTestRunService.cs lines 203–210.

Evidence: LocalRequestGuard authenticates callers, but reviewed read endpoints accept a project key and pass it to services. ManualTestRunService filters by the supplied key and object ID; no caller-to-project membership check is visible. Named write policies establish broad capability, not project ownership.

Root cause: local single-user access assumptions are not visibly separated from shared Entra-hosted deployment authorization.

Impact: in shared hosting, an authenticated caller who learns or guesses another project key may read its plans, runs, evidence or reports; broad write permission may also authorize changes. This is not an exploit proven in intended local loopback mode.

Reproduction: static code trace only; no live request made. Confidence high that reviewed paths lack project binding; impact is deployment-conditional.

Remediation: either reject shared/non-loopback hosting at startup and document single-user mode, or enforce principal-to-project/environment authorization centrally on every read, write, export and reset path.

Regression: use two principals with disjoint projects and assert 403/404 for cross-project list/get/export/mutate/run/reset. Inventory every controller action.

## Not verified / not claimed

- appsettings.Local.json was deliberately not read; configuration values and secrets are out of scope.
- Dependency advisory status is NOT VERIFIED; no current advisory feed or resolved graph was consulted.
- MarkupString sinks exist; sanitizer behavior against hostile fixtures was not exhaustively validated. No XSS vulnerability is asserted.
- Archive dynamic fuzzing and SSRF review across all URL-consuming modules were not run.
