# Workflow Approval System

## Overview

The workflow system tracks approval progress through stages:
1. Specification Review
2. Traceability Analysis
3. Implementation Review
4. Quality Gate
5. Release Readiness

---

## Components

### RecommendedWorkflow Page
- Displays workflow steps
- Shows step status (pending, approved, rejected, blocked)
- Approval/rejection buttons

### RecommendedWorkflowService (Backend)
- Builds workflow steps from artifacts
- Tracks approval state
- Determines step readiness

### WorkflowReadinessService (Frontend)
- Tracks overall workflow readiness
- Subscribes to ArtifactsChanged
- Fires ReadinessChanged when state updates

---

## Step Readiness Logic

Each workflow step has:
- **Status:** pending | approved | needsRevision | blocked
- **Requirements:** What must be true for step to proceed
- **Readiness:** Whether step can be executed

### Specification Review Step
**Requirements:**
- Specification artifact loaded
- Requirements defined
- No critical gaps

### Traceability Step
**Requirements:**
- Specification loaded
- Plan loaded
- Requirements traced to tasks
- All requirements have test coverage

### Implementation Step
**Requirements:**
- Tasks defined
- Plan defined
- Tasks complete

### Quality Gate Step
**Requirements:**
- All tests passing
- Coverage > threshold
- No compliance violations

### Release Readiness
**Requirements:**
- All previous steps approved
- Quality gate passed
- Sign-off obtained

---

## Approval Flow

### User Approves Step
```
RecommendedWorkflow.razor
  ↓
Click "Approve" button
  ↓
WorkflowApi.ApproveStepAsync(workspaceId, stepKey)
  ↓
POST /api/recommended-workflow/approve-step
{
  "workspaceId": "...",
  "stepKey": "specification_review"
}
  ↓
RecommendedWorkflowController.ApproveStepAsync()
  ↓
RecommendedWorkflowService.ApproveStepAsync()
  ↓
1. Load workspace
2. Find WorkflowApproval record
3. Update status = Approved
4. Save to database
  ↓
Response: Updated WorkflowReadiness
  ↓
Frontend updates UI
```

### Critical: WorkspaceId Must Be Known

**Problem:** If workspace has never been saved, GetCurrentStateAsync() returns Guid.Empty

**Solution:** GetCurrentStateAsync() queries database for most recent workspace

**Guarantee:** As long as auto-save has run once, approval buttons work

---

## Workflow State Persistence

### In Database
```sql
CREATE TABLE WorkflowApprovals (
    Id GUID PRIMARY KEY,
    WorkspaceId GUID NOT NULL,
    StepKey NVARCHAR(100) NOT NULL,
    Status NVARCHAR(50),  -- pending, approved, needsRevision, blocked
    ApprovedBy NVARCHAR(255),
    ApprovedAt DATETIMEOFFSET,
    Notes NVARCHAR(MAX),
    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(Id)
);
```

### Querying Workflow State
```csharp
// Get approval status for a step
var approval = await context.WorkflowApprovals
    .FirstOrDefaultAsync(a => 
        a.WorkspaceId == workspaceId && 
        a.StepKey == stepKey);

if (approval?.Status == "Approved")
    // Step is approved
```

---

## Readiness Determination

### WorkflowReadinessService Flow

```
GetReadinessAsync()
  ↓
1. Get artifact status from repository
2. Get workflow metadata from database
3. Load workflow steps from API
4. For each step: determine readiness
  ↓
Step Readiness Logic:
- Parse requirements
- Check artifacts
- Check approvals
- Check dependencies
  ↓
Build WorkflowReadiness:
- Current step
- Next recommended action
- Overall readiness score
- Warnings/blockers
```

### Readiness Determination Example

```csharp
private async Task<WorkflowStepViewModel> DetermineSpecificationStep()
{
    // Specification step is ready if:
    var isReady = 
        _artifactStatus.HasSpecification &&        // Artifact loaded
        _specification.Requirements.Count > 0 &&   // Has requirements
        !HasCriticalGaps(_specification);          // No critical gaps

    var stepReadiness = new WorkflowStepViewModel
    {
        Title = "Specification Review",
        Status = isReady ? "ready" : "blocked",
        Blockers = GetBlockers("specification"),
        NextAction = "Load specification artifact"
    };

    return stepReadiness;
}
```

---

## Event Synchronization

### Artifact Changes → Workflow Updates

```
Artifact changed (e.g., specification loaded)
  ↓
WorkspaceUpdateCoordinator.ArtifactsChanged
  ↓
WorkflowReadinessService.OnArtifactsChanged()
  ↓
Recalculate workflow readiness
  ↓
Fire WorkflowReadinessService.ReadinessChanged
  ↓
RecommendedWorkflow.razor subscribes
  ↓
Refresh workflow steps UI
  ↓
Button states update (approve/reject enabled/disabled)
```

---

## Error Handling

### Approval Fails

**Scenarios:**
1. WorkspaceId is Guid.Empty
   - Cause: Auto-save never ran
   - Fix: Save workspace first

2. Workspace not found in database
   - Cause: Workspace was deleted
   - Fix: Save again (creates new workspace)

3. User not authorized
   - Cause: Approval by different user
   - Fix: Check permissions

4. Step already approved
   - Cause: Duplicate approval click
   - Fix: Handle idempotently

---

## Blocking & Dependencies

### Step Dependencies
```
Specification Review
    ↓ (must complete before)
Traceability Analysis
    ↓ (must complete before)
Implementation Review
    ↓ (must complete before)
Quality Gate
    ↓ (must complete before)
Release Readiness
```

### Blocking Logic
```csharp
if (!previousSteps.All(s => s.Status == "Approved"))
    return new WorkflowStep { Status = "blocked" };
```

---

## Future Enhancements

Possible improvements:
- Role-based approval (only certain users can approve)
- Approval tracking history
- Conditional steps (skip based on project type)
- Custom readiness rules per project
- Multi-person approval gates

