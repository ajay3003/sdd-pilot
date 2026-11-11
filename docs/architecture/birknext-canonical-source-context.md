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
| Composite source context | Integration Suites composite (unchanged) | An explicit selection across different sources/stages; discovery now offers the baseline (or the newest) per project, never all alternatives. |
| Deployment baseline | not created here | An analysis baseline is **not** a deployment claim. Deployment identity stays `DEPLOYMENT_BASELINE_NOT_VERIFIED` unless deployment evidence exists. |
| Target Environment | unchanged | Runtime configuration. Project Evidence reads the source context but never takes deployment from it. |

## The shared rule (`SourceBaselineService.Resolve`)

Per logical source, exactly **one** snapshot is used:

1. The selected analysis baseline.
   - If that snapshot no longer exists, the source is **left out**; nothing is substituted. State: `SelectedUnavailable`.
2. Otherwise:
   - the only snapshot (`SingleSnapshot`); or
   - the newest of identical-content imports (`IdenticalContent`); or
   - the newest import, flagged **`NotSelected`**, with the message that implementation-specific results depend on choosing.

Alternative snapshots of one source are never combined.

`SourceContextView.ContextFingerprint` changes whenever any used snapshot changes, so callers can key caches and detect stale pages.

### Modules switched to the rule

| Module | Before | Now |
|---|---|---|
| Integration Topology (`IntegrationTopologyService`) | newest snapshot per **archive file name**, so five branch ZIPs counted as five repositories and were merged | resolved set |
| Contract Testing source candidates (`ContractRegistryService.Source`) | per contract id, the newest snapshot of the group that holds it, so contracts were mixed across branches | only the resolved snapshot per source |
| System Understanding guide basis (`SystemGuideService.CurrentSourcesAsync`) | every matching snapshot (all five versions cited) | resolved set; a baseline switch shows as a source change for affected claims |
| Composite source discovery (`CompositeSourceRules.Discover`) | newest per project key | baseline else newest; an unavailable baseline is not substituted |
| Technology coverage → Dashboard / Recommended Workflow (`TechnologyCoverageController`) | newest snapshot overall | newest of the resolved set (an explicitly named snapshot still wins) |
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
- Its panel, previously not hosted on any page, is reachable from Source Management → Source versions → "Compare with the version in use". The direction (baseline → target) is kept.

## Migration

`20261111230323_AddSourceDecisions`: one new table and one index. Additive, no data rewritten. It is applied by the normal startup migration, and was
tested on an empty isolated database (`birknext_mb`, disposable PostgreSQL 16 cluster).

Older snapshots have no content fingerprint. They are compared by archive hash (`archive:` prefix) and never guessed equal.
