# BirkNext Audit Reconciliation and Sign-off — 2026-11-07

**Authoritative current status.** This document reconciles the existing audit, remediation, verification and release reports against the
verified source baseline. It adds no new audit, test or product change. Where an earlier report's *current* conclusion differs, this document
states which applies and why; earlier sections of all reports remain as historical record.

## 1. Executive conclusion

| Item | Status |
|---|---|
| G1–G8 | **PASS** (G7 within its defined journeys, G8 within its defined scope) |
| G9 Localhost security | **PARTIAL** — runtime binding variants and cross-account ACL acceptance not run |
| G10 Tester package | **PARTIAL** — clean-PC first-run acceptance not run |
| Developer readiness | **GO** |
| Controlled tester rollout | **CONDITIONAL GO** |
| Broader distribution | **NO-GO** until G9 and G10 acceptance |
| Major hardening phase | Technical assurance recommendation: **COMPLETE WITH ACCEPTED EXCEPTIONS** · Formal approval: **PENDING HUMAN DECISION** |
| Git Sources | Local setup **VERIFIED**; real Microsoft sign-in and Azure DevOps access **NOT VERIFIED** (external) |

## 2. Source baseline

| Item | Value |
|---|---|
| Branch | `008-traceability-first` |
| HEAD | `afe08f2082fff5c2e62bf973e3c2c5c141c12fd1` (tree `5a7f8a21e412ef3f4db6368639369e7135221b7c`) |
| Latest code-bearing commit | `60671884` = `606718846a1640d6cac867b0f90e50961377fecd` (tree `7112b0a8…`) |
| Commits after it | `ffb75ab1` and `afe08f20` — both change only `docs/audits/*.md` (checked with `git diff --name-only 60671884 afe08f20`) |
| Code identity | `AIAssisted/` tree object `81b5fdb782ca5c4cd6dc36c6a7cabf16ca69768b` is **identical** at `60671884`, `ffb75ab1` and `afe08f20`; no `scripts/` change either |
| Uncommitted, outside the baseline | `appsettings.Local.json` (user, line endings only), `testdata/` (user), `AIAssisted/frontend/BirkNext.Web/Services/GitSourcesSetupState.cs` (an interrupted, unreferenced UI-redesign draft — never built into any tested artifact), and three untracked reports of a concurrent session (§17) |

Every verification cited below ran on code byte-identical to HEAD's `AIAssisted/` tree unless marked dated/historical.

## 3. Evidence inventory

| Evidence | Source | Result | Applies to HEAD |
|---|---|---|---|
| Full backend regression, 6 partitions incl. 6c | `ffb75ab1` (worktree 3,482 files byte-identical; tests rebuilt) | 6,011 passed · 0 failed · 19 skipped · 6,030 rows · 4,716/4,716 methods | **Valid** (identical code tree) |
| Full frontend regression | `60671884` build | 8,463 / 8,463 | **Valid** |
| API + Web Release publish, package build | `60671884` | 0 errors; manifest `sourceCommit 60671884`, 846 files, 59 migrations | **Valid** |
| G7 Delivery → Release Readiness browser | `60671884`-era isolated stack | 16/16 | Valid (code unchanged) |
| Git Sources browser | `60671884` publish | 34/34 | Valid |
| G6 XML fixture | `04845c9b`-era isolated stack | 22/22 | Dated; no XML/persistence code changed since |
| G8 18-page axe/keyboard/390 px | `7410e179` | 0 serious/critical | Dated; only Git Sources markup changed since, and it was re-scanned (0 serious) |
| G9 runtime checks | 2026-11-07 gap closure + `a6ede4c2` | 0.0.0.0 refused; 13 runtime checks; HTTP_PORTS fix red→green | Valid for what was run |
| G10 clean copy | `60671884` package | validate 0; preflight 0; launcher cases 2/3 | Valid; clean PC not run |

## 4. Reconciliation method

Precedence: (1) tests on the current code-bearing source → (2) tests on byte-identical code → (3) scoped tests with a traceable SHA →
(4) historical evidence → (5) source inspection → (6) unverified statements. Documentation-only commits were confirmed by diff, not assumed.
A gate is PASS only with evidence of its *defined* acceptance criterion; nothing is inferred from a build alone.

## 5. G1–G10 final matrix

| Gate | Acceptance requirement | Previous statuses | Latest evidence (SHA) | Applicability | Reconciled | Remaining gap | Supporting report |
|---|---|---|---|---|---|---|---|
| G1 | Stable source identity and manifest | PASS | Tree identity above (`afe08f20`) | Valid | **PASS** | — | this §2 |
| G2 | Backend Release build | PASS | Publish 0 errors (`60671884`) | Valid | **PASS** | — | remediation summary §1 |
| G3 | Frontend build | PASS | Publish 0 errors (`60671884`) | Valid | **PASS** | — | remediation summary §1 |
| G4 | Complete backend regression, correct accounting | PARTIAL (6c interrupted) → PASS | 6,011/0/19, all 4,716 methods (`ffb75ab1`) | Valid | **PASS** | — | remediation summary §11; test-execution report, final section |
| G5 | Complete frontend regression | PASS | 8,463/8,463 (`60671884`) | Valid | **PASS** | — | remediation summary §1 |
| G6 | Real isolated XML save/reload | PASS | 22/22 | Dated, unaffected | **PASS** | — | verification audit, gap closure |
| G7 | Defined synthetic cross-module workflows | PARTIAL → PASS | A–F; Delivery→Readiness 16/16; interruption tests | Valid | **PASS** (defined journeys) | Other module pairs not claimed | cross-module report, final section |
| G8 | Accessibility checks within stated scope | PASS (scope) vs "PARTIAL overall" | 18 pages + Git Sources re-scan | Valid for scope | **PASS (defined scope)** | Broader WCAG not verified (§7) | verification audit G8 row; UI report |
| G9 | Required localhost/security tests actually verified | PARTIAL | §8 | Valid for what ran | **PARTIAL** | Runtime binding variants; second account | security report |
| G10 | Clean-PC tester package acceptance | PARTIAL | Clean-copy validation (`60671884`) | Valid; not clean-PC | **PARTIAL** | Clean-PC checklist | clean-PC checklist |

## 6. Regression summary and G4 chronology

1. 2026-11-06 (`37b918f7`): 5,972 / 13 / **1 failed** (intermittent shutdown, BQA-012) → PARTIAL.
2. 2026-11-07 (`04845c9b`): partitions 1–5, 1 environment-dependent failure; partition 6 stopped by low memory → PARTIAL.
3. 2026-11-07 (`13c7821c`): Downloads dependency removed; 1–5, 6a, 6b passed (4,697 / 11 / 0); **6c stopped** by memory protection → PARTIAL.
4. **2026-11-07 (`ffb75ab1`, final): all six partitions, 6,011 passed, 0 failed, 19 skipped** → **G4 PASS.**

Accounting: 6,030 result rows = facts + theory rows; 4,716 discovered methods = 4,716 executed; none in two partitions; 16 identical full names
are theory rows inside one partition. The 19 skips are opt-in external tests (7 real archives, 5 live M2LB dev, 6 real ZAP, 1 real headless
Edge), each reported Skipped with its reason. Frontend: 8,463/8,463 at `60671884`.

## 7. G8 scope clarification

- **Defined gate (A):** "Accessibility checks within stated scope" — axe-core on the recorded 18 pages (0 serious/critical), visible keyboard
  focus on each page's first control, no horizontal scroll at 390 px, tab patterns on FQR/AQR/Topology (2026-11-06); Git Sources re-scanned
  after its UI changes (0 serious/critical, focus, 390 px). Since `7410e179` only Git Sources markup changed. **Satisfied → PASS (defined scope).**
- **Broader assurance (B):** WCAG conformance across all high-use routes, screen readers, manual review — **NOT FULLY VERIFIED.** axe-core
  scans are not WCAG conformance.
- The concurrent report's "historical pass covers a Git Sources subset" understates (A): the defined scan covered 18 pages. Its "PARTIAL
  overall" is a statement about (B), recorded here as such, not as a gate failure.

## 8. G9 residual security checks

| Category | Items |
|---|---|
| Verified implementation | Loopback guard for `urls`, Kestrel endpoints and (since `a6ede4c2`) HTTP_PORTS/HTTPS_PORTS; owner-only launch-code ACL; central request guard |
| Verified runtime behaviour | Real `0.0.0.0` start refused, no listener; listeners only on 127.0.0.1/::1; 13 runtime checks (session, CSRF, origin, Host, extension origin, revocation, no secret in responses/logs); ACL owner + SYSTEM |
| Test-only assertions | `[::]`, LAN IP, mixed, Kestrel endpoint, HTTP_PORTS/HTTPS_PORTS variants (TestServer, no socket) |
| Not verified (environment) | Runtime starts of those variants (permission policy denied); read attempt from a second ordinary Windows account (none available) |

**G9: PARTIAL.**

## 9. G10 clean-PC acceptance

Rebuilt package (`60671884`): manifest, preflight tests, PostgreSQL readiness (real Podman; Docker simulated), backend port conflict (exit 3),
missing runtime (exit 2), clean short-path copy validation — all pass. **Clean-PC first run under an ordinary tester account: NOT RUN.**
A clean source copy is not a clean PC. Required evidence plan: `birknext-tester-package-clean-pc-checklist-2026-11-07.md`. **G10: PARTIAL.**

## 10. BQA disposition

| ID | Class | Sev. | Fix | Regression evidence | Disposition | Remaining risk |
|---|---|---|---|---|---|---|
| BQA-001 | Conditional security risk | P2 | `a6ede4c2` (HTTP_PORTS) | In-process matrix; 0.0.0.0 runtime | **OPEN — VERIFICATION GAP** | Runtime variants |
| BQA-002 | Confirmed defect | P2 | `37b918f7` | ACL tests; runtime owner+SYSTEM | **FIXED — ACCEPTANCE PENDING** | Cross-account read |
| BQA-003 | Verification gap | — | — | Full backend/frontend runs | **CLOSED — VERIFIED** | — |
| BQA-004 | Verification gap | — | — | G7 journeys + chain test | **CLOSED — VERIFIED** (defined journeys) | Other module pairs not claimed |
| BQA-005 | Verification gap | — | — | Browser/axe/keyboard (G8 scope) | **CLOSED — VERIFIED** (defined scope) | Broader WCAG (§7) |
| BQA-006 | Verification gap | — | — | Full runs on identical code | **CLOSED — VERIFIED** | — |
| BQA-007 | Verification gap | — | — | 4 interruption tests | **CLOSED — VERIFIED** (tested cases) | Untested cross-store paths |
| BQA-008 | Architecture | P2 if shared | — | Source-confirmed | **ACCEPTED DESIGN BOUNDARY** | Unsupported for shared hosting |
| BQA-009, 010, 011, 012, 014, 015 | Confirmed defects | P2–P3 | `fa521a9d`, `7410e179`, `8cc2139e`, `e0077f15`, `04845c9b`, `6e794e94` | Red/green tests; full runs | **CLOSED — VERIFIED** | Regression only |
| BQA-013 | Observation | P3 | — | Not rechecked | **OBSERVATION** | Harness-related slowness |
| BQA-016 | Confirmed launcher gap | P3 | `4edc0885` | Preflight tests; package copy exit 3 | **CLOSED — VERIFIED** | — |
| BQA-017 | Confirmed launcher gap | P3 | `4edc0885` | Real Podman states; Docker simulated | **FIXED — ACCEPTANCE PENDING** | Docker path on a real machine |
| BQA-018 | Test defect | P2 | `503df79c` | Full run; opt-in run | **CLOSED — VERIFIED** | — |
| BQA-019 | Confirmed defect | P3 | `503df79c` | Red → green | **CLOSED — VERIFIED** | — |
| NEW-001 (concurrent register) | Audit coverage limitation | P2 | — | — | **OBSERVATION** — its G4 part is resolved (§6); G8 (B), G9, G10 remain tracked above | — |

## 11. Git Sources — local vs external

| Scope | Item | Status |
|---|---|---|
| Local | Six-step guided configuration (`60671884`); non-secret settings in `App_Data/microsoft-sign-in.json`; runtime reload without restart; client ID/tenant/organization validation; sign-in and test eligibility; stale-test detection; repository selection; read-only import through Import Project review | **VERIFIED** — backend 181, frontend 101, browser 34/34 (real backend for configuration; mocked Microsoft/Azure DevOps boundaries) |
| External | Real Entra app registration; real delegated sign-in; consent; organization permission; real Azure DevOps connection; discovery with a real account | **NOT VERIFIED — external setup and authorized runtime test required** |

The interrupted visual redesign draft (`GitSourcesSetupState.cs`, uncommitted) is not part of this assessment.

## 12. Architecture limitations (accepted)

Single-user local deployment (BQA-008); loopback-only binding by design; Git Sources depends on an organization-owned Entra registration;
release readiness reads execution evidence only from its own sources (other reviews show "Not assessed" in it).

## 13. Release decisions

- **A. Developer readiness — GO.** Builds and full regressions pass on HEAD-identical code; no open confirmed defect.
- **B. Controlled tester rollout — CONDITIONAL GO.** Conditions: named testers; supported local Windows 10/11 setups; localhost-only;
  synthetic/non-protected data; the documented setup, limitations and clean-PC checklist handed to testers; a named owner for troubleshooting;
  the `60671884` package. Not evidence of broader distribution readiness.
- **C. Broader distribution — NO-GO** until G9 (runtime binding variants, second-account ACL) and G10 (clean-PC acceptance) are accepted.
  G4 is **no longer** a blocker (final complete run, §6). Broader accessibility assurance (§7 B) is recommended, not a defined gate.

Change from the concurrent decision report: its broader-distribution NO-GO cited G4 ("partition 6c not run"); that reason is superseded by the
final run. The NO-GO itself stands on G9 and G10.

## 14. Major hardening phase

Scope completed: localhost security, launch code, binding, CSS build guard, shutdown determinism, persistence and reload, cross-module
journeys, interrupted import, launcher preflight/readiness, test-environment independence, Git Sources configuration. Verified by full
backend/frontend regressions on HEAD-identical code. Accepted design limitations: BQA-008, loopback-only. Unverified external acceptance: G9
environment checks, G10 clean PC, BQA-017 Docker, Git Sources with a real tenant. New general hardening work is **not** justified by current
evidence; remaining work is acceptance testing in the named environments.

**Technical assurance recommendation: COMPLETE WITH ACCEPTED EXCEPTIONS**
**Formal approval: PENDING HUMAN DECISION**

## 15. Outstanding evidence and approvals

1. G9: isolated runtime starts of the remaining binding variants (needs permission to start a listener); denied read from a second ordinary Windows account.
2. G10: clean-PC checklist with the exact package (ordinary account).
3. BQA-017: Docker readiness path on a machine with Docker.
4. Git Sources: Entra registration, then an authorized real sign-in and connection test.
5. Optional: broader accessibility review of high-use routes.
6. Human approvals (§16).

## 16. Sign-off record

| Approval role | Decision | Status |
|---|---|---|
| Independent technical auditor | Recommendation: COMPLETE WITH ACCEPTED EXCEPTIONS; GO / CONDITIONAL GO / NO-GO as §13 | Recorded |
| Test Lead | Pending review | Not signed |
| Product/project owner | Pending review | Not signed |
| Security/release authority, if required | Pending review | Not signed |

## 17. References and concurrent reports

Supporting: `birknext-final-remediation-summary-2026-11-07.md` (§1, §8, §11), `birknext-test-execution-results-2026-11-06.md`,
`birknext-verification-audit-2026-11-06.md`, `birknext-verified-gap-register-2026-11-06.md`, `birknext-cross-module-verification-2026-11-06.md`,
`birknext-security-verification-2026-11-06.md`, `birknext-ui-browser-verification-2026-11-06.md`, `birknext-tester-package-clean-pc-checklist-2026-11-07.md`.

Concurrent, **untracked** reports of another session (preserved, not edited, not committed — that session was still running):
`birknext-final-gap-register-2026-11-07.md`, `birknext-release-assurance-decision-2026-11-07.md`, `birknext-final-independent-audit-2026-11-07.md`.
Their current statements superseded here: G4 "PARTIAL / OPEN, 6c not run" → **PASS** (§6); broader-distribution NO-GO reason "G4" → G9 and G10
only (§13); G8 "Git Sources subset" → 18-page defined scope PASS plus broader assurance not verified (§7); their "HEAD tree
`606718846a…`" is the commit SHA of `60671884`, not a tree (§2). Their remaining statements agree with this document.
