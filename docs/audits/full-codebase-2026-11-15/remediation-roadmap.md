# Remediation roadmap

| Order | Priority | Work | Effort / risk | Acceptance |
|---:|---|---|---|---|
| 1 | Immediate SEC-01 | Decide local-only versus shared topology; implement resource authorization or startup refusal. | Large / high regression risk | Disjoint principals cannot access each other’s list/get/export/mutate/run/reset data; local mode remains explicitly supported. |
| 2 | Next TEST-01 | Disposable PostgreSQL CI fixture and critical invariant matrix. | Medium / medium | Migration, concurrency, reset, isolation and payload compatibility tests run on disposable DB; skipped external tests reported separately. |
| 3 | Next ARCH-01 | Domain ownership matrix and architecture completeness test. | Medium / low-medium | Every context maps to schema, owner, migrations, reset semantics, provenance key and tests. |
| 4 | Next PERF-01 | Bound project run history reads or add aggregation/retention. | Medium-large / medium | 10k/100k benchmark; bounded memory and matching coverage totals against full-scan oracle. |
| 5 | Next SEC-02 | Resolved dependency inventory/SBOM and authoritative advisory scan. | Small-medium / low | Report exact resolved versions and advisory source; no age-only vulnerability claims. |
| 6 | Later UX-01 | Role-based usability and accessibility acceptance. | Medium / low | Manual keyboard/screen-reader checks plus automated axe; task completion and recovery documented. |
| 7 | Later DATA-01 | Version aggregate JSON and retain historical fixtures. | Medium / medium | Old fixtures load/update/export preserving provenance. |
| 8 | Later SEC-03 | Hostile Markdown corpus across all MarkupString sinks. | Small-medium / low | Script/event/unsafe URL payloads inert with sanitizer-boundary tests. |
| 9 | Later OPS-01 | Disposable migration, backup/restore and reset failure drills. | Medium / medium | Restore and partial-failure runbooks validated without real database access. |
| 10 | Later ARCH-02 | Split composition root after ownership inventory. | Medium / medium | Named domain registrations preserve behavior; composition smoke test passes. |

Dependencies: decide deployment before authorization work; create ownership matrix before DI/reset refactors; establish PostgreSQL test infrastructure before persistence changes; retain full-scan oracle before optimizing coverage. Concurrent uncommitted Azure DevOps export work was not touched.

Top ten sequence: deployment decision; project authorization; cross-project tests; disposable PostgreSQL; migration verification; concurrent run test; context ownership map; history bounding; SBOM/advisories; role-based accessibility acceptance.
