# BirkNext Configuration Guide

## Overview

BirkNext supports Level 1 configurable deterministic extraction rules.

The goal is to adapt extraction behavior safely without introducing AI, scripting, or unrestricted regex.

## Configuration Location

Typical location:

```text
wwwroot/appsettings.json
```

Configuration is loaded at startup and compiled into an immutable deterministic rule set.

Restart the frontend application after changing configuration.

## Example Configuration

```json
{
  "ExtractionRules": {
    "TestPrefixes": [ "Verify", "Test", "Check" ],
    "ClarificationPrefixes": [ "Clarify", "Question", "Open question" ],
    "IgnorePrefixes": [ "IGNORE:" ],
    "RequirementKeywords": [ "should", "shall", "must", "can" ]
  }
}
```

## Supported Level 1 Configuration

Supported:

- requirement keywords
- test prefixes
- clarification prefixes
- ignore prefixes
- rule group enable/disable
- bounded priority overrides, if enabled by implementation

## Unsupported Configuration

Not supported:

- arbitrary scripting
- runtime code execution
- unrestricted regex editing
- AI rules
- ML-based rules
- external rule services
- user-supplied plugins

## Deterministic Behavior

Same input plus same configuration should produce the same extraction result.

```text
same text + same config = same result
```

## Fallback Behavior

If configuration is missing or invalid:

- system should fall back to default rules
- extraction should continue working
- logs should indicate fallback
- logs should not expose raw configured values

## File Import Configuration

Current supported import types:

- `.md`
- `.txt`

Current import principles:

- file content is read client-side
- file content is placed into the existing extraction text area
- file content is not uploaded during import
- file content is not logged
- only selected extracted scenarios are persisted
