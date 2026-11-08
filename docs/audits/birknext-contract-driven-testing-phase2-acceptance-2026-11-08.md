# BirkNext — Contract-Driven Testing phase 2: acceptance (2026-11-08)

Baseline HEAD `93e38abe`; final source HEAD `398a8952` plus `ContractImpactRulesTests` (committed with these reports) on `008-traceability-first`.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.

**Overall: PARTIAL.** The REST and GraphQL journeys, unattended authentication, source integration, impact/migration, coverage, lifecycle and
permission enforcement pass end to end in the browser. Three things are not verified in the browser:
- the **attended** (proxy session) browser journey: NOT RUN;
- AQR stored-contract **selection** with discovered targets: PARTIAL;
- full YAML/Swagger 2 structural parity: PARTIAL.

They are listed in the gap register and are why this is not declared complete.

## Environment (isolated, synthetic)

| Item | Value |
|---|---|
| API | :5099, published from the scratch worktree to `pub-api-cdt2` and copied to the content root `api-cdt2`. The copied `appsettings.Local.json` was removed from that scratch copy so the user's local configuration was not used; the repository file was not touched. `ASPNETCORE_ENVIRONMENT=Development`; outbound proxy pointed at a dead local port. |
| Database | `birknext_cdt2_20261108` (new, disposable, recreated before the final run) |
| Web | `webpub-cdt2` served by `serve-web-cdt2.cjs` on :5199. `api-gaps` and `webpub` were **not** written or deleted. |
| Target | `mock-target2.cjs` on 127.0.0.1:5288: v1/v2/secure OpenAPI, GraphQL SDL, OAuth2 client-credentials token endpoint, bearer- and API-key-protected routes, `/api/gql-keyed`. It records every request (method, path, body, correlation id, and whether a valid bearer/key was present, never the value) and is the **independent oracle**. |
| Credentials | Synthetic only: `synthetic-client` / `synthetic-secret-cdt2-4471`, token `synthetic-token-cdt2-9921`, key `synthetic-key-cdt2-5510` |
| Browser | Playwright, Microsoft Edge; every non-localhost request aborted and counted |
| Write permission | Main run: `Authorization:LocalConfigurationWrites=true` (existing Development + loopback path). Restart run: `false`. |

The user's stack on 5000/5173 and the user databases were not touched. No external system was contacted: P2-Z2 and P2R-Z2 counted 0 external requests.

## Browser journeys — `cdtval2/accept2.cjs main`: 36/36 PASS

| ID | Check | Result |
|---|---|---|
| P2-00 | Synthetic datasets created and approved through the Test Data API | PASS |
| P2-01 | Sidebar "Contract Testing" opens the page; AQR link kept | PASS |
| **REST + unattended OAuth2 client credentials** | | |
| P2-02/03 | Secure contract imported by URL; base path proposed from `servers` (path only) | PASS |
| P2-04 | Credential saved encrypted. The API returns configuration only (`secretStored: true`); neither the JSON file nor the `.bin` contains the secret | PASS |
| P2-05/06 | MissingAuthentication negative proposed; positive test Ready with credential + binding + Test Data | PASS |
| P2-07 | Oracle: `GET /api/secure/children/7` with `authorization: valid-bearer`; call Passed (HTTP 200) | PASS |
| P2-08 | Oracle: the negative went **without** Authorization in the same run; passed on the documented 401 | PASS |
| P2-09 | Exactly 1 token request for the run; run records "Unattended: OAuth2 client credentials" and `api-credential:local-synthetic-cdt2:ClientCredentials:v1` | PASS |
| P2-10 | Neither token nor secret is in stored evidence or the run export; the Authorization header is recorded as redacted | PASS |
| P2-11 | Execution Results shows the authentication mode and no token | PASS |
| **REST lifecycle (anonymous, base path /api)** | | |
| P2-12/13 | v1 imported (4 operations); anonymous binding `/api` saved from the declared server | PASS |
| P2-14 | UI edit creates an immutable revision (≥ 3 revisions, change note kept) | PASS |
| P2-15 | Run: anonymous calls to `/api/children…` only (oracle); evidence names the edited revision `@r{n}`; response contract check Valid | PASS |
| P2-16 | Coverage of v1 reconciles with the run evidence. Oracle from evidence: operations executed {GET /children, GET /children/{id}} = 2/4, 2 not assessed; valid responses 2/3 (the 400 negative has no body schema → not assessed); assertions 7/7 | PASS |
| P2-17 | v2 imported as the next version | PASS |
| P2-18 | Impact: `/admin/reports` Definite (removed); POST Definite (new required `birthYear` not sent); edited GET /children/{id} No evidence ("not asserted and optional") | PASS |
| P2-19 | Reviewed migration proposal applied → revision bound to v2; the earlier execution still names `@v1` | PASS |
| P2-20 | Coverage of v2 shows 0 executed (v1 evidence not borrowed) | PASS |
| P2-21 | Retention: v1 kept (test revisions + execution records), v2 latest; contract deletion refused ("archive instead") | PASS |
| P2-22 | Archived: banner, every test Blocked, generation refused | PASS |
| P2-23 | Unreferenced contract deleted only after typing its key (partial key keeps the button disabled) | PASS |
| **Source integration** | | |
| P2-24 | Synthetic archive (`SyntheticChildren/…/children-api.json`, .csproj, Program.cs) analyzed into a snapshot via `POST /api/source-analysis/snapshots` | PASS |
| P2-25 | Imported from the snapshot without re-upload: `source: SourceSnapshot`, snapshot id `8298ee0a-…`, path `openapi:SyntheticChildren/openapi/children-api.json`; limitation stated (structural validation only) | PASS |
| **AQR** | | |
| P2-26 | Stored-contract picker renders under "Technical setup & diagnostics". **0 rows**: a fresh DB has no discovered AQR targets, so selecting a stored version was not exercised (see PARTIAL) | PASS (render only) |
| **GraphQL + API key on a non-default path** | | |
| P2-27/28 | SDL imported by URL; binding `/api/gql-keyed` + environment API key `X-Synthetic-Key` | PASS |
| P2-29 | Oracle: both GraphQL POSTs went to `/api/gql-keyed` with `key: valid-key`; the positive sent `{"id":"7"}` and passed; response contract check Valid. The negative (no `id`) passed on the GraphQL error | PASS |
| P2-30 | No mutation was sent | PASS |
| P2-31 | API key never in evidence; header redacted | PASS |
| **Layout / a11y** | | |
| P2-32 | No horizontal page scroll at 1280/1024/768/390 on Tests, Coverage, Lifecycle and Changes (1440 is the main context) | PASS |
| P2-33 | Keyboard: the selected view tab is the only Tab stop; Enter opens Lifecycle | PASS |
| P2-Z1/Z2 | No page or console errors; no external requests | PASS |

Screenshots (synthetic data only), in `scratchpad/cdtval2/shots/`, not committed: `p2-01-endpoint-auth.png`, `p2-02-evidence-auth.png`,
`p2-03-coverage.png`, `p2-04-impact.png`, `p2-05-lifecycle.png`, `p2-06-changes-390.png`, `p2-07-coverage-390.png`.

## Restart and permission — `accept2.cjs after-restart`: 6/6 PASS

The API was stopped (by the PID owning :5099) and restarted with `LocalConfigurationWrites=false`.

| ID | Check | Result |
|---|---|---|
| P2R-01 | REST, authenticated and GraphQL runs still present after the restart | PASS |
| P2R-02 | Coverage and contract versions (v2) survive the restart | PASS |
| P2R-03 | Without the write permission: `PUT credentials`, `DELETE contracts/{key}` and `PUT …/lifecycle` → **403**, and the UI says "you do not have permission…" | PASS |
| P2R-04 | Credential unchanged; browsing after restart sent nothing to the target (oracle count unchanged) | PASS |
| P2R-Z1/Z2 | No errors; no external requests | PASS |

## Defect found by acceptance

**CDT2-DEF-1.** "Select ready" picked a secured contract's positive test together with its MissingAuthentication negative, and the run was refused
(422, mixed authentication). It was reproduced through the API, a failing regression test was added, and it was fixed in `398a8952`. The acceptance was
then rerun from a fresh database and passes (P2-07/P2-08). Two other first-run failures were wrong expectations in the acceptance script, not product defects:
- P2-18 expected Potential for an optional, unasserted field; the stage-7 rules correctly say No evidence.
- P2-16 assumed 2 latest calls; there were 3, including the 400 negative.

Both checks now reconcile against the run evidence instead.

## Authentication modes tested

| Mode | Backend tests | Browser |
|---|---|---|
| None (anonymous) | PASS | PASS (P2-15) |
| OAuth2 client credentials (unattended) | PASS | PASS (P2-04…P2-11) |
| API key (unattended) | PASS | PASS (P2-28…P2-31) |
| Attended session (Local HTTPS proxy, delegated) | PASS (scope, profile, fingerprint, no substitution) | **NOT RUN** (needs proxy + HTTPS + real sign-in) |
| Missing authentication by design (negative) | PASS | PASS (P2-08) |
| Basic | — | **NOT SUPPORTED** |

## Regression (scratch worktree build, memory-guarded, sequential)

| Suite | Result |
|---|---|
| Backend partition 1 | 984 passed, 0 failed, 2 skipped |
| Backend partition 2 | 1096 passed, 0 failed, 5 skipped |
| Backend partition 3 | 830 passed, 0 failed |
| Backend partition 4 | 652 passed, 0 failed, 1 skipped |
| Backend partition 5 | 520 passed, 0 failed, 3 skipped |
| Backend partition 6 (rerun) | 2069 passed, 0 failed, 8 skipped |
| **Backend total** | **6151 passed, 0 failed, 19 skipped** |
| **Frontend (full)** | **8486 passed, 0 failed** |
| Focused after the last change (ContractTesting incl. `ContractImpactRulesTests`, ManagedEdge) | 131 passed, 0 failed |

Partition 6 first run: 3 failures, investigated before the rerun:
- 2 × `ManagedEdge*` read source files from a doubled `wt\AIAssisted\AIAssisted\…` path. An empty stray folder in the scratch worktree, created today by my own misdirected sync, made their repository-root lookup stop one level too deep. The folder was verified to contain no files and removed. This was not a product defect.
- 1 × `Negative_proposals_cover_many_variants…`: a GraphQL call timed out (29 s) at the partition's memory peak (~4 GB used). It passed in every focused run and in the rerun. It is recorded as a one-off timeout seen once under memory pressure, not fixed.

The rerun of partition 6 is green, and the frontend run covered all frontend tests (filter `FullyQualifiedName~BirkNext`). Partitions 1–5 ran on
the final source; the only later change is the new test file `ContractImpactRulesTests`, which runs in partition 6 and in the focused run.

## Cleanup (needs your approval; nothing deleted yet)

These are my phase-2 acceptance resources: database `birknext_cdt2_20261108`, folders `pub-api-cdt2`, `api-cdt2`, `webpub-cdt2`, `cdtval2`, and
scripts `run-api-cdt2.sh`, `reset-cdt2.sh`, `publish-cdt2.ps1`, `serve-web-cdt2.cjs`. All processes on 5099/5199/5288 are stopped.
`api-gaps`, `webpub` and every user database are untouched and excluded.
