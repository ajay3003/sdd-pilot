# BirkNext Configuration Guide

## Overview

BirkNext supports Level 1 configurable deterministic extraction rules.

The goal is to adapt extraction safely without AI, scripting, arbitrary plugins, or unrestricted runtime behavior.

The configuration model must match the actual runtime implementation used by the extraction engine.

---

## Configuration Location

Typical location:

```text
wwwroot/appsettings.json
```

Configuration is loaded at startup.

After changing configuration:

1. restart the frontend application
2. verify extraction behavior manually
3. confirm logs show configuration loaded successfully

---

## Important

Older documentation examples used outdated field names such as:

- TestPrefixes
- ClarificationPrefixes
- RequirementKeywords

Those names may not match the actual runtime configuration model anymore.

Always align examples with the real implementation classes and rule-loading pipeline.

---

## Example Configuration

The exact property names depend on the current implementation.

Example structure:

```json
{
  "ExtractionRules": {
    "IgnorePrefixes": [
      "Feature Branch:",
      "Created:",
      "Status:"
    ],
    "RequirementLanguage": [
      "must",
      "shall",
      "should"
    ],
    "ClarificationSignals": [
      "?",
      "clarify",
      "open question"
    ],
    "TestOpeners": [
      "given",
      "when",
      "then",
      "verify",
      "test"
    ]
  }
}
```

The actual field names must reflect the runtime extraction model.

---

## Supported Level 1 Configuration

Current supported concepts include:

- requirement language indicators
- test openers/prefixes
- clarification signals
- ignore prefixes
- safe rule enable/disable behavior
- bounded deterministic rule priority behavior

---

## Unsupported Configuration

The following are intentionally unsupported:

- arbitrary scripting
- runtime code execution
- unrestricted regex editing
- AI-generated extraction rules
- machine-learning classification
- external executable plugins
- user-provided compiled extensions

---

## Deterministic Behavior

BirkNext extraction is deterministic.

```text
same text + same configuration = same result
```

This behavior is important for:

- QA repeatability
- predictable reviews
- auditability
- regression testing
- stable scenario extraction

---

## Context Heading Behavior

The extraction engine preserves source headings as ContextHeading metadata.

Examples:

```text
Functional Requirements
Acceptance Criteria
Observability
Edge Cases
Open Questions
```

These headings are used for:

- grouped review sections
- collapsible subsections
- traceability to the source document

Headings themselves should not become candidate text unless they contain actionable content.

---

## Classification vs Review Status

Classification answers:

```text
What kind of candidate is this?
```

Review status answers:

```text
What did QA decide about this candidate?
```

Examples:

| Classification | Review Status |
|---|---|
| REQUIREMENT | Accepted |
| TEST | Needs Review |
| NEEDS_CLARIFICATION | Rejected |

This separation should always be preserved.

---

## Safe Configuration Principles

Configuration should:

- remain bounded
- remain deterministic
- avoid user-defined execution
- avoid arbitrary regex complexity
- avoid hidden runtime behavior
- remain understandable to QA and developers

The extraction engine should stay explainable and testable.
