# BirkNext User Guide

## Overview

BirkNext helps teams transform specification documents into structured QA review items and finalized scenarios.

Users can paste or import `.md` / `.txt` files, extract candidates, review them, save the review state, and save selected items as finalized scenarios.

## Main Workflow

1. Open **Extract**
2. Paste text or import a `.md` / `.txt` file
3. Click **Extract Scenarios**
4. Review grouped candidates
5. Mark items as **New**, **Accepted**, **Rejected**, or **Needs Review**
6. Click **Save Review** to persist the review session
7. Select finalized candidates
8. Click **Save Selected** to create scenarios

## Candidate Types

| Type | Meaning |
|---|---|
| REQUIREMENT | Expected system behavior |
| TEST | Verification, acceptance criteria, or Given/When/Then flow |
| NEEDS_CLARIFICATION | Question, uncertainty, or unresolved decision |

## Review Statuses

| Status | Meaning |
|---|---|
| New | Not yet reviewed |
| Accepted | Useful and approved for further work |
| Rejected | Noise, duplicate, or not useful |
| Needs Review | Requires human clarification or follow-up |

## Reviewed Candidates vs Scenarios

| Concept | Meaning |
|---|---|
| Reviewed Candidate | Extracted item plus QA review decision |
| Scenario | Finalized item saved to the scenario registry |

A reviewed candidate does not automatically become a scenario. Use **Save Selected** for finalized scenarios.

## Grouped Review

Results are grouped by type and then by source heading/context.

```text
REQUIREMENT
  Functional Requirements
  Observability

TEST
  User Story 1
  Acceptance Criteria

NEEDS_CLARIFICATION
  Edge Cases
  Open Questions
```

## Importing Files

Supported file types:

- `.md`
- `.txt`

Files are read in the browser. The uploaded file itself is not stored automatically.

## Key Principles

- Nothing is auto-saved
- Extraction is deterministic
- Users review before saving
- Raw specification text is not logged
- Review state and finalized scenarios are separate
