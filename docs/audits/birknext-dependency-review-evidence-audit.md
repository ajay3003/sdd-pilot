# Dependency Review — evidence audit, part 1 (2026-11-13)

Branch `008-traceability-first`.

## "No stored inventory yet": root cause

The existing architecture **already** makes every stored source review a reusable declared inventory.
`DependencyHealthService.AllInventoriesAsync` merges two sources:

- stored inventories, which are SBOM, lock-file or deployed captures;
- `InventorySources.FromSourceReview(run)` for the latest 50 source-review runs. This creates one *Declared* inventory per reviewed
  repository, with archive sha256, run id, `owner file:line` locations, version vs version range, and its limitations ("declared only;
  not packaged/deployed").

So a completed source review does not lack an inventory. The defect was in the page. `LoadHealthListsAsync` caught a failed
`GET api/dependency-review/inventories`, put the reason only into the health status line, and left the inventory list empty. The picker
then said **"No stored inventory yet"**: a failure presented as "nothing exists".

## Fix

- The picker placeholder now distinguishes the cases:
  - **"Loading stored inventories…"**;
  - **"Stored inventories could not be loaded"**, with the reason shown in the status line;
  - **"No stored inventory yet — a completed source review or an imported SBOM/lock file creates one"**;
  - "Choose an inventory".
- Tests in `DependencyHealthPageTests` cover a failed request and an empty list.

## Risk observed (not changed)

`AllInventoriesAsync` deserializes up to 50 complete `DependencyReviewResult` JSON documents, plus 200 stored inventories, on **every**
inventories request. For M2LB-size runs (536 dependencies, 163 simulations), this is a plausible cause of the slow or failing requests
behind the misleading empty state, and of API memory growth. A persisted declared-inventory projection per run, or a summary-only query,
should replace it. That needs a backend design and migration, and was not done here.

## Not done (rest of the v2 brief)

- The three-workflow landing page.
- An evidence summary.
- Results shown before configuration.
- Explorers for findings, simulations and inventory.
- Real M2LB and browser acceptance.
