# QA Review Studio Dashboard Guide

## Overview

The Dashboard is the executive QA summary for the current specification. It surfaces traceability KPIs, implementation coverage, test coverage health, and open QA risks in one place.

Its purpose is to help test leads, product owners, and development teams quickly understand:

* The overall health of a specification
* Traceability and coverage status
* Implementation coverage
* Outstanding QA risks
* Recommended next actions

For detailed coverage validation, use **Traceability & Coverage**.

For implementation status, use **Task Explorer**.

---

# QA Health Score

The QA Health Score is the primary indicator of specification readiness.

The score combines several quality signals:

* Implementation Coverage
* Requirement Coverage
* Traceability
* Open Risks
* Clarification Items

### Status Levels

| Status    | Meaning                                                                  |
| --------- | ------------------------------------------------------------------------ |
| Healthy   | Specification quality is strong and major QA concerns are under control. |
| Attention | Some quality gaps or risks require review.                               |
| At Risk   | Significant quality, coverage, or traceability issues exist.             |
| No Data   | No specification review data is available yet.                           |

### Example

**QA Health Score: 82 (Healthy)**

This indicates that most requirements are reviewed, covered by tests, and properly linked, although some issues still require attention.

---

# Coverage

The Dashboard surfaces three coverage KPIs drawn from traceability data:

| KPI | What it shows |
|---|---|
| Coverage Requirements | Total requirements in scope |
| Covered Requirements | Requirements linked to at least one test |
| Implementation Coverage | Percentage of requirements with test coverage |

These values reflect what is recorded in **Traceability & Coverage**. To investigate gaps or update links, open that view directly.

### Why It Matters

Requirements without tests may represent unverified functionality and increase delivery risk.

---

# Open Risks

Open Risks represent known QA concerns that require attention.

Examples include:

* Requirements without tests
* Unresolved clarification items
* Missing traceability
* Incomplete reviews

### Example

Open Risks = 3

This means three issues are currently contributing to QA risk.

### Goal

Reduce Open Risks to zero whenever possible.

---

# Traceability

Traceability measures how well requirements, tests, and clarification items are linked together.

### Why It Matters

Good traceability helps teams:

* Understand why a test exists
* Identify affected requirements
* Perform impact analysis
* Demonstrate coverage

### Example

Traceability = 87%

This indicates that most review artifacts are properly connected to requirements.

---

# Top QA Risks

This section highlights the most important quality concerns.

### Examples

🔴 3 requirements missing tests

🟡 2 unresolved clarifications

### How to Use

Review this section regularly and prioritize high-risk items before implementation or release.

---

# Next Actions

Next Actions provides recommended tasks based on current dashboard metrics.

### Examples

* Link tests to uncovered requirements
* Resolve clarification items
* Review pending candidates
* Improve traceability

### Purpose

This section helps teams understand what should be done next to improve specification quality.

---

# Quality Overview

The Quality Overview provides a summary of saved review artifacts.

## Requirements

Requirements identified in the specification.

### Example

24 Requirements

---

## Tests

Saved test scenarios linked to requirements.

### Example

68 Tests

---

## Clarifications

Questions or ambiguities that require resolution.

### Example

4 Clarifications

---

## Missing Tests

Requirements that have no linked test.

### Example

2 Missing Tests

---

## Orphan Tests

Tests that are not linked to any requirement.

### Example

5 Orphan Tests

---

# Test Coverage Breakdown

This section categorizes saved tests by type.

## Functional Tests

Validate expected system behavior.

### Examples

* User login
* Save record
* Search results

---

## Negative Tests

Validate error handling and invalid inputs.

### Examples

* Invalid username
* Missing mandatory fields
* Unauthorized access

---

## Edge Cases

Validate boundary and uncommon scenarios.

### Examples

* Maximum values
* Empty inputs
* Large datasets

---

## Performance Tests

Validate speed and responsiveness.

### Examples

* Response times
* Load handling
* Stress testing

---

## Security Tests

Validate security controls.

### Examples

* Authentication
* Authorization
* Access control

---

## Other

Tests that do not match a predefined category.

---

# Recommended Dashboard Workflow

1. Review the QA Health Score.
2. Examine Open Risks.
3. Complete the Next Actions.
4. Verify Coverage and Traceability.
5. Review Quality Overview statistics.
6. Monitor Test Coverage Breakdown.
7. Repeat after each specification review.

---

# Best Practices

* Aim for high Coverage and Traceability.
* Resolve Clarifications early.
* Keep Open Risks low.
* Ensure every requirement has at least one test.
* Use traceability links consistently.
* Monitor trends over time rather than relying on a single score.

A healthy dashboard does not guarantee a perfect specification, but it provides strong indicators that the specification is reviewable, testable, and ready for implementation.
