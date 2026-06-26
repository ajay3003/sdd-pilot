# AI Change Auditor Guide

## What is the AI Change Auditor?

The AI Change Auditor helps a test lead answer: **"What did this change affect and what should I test?"**

You describe a software change in plain English. The AI reads your project's requirements and tests, identifies which ones are plausibly affected, and then uses the deterministic Impact Analysis engine to compute formal risk levels and a regression recommendation.

---

## How it works

```
Change description (free text)
        │
        ▼
[Claude — semantic matching]
  Reads all requirements + tests
  Returns: affected IDs, reasons, coverage gaps, regression scope
        │
        ▼
[ImpactAnalysisService — formal risk]
  For each identified requirement:
    counts linked tests → risk level
    builds regression recommendation from trace links
        │
        ▼
AI Change Audit Report
  ├─ KPI cards (risk, counts)
  ├─ Affected Requirements (with risk badge + AI reason)
  ├─ Affected Tests (with AI reason)
  ├─ Coverage Gaps
  ├─ Regression Recommendation (deterministic, from trace links)
  └─ AI Reasoning (Claude's explanation)
```

**Claude** identifies *which* components are likely affected.  
**ImpactAnalysisService** computes *how risky* they are and *which tests to run*.

---

## How it uses existing features

| Feature | What AI Change Auditor uses |
|---|---|
| **Scenario Management** | Reads all requirements and tests as AI context |
| **Traceability & Coverage** | Reads `Covers` trace links to compute regression recommendations |
| **Delta Impact Analysis** | Calls `ImpactAnalysisService.GetRequirementImpactAsync()` for formal risk per requirement |

The AI does **not** duplicate risk logic. It performs semantic matching; the Impact Analysis engine handles risk.

---

## How to configure

The AI Change Auditor requires an Anthropic API key. Set it in the backend:

**Option 1 — appsettings.json** (development only, never commit the key):
```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-...",
    "Model": "claude-sonnet-4-6"
  }
}
```

**Option 2 — environment variable** (recommended for production):
```
Anthropic__ApiKey=sk-ant-...
Anthropic__Model=claude-sonnet-4-6
```

Without an API key the page still loads. The "Analyze Change" button returns a clear error message explaining that the key is missing.

---

## How to test manually

### Prerequisites

- Full stack running (`podman compose up` from the repo root)
- At least one requirement and one test in the QA Artifact Library
- The backend has a valid `Anthropic:ApiKey` configured

### Steps

1. Click **AI Change Auditor** in the left sidebar (under Analysis, below Impact Analysis).
2. Enter a change description in the text area. Examples:
   - `"Added duplicate scenario validation"`
   - `"Modified traceability linking logic"`
   - `"Updated risk threshold calculation"`
3. Click **Analyze Change**. The button shows a spinner while the AI runs (typically 3–10 seconds).
4. Review the report:
   - **KPI cards** — overall risk, requirements impacted, tests impacted, regression count
   - **Affected Requirements** — each shows its risk badge, linked test count, and why the AI flagged it
   - **Affected Tests** — tests the AI believes are directly impacted
   - **Coverage Gaps** — affected requirements with no linked tests (High Risk, manual testing required)
   - **Regression Recommendation** — the deterministic list of tests to run, one per linked test
   - **AI Reasoning** — Claude's explanation of the analysis

### Validating the output

| What to check | How to verify |
|---|---|
| Risk levels are correct | Cross-reference with the Impact Analysis page for the same requirements |
| Regression tests are accurate | Trace links on the Traceability page should match what's listed |
| Coverage gaps are real | Requirements listed as gaps should have 0 linked tests in Impact Analysis |
| AI reasoning is coherent | It should reference terms from your change description and requirement titles |

### Error states

| State | What you see |
|---|---|
| API key missing | "Verify that Anthropic:ApiKey is configured" error message |
| Backend unreachable | "An unexpected error occurred" message |
| Empty description | Analyze button is disabled |
| AI returns no matches | "No components identified" card with a suggestion to rephrase |

---

## Known limitations (v1)

- Only `Covers` trace links feed into the regression recommendation — `RelatedTo` links are stored but ignored.
- Audit reports are not persisted — each analysis is computed fresh.
- The AI may miss affected components if requirement titles are very generic or the change description is vague.
- Risk thresholds are fixed (same as Impact Analysis): 0 = High, 1 = Medium, 2+ = Low.
- Single project scope (hardcoded `ProjectId`).

---

## Future extension points

The feature is designed to accept richer input in future versions:

| v2 | v3 |
|---|---|
| Git commit hash | AI coding session context |
| Pull request URL | Spec Drift Detection payload |
| Changed file list | |

When these are implemented, the `ChangeAuditRequest` model and `AIChangeAuditService` are the extension points — no changes to the UI or GraphQL mutation signature are expected.
