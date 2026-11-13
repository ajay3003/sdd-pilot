# Document Quality Review — UX audit and change (2026-11-13)

Starting HEAD: `01cb16e1`. Page: `Pages/QualityReview.razor` (`/quality/document`, about 3 000 lines).

## Verified root cause of "Run is far down the page"
The page rendered, in this order:
1. Feature delivery readiness (`ImplementationGapExplorer ReadinessOnly`), then Shared SDD evidence (from Requirements Traceability);
2. then step **1** Current workspace artifacts, step **2** Review Packs and step **3** Review readiness with **Run Document Quality Review**.

So both supporting-evidence sections, which hold large feature cards, came before the review workflow they support.

## Change (presentation only)
- The two sections moved, unchanged, **after** step 3 (Run), into a *Supporting evidence* section. One sentence says this is context, not a documentation finding, and that a checked task is not verified implementation.
- The order is now **scope → Review Packs → readiness and Run → (results) → supporting evidence**.
- All test ids, calculations, pack readiness, run behaviour, results and history are unchanged.

**Already correct on HEAD (kept):**
- readiness is worded as "ready to run", not as a result;
- a run failure says "No result was recorded for this run";
- the shared evidence says *Not assessed* is not a failure;
- policy significance is shown.

## Not done in this pass (gaps)
- No compact or searchable supporting-document inventory.
- No change to Review Pack card height or to the wording of the feature status labels.
- No sticky Run bar.
