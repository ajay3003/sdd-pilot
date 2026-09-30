# IQR source evidence implementation report

Date: 2026-09-30. This feature inspects source and developer test definitions. It does not execute those tests or send Event Hub messages.

## Repository state (A–B, BN–BQ)

- Starting HEAD: `9a023f9395e71619da6ad9820bca408ead3b24d8`.
- Initial tree: modified `IntegrationReviewEngine.cs` and `IntegrationCatalogContracts.cs`, plus untracked `Services/Integrations/EventHub/`. These were pre-existing changes.
- During this session, those changes were committed independently as `4f20dbd8fce330bff39517e00a7d55433230388e`. This feature is based on that commit and preserves it.
- Feature commit hash is provided in the final task response; this report belongs to that commit.
- Changed files: shared source contracts; API source upload controller, reader, analyzer, store, domain augmentation, database record/migration and registration; review selection/history integration; frontend upload/selection/details components, API client, export; backend/frontend regressions; this report. No active messaging capability was added.
- Final whitespace check and repository status are provided in the final task response.

## Architecture and retention (C–L)

| Item | Implemented behavior |
| --- | --- |
| Existing architecture | Wolverine and SCIM already have bounded in-memory ZIP readers and source evidence. Wolverine uses Roslyn syntax analysis. IQR stores immutable run JSON. File traceability stores metadata rather than a required source tree. |
| Input | One `.zip`, attached explicitly to a configured integration. Saved analyses can be selected for a run. No repository connector or CI integration was invented. |
| Retention | Insert-only structured JSON in `iqr_source_snapshots`: identifiers, safe locations, project/configuration-key inventory, rules, developer definitions, mappings, gaps and limitations. No source bodies, configuration values, test bodies or uploaded archive are stored by this feature. |
| Temporary handling | A bounded virtual workspace in memory; no extraction directory is created. ASP.NET multipart buffering can temporarily spool the original request upload above its memory threshold; framework request disposal cleans that buffer. There is no application-retained upload or extracted tree. |
| Fingerprint | Lowercase SHA-256 of the archive bytes. Identical bytes give the same hash, including when renamed. Changed bytes give a different hash; repacking equivalent source may change the hash. Each analysis has a new snapshot ID. |
| Version | Analyzer version 1 and UTC analysis timestamp. A single literal 40-hex `SourceRevisionId` in project metadata is accepted; conflicting/absent metadata means `Unknown`. Branch is not resolved. `.git` is ignored. |
| Discovery | `.csproj` XML and C# syntax, longest matching project-directory ownership. Malformed projects/syntax retain usable evidence from other projects and produce Partial. No usable production project produces Failed. |
| Production | Event Hub package references, Web SDK, or production/shared project evidence. An SDK reference alone does not prove a consumer role. Mapper/service symbols are discovered from code, not a hardcoded M2LB directory. |
| Tests | Explicit test-project metadata/framework packages, then test-level fixtures, production calls, mocks and host-use syntax. Unit/Integration/Component/Architecture/Unknown vocabulary; uncertain classification has Partial confidence. |
| Security | Configuration values are discarded before analysis; only bounded JSON/YAML key inventories remain. No arbitrary source snippets or literals are persisted. Metadata uses existing dependency and proxy redaction plus bounded allowlisting and sentinel/credential-pattern removal. Logs receive no raw source. |

Limits: 50 MB archive, 20,000 entries, 100 MB declared expanded total and actual analyzed read total, 2 MB per analyzed file, 5,000 rules, 10,000 test definitions, 1,024 configuration keys/file. Absolute paths, traversal, symlinks and duplicate case-insensitive paths are rejected before ignoring files. Nested archives and binaries are not extracted. Unsupported languages are reported. `bin`, `obj`, `node_modules`, packages, `.git`, coverage and test caches are ignored. C# primary constructors are normalized through the existing Wolverine helper; this is syntax inspection, not compilation.

## Real-source audit boundary (M–AC, questions A–Q)

No current M2LB repository/archive was found in this workspace. Consequently the real Person Adapter project, fields, envelope, operation/table routing, security wiring, checkpoint behavior and real developer inventory remain **not assessed**. Historical expectations in the task were not substituted for current source. No production defect is claimed.

The safe synthetic browser fixture verifies the ingestion and evidence pipeline; its facts below are **fixture-only**, not findings about M2LB. Its fingerprint is `7ffbeb8d702fb3bc3ab50e0409e74feeb8e7eccefd87d0baf871932ac65799ae`, commit Unknown, analyzer status Ready. Ready means analysis completed, not protection or tests passed.

| Fixture area | Source evidence |
| --- | --- |
| Projects | `src/PersonAdapter/PersonAdapter.csproj` with Event Hub SDK reference; `tests/Adapter.Tests/Adapter.Tests.csproj` with xUnit metadata. |
| Envelope | JSON-model property annotations for payload/op/source/before/after/table. These annotations alone do not prove mandatory deserialization. |
| Operations | Switch branches c/u/r read after; d returns. Default/other operation evidence is retained only when present. No behavior is imported from Debezium documentation. |
| Tables | Person → PersonMapper; Barn → ChildRegistrationMapper. No claims about Kommune/reference/organisational tables. |
| Deletes | Fixture d branch returns before mapper routing. This is source branch evidence, not runtime verification. |
| Malformed envelopes | Fixture JsonException catch/log path found. Following-valid-event processing is not proven. Valid-envelope structural validation remains unresolved unless code proves it. |
| Checkpoints | Checkpoint invocation inside processing try block found. Exact successful/failed-batch advancement, deliberate withholding and poison-event resilience require further source/runtime evidence. |
| Error handling | Catch/log/retry/error-queue/discard call evidence is extracted when present; no observed retry or downstream success is implied. |
| Security | Constant-zero model assignment plus same-named guard argument yields a Partial potential cross-layer gap. Raw input and downstream binding remain unresolved. Isolated guard test is linked but does not suppress the gap. |
| Developer definitions | 3 Unit, 1 Integration (heuristic host-use classification), 1 Unknown. None were executed by the analyzer. |

Person fixture contract (Q):

| Field | Implementation semantics | Developer evidence |
| --- | --- | --- |
| PersonPK | Negative TryGetValue/type-check branch returns null; required for mapper output. Exact accepted-type behavior requires review. | `MissingPersonKeyReturnsNull` Unit and `MissingPersonInHost` Integration classification. No same-layer duplicate. |
| Navn | Conditional read with missing → null fallback; not labeled required. | No confidently equivalent definition resolved. |

Barn fixture contract (R):

| Field | Implementation semantics | Developer evidence |
| --- | --- | --- |
| BarnPK | Missing key returns null; required for mapper output. Invalid value handling not resolved. | `MissingBarnKeyReturnsNull` Unit. No same-layer duplicate. |
| BarnStatusTypeFK | Missing field uses numeric fallback 0; invalid-type behavior not resolved. | No confidently equivalent definition resolved. |

Other requested Person/Barn fields remain unknown for actual M2LB. Additional regression fixtures verify enum invalid-value warning/fallback, nullable fallback, field reads without requiredness, operation/table branches and null rejection.

## Coverage and gaps (AD–AL)

The correlation engine requires the same nonambiguous production symbol, an explicit missing-key mutation on the actual tested input before the production call, and a null assertion on that production result. Intervening input mutations, different inputs, method-name similarity, unrelated assertions and unresolved overloads do not establish equivalence. xUnit/NUnit/MSTest definitions and supported null assertions are inventoried; other assertion intents are summarized conservatively.

| Dimension | Behavior |
| --- | --- |
| Unit covered (AF) | Fixture PersonPK and BarnPK rules link the actual test definitions/locations. |
| Integration covered (AG) | Fixture PersonPK links a hosted test definition, with classification uncertainty visible. This is not proof its assertion traverses a deployed application. |
| Source-only (AH) | Unmatched rules remain source evidence with no matching developer test found/manual verification, rather than an absolute uncovered claim. |
| Cross-layer (AI) | Zero-assignment security path remains visible despite `Level2Rejected` isolated guard definition. No complete compiler dataflow claim. |
| Runtime (AJ) | RuntimeGap remains independently present on contract rules, including developer-covered rules. |
| E2E (AK) | E2EGap remains; no end-to-end processing exercised. |
| Manual/not assessable | Ambiguous symbol relationships and unsupported patterns preserve uncertainty. |
| Duplication (AE) | Equivalent same-layer evidence says “Covered by developer test; no same-layer duplicate required.” No executable candidates are created for any source rule in this implementation. Future candidates must first resolve developer equivalence. |
| Results (AL) | “Developer test exists; execution result unavailable.” No TRX/JUnit/CI result importer was found/reused. The new pipeline does not claim definitions passed. |

Counts are definitions, not theory-row counts or global code-coverage percentages. Test discovery precedes mapping/gaps. There is no separate source test product.

## IQR integration and history (AM–AU)

| Domain/item | Behavior |
| --- | --- |
| Contracts | Field/model/validation evidence makes implementation-source review available/partial. Formal schema and runtime compatibility remain independent. |
| Message Flow | Actual source route/operation items accompany existing runtime domains. Resolved source tables are compared with the explicitly configured integration source; unresolved mismatches are not confirmed drift. Azure/Event Hub evidence remains independently observed/configured. |
| Reliability | Source checkpoint/retry items accompany runtime reliability evidence; no runtime retry or advancement claimed. |
| Error Handling | Source catch/log/discard/checkpoint items; malformed-message resilience remains unexecuted. |
| Security | Source guard/dataflow items and warnings; isolated test definitions do not establish raw wiring/runtime protection. |
| Data Quality | Structural mapper/model rules only; no business-data correctness claim. |
| Source/deployment | Explicit “Deployment/source correlation not established”; no assumed deployment commit. |
| History | Run request selects integration + snapshot ID. Ownership is checked against environment and enabled integration. The exact analysis is embedded in immutable run JSON; upload B does not replace historical A. Historical rendering performs no re-analysis. |
| Export | Source identity, safe project/key inventory, contract rules, definitions, mappings, gaps, flow confidence and limitations. No archive, source body, config values or absolute temporary paths. Source runs use “inspected or observed,” not “tests passed.” |

Target Environment still owns runtime configuration; source is implementation evidence attached to an integration/review, not imported runtime topology. Upload does not overwrite Terraform/integration configuration. Existing Wolverine/application-messaging source analysis remains available through its existing IQR evidence workflow; this generic archive pipeline does not automatically run the full Wolverine-specific analyzer. Generic retry/error-queue calls can be inspected here. Terraform parsing, repository checkout, CI artifacts and formal schema validation are not added.

## Verification (AV–BM)

| Requested check | Result |
| --- | --- |
| Safe ZIP/security (AV) | Valid ZIP, invalid ZIP/extension, traversal variants, symlink, duplicate path rejection; bounded reads and ignored trees. No extraction directory exists to leak. |
| Secret sentinel (AW) | Backend persisted-snapshot, safe metadata/config, UI/export regressions plus browser fixture. Isolated PostgreSQL: 4 snapshots, 0 sentinel matches, 0 raw class-body matches. Browser export and API log sentinel absent. |
| Focused backend (AX) / source regression (AZ) | 29 source-analysis cases pass. Includes hash/unknown commit, Person/Barn, defaults/routes, unit/integration equivalence, input mutation, ambiguity, cross-layer guard, partial analysis, immutable history and domain correlation. |
| Focused frontend (AY) | 3 source UI/export tests plus 1 active environment ID binding regression pass; source + existing IQR evidence group 12 pass. |
| Combined backend regression | 421 pass, 0 fail. |
| IQR (BA) | Backend 126 pass; frontend 48 pass. |
| Event Hub runtime (BB) | Backend 49 pass; frontend 25 pass. |
| Dependency Review (BC) | Backend 100 pass; frontend 14 pass. |
| Frontend Review/engine group (BD) | Backend 38 pass; frontend 696 pass. |
| API Review (BE) | Backend 154 pass; frontend 222 pass. |
| Security Classification (BF) | Backend 47 pass; frontend 27 pass. |
| Full backend (BG) | 3,067 pass, 6 skip, 0 fail; 3,073 total. |
| Full frontend (BH) | Unit suite: 4,417 pass, 0 fail. Existing Playwright suite: 9 pass, 8 fail, 17 total. Same eight tests fail on pre-feature commit `4f20dbd` when run serially. Full frontend solution is therefore not entirely green. |
| Debug (BI) | Backend and frontend solution builds succeed, 0 errors. Final incremental backend 0 warnings; frontend 1 existing warning. |
| Release (BJ) | Backend and frontend solution builds succeed, 0 errors; 72/138 existing warnings respectively. |
| Responsive (BK) | Synthetic upload/details/review at 1440, 1100, 768, 390; document width equals viewport. Coverage area scrolls independently. |
| Accessibility (BL) | Source panel scoped axe: zero violations at all four widths; native details keyboard Space opens. This is a scoped check, not a whole-application accessibility certification. |
| Browser smoke (BM) | Upload → evidence → explicit selection → IQR → historical snapshot preserved after reanalysis → export; no page JavaScript errors. Azure provider disabled and isolated local database; no real adapter or Event Hub used. |

Regression groups overlap and must not be added together. Test logs/TRX, fixture ZIP, screenshots and browser verification scripts are outside the feature repository in the parent workspace. The existing Playwright failures concern outdated endpoint-discovery selectors and live FQR/backend/frontend setup; serial baseline comparison establishes they are unchanged. An initial browser attempt used the default local database before explicit connection-string isolation; only the synthetic fixture rows were subsequently removed. Final source checks used `birknext_source_iqr_verify_20260930`.

## Remaining limitations (BR)

Actual M2LB audit is pending a current archive. The analyzer is deliberately heuristic: no semantic binding, complete call graph/dataflow, runtime execution or comprehensive test-intent solver. Missing/null equivalence is narrow; other relationships stay unresolved. Exact checkpoint advance/withhold behavior, raw classification propagation and production runtime resilience require additional evidence. Unsupported languages, dynamic routes, conditional compilation, external helpers and unparsed projects remain explicit limitations. Existing analyses are listed up to the newest 50 per environment; old run evidence still renders from its stored snapshot. Repository/CI/test-result connectors, deployed-build identity, branch discovery, complete Wolverine re-analysis through this new input, and active runtime tests are outside this implementation.

This delivers source ingestion and conservative IQR evidence, but does not claim every real-source acceptance criterion is verified without the M2LB snapshot or that the existing full Playwright suite passes.
