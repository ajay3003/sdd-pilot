# Git-only source management — 2026-11-19

Branch `008-traceability-first`. Local commits only; nothing pushed.

**Product decision.** BirkNext uses Git, and only Git, for repository access. Git for Windows and its configured credential helper do the
connecting and authenticating:

- Git Credential Manager for HTTPS repositories.
- SSH keys for SSH repositories.

BirkNext has no Microsoft Entra app registration, no client id, no tenant, no redirect URI, no BirkNext sign-in and no OAuth tokens. It never
reads, displays, copies or stores a credential.

## 1. Previous architecture (removed)

| Area | Before | Now |
| --- | --- | --- |
| Setup | A six-step System Settings wizard: provider, Entra application (client id, tenant), organization, Microsoft sign-in (auth code + PKCE or device code), connection test, import. | The Git installation is detected. Nothing to configure. |
| Authentication | Backend MSAL tokens in memory, delegated `499b84ac-…/.default` scope. | Git and its credential helper. A Git Credential Manager sign-in window appears when a private HTTPS repository needs it. |
| Repository access | `AzureDevOpsGitClient`, the Azure DevOps REST API: organizations, projects, repositories, branches, commits, zip download. | The `git` CLI: `ls-remote`, `fetch` into a BirkNext-owned bare repository, `for-each-ref`, `rev-parse`, `cat-file`, `archive`. |
| Discovery | Organization → project → repository browsing through REST. | Local repositories (a chosen folder, or a bounded scan of one) and Git URLs typed by the person. |
| Identity | Organization URL + Azure DevOps repository id. | Canonical URL, or the local root folder, hashed to `git-<16 hex>`. Existing links keep their old identity. |

Removed code:

- Backend: `Services/GitSources/AzureDevOpsGitClient.cs` and the Git Sources REST client registration, plus their tests (`GitSourcesTests`,
  `GitSourceLinksTests`). The two tests that still applied were carried over to `GitOnlySourcesTests`.
- Shared: the `GitLinkRequest` contract and the `AzureDevOpsScope` constant.
- Frontend:
  - The ten wizard step components under `Components/GitSources/`.
  - `GitSourcesSetupState`, and the organization/project browsing in `GitImportWizard`.
  - `GitSourcesSetupUiTests`, and the old `GitSourcesUiTests`, which was rewritten.

`EntraClientSettings` stays: it serves Active Event sign-in, not Git. The Delivery Reporting `AzureDevOpsGitClient` is a separate class in
another namespace and is unaffected.

## 2. New architecture

- **`GitCli`** (`Services/GitSources/GitCli.cs`) runs the person's `git`:
  - Through `ProcessStartInfo.ArgumentList`. There is no shell and no command-line string.
  - Only subcommands on an allow-list run: `version`, `config`, `init`, `fetch`, `ls-remote`, `rev-parse`, `for-each-ref`, `symbolic-ref`,
    `archive`, `cat-file`, `log`, `status`, `remote`, `check-ref-format`. There is no `push`, `checkout`, `reset`, `clean`, `pull`, `merge`
    or `rebase`.
  - Every call sets:
    - `core.hooksPath` to an empty BirkNext folder;
    - `core.fsmonitor=false`;
    - `submodule.recurse=false` and `fetch.recurseSubmodules=false`;
    - `protocol.ext.allow=never`;
    - `GIT_TERMINAL_PROMPT=0` (no hidden prompt; Git Credential Manager still shows its own window);
    - `GIT_LFS_SKIP_SMUDGE=1`;
    - `GIT_OPTIONAL_LOCKS=0`;
    - `GIT_SSH_COMMAND=ssh -o BatchMode=yes`, unless the person set their own. An unknown host key is refused, never accepted.
  - BirkNext-owned repositories are read with no global or system configuration (`GitConfigMode.Isolated`), so no filter driver or helper
    that a configuration adds can run.
  - Every call is bounded:
    - 2 minutes for local operations, 10 minutes for network operations (long enough for a sign-in window).
    - Cancellation and timeout kill the whole process tree.
    - Output is capped (archives at 512 MB).
  - Failures are classified, never collapsed into "connection failed": sign-in required, sign-in cancelled, access denied, host key not
    trusted, repository not found, unreachable, timed out, cancelled, Git missing, untrusted folder (`safe.directory`), unsupported URL,
    not a repository.
- **`GitSourcesService`** handles:
  - status (no remote call);
  - folder scan and inspection of one repository;
  - add repository;
  - Fetch latest;
  - branches (read from the BirkNext-owned repository);
  - import;
  - check for updates;
  - links: roles, pinned commit, branch, unlink;
  - matches with earlier ZIP imports.
- **The source pipeline is reused.** The commit is exported with `git archive` and staged through the existing `ProjectImportService.Preview`,
  exactly like a chosen ZIP. The `GitSourceRevision` provenance carries repository, branch, exact SHA, commit time, retrieval time, URL or
  folder, and the BirkNext project. Source snapshots, Source Analysis, baseline comparison and change analysis are the existing engines.
  There is no second source system.

## 3. Repository discovery limits

Git does not enumerate the repositories of an Azure DevOps organization: that needs a provider API, which was removed with the sign-in.
Repositories come from three places:

- A local clone, chosen by folder.
- A scan of one folder the person chose:
  - at most 5 levels deep (3 by default), 3 000 folders and 200 repositories;
  - symbolic links and junctions are not followed;
  - `node_modules`, `bin`, `obj` and `.vs` are skipped;
  - a repository's own subfolders are not searched.
- A Git URL: https, `ssh://`, or the scp-like `user@host:path`.

## 4. Local repositories

- An inspection reports the root, the current branch or detached HEAD, the head commit, whether there are uncommitted changes, the remotes
  (any https credential part is removed from what is shown) and whether the folder is a linked worktree.
- Adding a local repository fetches its branches **from** it into the BirkNext-owned bare repository.
- The person's working tree, index, HEAD, refs and configuration are never changed. A test compares all of them before and after, including
  the index file's timestamp.
- Uncommitted changes are never imported: only commits are.

## 5. Remote URLs

`GitUrlPolicy.Normalize` refuses the following before any process starts:

- a password or token in the URL (an https user name alone is dropped);
- `http`, `file://`, local paths and any other scheme;
- transport helpers (`…::`);
- options (`-…`), and scp-style or `ssh://` user, host or path parts starting with `-` (the `-oProxyCommand` class, CVE-2017-1000117);
- whitespace and control characters;
- a query or fragment;
- a URL with no repository path.

URLs, local source folders and branch refspecs are passed after `--end-of-options`. Other data arguments never come straight from the person:

- Branch names are validated as `refs/heads/<name>` (no `..`, no leading `-`, no `.lock` ending).
- Commits must be full 40-character lowercase hex SHAs that exist in the fetched history, so they can never be read as an option.

## 6. Branches, fetch, snapshots and comparison

- **Fetch latest** is explicit. It runs `ls-remote --symref` (to learn the default branch) and then
  `fetch --prune --no-tags +refs/heads/*:refs/heads/*` into BirkNext storage. Never pull, merge or rebase.
- **Branches** are read from the fetched refs: real names, newest commit first, with the default marked. None are invented.
- **Import** takes either the branch head as last fetched or an exact commit. The revision is recorded on the importing project's link only.
- **Check for updates** fetches, then compares the linked branch head (or the pinned commit) with the last imported commit. It never imports.
- **Comparison and impact** use the existing Source Analysis snapshot comparison. A raw Git diff is not shown in their place.

## 7. Project isolation

A link is (repository identity, BirkNext project). The same repository can be linked to several projects, each with its own branch, pin,
roles and history.

- **Import:** an import request carries the project (`GitImportRequest.EnvironmentId`). A request that would match the links of more than
  one project is refused instead of taking the first one.
- **Recording:** `Record` writes history only to the importing project's link. Provenance from before this change has no project and keeps
  the old behaviour.
- **System Settings:** the card lists every project's repositories but offers Import only for the active project. Another project's
  repository says "Switch to <project> to import".

## 8. Configuration migration

- `git-sources.json` keeps its links, branches, pins, roles, imports, checks and match decisions.
- A link created through the retired Azure DevOps sign-in has no `Kind`. It is read as a remote Git URL with its `WebUrl` as the clone URL,
  so Fetch latest goes through Git and Git Credential Manager instead.
- The settings never held a token. Old client id, tenant and sign-in switches in `appsettings` are ignored by Git Sources. They can be
  removed by hand; BirkNext does not edit `appsettings.Local.json`.
- Previously imported snapshots and their provenance are unchanged.

## 9. Packaged Windows behaviour

- The same code path runs in Development, a local Release build and the tester package. Nothing depends on
  `ASPNETCORE_ENVIRONMENT=Development`.
- Git is found on `PATH` or in the default Git for Windows folders. A missing Git shows install instructions, never "configured".
- Git Credential Manager's sign-in window appears on the person's desktop because the backend runs as the signed-in user.

## 10. UI

- **System Settings → Git Sources:**
  - Git on this computer: version, executable, credential helper, and how sign-in works.
  - Connected repositories: a searchable table, stacked into cards below 48rem. The details pane has a branch selector (fetched
    branches), the last fetch, recent imports, Fetch latest, Import (active project only), Open Source Management and Remove.
  - Add repository: local repository (check a folder, or find repositories in it) or clone from a Git URL.
- **Project Import → Git:** this project's repositories with branch, optional exact commit and roles; Fetch latest; add a repository; one
  import queue through the normal review.
- **Source Management:** unchanged, apart from saving roles on the link and importing with the project.

## 11. Verification

All automated Git tests use synthetic local repositories made with git itself. No organizational remote was contacted, nothing was pushed,
and no credential was read.

| Run | Result |
| --- | --- |
| Backend focused (`GitSources` + `ProjectImport`) | **109 passed, 0 failed** |
| Frontend focused (Git Sources UI, Source Management, System Settings, Project Import, Local Data Reset) | **259 passed, 0 failed** |
| Full frontend suite | **9 258 passed, 0 failed** |

What the backend tests cover, with the real `git` on this machine:

- URL policy:
  - credentials, `http`, `file://`, local paths, `ext::` and options are refused;
  - scp and `ssh://` user, host or path parts starting with `-` are refused;
  - a refused URL never starts git.
- Git version and credential helper are reported; a missing Git is explained.
- Bounded folder scan: depth limit, `node_modules` skipped, detached HEAD, uncommitted changes to a tracked file, linked worktree.
- Adding a local repository leaves the working tree, index (including its timestamp), HEAD, refs and configuration unchanged.
- Hooks (`reference-transaction`, `post-checkout`, `pre-auto-gc`, `post-index-change`, `post-commit`) and a `smudge` filter driver in the
  person's repository never run during add, fetch and import.
- Import:
  - an exact commit or the branch head goes through the Project Import preview, with the revision as provenance;
  - an unknown commit and an option-like branch are refused.
- Update checks never import. A pinned commit is compared instead of the branch head.
- One repository in two projects: an ambiguous import is refused; each project imports its own branch; history is recorded only on the
  importing project's link; provenance without a project keeps the old behaviour.
- Failure classification: 403, host key, timeout, cancellation. An already cancelled call ends as Cancelled.
- Earlier ZIP matches by content, commit or structure, never by name.
- A retired Azure DevOps link is read as a remote Git URL.

Defects found while verifying, all fixed:

- **Product.** Cancellation before the first call threw instead of returning Cancelled.
- **Product.** Imports and import history could cross projects.
- **Product.** scp/ssh URLs with a leading `-` part were accepted.
- **Tests from the earlier session:**
  - The hooks test triggered its own hook in its setup commit.
  - Two tests "edited" a README that the fixture does not contain, so they created an untracked file instead of an uncommitted change.
  - One test refreshed the index whose timestamp it compared.

**Not run yet:**

- Backend partitions 1–6 (the full backend suite).
- API and Web Release publishes.
- A Windows package with this change.
- Browser acceptance of System Settings → Git Sources at 1440, 1024, 768 and 390 px.
- A Git Credential Manager sign-in against a real HTTPS remote.

The last of these needs an attended sign-in and an organizational repository, so it was deliberately not automated.

## 12. Unsupported

- Listing an Azure DevOps organization's repositories, pull requests, work items or any other provider API.
- Submodules and Git LFS content: LFS pointers stay pointers, and submodules are not fetched.
- A sign-in BirkNext can show itself. Git Credential Manager shows its own window, or the person runs `git fetch` once in a terminal.
