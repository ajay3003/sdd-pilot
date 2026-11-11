# System Settings — UX implementation (task 12)

## What changed

- **Overview** (`Components/Admin/SettingsOverview.razor`): five tiles, each a summary of one source that the page already owns.
  - **Application**: backend version, build channel, and FE/BE compatibility from the existing `BuildCompatibilityRules.Compare`. No new
    version calculation.
  - **Runtime**: the profile and distribution the backend reports.
  - **Database**: the PostgreSQL connection and version, plus the schema contexts that are current. A connected database with pending or ahead
    migrations is amber: a TCP connection does not mean the schema is current.
  - **Performance tests**: k6 availability and the runner; otherwise the reason it is unavailable.
  - **Configuration**: the page's own Configuration Health evaluation, passed in as parameters, never recalculated. An optional Git Sources
    setup is amber "No failures · Git Sources not set up", never "All clear". The tile links to Configuration Health.
  - Status uses a glyph plus a word (● ▲ ■ ○), so it never depends on colour alone.
  - A freshness line reads "Checked hh:mm:ss · read when the page opened, not monitored live". The explicit Refresh button is the only reload;
    there is no polling.
  - If the backend does not answer, the four backend tiles read "Unknown" with the reason, never green.
- **Status ribbon removed.** It repeated Host environment, Database, Package and Logging, and its Config Health pill is now the Configuration
  tile. Test ids `ss-config-health` and `data-state` = `issues`/`setup`/`clear` are kept.
- **General** keeps only what no other card shows: Application name, Host environment, Logging level and Frontend hosting.
  - Version, package, database and migrations were removed: Application information and Runtime & Environment are their authorities.
  - Endpoints moved behind `<details>` "Technical details: endpoints", with labelled Copy buttons.
- **One read per source.** `BuildInfoApiService` shares one in-flight read of `build-info` and of `runtime` for 5 seconds across all cards.
  - The shared read is never cancelled by one caller.
  - `Invalidate()` (Refresh) forces a new read.
  - Browser-measured: 1 request each per page load.
- **Help Center**: the new reference card "Settings overview" (`#settings-overview`) explains each tile, the freshness wording and where each
  fact lives. It is linked from the System Settings topic.

## Not changed

- Section ids, deep links, Back/Forward, section search, edit mode, authorization.
- Every configuration and diagnostic section.
- The Application information and Runtime & Environment cards.
- No runtime-mode switch was added.

## Files

- `frontend/BirkNext.Web/Components/Admin/SettingsOverview.razor(.css)` (new)
- `frontend/BirkNext.Web/Pages/Admin/SystemSettings.razor(.css)`
- `frontend/BirkNext.Web/Services/BuildInfoApiService.cs`
- `frontend/BirkNext.Web/Components/Guide/GuideReference.razor`, `Services/UserGuideContent.cs`
- `frontend/BirkNext.Web.Tests/Components/SettingsOverviewTests.cs` (new, 9 tests)
