# BirkNext Configuration Guide

## Overview
BirkNext supports Level 1 configurable deterministic extraction rules.

## Configuration Location
```text
wwwroot/appsettings.json
```

## Example Configuration
```json
{
  "ExtractionRules": {
    "TestPrefixes": [ "Verify", "Test", "Check" ],
    "ClarificationPrefixes": [ "Clarify", "Question" ],
    "IgnorePrefixes": [ "IGNORE:" ]
  }
}
```

## Supported Configuration
- Prefixes
- Keywords
- Ignore rules
- Rule enable/disable
- Limited priority overrides

## Unsupported Configuration
- Arbitrary scripting
- AI rules
- Unrestricted regex
- Runtime code execution
