# Feature Visibility — acceptance (task 13)

## Synthetic fixtures (`FeatureVisibilityInventoryTests`, bUnit page tests, backend `FeatureVisibilityDefaultsTests`)

| Fixture | Test | Result |
|---|---|---|
| A mapped | `A_switch_gates_its_own_entry` (5 cases) | PASS |
| B hidden intentionally / C disabled by configuration | `A_switch_that_is_off_hides_its_entry_and_names_itself_as_the_reason`, `A_legacy_entry…master_switch` | PASS |
| D implemented but unavailable | `An_unavailable_capability_stays_shown…` | PASS |
| E authorization-restricted | no per-module authorization exists (gap FV-G1); saving without a session is refused (browser FV-20) | NOT APPLICABLE / PASS for session |
| F needs project context | not modelled in the panel (gap FV-G4) | NOT RUN |
| G backend-only / no page | `Switches_without_a_page_are_declared_no_effect` | PASS |
| H several switches, one entry | `Import_Project_is_shown_while_either…` | PASS |
| I always-visible entry without a switch | `Entries_without_a_switch_are_always_shown` (4 cases) | PASS |
| J duplicate id | `Duplicate_entries_and_duplicate_switches_are_issues` | PASS |
| K missing route | `An_entry_without_a_page_is_an_issue` | PASS |
| L wrong route / wrong switch | `An_entry_wired_to_the_wrong_switch_is_found_by_identity` | PASS |
| M retired with legacy alias | `Retired_features_are_listed…classified_redirects` | PASS |
| N new feature missing from the registry | `A_new_page_missing_from_the_sidebar_is_an_issue` | PASS |
| O registered without an entry | `A_switch_that_gates_nothing_is_an_issue_unless_declared` | PASS |
| P entry without a valid switch | `An_entry_that_can_never_be_shown_is_an_issue` | PASS |
| Q dev-only vs tester | no profile-specific visibility exists | NOT APPLICABLE |
| R partial configuration | `A_partial_configuration_reports…`, `Without_a_backend_answer…stay_hidden` | PASS |
| Defaults / validation (backend) | 6 facts: one default per key, explicit wins, unknown key rejected (nothing written), platform key locked, no orphan keys | PASS |

Focused runs:
- Frontend (Feature Visibility, System Settings, NavMenu, Dashboard, Help/User Guide, Documentation, Navigation, Traceability): 799/799.
- Backend (FeatureVisibility, AdminService, FQE settings save): 68/68.

## Real inventory (shipped configuration)

- Switches: 30 = 22 core + 8 advanced (25 with an effect, 5 without), plus 4 platform switches.
- Sidebar: 53 entries, 47 shown, 6 hidden, 20 always shown.
- 16 classified pages are reached from other pages.
- Navigation check: no issues.

## Browser (Microsoft Edge, Release frontend, isolated API with its own content root; only localhost requests allowed)

Harness: `scratchpad/fv13/accept.cjs`.

| Phase | Result |
|---|---|
| main | 26/26 |
| after-restart (API process restarted, verified PID) | 21/21 |
| visual 1440/1280/1024/768/390 | 6/6: no overflow; panel text ≥ 13.1 px |

The checks cover:
- **Header and counts:** header and subtitle; switch count against the backend's `editable-settings`.
- **Real sidebar (FV-03, independent):** every shown entry is a link in the rendered sidebar (47 = 47) and no hidden one is.
- **Hidden entries:** they match the backend flags exactly; Navigation reads Consistent.
- **Search, filters and details:**
  - search finds a current feature and a hidden one, with its reason;
  - the Hidden and Unavailable filters show the right entries;
  - Details show the rule, key, lifecycle, successor and access, with `aria-expanded`;
  - a hidden feature gets no Open link;
  - Open goes to the page, and Back returns.
- **Other entries and Help:** No-effect switches have no toggle; contextual Help opens `#feature-visibility`.
- **Access policy:** a hidden page still opens by address, as documented.
- **API:**
  - rejects an unknown key and a disabled platform key, and nothing is written;
  - refuses a save without a session (401).
- **Editing and saving:**
  - edit preview;
  - the save writes only `ImpactAnalysis` to the isolated root's `appsettings.Local.json`, and the sidebar follows without a reload;
  - the change is logged as `ImpactAnalysis: off -> on`;
  - restoring the value hides the entry again;
  - no console or page errors.

The installation's own `appsettings.Local.json` was never written. Saves went only to `scratchpad/fv13/root`.

## Defects found by acceptance and fixed

- **Toggle click target:** the decorative track intercepted pointer clicks on the switch. The track now has `pointer-events: none` and the input sits on top.
- **390 px header overlap:** the page header's title collided with two header buttons. Fixed globally with `.page-header-left` `flex: 1 1 18rem`.

Harness fixes (not product changes):
- scroll the toggle to the centre so the floating Activity row does not cover it;
- read the plain-text log, because the JSON log stores a message template.

## Screenshot review

Screenshots: `fv-1440-top`, `fv-1440-detail`, `fv-768`, `fv-390`.

- Metrics in one row at 1440, 2×2 at 768 and stacked at 390.
- Rows show name, short description and one state, with the reason inline for hidden entries.
- Details are a compact two-column definition list.
- No tiny text, overflow, empty columns or repeated badges.
