# Canonical source context (tasks 14/15)

One authoritative answer to "which imported source does BirkNext analyse?", reused by every module. No second registry, no second comparison
engine, no second association engine.

## Concepts (kept apart)

| Concept | Where it lives | Meaning |
|---|---|---|
| Logical application | front-end `SddApplicationContext` (unchanged) | The application a person works on, e.g. M2LB. |
| Logical source | Existing `SourceRepositoryIdentity` on each snapshot (`ReviewSourceEvidenceProvider.Identity`) | A codebase. The **basis** of the grouping is always shown: `Archive file name` means grouped by name only, **not** verified repository identity. |
| Source snapshot | `iqr_source_snapshots` (insert-only, unchanged) | Immutable imported content. Two identities: the **archive SHA-256** (bytes) and the new **canonical content fingerprint** (`SourceArchive.ContentSha256`). |
| Branch | snapshot `Branch`/`Commit` (Git imports) plus `DeclaredBranch` decisions | `Verified` from Git metadata only; `Declared` is a person's label, shown as such; otherwise `Unknown`. The ZIP name is never used. |
| **Analysis baseline** | new append-only `source_decisions` (kind `AnalysisBaseline`) | The snapshot that represents one logical source **for analysis**. Chosen by a person. Importing never changes it. |
| Composite source context | Integration Suites composite | An explicit selection across **different** sources/stages. Discovery offers each project's authoritative version only. Saving a context with two different versions of **one** source (e.g. MM from one M2LB branch and SkoleService from another) is refused: component-level composition across versions is not supported. |
| Deployment baseline | not created here | An analysis baseline is **not** a deployment claim. Deployment identity stays `DEPLOYMENT_BASELINE_NOT_VERIFIED` unless deployment evidence exists. |
| Target Environment | unchanged | Runtime configuration. Project Evidence reads the source context but never takes deployment from it. |

## The shared rule (`SourceBaselineService.Resolve`)

Per logical source, **at most one** snapshot is authoritative (`SourceBaselineService.Authoritative`, shared by every consumer):

1. The selected analysis baseline (`BASELINE_SELECTED`).
   - If that snapshot no longer exists, the source is **left out**; nothing is substituted (`BASELINE_UNAVAILABLE`).
2. Otherwise:
   - the only snapshot (`SINGLE_SNAPSHOT`); or
   - the newest of identical-content imports (`IDENTICAL_CONTENT`: one version, so no decision is needed); or
   - **nothing** (`BASELINE_NOT_SELECTED`) when versions with different content exist and no baseline is chosen.

**Correction (2026-11-12).** The first version (412f13bb) fell back to the newest import, flagged `NotSelected`. That let import order decide which
branch every module analysed, so it was removed. Now:
- With `BASELINE_NOT_SELECTED`, the source is left out of every cross-module conclusion: topology, contract candidates, System Understanding basis,
  composite candidates, technology coverage (Project Quality and Recommended Workflow) and Project Evidence. Each says why.
- Inventory (Source Analysis of any snapshot), decision history and comparison of any two versions stay available.
- The newest import is exposed only as `PreviewSnapshotId`, labelled "Newest import · preview only, not authoritative". It is never persisted as a
  decision; reading or comparing records nothing.
- A person's choice is stored in `source_decisions` and survives navigation, newer imports and API restarts.

Alternative snapshots of one source are never combined.

`SourceContextView.ContextFingerprint` changes whenever any used snapshot changes, so callers can key caches and detect stale pages.

### Modules switched to the rule

| Module | Before | Now |
|---|---|---|
| Integration Topology (`IntegrationTopologyService`) | newest snapshot per **archive file name**, so five branch ZIPs counted as five repositories and were merged | resolved set |
| Contract Testing source candidates (`ContractRegistryService.Source`) | per contract id, the newest snapshot of the group that holds it, so contracts were mixed across branches | only the resolved snapshot per source |
| System Understanding guide basis (`SystemGuideService.CurrentSourcesAsync`) | every matching snapshot (all five versions cited) | resolved set; a baseline switch shows as a source change for affected claims |
| Composite source discovery (`CompositeSourceRules.Discover`) | newest per project key | the authoritative version only; a project with `BASELINE_NOT_SELECTED` is no candidate (named in the limitations); an unavailable baseline is not substituted |
| Composite context save (`CompositeContextService.SaveAsync`) | any snapshots | two different versions of one source in one context are refused |
| Technology coverage → Dashboard / Recommended Workflow (`TechnologyCoverageController`) | newest snapshot overall | newest of the resolved set (an explicitly named snapshot still wins); when the newest import's source has `BASELINE_NOT_SELECTED`, no coverage and a `BASELINE_NOT_SELECTED` notice (no other source stands in) |
| System Understanding role without an authoritative version | "not imported" | `BASELINE_NOT_SELECTED — imported in alternative versions …` |
| Source Analysis (inventory of one snapshot) | "Current snapshot" without its role | unchanged selection; a badge says whether it is the analysis baseline, an alternative version, or one of several versions without a baseline |
| Project Evidence (front end, `ProjectEvidenceAssociation.Of`) | five same-named imports = "Conflict"; four "related" sources needing review | versions of one source: the baseline is the application source and the others are "alternative version — not used"; without a baseline, **one** review item: choose it |
| Review source scopes | already explicit (one snapshot per repository, nothing substituted) | unchanged |

## Decisions and history

- `source_decisions` is append-only: id, kind, subject, snapshot, value, label, content fingerprint, decided_at, decided_by, reason.
- The newest row per subject is current. Selecting the same snapshot again records nothing. Timestamps are strictly increasing per subject.
- The actor is derived: `authenticated-user:<hash>` or `local-session:loopback`. It is never a name.
- Writes need the backend policy `SystemConfigurationWrite`; reading is open to the local session.

API:
- `GET api/source-context`
- `GET api/source-context/history?repositoryKey=`
- `POST api/source-context/baseline`
- `POST api/source-context/declared-branch`

## Comparison

The existing **Source Change Impact** engine compares architecture, contracts, configuration, infrastructure, database and CI/CD.
- It already refused different logical sources.
- It also refused two **different imports** of one source, which blocked branch comparison. Now one side may be an alternative version of the same source.
- Its panel, previously not hosted on any page, is reachable from Source Management → Source versions. A person can compare **any two versions**
  ("Compare … with …") or compare a version with the one in use (with the newest import while no baseline is selected). The direction is kept,
  and comparing never changes a decision.

## Migration

`20261111230323_AddSourceDecisions`: one new table and one index. Additive, no data rewritten. It is applied by the normal startup migration, and was
tested on an empty isolated database (`birknext_mb`, disposable PostgreSQL 16 cluster).

`20261112070530_GuardSourceDecisions`: a `BEFORE UPDATE` trigger refuses every update of `source_decisions` (the execution-manifest pattern).
DELETE stays possible for Local Data Reset. There is no schema change, and the model snapshot is unchanged. Validation is recorded in the
acceptance report:
- on the isolated test databases of the transfer round-trip test, where an UPDATE through EF fails;
- as an upgrade of the populated acceptance database `birknext_mb`.

Older snapshots have no content fingerprint. They are compared by archive hash (`archive:` prefix) and never guessed equal.

## Import history and portability

- **Import records** (front end `SddProjectImportRecord`): the import id is derived from the archive hash, so importing the same bytes again reuses
  the record. Its top-level fields are the mutable operational view of the latest import. The new append-only `History` keeps each import that
  produced a different snapshot (time, archive, content fingerprint, snapshot, state). An earlier snapshot no longer loses its import provenance.
  A record saved before this change is seeded with its previous facts on its first re-import.
- **Project packages** (`.birknext`, Workspace Transfer): a new object type `source-decision` carries each subject's complete decision history (the
  project's analysis baselines and its snapshots' declared branch labels), unchanged, with the original actors and times. On import:
  - a destination with **no** decisions for that subject receives the history, so its baseline matches the origin;
  - a destination with **its own** history is reported as a conflict and never changed;
  - a decision naming a snapshot that is missing is still carried, and the missing snapshot is reported.

  An older BirkNext build sees `source-decision` as UNSUPPORTED (the existing rule for unknown types) and skips it visibly.
