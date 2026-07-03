We are freezing the architecture.

The architecture investigation is complete.

Do NOT redesign the architecture again.

Do NOT introduce additional abstraction layers.

Do NOT create new facades or God objects.

Use the architecture analysis as reference, but simplify it before implementation.

====================================================
IMPLEMENTATION PRINCIPLES
====================================================

1. Keep ReviewContext focused ONLY on semantic analysis.

2. Keep WorkspacePersistenceService responsible ONLY for persistence.

3. WorkspaceArtifactRepository remains the single runtime owner of loaded artifacts.

4. WorkflowReadinessService remains responsible for readiness computation.

5. RecommendedWorkflowService remains responsible for workflow definitions and prerequisites.

6. Introduce the MINIMUM number of new services.

7. Prefer modifying existing services over creating new ones.

8. Eliminate duplicate ownership instead of replacing working components.

====================================================
IMPLEMENTATION GOAL
====================================================

Produce a practical implementation plan.

NOT another architecture document.

NOT another redesign.

The objective is to stabilize the existing implementation with the fewest architectural changes.

====================================================
REVISE THE MIGRATION PLAN
====================================================

Replace the existing 10-phase plan with a concise implementation plan.

Maximum 6 phases.

Each phase must:

• compile
• build
• be shippable
• include rollback
• include regression tests

====================================================
EXPECTED PHASES
====================================================

Phase 1
Persistence

Fix:
- SaveCurrentAsync
- LoadAsync
- RestoreWorkspaceAsync

Exit criteria:
- Save/Open restores all artifacts correctly.

----------------------------

Phase 2
Artifact ownership

Remove duplicate artifact ownership.

WorkspaceArtifactRepository becomes the only runtime artifact owner.

Remove WorkspaceArtifactStatusService cache.

Exit criteria:
- All pages show identical artifact counts.

----------------------------

Phase 3
Approval ownership

Introduce a lightweight ApprovalService ONLY if necessary.

Otherwise improve existing approval flow.

Fix:
- Approve
- Review
- Needs Changes

Exit criteria:
- Approval immediately updates runtime state.
- Readiness changes immediately.

----------------------------

Phase 4
Workflow state

Fix workflow locking.

Fix step transitions.

Fix readiness recomputation.

DO NOT move workflow logic between backend and frontend unless absolutely required.

Exit criteria:
- Workflow behaves correctly.

----------------------------

Phase 5
ReviewContext integration

Connect ReviewContext where it already belongs.

Do NOT make ReviewContext own workflow state.

Exit criteria:
- Analysis pages use ReviewContext.
- Workflow optionally consumes semantic information.

----------------------------

Phase 6
Cleanup

Remove obsolete caches.

Remove duplicate ownership.

Remove dead code.

Regression test everything.

====================================================
FOR EACH PHASE PROVIDE
====================================================

Files to modify.

Expected behavior change.

Risk level.

Rollback strategy.

Regression tests.

Estimated duration.

====================================================
IMPORTANT

The objective is NOT a perfect architecture.

The objective is:

• stable
• maintainable
• minimal risk
• minimal code churn
• maximum reuse of existing implementation

If an existing service can simply be corrected, do that instead of introducing a replacement service.

Optimize for implementation success rather than architectural purity.