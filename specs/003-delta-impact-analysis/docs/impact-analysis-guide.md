# Impact Analysis Guide

## What is Impact Analysis?

Impact Analysis answers the question a test lead faces before accepting any requirement change: **"What do I actually need to test?"**

The page reads the existing trace links from the Traceability & Coverage feature and converts them into a risk-ranked view of all requirements. For each requirement it shows:

- **Risk level** — derived deterministically from how many tests are linked
- **Linked test count** — how many test scenarios currently cover the requirement
- **Accepted tests** — tests that have been reviewed and accepted
- **Missing coverage** — the gap between what is linked and what is accepted
- **Regression recommendation** — the exact list of tests to run, with a reason for each

No AI is involved. Every value is computed directly from database state.

---

## Risk Thresholds

| Linked Tests | Risk Level | Meaning |
|---|---|---|
| 0 | High Risk | No automated regression safety net — manual testing required |
| 1 | Medium Risk | Some coverage, but a single test failure leaves the requirement unverified |
| 2+ | Low Risk | Well-covered; run the linked tests before accepting the change |

These thresholds are fixed in v1 and not user-configurable.

---

## How it Uses Traceability

Impact Analysis is a read-only consumer of the data created in the Traceability & Coverage feature (branch `002-traceability-coverage`).

The dependency chain:

1. **QA Artifact Library** — requirements and tests exist as scenarios with `Kind = Requirement` or `Kind = Test`.
2. **Traceability page** — a test lead draws a `Covers` link from a test to a requirement via the matrix UI. Each link is stored as a `TraceLink` record with `LinkType = Covers`.
3. **Impact Analysis page** — reads `TraceLink` records for each requirement, counts `Covers` links, assigns a risk level, and builds the regression recommendation list.

Only `Covers` links affect risk and regression. `RelatedTo` links are stored but ignored by this feature.

---

## How to Test Manually

### Prerequisites

- The full stack is running (`podman compose up` from the repo root)
- At least one requirement and one test scenario exist in the QA Artifact Library
- At least one `Covers` trace link has been created on the Traceability page

### Steps

1. **Open the page** — click **Impact Analysis** in the left sidebar under the Analysis section.

2. **Check the KPI cards** — the three clickable cards (High / Medium / Low Risk) show how many requirements fall into each risk band. Confirm the numbers match what you expect from the Traceability matrix.

3. **Filter by risk** — click the High Risk card. Only high-risk requirements should appear in the list below. Click the card again to clear the filter.

4. **Select a requirement** — click any row in the requirements list. The detail panel on the right should appear with:
   - The requirement title and display label (REQ-001, REQ-002, …)
   - Risk badge
   - Three mini KPI cards: Linked Tests, Accepted Tests, Missing Coverage
   - A Regression Recommendation section

5. **Verify regression list** — for a requirement with linked tests, each test should appear with its display label (TEST-001, …), title, and reason ("Directly covers this requirement").

6. **Verify empty state** — select a requirement with no linked tests. The Regression Recommendation section should show the "No tests linked — manual testing required" message with a link to the Traceability page.

7. **Verify empty library state** — if no requirements exist at all, the requirements panel shows an empty-state card with a link to the QA Artifact Library.

### Confirming risk levels

| Setup | Expected result |
|---|---|
| Requirement with 0 `Covers` links | High Risk badge, manual testing message |
| Requirement with exactly 1 `Covers` link | Medium Risk badge, one test in regression list |
| Requirement with 2+ `Covers` links | Low Risk badge, all linked tests in regression list |
