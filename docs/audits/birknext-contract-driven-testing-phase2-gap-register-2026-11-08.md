# BirkNext — Contract-Driven Testing phase 2: gap register (2026-11-08)

Baseline HEAD `93e38abe`, final source HEAD `398a8952` on `008-traceability-first`. Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.
"Browser" means the isolated stack: web :5199 (`webpub-cdt2`), API :5099 (DB `birknext_cdt2_20261108`, content root `api-cdt2`), synthetic API + token
endpoint :5288. Acceptance check IDs (`P2-…`, `P2R-…`) refer to `docs/audits/birknext-contract-driven-testing-phase2-acceptance-2026-11-08.md`.

## Original gaps

| GAP | Status | Commit(s) | Backend tests | Browser | Remaining |
|---|---|---|---|---|---|
| GAP-CDT-1 Authenticated execution | **PARTIAL** | `363c3e9c`, `398a8952` | `ExecutionAuthenticationTests` (client credentials, API key, refused token, no substitution, attended scope/profile/fingerprint, unsafe token URLs, oversized/unsafe/redirected token answers, anonymous negative in an authenticated run) | PASS for unattended OAuth2 client credentials (P2-04…P2-11) and API key (P2-28…P2-31) | **Attended** (Local HTTPS proxy session) is verified by backend tests only. The browser E2E is NOT RUN: it needs the proxy, an HTTPS target and a real sign-in. Basic: NOT SUPPORTED (no policy). A separate OAuth2 delegated (authorization code) mode: NOT SUPPORTED; delegated access is the attended proxy session. |
| GAP-CDT-2 Contract validation | **PASS** | `c00085f3`, `8b97fe40` | `ContractValidationTests`, `ContractTestingTests` (invalid response fails with schema path; invalid enforced request never sent) | P2-15, P2-29 (response check Valid, REST and GraphQL) | Keywords outside the evaluated set are reported as unsupported → NotAssessed, never a pass. Source-backed versions without document text: structural validation only. |
| GAP-CDT-3 Editable versioned tests | **PASS** | `c64a0585` | revisions, conflict, revert, duplicate, credential-literal refusal | P2-14 (edit → new revision, history kept), P2-15 (evidence names the edited revision) | The assertion editor is verified by bUnit, not by a browser edit. |
| GAP-CDT-4 Configurable endpoints | **PASS** | `363c3e9c` | base path / GraphQL path validation, server proposals | P2-03, P2-13 (proposal from `servers`), P2-28/P2-29 (GraphQL at `/api/gql-keyed`) | — |
| GAP-CDT-5 Negative generation | **PASS** | `d0fa364b` | `ContractTestingTests` (12 variants, documented or "Needs configuration") | P2-05, P2-08 (MissingAuthentication sent without credential, passes on documented 401) | — |
| GAP-CDT-6 Diff/impact classes | **PASS** | `a7a95122` | `ContractTestingTests` (Definite, No evidence, migration); `ContractImpactRulesTests` (Potential: removed required response field; Needs human review: security and servers changed), added while writing this register because no test covered those two classes | P2-18 (Definite / No evidence with reasons), P2-19 (reviewed proposal → revision bound to v2; old execution still v1) | The browser fixture produced Definite and No evidence only. |
| GAP-CDT-7 Source Analysis integration | **PASS** | `3432cd2b` | `SourceContractImportTests` | P2-24/P2-25 (synthetic archive → snapshot → import with snapshot id + path, no re-upload) | The archive was uploaded through the API endpoint, not through the Source Analysis page. |
| GAP-CDT-8 OpenAPI/Swagger reading | **PARTIAL** | `3432cd2b`, `8b97fe40` | diagnostics (JSON line/position, unclosed flow on line N, deep JSON/YAML refused) | — | YAML and Swagger 2 are still read structurally by the generic reader (ParseSupport Partial, named limitation); `$ref` chains are not resolved there. The validator reads their raw document, so validation does not depend on it. |
| GAP-CDT-9 AQR uses stored GraphQL versions | **PARTIAL** | `55e0dd5a` | `ApiReviewEngineStoredContractTests`, `ApiReviewStoredContractPickerTests` | P2-26: picker renders; **0 rows** because a fresh DB has no discovered AQR targets | Choosing a stored version for a target in the browser was not exercised. `graphql_schema_artifacts` remains a runtime fallback (not migrated). |
| GAP-CDT-10 AQR uses stored REST versions | **PARTIAL** | `55e0dd5a` | as above | as above | as above |
| GAP-CDT-11 Navigation, deletion, retention | **PASS** | `174a9304` | `ContractTestingTests` (retention, hide, delete refusals, archive/deprecate), `ContractTestingEndpointSecurityTests`, nav + docs tests | P2-01, P2-21…P2-23, P2R-03 (403 without permission) | — |
| GAP-CDT-12 GraphQL selections | **PASS** | `c64a0585` | editor + GraphQL validator tests | — | Covered through the editable documents of GAP-CDT-3; not edited in the browser. |

## Defects found during phase 2

| ID | Found by | Defect | Fix | Regression |
|---|---|---|---|---|
| CDT2-DEF-1 | Browser acceptance (stage 10, P2-07 timeout) | "Select ready" picked a secured contract's positive test and its MissingAuthentication negative; the run was refused (422) for mixing authentication kinds. | `398a8952`: `ExecutionCallDefinition.WithoutAuthentication`, so the negative goes without the credential inside the authenticated run; only drops a credential, never adds one. | `A_missing_authentication_negative_runs_in_the_same_run_without_the_credential`; P2-07/P2-08 PASS |
| CDT2-DEF-2 | Security test (stage 9) | `paths: [unterminated` YAML imported as an empty contract. | `8b97fe40`: unclosed flow collection + nothing readable → refused with its line. | `Hostile_contract_documents_and_URLs_are_refused_with_a_reason_and_never_fetched` |
| CDT2-DEF-3 | Security audit (stage 9) | Token endpoint answer read unbounded; any string accepted as a bearer token. | `8b97fe40`: 64 KB bound, JSON depth 16, header-safe token check. | `An_oversized_unsafe_or_redirected_token_answer_is_never_used` (3 cases) |

## Open items carried forward

1. Attended (proxy session) browser E2E. It needs an HTTPS synthetic target behind the Local HTTPS proxy with a scripted sign-in. **NOT RUN.**
2. YAML / Swagger 2 structural reading to full parity with JSON OpenAPI 3 (GAP-CDT-8).
3. AQR stored-contract selection in the browser with discovered targets (GAP-CDT-9/10).
4. Migrating `graphql_schema_artifacts` into the contract store, if wanted. Kept separate on purpose for now.
5. Basic authentication: NOT SUPPORTED until a policy for it exists.
