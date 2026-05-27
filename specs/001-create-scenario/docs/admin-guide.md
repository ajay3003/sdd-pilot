# BirkNext Admin Guide

## Overview

BirkNext currently supports:

- manual scenario management
- deterministic scenario extraction
- configurable extraction rules
- local `.md` / `.txt` import
- review workflow
- reviewed candidate persistence
- finalized scenario persistence
- grouped review sections
- modern frontend UX with shared design-system styles

## Responsibilities

Administrators or maintainers should:

- validate local startup configuration
- verify database/container startup
- configure extraction rules
- verify deterministic extraction behavior
- monitor logs
- verify raw specification text is not logged
- maintain startup scripts
- verify review persistence
- verify finalized scenario persistence

## Local Startup

Recommended startup method:

```text
scripts/start-local.bat
```

The PowerShell launcher uses Podman by default, detects compose files, checks Podman readiness, starts containers, starts backend, then starts frontend.

## Persistence Responsibilities

| Area | Purpose |
|---|---|
| reviewed_candidates | QA review workspace and audit trail |
| scenarios | finalized scenario registry |

**Save Review** persists reviewed candidates.  
**Save Selected** creates finalized scenarios.

## Observability Expectations

Logs should help diagnose startup, rule loading, extraction, review save, scenario save, and validation failures.

Logs must not contain:

- raw pasted specification text
- uploaded file content
- candidate body text
- private configured vocabulary values

## Post-Deployment Checks

Verify:

1. Backend starts
2. Frontend starts
3. GraphQL endpoint is reachable
4. `.md` / `.txt` import works
5. extraction produces grouped candidates
6. filters/search work
7. review statuses can be changed
8. Save Review persists reviewed candidates
9. Save Selected creates scenarios
10. saved scenarios appear on Scenarios page
11. no browser console errors appear
