# BirkNext User Guide

## Overview

BirkNext helps teams transform specifications, notes, and requirements into structured scenarios.

Users can:

- paste specification text manually
- import a local `.md` or `.txt` file
- extract candidate scenarios
- review extracted items
- save only selected scenarios

Nothing is saved automatically.

## Main Workflow

1. Open the Extract page
2. Paste text or import a `.md` / `.txt` file
3. Click **Extract Scenarios**
4. Review extracted candidates
5. Select candidates to keep
6. Click **Save Selected**
7. Saved items become normal scenarios

## Scenario Types

| Type | Meaning |
|---|---|
| REQUIREMENT | Expected system behavior |
| TEST | Verification or test-related item |
| NEEDS_CLARIFICATION | Question, uncertainty, or unresolved decision |

## Importing Files

Supported file types:

- `.md`
- `.txt`

The file is read in the browser. It is not uploaded to the backend during import.

After import:

- file content is placed into the text area
- user can edit the text before extraction
- extraction runs client-side
- only selected scenarios are saved

## Example Input

```text
- User can archive scenarios
- Verify archive button visibility
- Clarify archive retention policy
```

Expected result:

| Text | Type |
|---|---|
| User can archive scenarios | REQUIREMENT |
| Verify archive button visibility | TEST |
| Clarify archive retention policy | NEEDS_CLARIFICATION |

## Key Principles

- Nothing is auto-saved
- Extraction is deterministic
- Users review before saving
- Raw specification text is not logged
- Imported files are not stored automatically
- Only selected scenarios are persisted

## Current Limitations

Current version does not support:

- PDF import
- DOCX import
- OCR
- AI-assisted interpretation
- duplicate detection
- automatic requirement linking
