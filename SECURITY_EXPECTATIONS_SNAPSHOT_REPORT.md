# Security Expectations snapshot consumption and candidate review report

## Repository and audit (A–I)

- Starting HEAD: `d912bb2e2a40fd6207e0c99500d9cb7bf96bcabd`.
- Branch: `008-traceability-first`. Initial tree: clean. The supplied workspace parent was not a Git repository; these checks were repeated in BirkNext before edits.
- Old source ownership: Source Analysis already owned upload and immutable `IqrSourceSnapshotRecord` persistence. `IqrSourceStore.AnalyzeAsync` ran the security analyzer using the same architecture input/facts at ingestion, and stored its allow-listed projection inside `IqrSourceSnapshot.SecurityExpectationsEvidence`. Security discovery read this JSON; no separate ZIP upload/store existed.
- Old candidate/evidence model: one candidate held one source location, and its ID included component/file/symbol/value. Exact values at different locations became separate rows. Evidence occurrences had no separate aggregated representation.
- Old conflict rule: a singleton with multiple distinct detected values became Conflict even with no approved value. The component independently repeated that rule.
- Old actions: Conflict showed “Replace / choose detected” and “Keep current”; ambiguity inherited both despite no approved value. Matching candidates still had Ignore.
- Old summary: candidate count and needs-review count counted location rows. Snapshot label used “Current”. Refresh loaded the latest snapshot again. A newer upload made prior results stale and prevented acceptance.
- Approved values remain in browser-local Target Environment profiles (`birknext:frontend-analysis-settings`). Manual edits use existing Save/Cancel. Source Accept invokes `SaveSecurityApprovalAsync`, which saves the selected profile immediately and rolls it back on save failure. Backend review decisions have their existing separate revision/concurrency persistence. Refresh never saves approved values.
- Baseline focused test attempt was blocked by a stale physical scoped CSS bundle and an already-running apphost. The generated stale bundle was removed; later tests used isolated outputs first, then standard outputs to resolve repository-relative fixture assumptions.

## Source Analysis and source scope (J–P)

- Reused `IqrSourceSnapshot`, `IqrSourceSnapshotRecord`, the existing `IqrSourceSnapshots` DbSet, and the ingestion-owned safe security/architecture projection. Discovery does not read or parse an archive, upload, or create a source snapshot. Its only write is the existing discovery/review record.
- New discovery retains primary ID, fingerprint, archive display name, analyzed timestamp, explicit related IDs, and a fingerprint map for every included snapshot. Review checks those immutable bindings and environment ownership.
- Scope has exactly one primary and zero or more explicitly included related snapshots. Same canonical values aggregate evidence across snapshots; different values stay separate. No synthetic source snapshot is created.
- Related snapshot checkboxes are opt-in; no repository is auto-included. No pilot repository name is hard-coded.
- Automatic related-repository suggestions are deferred: the persisted security projection used by existing snapshots does not retain a reliable external repository identity/reference-to-snapshot mapping. Internal `ArchitectureInput` has package/project references during ingestion, but using those as an automatic repository match would guess provenance. Available snapshots can be included explicitly. Source Analysis remains the place to add missing sources.
- Source list contains standalone Source Analysis snapshots for the target. Initial selection binds to the most recent discovery’s exact snapshot. Refresh keeps that selection. A newer available snapshot produces a notice with Review newer snapshot and Keep current snapshot; it does not invalidate or rebind the selected review.
- Historical JSON is not rewritten. Legacy occurrences are grouped in a read projection and remain read-only; original occurrence IDs and review decisions remain accessible. Missing IDs/fingerprints are not fabricated. Approved policy remains approved across snapshot changes.
- Feature visibility follows Source Analysis’s existing convention: hiding the feature hides the menu entry; approved settings remain available. No live validation is added.

## Field inventory and normalization (Q–U)

| Field | Cardinality | Value type | Conservative normalization |
|---|---|---|---|
| Authority | Singleton | URL | Trim; scheme/host case and DNS terminal dot; standard URL default-port handling; preserve path/version |
| Tenant ID | Singleton | GUID or tenant domain | GUID D format; domain case; no domain/GUID alias mapping |
| Client ID | Singleton | GUID/public legacy identifier | GUID D format; exact legacy identifier comparison; no fuzzy match |
| Redirect URL | Multi-value | URL | Scheme/host normalization; preserve path case, distinct paths and trailing slash |
| Backend domain | Multi-value | Host/port | Host case/DNS terminal dot; preserve non-default ports; endpoint paths intentionally project to hosts |
| REST host | Multi-value | Host/port | Same host semantics |
| GraphQL host | Multi-value | Host/port | Same host semantics |
| CDN host | Multi-value | Host/port | Same host semantics |
| Security header | Multi-value | Header name | Case-insensitive allow-listed names; the existing model stores names only, not policy values |

`SecurityExpectationValues` centrally supplies field definitions, normalization, grouping, state derivation and acceptance action labels. Razor performs no candidate deduplication. Secret-shaped, credential, token, wildcard, malformed and unresolved values remain excluded/redacted. Client IDs remain public identifiers. Existing header extraction intentionally excludes values, so header name equality does not claim policy equality.

## Authority audit (V–Z)

The audited ZIP `Downloads/M2LB (2).zip` has the same SHA-256 as the selected immutable snapshot. Frontend `Program.cs:84` registers MSAL authentication; Person `AuthExtensions.cs:65` and ScimAdapter `ScimAuthenticationExtensions.cs:33` register Microsoft Identity Web. Proxy `AuthenticationExtensions.cs:52` explicitly constructs `Instance + TenantId + /v2.0`. BirkNext forwards approved authority as `AllowedAuthority` in `FrontendQualityReviewOrchestrator.cs:1178`; no field-wide path/version equivalence is proved. Consequently `/v2.0` is retained, not stripped.

| Raw/canonical authority | Occurrences | State with no approved authority |
|---|---:|---|
| https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3 | 7 | Needs review |
| https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3/v2.0 | 1 | Needs review |

Total: 8 occurrences, 2 raw values, 2 canonical values, 2 candidates, 0 current-vs-source conflicts.

## Candidate, state and action rules (AA–AR)

- Unique candidate: one `(FieldType, NormalizedValue)` group across the explicitly selected scope. IDs hash field, canonical value and sorted snapshot IDs. Source occurrences retain their original IDs separately.
- Evidence: one retained source observation with snapshot/fingerprint, source identity, file/line, symbol/key, raw value, kind, confidence/state. New ingestion retains the safe raw value; historical host projections cannot recover endpoint text already omitted by the old analyzer.
- Supporting evidence count is the sum of retained occurrences, not candidate groups. Needs review counts distinct fields with pending candidate decisions; matched, accepted, rejected and stale candidates do not increase it.
- Ordering: field enum order, ordinal canonical value; evidence repository/file/symbol/line. No occurrence is deleted to make history prettier.
- No approved + one singleton: Detected (or Suggested for inferred evidence), Accept.
- No approved + multiple singletons: Needs review, Choose this value on each. Never Conflict or Keep current.
- Approved match: Matches source; no replacement or ignore action.
- Approved differing singleton: Conflict; Replace with detected requires explicit replacement; Keep current rejects the candidate without changing approval.
- Multi-value match: Matches source. New multi-value member: Detected/Suggested addition; Add or Ignore, never generic Conflict.
- Ignore remains the existing persisted backend decision; no new casual ignore store was introduced.
- Refresh only recomputes from selected stored evidence; zero automatic approvals. Source Analysis status is displayed separately and no longer determines candidate state or the Security Expectations result status.

## UI (AS–BC)

The old location-row dump is replaced by approved values and compact per-field unique/evidence/review summaries. Candidate details are initially absent from the DOM and one field opens at a time. Evidence rows render only after View evidence, in a semantic table inside a horizontal scroll region. Long public values wrap safely. State and action labels are text, not color-only. Review controls carry aria-expanded; snapshot selector is labeled and omitted when no snapshots exist. Exact archive/fingerprint/time is visible, with Open Source Analysis. “Discover from source” is now “Refresh candidates”. The blue expectations-only/no-secrets banner remains. The optional related scope has explicit checkboxes; newer-source notices preserve the existing selection.

## M2LB pilot (BD–BR)

Snapshot: `ee5237bb-6344-4c5b-9a7f-59405d8cc880`; archive: `M2LB _2_.zip`; analyzed: `2026-10-01T11:14:55.6066602+00:00`; Source Analysis: `Partial`.

Fingerprint: `c850a1b2813bbf6e2a9eba1f311d63ff0332eacf35764cd6b767489ac22dc41e`.

Pilot approved policy: the five existing default header names and no identity/hosts, matching the supplied no-approved identity scenario. Browser-local user profile policy is not a backend global store; the smoke does not modify that user profile. Counts: **5 approved, 56 unique candidates, 119 supporting occurrences, 7 fields needing review**. Refresh did not approve anything.

| Field | Unique candidates | Supporting occurrences | Result |
|---|---:|---:|---|
| Authority | 2 | 8 | NeedsReview |
| TenantId | 2 | 19 | NeedsReview |
| ClientId | 13 | 26 | NeedsReview |
| RedirectUrl | 1 | 2 | Detected |
| BackendDomain | 17 | 30 | Detected |
| RestHost | 14 | 25 | Detected |
| GraphQlHost | 7 | 9 | Detected |
| CdnHost | 0 | 0 | No candidate; not evidence of a defect |
| SecurityHeader | 0 | 0 | No candidate; not evidence of a defect |

Exact candidate values and evidence counts:

| Field | Canonical value | Evidence | State |
|---|---|---:|---|
| Authority | `https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3` | 7 | NeedsReview |
| Authority | `https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3/v2.0` | 1 | NeedsReview |
| TenantId | `00000000-0000-0000-0000-000000000000` | 1 | NeedsReview |
| TenantId | `25609970-3b75-45b9-9899-036bb1693ff3` | 18 | NeedsReview |
| ClientId | `00000000-0000-0000-0000-000000000000` | 1 | NeedsReview |
| ClientId | `1da6804f-3325-4d9e-ad90-083e541223e1` | 1 | NeedsReview |
| ClientId | `23be8783-a768-47c0-804e-2740c6216272` | 4 | NeedsReview |
| ClientId | `48e2a0c3-b5a7-4b72-99c7-1a148e6d5eab` | 1 | NeedsReview |
| ClientId | `534521ba-7c62-41cf-8fae-4b219189186e` | 3 | NeedsReview |
| ClientId | `66670d36-1c7b-4eb5-8a08-80412ea57271` | 1 | NeedsReview |
| ClientId | `8484a212-0ece-429b-bfb1-87e261632011` | 1 | NeedsReview |
| ClientId | `8adf8e6e-67b2-4cf2-a259-e3dc5476c621` | 3 | NeedsReview |
| ClientId | `b37a7566-3910-4e7d-9bf6-1a4466b1d6fb` | 4 | NeedsReview |
| ClientId | `b3803a4d-30d9-4d2e-a29c-48bec82903fb` | 2 | NeedsReview |
| ClientId | `b7bde6da-624b-41dc-b6fa-532eb30abfde` | 2 | NeedsReview |
| ClientId | `b8d625b0-b651-463b-8127-47bd8d96c25f` | 1 | NeedsReview |
| ClientId | `ca74cd5c-34dc-45e9-a4a6-ba65875d1221` | 2 | NeedsReview |
| RedirectUrl | `http://localhost:7284/authentication/login-callback` | 2 | Detected |
| BackendDomain | `ca-m2lb-autorisasjon-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 4 | Detected |
| BackendDomain | `ca-m2lb-autorisasjon-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 2 | Detected |
| BackendDomain | `ca-m2lb-hendelse-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| BackendDomain | `ca-m2lb-hendelse-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| BackendDomain | `ca-m2lb-person-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| BackendDomain | `ca-m2lb-person-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| BackendDomain | `ca-m2lb-tjeneste-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| BackendDomain | `ca-m2lb-tjeneste-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| BackendDomain | `graph.microsoft.com` | 4 | Detected |
| BackendDomain | `localhost:5003` | 1 | Detected |
| BackendDomain | `localhost:7029` | 2 | Detected |
| BackendDomain | `localhost:7096` | 1 | Detected |
| BackendDomain | `localhost:7112` | 2 | Detected |
| BackendDomain | `localhost:7181` | 1 | Detected |
| BackendDomain | `localhost:7196` | 1 | Detected |
| BackendDomain | `m2lbdev.bufetat.no` | 2 | Detected |
| BackendDomain | `m2lbqa.bufetat.no` | 1 | Detected |
| RestHost | `ca-m2lb-autorisasjon-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 4 | Detected |
| RestHost | `ca-m2lb-autorisasjon-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 2 | Detected |
| RestHost | `ca-m2lb-hendelse-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| RestHost | `ca-m2lb-hendelse-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| RestHost | `ca-m2lb-person-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| RestHost | `ca-m2lb-person-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| RestHost | `ca-m2lb-tjeneste-dev-nwe-001.internal.kindwave-9f2ef3f6.norwayeast.azurecontainerapps.io` | 2 | Detected |
| RestHost | `ca-m2lb-tjeneste-qa-nwe-001.internal.gentleocean-1252cb98.norwayeast.azurecontainerapps.io` | 1 | Detected |
| RestHost | `graph.microsoft.com` | 4 | Detected |
| RestHost | `localhost:5003` | 1 | Detected |
| RestHost | `localhost:7029` | 2 | Detected |
| RestHost | `localhost:7096` | 1 | Detected |
| RestHost | `localhost:7181` | 1 | Detected |
| RestHost | `localhost:7196` | 1 | Detected |
| GraphQlHost | `localhost:7029` | 1 | Detected |
| GraphQlHost | `localhost:7096` | 1 | Detected |
| GraphQlHost | `localhost:7112` | 2 | Detected |
| GraphQlHost | `localhost:7181` | 1 | Detected |
| GraphQlHost | `localhost:7196` | 1 | Detected |
| GraphQlHost | `m2lbdev.bufetat.no` | 2 | Detected |
| GraphQlHost | `m2lbqa.bufetat.no` | 1 | Detected |

## Verification (BS–CK)

Focused tests cover duplicate collapse/evidence retention, generic source duplicates in ten files, no-approved singleton/ambiguity, approved match/conflict, multi-value addition, authority path/version, redirect path/case/slash, host port, GUID/domain distinction, exact snapshot switching, newer snapshot stability, related same/different values and legacy read-only grouping. Frontend tests cover absent initial candidate/evidence rows, Accept/Choose action semantics, explicit approval and save flow, rejection, manual edit/target-switch races, accessibility labels and matching provenance.

Browser smoke used the existing immutable M2LB snapshot, not a new Security Expectations upload. At 1440/1100/768/390, collapsed and expanded: zero page overflow, keyboard review works, zero feature-attributable WCAG 2.1 AA axe violations, zero page errors. Opening Authority gives exactly two candidate rows and seven evidence rows when its first candidate is opened. The generic existing snapshot produces 12 candidates from 15 occurrences with no false conflict; the ten-file source fixture independently verifies collapse. The final browser smoke was repeated against the final Debug build, and its independent Security Expectations status is NeedsReview while Source Analysis remains Partial.

| Check | Result |
|---|---|
| BS–BY / CA: Security Expectations backend semantics | 38 passed, 0 failed |
| BZ / CA: Security Expectations frontend | 21 passed, 0 failed |
| CB: Backend Security Expectations + Source Analysis/IQR regressions | 74 passed, 0 failed (38 security + 36 source) |
| CC / authentication regression / frontend Source Analysis | Combined targeted set: 81 passed, 0 failed, including explicit Target Environment save/race/draft and Source Analysis tests plus the sample-project timing rerun |
| CD: Full backend standard-layout run | 3,246 passed, 2 failed, 6 skipped; 3,254 total |
| Backend isolated failure rerun | 14 passed, 1 failed in connector/scenario areas. Connector failures passed; scenario query still exceeded its 2-second budget (2.655 seconds; final single-test rerun 8.287 seconds) |
| CE: Full frontend | Final sole run: **4,628 passed, 0 failed**. Earlier concurrent-run authentication/spec-comparison failures also passed on isolated 107-test rerun |
| CF–CH: Final browser/responsive/axe | All eight width/expansion combinations passed; 0 feature violations, 0 page errors; keyboard Enter opens review; no auto-approval; initial DOM has no candidate or evidence rows |
| CI: Debug API and Web builds | Both passed, 0 errors |
| CJ: Release API and Web builds | Both passed, 0 errors (existing warnings retained) |
| CK: git diff --check | Passed |

The full backend failures were `ManagedEdgeConnectorTests.AdvertisedWebSocketMustRemainAtValidatedEndpoint` and `ScenariosQueryTests.Scenarios_With100Records_ReturnsAllOrderedDescAndCompletesWithinTwoSeconds`. The former passed isolated; the latter remains a timing-limit failure in code untouched by this patch. Concurrent full frontend runs produced failures in `AuthenticatedTestingMethodTests.EditingTheEnvironmentPreservesAndLabelsTheOriginalRuntime`, `AuthenticationPaneSemanticsTests.AnAuthenticatedApiContextDoesNotMarkVerificationDone`, `SpecComparisonPanelTests.ChangeExplorer_ShowsEmptyState_WhenDeltaFilterHasNoMatches`, and `SpecComparisonPanelSaveTests.SaveSection_AppearsAfterComparison`; their areas passed isolated and the final sole full frontend run passed all 4,628 tests. Full backend green acceptance is not claimed.

## Final scope and limitations (CL–CO)

Changed files are the shared contracts, discovery service/controller, ingestion evidence projection, frontend API/panel/CSS, focused backend/frontend tests, and this report. No migration is necessary: new metadata is additive inside existing JSON, and source history is unchanged. Authentication and Target Environment policy ownership are unchanged.

Remaining ambiguity: authority version equivalence is unproved; multiple tenant/client IDs (including zero GUIDs) in the actual archive require human selection. Related repository detection is deferred pending a reliable persisted reference-to-source identity mapping. The snapshot is source evidence, not proof of DEV/QA deployment or runtime security. Full backend timing failures are reported rather than hidden or fixed outside this task.
