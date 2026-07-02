# Recommended Workflow — User Guide
**Refined Architecture Edition**  
**Date**: 2026-07-02

---

## Table of Contents

1. [Overview](#overview)
2. [Workflow States](#workflow-states)
3. [Step Progression](#step-progression)
4. [Approval Model](#approval-model)
5. [Readiness Metrics](#readiness-metrics)
6. [Artifact Requirements](#artifact-requirements)
7. [Troubleshooting](#troubleshooting)

---

## Overview

The Recommended Workflow is a **guidance system** that tracks your progress through a structured review and approval process. It monitors:

- **Artifact Availability** — Which project files you've loaded
- **Review Status** — Whether you've reviewed each step
- **Approval Status** — Whether you've formally approved each step
- **Overall Readiness** — Combined progress toward release

### Key Principle: Loading ≠ Approval

Loading an artifact (e.g., uploading constitution.md) makes a step **Available**. But the step isn't truly **Complete** until you've:
1. Opened and reviewed the step
2. Explicitly approved it

This distinction ensures quality: your approval is a deliberate decision, not automatic.

---

## Workflow States

Each step in the workflow progresses through distinct states. Here's what each means:

### Locked 🔒
**Status**: Step cannot be accessed yet.

**Caused by**:
- Required artifacts haven't been loaded
- OR prerequisite approval steps aren't yet approved

**Action**: Load required artifacts or complete prerequisite steps first.

**Example**: "Artifact Traceability" is locked until you approve "Specification Review" first.

---

### Available 🟦
**Status**: Step is ready to open, but not yet reviewed.

**Caused by**:
- All required artifacts are loaded
- All prerequisite approvals are complete
- But you haven't opened this step yet

**Action**: Click the step to open and begin review.

**Example**: "Plan Explorer" turns Available as soon as plan.md is loaded.

---

### In Progress ⏳
**Status**: You're currently reviewing this step.

**Triggered by**: Clicking to open the step.

**Action**: Review the content, then mark as Reviewed when done.

---

### Reviewed ✓
**Status**: Review complete, now requires your approval decision.

**Triggered by**: Clicking "Mark Reviewed" after examining the step.

**Action**: Choose one of:
- **Approve** — Step meets quality standards, proceed
- **Needs Changes** — Issues found, requires revision

---

### Approved ✅
**Status**: Step is complete and approved. (GREEN)

**Triggered by**: Clicking "Approve" button.

**Meaning**: You've formally confirmed this step is ready. It won't change unless the underlying artifacts change.

**Impact**: Unlocks downstream steps that depend on this approval.

---

### Needs Attention ⚠️
**Status**: Step requires action. (ORANGE)

**Two causes**:
1. **You marked it "Needs Changes"** — You found issues during review
2. **Artifacts changed** — An artifact was modified after you approved, invalidating your approval

**Action**: Investigate the issue, fix if needed, then re-review and re-approve.

---

## Step Progression

### Typical Happy Path

```
Locked → Available → In Progress → Reviewed → Approved ✅
```

### With Rejection/Changes

```
Locked → Available → In Progress → Reviewed → Needs Changes ⚠️
                                               ↓
                                    Re-open, review, approve
                                               ↓
                                          Approved ✅
```

### With Artifact Invalidation

```
                           Approved ✅
                                 ↓
Artifact modified (e.g., spec.md changed)
                                 ↓
                          Needs Attention ⚠️
                                 ↓
                          Re-review & re-approve
                                 ↓
                          Approved ✅ (New hash recorded)
```

---

## Approval Model

### Manual Explicit Approval

Each workflow step requires **explicit user approval**, not automatic completion. This means:

- ✅ You control which steps are approved
- ✅ Quality is enforced by decision, not automation
- ✅ Approvals are auditable (timestamp, user, comment)
- ✅ Approvals are immutable (history preserved even if invalidated)

### Audit Trail

When you approve a step, the system records:

| Field | Purpose |
|-------|---------|
| **Approved At** | Timestamp of approval |
| **Approved By** | Your user ID (or "Local Developer") |
| **Artifact Hash** | Fingerprint of artifacts at time of approval |
| **ReviewContext Version** | Version of analysis at time of approval |
| **Comment** | Optional note explaining approval |

**If artifacts change** and the hash differs from what was recorded, your approval is marked "Needs Attention" to flag the mismatch.

---

## Readiness Metrics

The workflow calculates overall **Readiness** (0-100%) as a weighted combination of three dimensions:

### Artifact Readiness (30% weight)
**Question**: "Are required project files loaded?"

**Calculation**: Percentage of required artifacts that have been loaded.

**Required Artifacts**:
- Constitution
- Specification
- Plan
- Tasks

**Optional Artifacts**:
- DataModel

**Scoring Example**:
- All 4 required + optional → 100%
- 3 of 4 required → 75%
- 2 of 4 required → 50%

### Review Readiness (30% weight)
**Question**: "Have required steps been reviewed?"

**Calculation**: Percentage of review-required steps where ReviewState = Reviewed.

**Scoring Example**:
- 8 of 8 steps reviewed → 100%
- 4 of 8 steps reviewed → 50%

### Approval Readiness (40% weight)
**Question**: "Have required steps been approved?"

**Calculation**: Percentage of approval-required steps where ApprovalState = Approved.

**Scoring Example**:
- 8 of 8 steps approved → 100%
- 4 of 8 steps approved → 50%

---

### Overall Readiness Formula

```
Overall = (Artifact × 0.30) + (Review × 0.30) + (Approval × 0.40)
```

**Example Scenario**:
- Artifacts: 3/4 required loaded = 75%
- Reviews: 5/8 steps reviewed = 62%
- Approvals: 4/8 steps approved = 50%

```
Overall = (75 × 0.30) + (62 × 0.30) + (50 × 0.40)
        = 22.5 + 18.6 + 20
        = 61.1%  (61% Ready for Release)
```

---

### Ready for Release 🚀

The system shows "Ready for Release" when:

✅ **All artifact readiness requirements met** (all required artifacts loaded)  
✅ **All approval requirements met** (all required steps approved)  
✅ **No blocking issues** (no steps in "Needs Attention")

When this threshold is reached, you can proceed with confidence toward release sign-off.

---

## Artifact Requirements

### Step Artifact Dependencies

| Step | Required Artifacts | Optional | Unlock Upon |
|------|-------------------|----------|-------------|
| Load Sample Project | — | — | Always Available |
| Constitution Explorer | Constitution | — | Constitution loaded |
| Plan Explorer | Plan | — | Plan loaded |
| Task Explorer | Tasks | — | Tasks loaded |
| Data Model Explorer | — | DataModel | DataModel loaded (Optional) |
| Specification Review | Specification | — | Specification loaded |
| Artifact Traceability | Const, Spec, Plan, Tasks | — | All 4 + SpecReview approved |
| Implementation Review | Specification, Tasks | — | SpecReview + ArtifactTraceability approved |

### Load Artifacts

To load an artifact, click "Load Sample Project" or manually import markdown files:

1. Navigate to your project directory
2. Ensure these files are present:
   - `constitution.md` — Governance and quality rules
   - `specification.md` — Requirements and acceptance tests
   - `plan.md` — Architecture and implementation approach
   - `tasks.md` — Implementation tasks and deliverables
   - `data-model.md` (optional) — Entity definitions and relationships

3. Import them through the workspace manager

---

## Artifact Change Invalidation

### How It Works

When you **approve a step**, the system records a hash (fingerprint) of all artifacts:

```
Approve "Specification Review"
  ↓
Artifacts hashed: "abc123xyz..."
Stored in database
```

Later, if **any artifact changes**:

```
User modifies specification.md
  ↓
New hash computed: "different789..."
Compared to stored hash "abc123xyz..."
  ↓
Hashes don't match
  ↓
"Specification Review" invalidated → Needs Attention ⚠️
```

### Preventing False Invalidation

The system is smart about invalidation:

- ✅ If artifacts revert to their approved state, hashes match → stays Approved
- ✅ Only dependent artifacts trigger invalidation (changing plan.md won't invalidate "Specification Review")
- ✅ Approval history is preserved (you can see what was approved and why it's now invalid)

### When Invalidation Happens

Artifact changes that **invalidate** approvals:

| Changed Artifact | Invalidates These Steps |
|------------------|------------------------|
| Constitution | Constitution Explorer, Artifact Traceability |
| Specification | Specification Review, Artifact Traceability, Implementation Review |
| Plan | Plan Explorer, Artifact Traceability |
| Tasks | Task Explorer, Artifact Traceability, Implementation Review |
| DataModel | Data Model Explorer |

---

## Troubleshooting

### "Step is Locked — Load required artifacts first"

**Problem**: A step says it's locked.

**Diagnosis**:
1. Check which artifacts are required (from table above)
2. Verify those files are loaded in your workspace

**Solution**:
- Open "Manage Workspaces"
- Import the missing artifact files
- Return to the step

---

### "Step is Locked — Complete prerequisite steps first"

**Problem**: A step is locked even though artifacts are loaded.

**Diagnosis**:
- This step depends on approval of an earlier step
- Check the table above for dependencies

**Solution**:
- Go back to the prerequisite step (listed in "Locked" message)
- Review and approve it
- Return to this step; it should now be Available

---

### "Needs Attention — Artifact Changed"

**Problem**: A previously approved step now shows "Needs Attention".

**Diagnosis**:
- An artifact was modified after you approved this step
- The system detected a hash mismatch

**Solution**:
1. Review what changed in the artifact
2. Determine if the change affects your approval
3. **If still acceptable**: Re-approve the step (new hash recorded)
4. **If changes require action**: Fix the artifact, then re-approve

---

### "Needs Attention — I marked it Needs Changes"

**Problem**: A step shows "Needs Attention" because you previously rejected it.

**Diagnosis**:
- You clicked "Needs Changes" during a prior review
- The step is waiting for you to address the issues and re-approve

**Solution**:
1. Address the issues (usually in upstream artifacts)
2. Re-open the step
3. Review again
4. Click "Approve" (or "Needs Changes" again if issues persist)

---

### Readiness Score Stuck Low

**Problem**: Overall readiness shows 40% and isn't improving.

**Possible Causes**:

1. **No artifacts loaded**
   - Solution: Import artifact files to increase Artifact Readiness to 100%

2. **Steps not reviewed**
   - Solution: Open and review each available step

3. **Steps not approved**
   - Solution: Click "Approve" after reviewing (or "Needs Changes" if issues found)

**Remember**: Readiness is weighted 30% artifacts + 30% reviews + **40% approvals**. Approvals are most critical for overall readiness.

---

### Multiple Workflows in Same Workspace

**Each step is tracked independently per workspace.**

- Workspace A: Steps approved
- Workspace B: Same steps reset to pending

**Why?** Approval is a workspace-scoped decision. You might approve differently depending on project context.

---

## Best Practices

### Approval Workflow

1. **Load all artifacts first** — Don't add artifacts incrementally; bring in all required files upfront
2. **Review in order** — Follow the numbered steps; don't skip
3. **Approve with confidence** — An approval means you've verified this step meets standards
4. **Re-approve when artifacts change** — If artifacts are modified, revalidate your approval
5. **Document with comments** — Use the comment field to explain your approval reasoning

### Workspace Management

1. **Save early, save often** — Use "Save" button after major milestones
2. **Use "Save As" for variants** — Test scenarios in separate workspaces
3. **Manage workspaces** — Click "Manage" to resume prior work or clean up old workspaces
4. **Clear before starting over** — Use "Clear" to reset a workspace completely

---

## FAQ

### Q: Can I undo an approval?
**A**: You can mark a step "Needs Changes" to require re-review. The prior approval history is preserved.

### Q: Do approvals sync across workspaces?
**A**: No. Each workspace maintains independent approval state. This allows different projects to have different approval strategies.

### Q: What if I load a new artifact after approving steps?
**A**: Steps that depend on that new artifact become Available. Steps already approved stay approved (unless the artifact is one they depend on).

### Q: Can I skip optional steps?
**A**: Yes! Optional steps (like Data Model Explorer) don't block readiness or release. You can proceed without approving them.

### Q: What makes a step "Current"?
**A**: The system highlights the first available step that hasn't yet been approved. This guides you to the next actionable item.

---

## Advanced: Workflow Architecture

### For Technical Users

The workflow system separates:

- **Static Definitions** — Step metadata, requirements, dependencies (shared application-wide)
- **Persisted Progress** — Your review/approval decisions per workspace (stored in database)
- **Computed Status** — Available/Locked/Needs Attention (calculated at runtime from artifacts + progress)

**No computed fields are persisted.** This means:

✅ Artifact changes are reflected immediately  
✅ No stale data or synchronization issues  
✅ Audit trail is immutable and reliable  

---

## Support

For questions or issues:

1. Check the troubleshooting section above
2. Review the "Developer Maintenance" section on the workflow page (technical notes)
3. Consult the user interface tooltips (hover for hints)

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 2.0 | 2026-07-02 | Architectural refinement: separated definitions from progress, added readiness metrics |
| 1.0 | 2026-06-01 | Initial workflow implementation with artifact tracking and approval states |

---

**Last Updated**: 2026-07-02  
**By**: AI-Assisted Development  
**Status**: ✅ Complete & Ready for Release
