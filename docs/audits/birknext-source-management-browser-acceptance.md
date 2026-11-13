# Source Management — browser acceptance (2026-11-13)

**Setup:**
- isolated Release web published fresh (`mb/web5`, Blazor integrity 86/86) and the isolated API with the real M2LB workspace: 2 logical sources, 10 snapshots, 7 imported packages;
- Edge; non-localhost aborted; harness `scratchpad/mb/sm-accept.cjs`.

**11/11 PASS:**

| Check | Result |
|---|---|
| SM-01 | Header keeps *Import source* (primary) and *Snapshot Registry* |
| SM-02 | Each of the 7 rows shows one overall state, `Source Analysis: Partly analyzed`. The 5 area chips are collapsed: 0 rendered until expanded, checked with `checkVisibility()` and the row screenshot `sm-row.png` |
| SM-03 | *View analysis details* shows every area: Application code, Architecture, Configuration, CI/CD and Contracts, each · Partial |
| SM-04 | With several sources the tiles, the toolbar and the review-vs-analysis note are shown |
| SM-05 | Source versions panel unchanged ("not verified repository identity", baseline wording) |
| SM-R1440/1280/1024/768/390 | No horizontal overflow |
| SM-Z | 0 console errors, 0 external requests |

**Not run in the browser:**
- The single-source layout: the shared workspace has seven sources. It is covered by the component test with one source.
- Baseline selection was not changed, to keep the shared workspace unchanged; its behaviour and tests are untouched.

**Harness note:** the first run counted closed `<details>` content as visible (`getBoundingClientRect` reports sizes inside a closed `details`). It was rerun with `checkVisibility()`.

**Memory:** the isolated API held 2.3 GB private after one page load, before it was stopped. This is the open gap TPS-P2.
