# BirkNext Configuration Guide

## Overview

BirkNext supports Level 1 configurable deterministic extraction rules.

The goal is to adapt extraction safely without AI, scripting, or unrestricted regex.

## Configuration Location

Typical location:

```text
wwwroot/appsettings.json
```

Configuration is loaded at startup. Restart the frontend application after changing configuration.

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

- requirement keywords
- test prefixes
- clarification prefixes
- ignore prefixes
- rule group enable/disable
- bounded priority overrides, if enabled

## Unsupported Configuration

- arbitrary scripting
- runtime code execution
- unrestricted regex editing
- AI rules
- ML-based rules
- external rule services
- user-supplied plugins

## Deterministic Behavior

```text
same text + same config = same result
```

## Context Heading Behavior

Source headings are used for review grouping. They should not become candidate text unless they contain actionable content.

## Classification vs Review Status

Classification answers: “What kind of candidate is this?”

Review status answers: “What did QA decide about this candidate?”

| Classification | Review Status |
|---|---|
| REQUIREMENT | Accepted |
| TEST | Needs Review |
| NEEDS_CLARIFICATION | Rejected |
