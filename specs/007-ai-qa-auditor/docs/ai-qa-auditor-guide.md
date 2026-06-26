# AI QA Auditor Guide

## What is the AI QA Auditor?

The AI QA Auditor is the executive dashboard for BirkNext. It aggregates signals from every other QA feature into a single quality score and readiness status — so you get one answer to "Is this project ready to ship?" instead of checking five separate pages.

---

## Architecture

```
Traceability & Coverage
    ↓ (coverage %)
Impact Analysis
    ↓ (requirement risk levels)
Spec Drift Detection
    ↓ (orphan tests, drift findings)
Code Traceability
    ↓ (code file link stats)
    ↓
AI QA Auditor
    ├─ Deterministic quality score (0–100)
    ├─ Readiness status: Ready / Review Needed / High Risk
    └─ Optional: Claude executive summary + concerns + actions
```

No new DB tables — all data comes from existing features.

---

## Quality Score

The score starts at 100 and deductions are applied:

| Deduction | Trigger |
|---|---|
| Coverage critically low (−30) | Coverage < 50% |
| Coverage below 75% (−15) | Coverage 50–74% |
| Coverage below target (−5) | Coverage 75–79% |
| High-risk drift finding (−15 each, max −30) | Any CoverageGap or LowCoverage finding |
| Medium-risk drift finding (−5 each, max −10) | Any PartialCoverage or OrphanTest finding |
| Orphan tests (−3 each, max −10) | Tests not linked to any requirement |
| Unlinked code files (−2 each, max −10) | Code files with no QA links |

Minimum score: 0. Maximum: 100.

---

## Readiness Status

| Status | Badge Colour | Conditions |
|---|---|---|
| **Ready** | Green | Score ≥ 80 AND coverage ≥ 80% AND no high-risk drift findings |
| **High Risk** | Red | Score < 50 OR coverage < 50% OR any high-risk drift finding |
| **Review Needed** | Amber | Everything else |

---

## How to use

### Step 1: Open the AI QA Auditor

Click **AI QA Auditor** in the sidebar (bottom of the Analysis section).

The page loads immediately with:
- Quality score gauge (circular)
- Readiness badge
- 6 KPI cards
- Score deductions table
- Drift findings list
- Top requirements at risk
- Recommended actions

### Step 2: Generate the AI summary (optional)

Click **Generate Summary** to send the quality data to Claude.

Claude returns:
- **Executive Summary**: 3-4 sentences for a project manager or stakeholder
- **Key Concerns**: Specific risks not obvious from the raw numbers
- **AI Recommended Actions**: Complementary to the deterministic actions

> **Prerequisite**: `Anthropic:ApiKey` must be set in `appsettings.json`. If missing, the button will show an error message explaining what to configure.

### Step 3: Act on the results

| Situation | What to do |
|---|---|
| CoverageGap finding | Go to **Traceability & Coverage** → link tests to uncovered requirements |
| PartialCoverage finding | Add a second test to each single-test requirement |
| OrphanTest finding | Review orphan tests in **Spec Drift Detection** → link or remove |
| Unlinked code files | Go to **Code Traceability** → link files to requirements/tests |
| Requirements at risk | Open **Impact Analysis** for each at-risk requirement |

---

## Manual test steps

### Prerequisites

- Full stack running (`podman compose up` + `dotnet run`)
- At least one requirement and one test in the QA Artifact Library

### Steps

1. Click **AI QA Auditor** in the sidebar.
2. Confirm the quality score gauge renders with a number and colour.
3. Confirm the readiness badge shows one of: Ready, Review Needed, High Risk.
4. Confirm 6 KPI cards appear.
5. If there are drift findings, confirm they appear in the "Spec Drift Findings" section.
6. If there are top risks, confirm they appear in the "Top Requirements at Risk" section.
7. Click **Generate Summary** (requires API key) — confirm spinner appears.
8. Confirm the AI summary, concerns, and actions appear after loading.
9. If no API key is configured, confirm the error message explains the issue clearly.

### Without API key

Set `Anthropic:ApiKey` to an empty string in `appsettings.json`. Click **Generate Summary**. Expected: AI summary section shows "AI summary unavailable" message with explanation.

---

## Dashboard cards

| Card | What it shows |
|---|---|
| **Requirement Coverage** | % of requirements with ≥1 linked test |
| **Requirements at Risk** | Count at risk; how many have zero tests |
| **Drift Findings** | Count of drift findings; how many are high-risk |
| **Orphan Tests** | Tests not linked to any requirement |
| **Unlinked Code Files** | Code files with no QA traceability links |
| **High-Risk Requirements** | Requirements with zero linked tests |

---

## Known limitations (v1)

- No score history — each load re-computes from live data.
- No per-requirement AI risk narrative — use Impact Analysis for that.
- AI summary is per-session — clicking "Regenerate" calls Claude again.
- Score does not account for test quality, only coverage and structure.
