# BirkNext — Contract Testing final integration & identity hardening: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `ae1db8e5`. Audit: `docs/audits/birknext-contract-testing-final-hardening-audit.md`.
Local commits: `086e0caf` (identity, rename, migration) · `64952f64` (bulk binding) · `721b629d` (project coverage, chain suggestions) ·
`66130b61` (UI) · `770c8c99` (header guard found by acceptance) · reports commit.

## CTH-F01 — Project-safe identity

**Model.** A project is the existing Source Analysis repository identity of a snapshot (`ReviewSourceEvidenceProvider.Identity`: the root
solution, else the archive file name) joined only with identities a person declared "the same repository" (`source_repository_associations`,
transitive). No new global identifier. A contract stores its owning `ProjectKey`; every source-backed version stores `SourceProjectKey`.
The source path is provenance and a matching signal inside a project — never identity across projects.

**Rules** (`ContractRegistryService.Source.cs`): continuation, discovery de-duplication, listings (`?project=`) and explicit targets are scoped to
the project group; an explicit target of another project is refused ("Contracts are never shared across projects"); an uploaded (unscoped)
contract may be adopted explicitly by a project.

**Migration** `20261110072554_AddContractProjectIdentity` (forward-only, additive): `api_contracts.ProjectKey`, `api_contracts.IdentityState`,
`api_contract_versions.SourceProjectKey` (all nullable) + `contract_review_decisions`. No row is rewritten by the migration. **Legacy resolution**
(`ResolveLegacyIdentitiesAsync`, run at startup and lazily before any classification, idempotent): versions get their snapshot's project; a
contract whose versions all resolve to one project group gets it; no source-backed version → `Unscoped`; versions in unrelated projects or
snapshots that no longer exist → `NeedsReview` (never continued, never guessed; an import touching it is refused as Ambiguous until a person
assigns the project — `PUT contracts/{key}/project`, recorded). Execution evidence refers to `key@vN#op` strings and keys never change, so
historical evidence keeps its exact meaning.

## CTH-F02 — Rename / move detection

Classification of a source contract against the project's contracts: `SameContract` / `NewVersion` (same location); otherwise candidates =
contracts of the project group, same protocol, not archived, whose own locations are absent from the snapshot (still present = a copy, not a
move): identical document (SHA-256 of the retained text) → `MovedUnchanged` (one) / `Ambiguous` (several); ≥ 60 % of operations shared →
`MovedChanged` (one) / `Ambiguous`; ≥ 30 % shared or same title with a shared operation → `PossibleMatch` / `Ambiguous`; else `NewContract`.
A title alone never matches. `MovedUnchanged` is recorded as a location of the existing contract (no new version); the review cases are never
imported in bulk or silently; `POST contracts/identity-decisions` (confirm a candidate / reject one / import as new) records decision, evidence,
reviewer and time. Source location history = the versions' `SourcePath` + recorded locations.

## CTH-F03 — Bulk environment and authentication

`ContractBulkBindingService`: `POST bindings/preview` per contract — endpoint evidence (saved binding **AUTHORITATIVE**, one declared server path
**CANDIDATE**, several **AMBIGUOUS**, none **UNRESOLVED**; path only, never a host), proposed base path (keep / from servers / one explicit path,
validated by the existing `PathProblem`), the authentication fit with each operation's declared security (shared `AuthenticationFitProblem`, also
used by readiness; never downgraded to None), the environment's credential reference (never a secret; missing → blocked), environment gate
shared with runs (safety policy; production refused), project ownership. `POST bindings/apply`: explicit environment confirmation, per-row
review/overwrite confirmation, concurrency check (`ExpectedUpdatedAt`), the one binding writer, audit row (`binding`, before/after, who/when),
per-row status APPLIED / UNCHANGED / SKIPPED / NEEDS_REVIEW / BLOCKED / FAILED. Audience/scope validation is not available in the credential
resolver and is shown as a limitation.

## CTH-F04 — Browser execution

No product change was needed to run from the browser; acceptance exercised it end to end (BE-01…BE-09, BR-01) and found one rendering defect
(`770c8c99`). Runs keep using Execution Evidence and its gates.

## CTH-F05 — Project-wide coverage

Coverage metrics now carry a group: **Definition** (tests, test data), **Negative**, **Execution**, **Verification** (passing, *verified* = passed
with assessed assertions, valid responses, assertions), **ResponseCode**. `ContractProjectCoverageService` (`POST projects/{key}/coverage`)
aggregates the per-contract coverage: each contract once at its latest version (no double counting across snapshots), exact sums per metric,
older versions with execution as history (never in the current denominator), readiness blockers when an environment is given, latest execution
time. UI: KPI cards (NOT ASSESSED where nothing ran), module groups, search, filters (missing tests, not executed, failed verification, changed,
blocked), drill-down to the contract's coverage and from there to tests and Execution Results.

## CTH-F06 — Chain suggestions

`ContractChainSuggestionService`: create → (update) → read when a POST on a collection documents a typed 2xx response whose field names the item
path parameter of the same collection (`id`/`{x}Id`); list → details when the list returns an array of such a type. Untyped responses and
unrelated id-like fields never produce a chain; DELETE is never added. Each suggestion lists steps, data flow, evidence, confidence, missing
prerequisites (no test, not ready, data-driven limitation), data-changing steps. Accept creates a **Draft** chain (new `ContractTestChain.State`,
JSON — no schema change); drafts never run (`RunChainAsync` refuses) until explicitly approved; suggestions are idempotent (`SuggestionId`) and
rejections recorded.

## UI

Workspace bar (project selector; Import / Configure environment / Project coverage; URL state), identity review on discovery rows, identity banner
with project assignment, bulk configuration (select → preview → approve → confirm environment → apply → per-row results), project coverage
dashboard, chain suggestions with draft/approve, Integration Suite "Resolve environment blockers" hand-off (contracts preselected).

## Tests

Backend: `ContractIdentityTests` (7: scenarios A–H, association, move unchanged/changed with recorded decision, titles alone never match,
ambiguous + reject, GraphQL relocation, legacy resolution/blocking/assignment), `ContractBulkBindingTests` (2), `ContractProjectCoverageTests`
(2: loopback execution, verified, once per contract, history; chain suggestions), existing suites green. Frontend: `ContractTestingPageTests`
(+8 incl. the short-fingerprint regression), `IntegrationSuitesPageTests` (+1).
