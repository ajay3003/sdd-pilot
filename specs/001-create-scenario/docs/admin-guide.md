# BirkNext Admin Guide

## Overview

This guide explains operational and administrative responsibilities for BirkNext.

BirkNext currently supports:

- manual scenario management
- deterministic scenario extraction
- configurable extraction rules
- local `.md` / `.txt` import
- review-before-save scenario persistence
- modern frontend UX with shared design-system styles

## Responsibilities

Administrators or technical maintainers should:

- validate local startup configuration
- configure extraction rules
- verify deterministic extraction behavior
- monitor application logs
- verify that raw specification text is not logged
- verify backend/frontend startup
- maintain startup scripts
- keep documentation updated after feature changes

## Local Startup

Recommended startup method:

```text
scripts/start-local.bat
```

The PowerShell script:

- starts Podman/Docker containers
- waits for database/container initialization
- starts backend
- waits before starting frontend
- starts frontend
- prints guidance for accessing frontend URLs

## Configuration

Extraction rule configuration is loaded at startup.

Default expected location:

```text
wwwroot/appsettings.json
```

Configuration changes require frontend restart.

Invalid configuration should fall back safely to defaults.

## Observability Expectations

Logs should help diagnose:

- application startup
- rule configuration loading
- rule configuration fallback
- extraction completed
- save completed
- validation failures

Logs must not contain:

- raw pasted specification text
- uploaded file content
- candidate body text
- private vocabulary values from rule configuration

## Post-Deployment Checks

After deployment or local startup, verify:

1. Backend starts successfully
2. Frontend starts successfully
3. GraphQL endpoint is reachable
4. Extract page loads
5. `.md` import works
6. `.txt` import works
7. unsupported file is rejected
8. extraction produces candidates
9. selected scenarios can be saved
10. saved scenarios appear on Scenarios page
11. no console errors are visible
12. logs do not contain raw specification text
