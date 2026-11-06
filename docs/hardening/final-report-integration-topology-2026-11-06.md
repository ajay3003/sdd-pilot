# Integration Topology redesign (2026-11-06)

## 1. Confirmed UI defects (source verified)

- **One long page.** Actions, discovery, summary + attention, shared identities, source coverage, expected relationships and the full technical inventory were stacked into one long page.
  - The page had no URL state.
  - The attention "Show" links jumped within that page.
- **No visual topology.** The page had no map, although the repo already has an SVG graph renderer (`wwwroot/js/architectureGraph.js`, used by Source Architecture).
- **No links to related pages.** There were no links to Relationship Review, Connected Systems or the Integration Catalog. Identity decisions were only reachable through the collapsed canonical alias panel.
- **"Runtime observations" is browser-local.** It counts this browser's Endpoint Discovery observations, which are merged and never stored. It is now stated as such in the guided steps.

## 2. Architecture and data dependencies (unchanged)

The view is computed on every load from stored source facts (`IntegrationTopologyService`). Coverage and expected relationships come from the Test Pack topology profile (`SkoleNaervaerTopologyProfile`, 9 relationships), per environment.

| Metric | Definition (`TopologyPresentation`) | Scope |
|---|---|---|
| Components | Application-kind components only | environment, computed |
| Interfaces | Sum of component interfaces (one per normalized identity) | environment, computed |
| Resolved connections | Connections Resolved/Observed and not in Conflict | environment, computed |
| Expected but unresolved | Profile relationships not Resolved/Observed | environment + profile |
| Source bindings needed | Roles NotProvided / NeedsAnalysis / AvailableNeedsBinding / Ambiguous / ReferenceUnavailable | environment + profile |
| Runtime observations | Observations from this browser merged into the view | browser-local |
| Unresolved call targets / interfaces needing review / ambiguous correlations | Mutually exclusive attention categories | environment, computed |

The counts are never summed.

## 3. New UX structure

- **Four tabs, as URL state (`?tab=`):** Overview · Topology Map · Evidence & Gaps · Technical Inventory.
  - Panels are hidden, not removed, so deep anchors and existing behaviour keep working.
  - Attention links switch to the tab that holds their section.
- **Overview:** guided steps (`TopologyWorkflow`), plus the existing summary, attention and shared identities, plus a line linking Relationship Review, Connected Systems and the Integration Catalog ("topology registers and approves nothing"). The steps are:
  1. Select project and environment
  2. Review source coverage
  3. Inspect topology
  4. Resolve evidence gaps
  5. Review integrations
  6. Validate runtime evidence

  A step is never marked done because it was visited.
- **Topology Map** (`TopologyMap` + `TopologyMapPresentation`):
  - Two levels. **Expected flow** uses the profile's relationships with their own states. **System connections** shows components with connections grouped per pair, bounded to the 40 most connected; the rest are counted and stay in the inventory.
  - It is drawn with the existing SVG renderer (pan, zoom, drag, fit, keyboard selection) and fitted after drawing.
  - State is carried by line style and text, never by colour alone:
    - solid = Resolved/Observed;
    - dashed = Candidate/Strongly supported (inferred, not resolved);
    - dotted = Expected but unresolved;
    - dash-dot = Conflict;
    - "observed" in the label = runtime evidence.
  - The legend says "Nothing here is confirmed by a person". The topology contracts have no confirmed state.
  - A state filter is provided.
  - Edge details show the state, transport, runtime, why, what is missing and the evidence. For a grouped pair they show the connections themselves.
  - An accessible table lists exactly the same edges. It is also the fallback when drawing fails.
- **Evidence & Gaps:** source coverage and expected relationships (resolved / expected but unresolved), each with its existing owner action, unchanged.
- **Technical Inventory:** components, interfaces, connections and sources, unchanged.

## 4. Before / after

The single long scroll became a tabbed workspace. The default view is the summary with the next step. The map is drawn only on its tab, so other tabs pay no JS cost.

## 5. Evidence and status semantics

- No relationship is invented. The map draws only profile relationships or stated connections.
- Inferred topology is never drawn as resolved. A pair of connections is drawn by its best non-conflicting relationship, and a conflict wins.
- The Utdanning ↔ SkoleAdapter identity decision is untouched. Identity decisions are linked to Relationship Review.
- Topology never registers a system or approves an integration. This is stated on the page.

## 6. Performance

- The graph is built once per (view, coverage, level), not on every render.
- The JS draw happens only when its key changes (environment, level, filter, count).
- System nodes are bounded to 40.
- No new dependency was added, and there are no extra backend calls (the same `GetAsync` and `CoverageAsync` as before).

## 7. Files changed

- `frontend/BirkNext.Web/Pages/IntegrationTopologyPage.razor` (+ css): tabs and panels, steps, related links.
- `frontend/BirkNext.Web/Services/TopologyWorkflow.cs` (new)
- `frontend/BirkNext.Web/Services/TopologyMapPresentation.cs` (new)
- `frontend/BirkNext.Web/Components/TopologyMap.razor` (+ css, new)
- Tests:
  - `Pages/IntegrationTopologyPageTests`: +4, and one reason text updated (map filters also live in `.it-filters`);
  - `Services/TopologyMapPresentationTests`: new, 3 tests.

## 8. Automated tests (actual runs, 2026-11-06)

**Filter:** Topology*, SkoleNaervaer* and IntegrationQualityReview*. Result: **223/223 passed**, both after the first fix round and again after the fit/caption polish.

**New tests:**
- **Overview default tab.** Covers four tabs, hidden panels and no map off-tab. The steps come from the coverage: "1 available but not bound (bind, do not re-import)" and "3 of 5 expected unresolved". Visiting is never completion. Related links are present.
- **Map expected flow.** Line styles are stated in text:
  - strongly supported → dashed, never solid;
  - expected unresolved → dotted;
  - observed is marked.

  The filter works, and the details show what is missing. The existing SVG module is imported.
- **Systems level without a profile.** The conflict pair reads "Conflict · dash-dot line". Coverage is Not applicable.
- **Attention links.** They switch to the tab that holds their section.
- **`TopologyMapPresentationTests` (3).** Bounding to 40 nodes with the omitted count; candidate pairs are inferred and a conflict is never solid; the expected flow uses the profile's own states.

**First run:** 3 failures, all test-setup issues. Coverage loads only when the view lists a profile, and the Sample fixture has no interface needing review. Fixed in the tests; no product change was needed.

## 9. Browser verification

`topo-e2e.cjs` ran against the published build on the local stack (`birknext_gaps`, real Skolenærvær profile): **14/14 passed**.
- **Tabs and steps:** four tabs; six steps (source coverage is current); only Overview is visible by default.
- **Map tab:**
  - `?tab=map` in the URL;
  - 9 expected relationships in the table, each with state and line style in text (expected unresolved = dotted, candidate = dashed, resolved = solid);
  - the SVG has 10 nodes and 9 edges, matching the table;
  - keyboard Enter on an edge opens its evidence ("Altinn → MU: Expected but unresolved … Missing: MU configuration…").
- **System connections level:** 67 pairs, bounded to 40 nodes with "1 more" noted.
- **Layout:** no horizontal scroll at 390px.
- **Other tabs:** Evidence & Gaps and Technical Inventory hold their sections.
- **Safety:** no state-changing request (only the topology view/coverage POSTs, which recompute from stored facts), no external host, no page errors.
- Screenshots are in the scratchpad folder `shots-topology/`.

After the polish (the drawing is fitted after drawing, the table caption moved to the top, the canvas is shorter), the browser check was re-run: 14/14 passed.

## 10. Remaining limitations

- **Node layout** is the renderer's layered layout. Node positions can be saved in the browser (the renderer's existing save/reset), but this page does not expose Save.
- **Expected-flow nodes are display names** from the profile, not component ids. The relationship's `ConnectionIds` link to the real connections, which the System connections level shows.
- **Evidence & Gaps keeps the existing per-relationship worklist.** Grouping by severity, system or action beyond the existing "missing evidence groups" was not added.
