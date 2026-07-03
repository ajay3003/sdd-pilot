# Environment Diagnostics Table Lifecycle Verification

## Table Classification Analysis

| Table | Migration | When Populated | Fresh Install Empty? | Classification | Reason |
|-------|-----------|-----------------|----------------------|-----------------|--------|
| **project_documents** | 20260626_AddProjectDocuments | Artifact import | YES | **Platform Health** | Created by migration; schema verification needed |
| **scenarios** | 20260507_InitialCreate | Spec.md parsing | YES | **Platform Health** | Created by migration; schema verification needed |
| **reviewed_candidates** | 20260527_AddReviewedCandidates | Review sessions | YES | **Platform Health** | Created by migration; schema verification needed |
| **candidate_links** | 20260527_AddCandidateLinks | Review sessions | YES | **Platform Health** | Created by migration; schema verification needed |
| **qa_delta_reviews** | 20260528_AddQaDeltaReviews | Delta analysis | YES | **Platform Health** | Created by migration; schema verification needed |
| **trace_links** | 20260604_AddTraceLinks | Traceability work | YES | **Platform Health** | Created by migration; schema verification needed |
| **traceability_suggestions** | 20260615_AddTraceabilitySuggestions | Analysis engine | YES | **Platform Health** | Created by migration; schema verification needed |
| **code_files** | 20260604_AddCodeTraceability | Code scanning | YES | **Platform Health** | Created by migration; schema verification needed |
| **code_links** | 20260604_AddCodeTraceability | Code analysis | YES | **Platform Health** | Created by migration; schema verification needed |

## Key Finding

**ALL 9 tables are created by EF Core migrations.**

They are NOT created dynamically during artifact import. They are created once during initial migration setup and remain empty until data is populated during various workflows.

## Correct Classification

### Platform Health (Hard Failures)
- ✅ Database reachable
- ✅ EF Core migrations applied (schema exists)
- ✅ No pending migrations
- ✅ Required table schema exists (validates migrations created all expected tables)
- ✅ Required columns exist (validates schema structure)

**Why**: If migrations ran successfully, these tables MUST exist. Their absence indicates:
- Migrations failed silently
- Database corruption
- Incomplete deployment

### Workspace Readiness (Not Failures)
- Workspace initialization status (do any tables have data?)
- Project artifacts imported (project_documents populated)
- ReviewContext available (frontend check)
- Analysis complete (derived from data presence)

**Why**: Empty tables on a fresh install are EXPECTED and CORRECT. This is not a failure.

## The Actual Problem

The original diagnostics correctly checked for table existence (Platform Health). The issue was the presentation:
- When tables don't exist = FAIL (correct, migrations didn't run)
- When tables exist but are empty = FAIL (INCORRECT, should be WARNING or INFO)

**Solution**: Separate the checks:
1. Table schema exists → Platform Health check (FAIL if missing)
2. Table has data → Workspace Readiness check (NotAvailable/Warning if empty)
