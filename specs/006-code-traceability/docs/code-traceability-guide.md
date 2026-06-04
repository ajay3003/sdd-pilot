# Code Traceability Guide

## What is Code Traceability?

Code Traceability connects the QA artifact world (requirements and tests) to the code world (source files). It lets you answer two key questions:

- **"If this file changes, what must I retest?"** — select a code file → see linked requirements and tests → run those tests in regression.
- **"Which files implement this requirement?"** — look at the requirement in Impact Analysis → (future: reverse lookup via code links).

---

## Architecture

```
Requirement
    ↓  TraceLinkType.Covers  (Traceability & Coverage feature)
Test
    ↓  CodeLink              (Code Traceability — this feature)
Code File
    ↓  (future)
Git Commit  →  Pull Request  →  AI Coding Session
```

Code links are stored separately from QA trace links. This keeps the code graph independent and extensible without affecting coverage calculations.

---

## How to use

### Step 1: Register a code file

1. Click **Code Traceability** in the sidebar.
2. Click **+ Register File** in the top-left panel.
3. Enter the file path (e.g. `backend/Services/ScenarioService.cs`).
4. Optionally enter a description.
5. Click **Register** — the file appears in the list.

Tips:
- Use forward slashes for paths on all platforms.
- The file name is derived automatically from the path.
- Duplicate paths in the same project are rejected.

### Step 2: Link a code file to requirements and tests

1. Click the code file in the left panel to select it.
2. In the right panel, use the **Linked Requirements** dropdown to select a requirement and click **Link**.
3. Use the **Linked Tests** dropdown to select a test and click **Link**.
4. To remove a link, click the × button next to it.

### Step 3: Use code impact for regression planning

When a code file is about to change:
1. Select the file in Code Traceability.
2. The right panel shows all linked requirements and tests.
3. Run those tests as the first priority in regression.
4. For each linked requirement, open Impact Analysis for a full regression recommendation including all linked tests — not just the ones explicitly linked to this file.

---

## Dashboard cards

| Card | What it shows |
|---|---|
| **Code Files** | Total registered files |
| **Linked Requirements** | Total requirement → file links across all files |
| **Linked Tests** | Total test → file links across all files |
| **Unlinked Files** | Files with no links to any requirement or test |

---

## Manual test steps

### Prerequisites

- Full stack running (`podman compose up` + `dotnet run`)
- At least one requirement and one test in the QA Artifact Library

### Steps

1. Click **Code Traceability** in the sidebar.
2. Click **+ Register File** → enter `backend/Services/ScenarioService.cs` → click **Register**.
3. Confirm the file appears in the left list. KPI cards update.
4. Click the file row to select it.
5. In the right panel, link a requirement using the dropdown → click **Link**.
6. Link a test using the second dropdown → click **Link**.
7. Confirm both appear in their respective lists with × buttons.
8. Click × on the requirement link → confirm it disappears.
9. Click × on the file row → confirm the file and all its links are removed.
10. Attempt to register the same path again → confirm the "already registered" error.

### Testing error states

| Scenario | Expected |
|---|---|
| Empty file path | Register button stays disabled |
| Duplicate file path | Error: "already registered" |
| Duplicate link | Error: "already exists" |
| Link NeedsClarification | Rejected with "Only requirements and tests can be linked" |

---

## Known limitations (v1)

- Manual registration only — no repository scanning or automatic file discovery.
- File paths are free text — the app does not validate that the file exists.
- No reverse lookup from Requirement → Code Files yet (planned for Impact Analysis v2).
- Single project scope.
- No history — code links don't have timestamps for when they were last validated.

---

## Future extension points

| Extension | How it fits |
|---|---|
| Git commit hash | `CodeLink.CommitHash` field (nullable, reserved on model) |
| Pull request linking | New `PullRequestLink` entity following same pattern as `CodeLink` |
| AI-suggested links | Pass requirement + file list to Claude → auto-propose links for review |
| Repository scanning | Scan repo at a path → auto-register all source files |
| Spec Drift integration | Detect when a linked file changes without a corresponding test update |
