# BirkNext — System Understanding: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `2c00b29c`.
- **Method:** read-only inspection of HEAD (an independent read-only exploration of navigation, the Skolenærvær modules, source evidence, revision stores, diagrams and authorization), plus the reviewed cross-repository verification `AIAssisted/docs/skolenaervaer-cross-repo-verification.md`.

| Area | Exists | Reusable for System Understanding | Gap |
|---|---|---|---|
| Navigation (`NavigationCatalog`, `NavStatusPresentation`, collapsed sidebar) | Yes | One row in Workspace; nav tests list rows exactly | — |
| Skolenærvær knowledge | Curated in code: `SkoleNaervaerTopologyProfile` (components, paths, expected relationships), `SkoleNaervaerContractProfile` (contract chain, v2.0.0), `SkoleNaervaerJourneyPack` (journeys, scenarios), message verification, Altinn reference | Cited as evidence; not re-derived | No explanation layer for testers; MM→MU acknowledgment, Tjeneste/placement and Person detail are not modelled; v2.0.1 only appears in name parsing |
| Reviewed source evidence | `skolenaervaer-cross-repo-verification.md` (Skole (2).zip, Meldingsutvekslermottak (3).zip; V/T/I/NV labels with file:line) | Primary grounding for the curated draft | MU and SkoleAdapter source not reviewed |
| Source snapshots | `IqrSourceSnapshot` (no source text kept), `IqrSourceStore.ListSourceAnalysisAsync`, `TestPackSources.Recognizes` | Change awareness by role, identity only | **No file/line viewer** (source text is not retained), so citations name snapshot + file + symbol + lines and link to the owning explorer |
| Revision stores | Setup Library item/version, contract test revisions, manual review decisions, execution manifests | Patterns only (the semantics differ) | **No guide revision store** with draft / review / approve / publish and immutable published content |
| Diagrams | `architectureGraph.js` / `TopologyMap` (layered SVG, selectable) | Interaction model | No main-lane + side-flow layout |
| Recommended Workflow | `ProjectQualityStages` "understand" stage | A contextual link | — |
| Authorization | `IntegrationConfigurationWritePolicy` (Entra scope/role, or Development local writes) | For edit, submit, approve, publish | **No separate reviewer role**; no UI capability flag for configuration writes |
| Documentation features | Help Center, Documentation Drift, Generated Documentation | None of them explains a system under test | No conflict |

**Confirmed gaps implemented:**
1. A generic guide model.
2. The guide revision store with database-enforced immutability.
3. The lifecycle API.
4. The page (flow, steps, data and contracts, rules, tests, gaps, revisions).
5. The curated Skolenærvær draft.
6. Change awareness.
7. The Workflow link and the Help topic.
