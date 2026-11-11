# System Settings — audit (task 12)

Baseline: `3bed5d95` on `008-traceability-first`. Route `/admin/system-settings?section=<id>` (`Pages/Admin/SystemSettings.razor`), section
catalog `DocumentationCatalog.SettingsGroups` (one source for the left navigation and the Help Center's documented-sections check).

## Baseline compared with the code

The baseline screenshot was checked against the code. Every value had a real source, but the same facts appeared in up to three places:

| Fact | Baseline locations | Source |
|---|---|---|
| Host environment | status ribbon, General metric card, General table | `api/admin/system-settings` → `Application.Environment` |
| Database | ribbon ("Local"), General metric card, General table, Application information (schema), Runtime & Environment (PostgreSQL) | settings `Database.Mode` (a configuration label, not a connection) + `api/system/build-info` (schema) + `api/system/runtime` (connection) |
| Package | ribbon, General metric card, General table | settings `Application.PackageMode` |
| Version | General metric card, General table, Application information | settings `Application.Version` + build-info |
| Migrations | General table ("Up to date") and Application information (schema, per context) | two different checks of one fact |
| Config Health | ribbon pill; Configuration Health section | the page's own evaluation (`_configHealthIssueCount`, `_gitPrerequisites`) |
| Logging | ribbon, General metric card | settings `Logging.MinimumLevel` |

Confirmed gaps:
- **G1** Up to five places showed each of these facts. "Database: Local" in the ribbon is a configuration label, but it read like a connection status.
- **G2** General's "Migrations: Up to date" came from a different check than Application information's per-context schema check. The two could disagree.
- **G3** No overview answered "is this installation healthy" quickly. The ribbon showed configuration labels, not states.
- **G4** Each card read `build-info` and `runtime` itself, so the same sources were read again for every card on the page.
- **G5** General used dense tables and long technical rows. Endpoints were always visible as full-width monospace strings.
- **G6** Nothing said when values were read, so a read at page open could be taken for live monitoring.

## Capability inventory

R = read-only, W = editable. Authorization: every `api/admin/*` endpoint sits behind the backend's localhost guard. Writes also need a local
session (launch code). There is no role model (see gap SS-G4). Sections marked *dev* show only when the frontend host environment is Development
(`IsDeveloperDiagnosticsVisible`).

| Section (id) | Behaviour | Data source | R/W | Class | Duplication | Change |
|---|---|---|---|---|---|---|
| General (`general`) | host facts | `api/admin/system-settings` | R | informational | ribbon + metric cards + table (G1) | Overview + one fact list; endpoints behind Technical details |
| Application information (card in General) | build identity, FE/BE compatibility, Edge Companion, schema | `api/system/build-info`, Companion state | R | informational | version/migrations repeated in General | kept as the authority for version and schema |
| Runtime & Environment (card in General) | profile, PostgreSQL, k6, selection source | `api/system/runtime` | R | runtime | database repeated in ribbon | kept as the authority for profile, PostgreSQL and k6 |
| Configuration Health (`config-health`) | platform checks, migrations, Git prerequisites | settings + Git Sources status | R | runtime diagnostics | ribbon pill | summary moved into the Overview; section unchanged |
| Feature Visibility (`feature-visibility`) | feature flags | `api/admin/feature-visibility` | W (writes `appsettings.Local.json`) | admin | — | unchanged here (task 13) |
| Platform (`platform`) | platform configuration | settings | R | informational | — | unchanged |
| Target Environments (`target-environments`) | link-out panel | TE module | W (owner page) | tester-facing | — | unchanged |
| Authentication (`authentication`) | auth configuration panel | auth settings | W | security-sensitive | — | unchanged |
| Git Sources (`git-sources`) | Microsoft sign-in setup | Git Sources API | W | admin | — | unchanged |
| Frontend Testing Capabilities (`frontend-quality-engines`) | engine enablement | FQE status | W | tester-facing | — | unchanged |
| Performance Test Engines (`performance-test-engines`) | k6 engines | runtime/perf API | R | runtime | — | unchanged |
| AI (`ai`) | AI configuration | settings | R | informational | — | unchanged |
| Environment Diagnostics (`environment-diagnostics`) | env diagnostics | diagnostics API | R | runtime diagnostics, dev | — | unchanged |
| System Diagnostics (`diagnostics`) | documentation/system checks | diagnostics doc | R | dev | — | unchanged |
| Project Compatibility (`project-compatibility`) | project format checks | project API | R | runtime diagnostics | — | unchanged |
| Content Integrity (`content-integrity`) | content checks | content API | R | dev | — | unchanged |
| Explorer Text Coverage (`explorer-text-coverage`) | text coverage | catalog | R | dev | — | unchanged |
| Documentation Health (`doc-health`) | documentation findings | DocumentationCatalog | R | dev | — | unchanged |
| ReviewContext Validation (`review-context-validation`) | validation | ReviewContext | R | dev | — | unchanged |
| Runtime Diagnostics (`ui-migration`) | runtime diagnostics | runtime diagnostics API | R | dev | — | unchanged |
| Maintenance (`maintenance`) | maintenance actions | admin API | W | admin | — | unchanged |

The `diagnostics-legacy` block is kept and still reachable by its section id. No configuration or diagnostic capability was removed.

## Runtime profiles (read-only audit)

- The profile comes from the launcher (`RuntimeProfiles`): the dev launcher or a source checkout gives DeveloperContainer, and the packaged
  launcher gives TesterNative.
- The backend reports the profile, its selection source, the PostgreSQL connection and server version, and k6 availability and version via
  `api/system/runtime`.
- The page has no control that changes the profile. Runtime & Environment says "The profile cannot be changed while BirkNext runs."
- The audit found no live switch and no automatic fallback. Nothing was changed in this area.

## Security findings

- Endpoint values (frontend, API, GraphQL and backend URLs) contain no credentials. The browser check SSET-13 confirmed they show no
  password, token or secret.
- They are still shown only on demand, under Technical details.
- No new endpoint was added and no new write was added.
- Authorization is unchanged. The localhost guard plus local session remains the only protection; see SS-G4.
