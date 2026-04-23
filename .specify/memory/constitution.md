<!--
SYNC IMPACT REPORT
==================
Version change: (template, unversioned) → 1.0.0
Bump rationale: Initial ratification — all placeholders replaced with concrete project values.

Modified principles:
  - [PRINCIPLE_1_NAME] → I. Test-First Development (NON-NEGOTIABLE)
  - [PRINCIPLE_2_NAME] → II. Observability
  - [PRINCIPLE_3_NAME] → III. Security-First
  - [PRINCIPLE_4_NAME] / [PRINCIPLE_5_NAME] → removed (user selected 3 principles)

Added sections:
  - Development Standards (web application specifics)
  - Quality Gates

Removed sections:
  - [SECTION_2_NAME] / [SECTION_3_NAME] placeholders replaced with named sections above

Templates:
  - .specify/templates/plan-template.md  ✅ Constitution Check section reviewed; no structural
    change needed — gates are populated per-feature by /speckit-plan.
  - .specify/templates/spec-template.md  ✅ Reviewed; user story + acceptance scenario structure
    aligns with Test-First principle. No changes required.
  - .specify/templates/tasks-template.md ✅ Reviewed; test-first task ordering (write tests →
    fail → implement) already enforced. No changes required.
  - .specify/templates/checklist-template.md ✅ No constitution-specific references to update.
  - .specify/templates/agent-file-template.md ✅ No constitution-specific references to update.

Deferred TODOs:
  - None. All fields resolved.
-->

# SDD Pilot Constitution

## Core Principles

### I. Test-First Development (NON-NEGOTIABLE)

Tests MUST be written and approved by the team before any feature implementation begins.
The Red-Green-Refactor cycle is strictly enforced: write failing tests → implement to pass →
refactor. Every user story MUST have acceptance tests defined in its spec before coding starts.
Unit, integration, and contract tests are all in scope; coverage gates are enforced in CI.
No PR that adds or changes behaviour may be merged without accompanying tests.

**Rationale**: Late testing produces designs that are hard to test and bugs that are expensive
to fix. Catching regressions at commit time is orders of magnitude cheaper than in production.

### II. Observability

The system MUST emit structured, queryable signals at all meaningful boundaries.

- All services MUST produce structured JSON logs containing at minimum: level, timestamp,
  trace-id, and correlation-id.
- Distributed tracing (trace/span propagation) MUST be instrumented at every HTTP and
  async boundary.
- Key business metrics MUST be emitted and dashboarded before a feature ships to production.
- Debug-level logging MUST be suppressible in production without a code change.
- Observability instrumentation is a delivery requirement, not a post-launch concern.

**Rationale**: A system you cannot observe is a system you cannot safely operate. Embedding
observability in the build process ensures it is never an afterthought.

### III. Security-First

Security controls MUST be reviewed and enforced at every phase of development.

- Authentication and authorization MUST be designed and reviewed before any endpoint
  is implemented.
- All user-supplied input MUST be validated at system boundaries (HTTP layer, message queues,
  file uploads).
- Secrets MUST never be committed to version control; environment-based injection is required.
- Dependency audits and OWASP Top-10 checks MUST be part of the CI pipeline.
- A security review is a mandatory gate on all PRs that touch auth, data access, or
  external API integrations.

**Rationale**: Security retrofitted after launch is fragile and expensive. Embedding controls
in the development workflow catches vulnerabilities before they reach users.

## Development Standards

This project is a **web application** with separate backend and frontend deployable units.

- Backend and frontend are independently deployable; a versioned API contract governs
  communication between them.
- API contracts (REST or GraphQL schemas) MUST be defined in the feature spec before
  implementation begins.
- Frontend components MUST NOT contain business logic; services and API clients are
  responsible for all data access and transformation.
- Both backend and frontend MUST have independent test suites that can run without
  the other being present.
- Breaking API changes require a version bump and a migration plan documented in the
  corresponding spec before the change is merged.

## Quality Gates

All work merged to the main branch MUST satisfy the following gates:

- Automated tests (unit + integration + contract) pass in CI.
- Linting and static analysis pass with zero errors.
- At least one peer code review is approved.
- Observability instrumentation verified for any new service boundary or endpoint.
- Security review completed for any PR touching auth, secrets, data access, or
  external integrations.
- Performance regressions greater than 10% on key paths MUST be justified in writing
  or resolved before merge.

## Governance

This constitution supersedes all other practices and preferences within this project.
Amendments require:

1. A documented rationale (in the PR description or Sync Impact Report).
2. A version bump following semantic versioning:
   - **MAJOR**: Principle removals, governance restructuring, or backward-incompatible
     redefinitions.
   - **MINOR**: New principles, new mandatory sections, or materially expanded guidance.
   - **PATCH**: Clarifications, wording improvements, or typo fixes.
3. Propagation of any changes to dependent templates and docs in the same commit or PR.

All PRs and design reviews MUST verify compliance with the principles above. Complexity
beyond what the current requirement demands MUST be explicitly justified in the plan's
Complexity Tracking table. Use `.specify/memory/` for runtime development guidance and
living project documentation.

**Version**: 1.0.0 | **Ratified**: 2026-04-16 | **Last Amended**: 2026-04-16
