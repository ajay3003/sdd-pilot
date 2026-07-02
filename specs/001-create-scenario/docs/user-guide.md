# QA Review Studio User Guide

## Overview

QA Review Studio helps test teams analyze specification documents and turn them into structured QA artifacts.

Users can:

- paste specification text manually
- import a local `.md` or `.txt` file
- analyze the specification
- review extracted QA artifacts
- classify review decisions
- save review state
- save selected TEST artifacts as test scenarios
- browse artifacts in the QA Artifact Library

## Core Concepts

| Concept | Meaning |
|---|---|
| QA Artifact | A reviewed item extracted from a specification |
| Requirement | Expected system behavior |
| Test | Verification, acceptance criteria, or Given/When/Then flow |
| Needs Clarification | Open question, ambiguity, unresolved decision, or risk |
| Context Heading | Source section/group such as User Story, Functional Requirements, Edge Cases |
| Test Scenario | Executable or reviewable QA verification flow |

## Artifact Type vs ContextHeading

QA Review Studio separates artifact type from source context.

Artifact type means:

```text
REQUIREMENT
TEST
NEEDS_CLARIFICATION
```

ContextHeading means the source section/group, for example:

```text
User Story 1
Functional Requirements
Acceptance Criteria
Edge Cases
Observability
Assumptions
```

A User Story is normally treated as context/grouping metadata, not as a separate GraphQL artifact type.

Example:

| Spec Structure | Meaning |
|---|---|
| User Story 2 - View Scenario List | ContextHeading |
| Given one or more scenarios exist... | TEST |
| System MUST display all stored scenarios | REQUIREMENT |
| What happens if backend is unavailable? | NEEDS_CLARIFICATION |

---

## Workspace Persistence

Workspace Persistence allows you to save and resume your work across sessions. A workspace is a collection of artifacts (constitution.md, spec.md, plan.md, tasks.md, data-model.md) plus the metadata about your review progress.

### Key Concepts

| Term | Meaning |
|---|---|
| Workspace | A saved collection of artifacts plus project metadata |
| Artifact | A source document (spec.md, plan.md, tasks.md, etc.) |
| Workspace Metadata | Name, project name, artifact count, save status, last modified date |
| Dirty State | Indicates that artifacts have changed since the last save (shown in amber) |
| Auto-Save | Automatic save triggered after 3 seconds of inactivity, throttled to once every 30 seconds |
| Soft Delete | Workspaces are hidden but recoverable; not permanently deleted |

### Workspace Status Indicators

The Recommended Workflow page shows the current workspace status with a color indicator:

| Status | Color | Meaning |
|---|---|---|
| **Saved** | Green | Workspace was manually saved and matches the saved version |
| **Auto-Saved** | Amber | Workspace was auto-saved (triggered by inactivity after artifact changes) |
| **Unsaved Changes** | Amber | Artifacts have changed but the workspace has not been saved |
| **Not Saved** | Gray | No workspace is currently loaded |

### Saving a Workspace

#### Manual Save

1. Click **Save** in the Recommended Workflow workspace actions section
2. If a workspace is already loaded, it will be updated with the current artifacts
3. If no workspace is loaded, you will be prompted to enter a workspace name
4. After saving, the status will change to **Saved** (green)

#### Save As (Save with a new name)

1. Click **Save As** in the workspace actions section
2. Enter a new workspace name in the dialog
3. A new workspace will be created with this name and the current artifacts
4. The new workspace becomes the current workspace

#### Auto-Save

Auto-save happens automatically when:

1. You load or modify artifacts
2. 3 seconds pass without any artifact changes
3. At least 30 seconds have passed since the last auto-save (throttle window)

Auto-saved workspaces show **Auto-Saved** status in amber. You can manually save to create a **Saved** version.

### Loading a Workspace

1. Click **Manage** in the workspace actions section (or **Resume Workspace** if no workspace is loaded)
2. The Workspace Manager modal will open, showing all saved workspaces
3. Click on a workspace to select it (indicated by a radio button)
4. Click **Open** to load the workspace
5. The artifacts will be restored to the in-memory session and ReviewContext will rebuild
6. The workspace status will show as **Saved** or **Auto-Saved** depending on the last save

### Workspace Manager

The Workspace Manager is a modal dialog accessible from the Recommended Workflow page. It shows:

#### Workspace List

Each workspace displays:

- **Name** — the workspace name (with badges if auto-saved or marked as favorite)
- **Project** — the project name associated with the workspace
- **Artifacts** — count of imported artifacts (0–5)
- **Updated** — relative time since last modification (e.g., "2h ago")

#### Workspace Actions (per workspace)

After selecting a workspace with the radio button, the following actions appear:

| Action | What it does |
|---|---|
| **Open** | Load the workspace (blue button) |
| **Rename** | Change the workspace name |
| **Duplicate** | Create a copy with a new name |
| **Export** | Download workspace as JSON file |
| **Delete** | Hide the workspace (soft delete, can be recovered) |

#### Import Workspace

At the bottom of the Workspace Manager:

- Click **Import JSON** to restore a previously exported workspace
- Paste the JSON content in the dialog
- The workspace will be imported with a new ID and become the current workspace

### Clearing the Current Workspace

1. Click **Clear** in the workspace actions section (red button)
2. Confirm the action — you can resume this workspace later from Manage Workspaces
3. The current workspace metadata will be removed, but the workspace is not deleted from the database

This is useful for:

- Starting a fresh analysis of a different project
- Temporarily switching between projects
- Clearing the in-memory artifact cache

### Exporting and Importing Workspaces

#### Export Workspace

1. Open the Workspace Manager
2. Select a workspace
3. Click **Export**
4. A JSON file will be downloaded to your computer
5. Share this file with colleagues or save it as a backup

#### Import Workspace

1. Open the Workspace Manager
2. Click **Import JSON** at the bottom
3. Paste the JSON content from an exported workspace
4. The workspace will be created with a new ID
5. It becomes the current workspace

**Import Validation:**

The import process validates:

- **Schema version** — must be "1.0" (compatible with this application version)
- **Required fields** — workspace name and metadata are required
- **Artifact types** — unrecognized artifact types are skipped with a warning
- **Content integrity** — artifact content is validated and hashes are computed

If validation fails, an error message will explain what went wrong. Common issues:

- **"Unsupported schema version"** — the JSON was exported from a different or incompatible version
- **"Missing required field"** — the JSON is missing essential data
- **"Invalid JSON format"** — the JSON is malformed or corrupted

### Workspace Dirty Tracking

Artifacts are tracked using SHA256 hashes. When you load a workspace:

1. The artifacts are compared to the saved version using content hashes
2. If the hashes match, the status shows **Saved**
3. If they differ, the status shows **Unsaved Changes** (amber)

Dirty state is **computed on-the-fly** — it is not stored in the database. This ensures the status always reflects the true state of your current artifacts.

### Multi-Workspace Support

You can save and manage multiple workspaces:

- Each workspace is independent
- Only one workspace is "current" at a time
- Switch between workspaces using the Workspace Manager
- Workspaces are per-user (each user has their own saved workspaces)
- Deleted workspaces are soft-deleted and can be recovered if needed

### Auto-Save Configuration

Auto-save is controlled by two settings (visible in Admin → System Settings):

| Setting | Default | Purpose |
|---|---|---|
| **AutoSaveIntervalMs** | 3000 (3 seconds) | How long to wait after artifact changes before auto-saving |
| **AutoSaveThrottleMs** | 30000 (30 seconds) | Minimum time between auto-saves to prevent excessive database writes |

---

## Main Workflow

1. Open **Specification Review**
2. Paste text or import a `.md` / `.txt` file
3. Choose analysis profile
   - Speckit Structured Spec
   - Generic Document
4. Click **Analyze Specification**
5. Review grouped QA artifacts
6. Mark items as:
   - New
   - Accepted
   - Rejected
   - Needs Review
7. Click **Save Review** to persist review state
8. Select TEST artifacts that should become executable scenarios
9. Click **Save Selected** where supported

## Review Statuses

| Status | Meaning |
|---|---|
| New | Not yet reviewed |
| Accepted | Useful and approved for further work |
| Rejected | Noise, duplicate, or not useful |
| Needs Review | Requires human clarification or follow-up |

## QA Artifact Library

The QA Artifact Library stores reviewed artifacts.

It may contain:

- requirements
- tests
- clarification findings

The default library filter should prioritize **TEST** artifacts because testers usually want executable scenarios first.

## Create Test Scenario

The manual creation flow is for TEST scenarios only.

Use **Create Test Scenario** for:

- exploratory testing
- regression scenarios
- bug reproduction flows
- manual QA validation

The manual creation page should not require a type selector because manually created scenarios are always TEST artifacts.

## Task Explorer

Task Explorer parses a `tasks.md` file into a navigable hierarchy of phases, user stories, task groups, and tables.

Use it to understand:

- Task structure and completion status
- FR / SC linkage and coverage
- Implementation gaps and delivery risk

### Task Changes

Task change analysis is part of Task Explorer, not a separate tool.

Open **Task Explorer → Changes tab** to compare two versions of `tasks.md`. The Changes view shows:

- Added, removed, and modified tasks
- Scope expansions and reductions
- Risk classification by affected area
- Regression candidates

The current tasks.md is pre-filled from the loaded tree. Paste or import the previous version to run the comparison.

---

## Specification Review Session

After running **Analyze Specification**, the current review session should remain available when navigating away and back.

The session may restore:

- extracted artifacts
- review decisions
- filters
- search text
- expanded/collapsed groups
- selected analysis profile

This is temporary working-session continuity, not permanent spec storage.

## Working With Speckit/AI-Generated Artifacts

In early project phases, Speckit and AI can generate `spec.md`, `plan.md`, and `tasks.md` quickly.

Later, when the product becomes more stable:

- `spec.md` should be treated as the human-approved source of product intent
- `plan.md` should describe architectural direction and should be reviewed when updated
- `tasks.md` can change more often as implementation evolves

If a large feature such as persistent QA Delta Reviews is implemented, the plan may be updated because the architecture has changed.


## Important Principles

- Nothing is auto-saved
- Extraction is deterministic
- The original `spec.md` is not modified automatically
- Users review before saving
- Raw specification text should not be logged
- QA artifacts and test scenarios are related but not identical


## Admin — System Settings

The **Admin → System Settings** page is a diagnostics and configuration page. It provides a view of how the application is configured and how it is running locally. No sensitive values such as passwords, tokens, or API keys are shown.

In **read-only mode** the page shows the current configuration. Click **Edit Settings** in the page header to enter edit mode and change feature visibility, logging settings, and display options.

### Status bar

A compact status bar at the top of the page shows the five most important runtime values at a glance: Environment, Package Mode, Database Mode, Compose Project, and Logging Level.

### Copy Diagnostics Summary

The **Copy Diagnostics** button in the page header copies a sanitized plain-text summary of the current configuration to the clipboard. This is useful for sharing diagnostic context in a bug report or support request. Secrets are never included.

### What values are shown

The page is divided into seven sections:

| Section | What it shows |
|---|---|
| Application | Name, environment, version, package mode |
| Frontend | Frontend and API base URLs, GraphQL endpoint, hosting mode |
| Backend | Listening URLs, ASPNETCORE_ENVIRONMENT, CORS origins |
| Database | Mode, host, port, database name, username, provider, migration status |
| Container / Runtime | Compose project name, expected volume name, package mode |
| Logging | Provider, minimum log level, sinks, log path, structured logging status, Seq URL if configured |
| Maintenance | Reset Local Database button |

### Local vs shared database mode

QA Review Studio can run in two database modes:

- **Local** — uses a PostgreSQL container started by `start-local.ps1` on your machine
- **Shared** — uses a centrally-managed PostgreSQL server shared by a team

The System Settings page shows the current database mode under **Database → Mode**.

### Local database persistence and the fixed Compose project name

When running locally, the database is stored in a Docker/Podman named volume. Without a fixed Compose project name, the volume name changes each time the tester package is installed to a different folder.

`start-local.ps1` always sets `COMPOSE_PROJECT_NAME=birknext-studio-local` before starting containers. This ensures the volume is always named:

```
birknext-studio-local_postgres_data
```

Your data survives when you:

- Upgrade to a newer tester package version
- Move the package to a different folder
- Reinstall the package on the same machine

The **Container / Runtime** section on the System Settings page shows the Compose project name and expected volume name that are in use.

### How local data survives package upgrades

Because the volume name is fixed by the Compose project name, a new package installation will reconnect to the same volume as the previous one. No manual migration or export is required.

No existing user data is deleted automatically at any point by the startup script.

### Reset Local Database

The **Maintenance** section has a **Reset Local Database** button. This action:

- Deletes all application data: scenarios, reviews, traceability links, and code links
- Keeps the database server running — the container is not stopped or removed
- Never runs `docker compose down -v` — volumes are preserved at the container level
- Requires you to type `RESET` in a confirmation dialog before proceeding

The reset button is disabled when:

- The database mode is not **Local** — shared databases cannot be reset from the UI
- `AdminSettings:AllowLocalDatabaseReset` is set to `false` in the backend configuration

### Logging section

The **Logging** section shows:

- **Provider** — the logging framework in use (Serilog)
- **Minimum Level** — the lowest log level that will be recorded (e.g. Information, Debug)
- **Sinks** — where logs are sent (Console, File, Seq)
- **Log Path** — the folder where log files are written
- **Structured Logging** — whether logs are written as structured JSON
- **Seq URL** — the address of a Seq log server, if configured

### Where logs are stored

By default, logs are written to the `./logs` folder relative to the backend working directory and also to the console as structured JSON. If a Seq URL is configured, logs are also forwarded there.

### How logging helps troubleshooting

When the backend reports an error you can open the log files to see:

- The full stack trace of an exception
- The correlation ID that links a frontend request to a backend entry
- The sequence of operations that led to the failure

### Secrets are never shown

Passwords, API keys, connection string passwords, and other secrets are never displayed on the System Settings page. If a value is derived from a connection string, only the non-sensitive parts (host, port, username, database name) are shown.


## Admin — System Settings — Edit Mode

The **Edit Settings** button in the System Settings page header enters edit mode. In edit mode you can change:

- Which features are visible in the sidebar (feature visibility)
- The logging minimum level
- The Seq URL for log forwarding
- Whether diagnostic information is shown

When you are done, click **Save Settings** to apply changes. Click **Cancel** to discard and return to read-only mode.

Changes are written to `appsettings.Local.json` in the backend application directory. This file is never committed to source control.

### What cannot be changed from the UI

The following settings are not editable from the UI:

- Database credentials, connection strings, and secrets
- Compose project name and expected volume names
- Package mode and environment
- Version, build number, and commit SHA

These must be set in `appsettings.json` or via environment variables.

## Admin — System Settings — Feature / Menu Visibility

The **Feature / Menu Visibility** card in Admin → System Settings shows which menu items are currently enabled or disabled. Use **Edit Settings** to change visibility from the UI.

### Feature tiers

Features are grouped into three tiers:

| Tier | Default | Can be changed |
|---|---|---|
| **Platform** | Always enabled | No — these are the recovery and navigation features |
| **Core** | Enabled | Yes — toggle on or off |
| **Advanced** | Disabled | Yes — enable when needed |

**Platform features** (Dashboard, User Guide, Recommended Workflow, System Settings) are always enabled and cannot be disabled from the UI, configuration files, or environment variables. System Settings is the recovery page — disabling it would lock you out of the settings interface.

**Core features** are standard QA workflows that most users need. They are enabled by default.

**Advanced features** are hidden by default to reduce menu clutter for new users and tester packages. Enable them when the team is ready to use them.

### How menu visibility works

Each menu item in the left sidebar is controlled by a corresponding flag in the backend `FeatureVisibility` configuration section. Setting a flag to `false` hides the corresponding menu item from the sidebar. The underlying page and route still exist — only the menu link is hidden.

Section headers (Getting Started, Review, Library, etc.) are automatically hidden when all items in that section are disabled.

### Changing visibility from the UI

1. Open **Admin → System Settings**
2. Click **Edit Settings**
3. Toggle the features you want to enable or disable in the **Core** and **Advanced** sections
4. Click **Save Settings**
5. Refresh the page — the sidebar updates after a full page refresh

### Changing visibility from configuration

Override flags in `appsettings.Local.json` (never edit `appsettings.json` directly):

```json
"FeatureVisibility": {
  "SpecDrift": true,
  "TaskExplorer": true
}
```

Environment variables can also override flags:

```
FeatureVisibility__SpecDrift=true
FeatureVisibility__TaskExplorer=true
```

### When to use this

Feature visibility is useful for:

- **Tester packages** — hide advanced features that are not relevant for the current testing context
- **Demos** — show only the features relevant to the current demonstration
- **Onboarding** — keep the sidebar minimal while the team learns core workflows
- **Gradual rollout** — enable advanced features one at a time as the team is ready

### After saving

After saving feature visibility changes from the UI, the backend applies the new settings immediately. The sidebar in the frontend will not update until the page is refreshed. A full browser refresh (F5) is required to reload the Blazor application and pick up the updated feature flags.


## Troubleshooting

### Logs

All log files are written to the `logs/` folder inside your installation directory.

| File | What it contains |
|---|---|
| `logs/launcher.log` | Startup sequence: build steps, process PIDs, early-exit events |
| `logs/backend.out.log` | Backend (BirkNext.Api) standard output |
| `logs/backend.err.log` | Backend (BirkNext.Api) standard error — check this first on a crash |
| `logs/frontend.out.log` | Frontend dev server standard output |
| `logs/frontend.err.log` | Frontend dev server standard error |
| `logs/backend-serilog-YYYYMMDD.log` | Backend structured log — includes stack traces, request details, DB errors |

#### Which file to check for a crash

| Symptom | Where to look |
|---|---|
| Backend never became available | `backend.err.log` then `backend-serilog-YYYYMMDD.log` |
| Frontend page blank or fails to load | `frontend.err.log` |
| Startup script failed and closed | `launcher.log` |
| Unhandled exception shown in the UI | `backend-serilog-YYYYMMDD.log` |

#### Finding the logs folder

The exact path is shown on **Admin → System Settings** under **Logging → Log Files**. You can copy each path individually using the copy button next to it.

#### Recommended support bundle

When reporting a problem, include:

1. The full contents of `launcher.log`
2. The full contents of `backend.err.log`
3. The last 200 lines of the most recent `backend-serilog-YYYYMMDD.log`
4. A screenshot of **Admin → System Settings** using the **Copy Diagnostics** button output

Do not include `backend.out.log` or connection strings — they may contain sensitive runtime details.
