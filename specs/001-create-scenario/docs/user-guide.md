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
  "TaskDeltas": true
}
```

Environment variables can also override flags:

```
FeatureVisibility__SpecDrift=true
FeatureVisibility__TaskDeltas=true
```

### When to use this

Feature visibility is useful for:

- **Tester packages** — hide advanced features that are not relevant for the current testing context
- **Demos** — show only the features relevant to the current demonstration
- **Onboarding** — keep the sidebar minimal while the team learns core workflows
- **Gradual rollout** — enable advanced features one at a time as the team is ready

### After saving

After saving feature visibility changes from the UI, the backend applies the new settings immediately. The sidebar in the frontend will not update until the page is refreshed. A full browser refresh (F5) is required to reload the Blazor application and pick up the updated feature flags.
