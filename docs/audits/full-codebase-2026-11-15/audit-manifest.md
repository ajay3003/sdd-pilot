# Audit manifest

## Baseline

- Date: 2026-11-15.
- Repository: C:\Users\ajaan\source\sdd-repos\BirkNext.
- Branch: 008-traceability-first.
- Starting and ending HEAD: 0f3ca7dc505f1389a16f9787ac5450ff58d39820.
- Starting tree was dirty. Modified tracked paths include API/web project files, AppDbContext/model snapshot/startup, Manual Testing and Test Plan Suggestions, and the existing Kode67 gap register. Untracked paths include Azure DevOps test-plan export implementation, tests, migration/contracts, AIAssisted/testdata, and multiple existing reports.
- Concurrent Azure DevOps export work was treated as user-owned. No existing changes were staged or overwritten. New audit files are uncommitted; no commit was made because concurrent work is present.

## Inventory

- Backend and frontend solutions; API, API tests, Web, Web tests, Playwright tests, RealProjectAcceptance; launcher project referenced by tests and shared projects in solutions.
- Tracked source globs found 3,041 C# and 406 Razor files before excluding migration designer files/build output; these raw Git file counts include tests/generated migration code and are not LOC.
- Test globs: 1,235 C# files (including fixtures/shared test code; not test-case count).
- Migration C# files: 182 including designer files.
- Frameworks: .NET 8, ASP.NET Core, Blazor WebAssembly, EF Core/Npgsql, xUnit, bUnit, Playwright.
- Languages include C#, Razor, JavaScript/TypeScript, CSS, JSON/YAML, Markdown, and migration SQL.

## Areas inspected

CLAUDE.md and current spec plan; Git status/history/diff names; project files; API startup/DI/authentication; LocalRequestGuard and permission handlers; Project Import upload/controller/service/staging; Manual Test Runs controller/service/model/context mapping; Test Plan Suggestions controller/service; reset orchestration; routes/workspace inventory; test project provider declarations; migration/context topology and package declarations.

## Tools and commands

Read-only: git status/rev-parse/branch/log/diff/ls-files; rg files/search; Get-Content/Get-ChildItem. A broad recursive enumeration hit generated artifact recursion and a protected local-session directory; inspection was narrowed. No protected file contents were read.

No tests, builds, package installs, network calls, database connections, migrations, application starts, browser tests, dynamic security tests, or performance tests were run. Current local configuration/data are user-owned and isolated services were not established. Ports 5000 and 5173 and existing user processes were left untouched.

appsettings.Local.json, AIAssisted/testdata, and Kode67 gap register contents were not read or changed.

## Blind spots

No full runtime or migration verification; no resolved dependency/advisory graph (vulnerability status NOT VERIFIED); no exhaustive action-by-action authorization inventory; no complete review of 1,235 test source files; no live UX/accessibility/performance assessment. Existing logs/reports were not accepted as proof of current HEAD.
