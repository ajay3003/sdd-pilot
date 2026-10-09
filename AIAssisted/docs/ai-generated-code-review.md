# AI-Generated Code Review

**Quality & Testing → AI-Generated Code Review** (`/ai-generated-code-review`) reviews ordinary software that was generated or heavily
modified with coding assistants (Copilot, Claude Code, ChatGPT, Cursor, …) for risk patterns that are common after such changes.

## What it is not

- **Not an AI detector.** It never determines or claims that code was written by AI. The profile is used because the user chooses it.
- **Not an LLM review.** The MVP is deterministic: no model is called, no API key or prompt is configured, and there is no score
  (no "AI quality %", no confidence number).
- **Not a correctness proof.** "No indicators observed by the checks executed" never means the code is correct.

## How it works

```
Source Analysis snapshot (current, optional baseline of the same repository)
  + code-risk observations captured once at upload (C#, Roslyn syntax)
  + contract / configuration / CI/CD / infrastructure / architecture evidence (SourceEvidenceDiff, ArchitectureDiff)
  + dependency evidence and test inventory of the snapshot
  + generated-documentation evidence
  + the workspace's requirement graph (summarized in the browser from the existing SDD graph)
        ↓
AI-Generated Code Review profile → deterministic rules (AIC-*) → findings with provenance
```

- Source comes only from Source Analysis (Import Project or a source upload there). This page never uploads or re-reads source.
- Snapshots analyzed before the code-risk analyzer existed have no observations: source-level rules report **Not assessed** until the
  source is analyzed again.
- A baseline must be a different snapshot of the **same repository**; comparing unrelated projects is refused.
- Each run is stored with the exact current/baseline snapshot ids and is never re-bound to a newer snapshot.

## Modes

| Mode | When | What it adds |
|---|---|---|
| Current snapshot review | No baseline selected | Source-level rules over the whole snapshot |
| Snapshot change review | Baseline selected | Introduced/removed/changed counts per area (files, dependencies, contracts, configuration, CI/CD, infrastructure, security controls, tests, architecture), change-only rules, a review-attention level derived from finding severities |
| Changed files only | Baseline selected + scope | Location-based findings limited to files whose content fingerprint changed |

## Rules

Every rule reports **Executed**, **Not applicable**, **Not assessed**, **Unsupported** or **Failed**, with a reason — skipped rules are listed,
never hidden. Every finding names its evidence sources (Specification, Constitution, Source, Dependency, Contract, Configuration, Tests,
Build, Runtime, Generated documentation, Snapshot diff, Architecture), snapshot ids, file:line or related ids, a limitation and a review action.
The same logical issue seen by two rules is one finding with two evidence references.

| Category | Rules |
|---|---|
| Requirements alignment | AIC-REQ-001 requirements without implementation evidence · 002 completed tasks without evidence · 003 evidence bound to another snapshot · 004 changed source not linked to a requirement (change mode) |
| Architecture / constitution | AIC-ARCH-001 architecture evidence changed (change mode) · 002 explicit rules — Not assessed: prose principles are not turned into invented code rules |
| Unresolved references | AIC-REF-001 unresolved project namespace import ("Potential generated-code hallucination risk", filtered against packages and external projects) · 002 configuration key read but not declared · 003 endpoint references — Not assessed · 004 compiler confirmation — Not assessed (build not run = NotVerified) |
| Duplicate logic | AIC-DUP-001 identical DTO shapes in one project · 002 identical normalized method bodies (hash index, no pairwise comparison) |
| Dependencies | AIC-DEP-001 introduced/changed dependency · 002 introduced dependency without namespace usage (NuGet; others Unsupported) · 003 vulnerabilities — Not assessed (dependency health results are not attached to snapshots; nothing is looked up here) |
| Security / configuration | AIC-SEC-001 authorization removed between snapshots · 002 operation without authorization metadata among protected siblings · 003 permissive CORS · 004 unconditional developer exception page · 005 Security Configuration Review findings — reviewed on their own page |
| Validation / error handling | AIC-VAL-001 empty catch · 002 broad catch returning success · 003 exception details returned to clients · 004 request model without validation among validated siblings |
| Tests | AIC-TEST-001 no assertion / empty test · 002 skipped tests · 003 production change without test-project change (naming-convention mapping only) · 004 implementation and tests changed together (informational) · 005 imported test results · 006 mirror tests — Unsupported · 007 negative-test gaps — Not assessed |
| Placeholders | AIC-PLACEHOLDER-001 NotImplementedException · 002 TODO-marked stub returning a constant · 003 placeholder value returned · 004 TODO/FIXME comments (informational) |
| Dead code | AIC-DEAD-001 unused private method · 002 registrations/DTOs/unreachable branches — Unsupported (no semantic model) |
| Contract drift | AIC-CONTRACT-001 contract changes; compatibility by change class (removal/required-tightening = potentially incompatible, additions = additive, type changes = needs review). Contract changed ≠ incompatible ≠ consumer impact |
| Configuration drift | AIC-DRIFT-001 security-relevant configuration changes · 002 other configuration, pipeline and infrastructure changes |
| Generated documentation | AIC-DOC-001 source changed but module docs did not (change mode) · 002 open drift candidates |

## Semantics kept apart

- task marked done ≠ implementation verified · source file exists ≠ requirement implemented · test exists ≠ requirement verified
- source authorization rule ≠ runtime enforcement verified (use API Quality Review against a target)
- test files ≠ executed tests; no imported results = NotVerified
- unsupported language ≠ clean: only C# source is retained by Source Analysis; TypeScript/JavaScript/Python/Java/Go/Kotlin files are listed as Unsupported

## Extension point

The architecture allows an optional AI-assisted explanation later; deterministic findings would stay authoritative. Not implemented.
