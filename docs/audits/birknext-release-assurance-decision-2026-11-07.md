# BirkNext release assurance decision — 2026-11-07

## Decision

| Audience / release stage | Decision | Basis |
|---|---|---|
| Continued development | **GO** | Modular monolith remains coherent; the local single-user boundary is explicit; ownership of source, delivery, canonical interpretation and registry data is reasonably separated. No new blocking defect was confirmed. |
| Controlled tester rollout | **CONDITIONAL GO** | Prior synthetic workflows, frontend regression, fixture persistence and local package checks are substantial. Limit to selected testers on supported local Windows setups, use synthetic data, and provide a named operator/contact for setup. This is not clean-PC acceptance. |
| Broader distribution | **NO-GO** | Backend partition 6c is not run on the final code-bearing source; runtime binding and cross-account ACL acceptance remain partial; clean-PC acceptance is not run. |

## Source and evidence basis

Current HEAD is `ffb75ab1f748e742ee04bcfa3c063a2cf3ede990` on `008-traceability-first`. HEAD tree is `606718846a1640d6cac867b0f90e50961377fecd`. The seven commits/files added after the code-bearing `60671884` are reports under `docs/audits/`; no production source changed. The working tree already had a modified `AIAssisted/backend/BirkNext.Api/appsettings.Local.json` and untracked `AIAssisted/testdata/`; both were left untouched.

The final remediation summary records: frontend regression 8,463/8,463 at `60671884`; backend regression partial because 6c was not run at the final code-bearing source; XML fixture persistence 22/22; scoped cross-module journeys; scoped Git Sources accessibility; local package validation; and no clean-PC acceptance. This audit performed no tests or runtime launch, and reports those results at their actual recorded SHAs only.

## G1–G10 status

| Gate | Current assurance status | Release consequence |
|---|---|---|
| G1 Source baseline | PASS | Baseline is reproducible; preserve SHA on new runs. |
| G2 Backend build | PASS (dated, `60671884`) | No source delta since the recorded publish. |
| G3 Frontend build | PASS (dated, `60671884`) | No source delta since the recorded publish. |
| G4 Backend regression | PARTIAL | Complete the missing partition before broader distribution. |
| G5 Frontend regression | PASS (dated, 8,463/8,463) | Re-run on code changes. |
| G6 XML fixture persistence | PASS (dated, 22/22) | Clean-PC persistence still contributes to G10. |
| G7 Cross-module workflows | PASS within recorded scope | Does not mean every module pair or every external integration is verified. |
| G8 Accessibility | PARTIAL overall | Prior pass is Git Sources scoped; broaden high-use routes. |
| G9 Localhost security | PARTIAL | Complete isolated actual-listener variants and separate-account ACL acceptance. |
| G10 Tester package | PARTIAL | Clean-PC checklist remains a release prerequisite for broad distribution. |

## Conditions for controlled rollout

1. Use the local, loopback-only deployment model; do not host as a shared service.
2. Limit the pilot to named testers whose Windows/.NET/container setup is known, and keep synthetic/non-protected data in the pilot.
3. Give testers the package version/commit and documented setup steps. Capture startup failures and logs without secrets.
4. Treat Git Sources as unconfigured until the organization supplies an Entra app registration/client ID and an administrator grants required delegated permissions. No real Microsoft sign-in was verified.
5. Do not describe runtime evidence as assessed where the module reports static/source-only evidence or “Not assessed.”

## Required before broader distribution

- G4: execute the omitted backend partition and produce reconciled full backend results at a named SHA.
- G9: verify supported bind overrides against actual isolated sockets and validate launch-code directory access from a second ordinary account.
- G10: run `birknext-tester-package-clean-pc-checklist-2026-11-07.md` against the exact artifact on a clean Windows 10/11 PC under a standard account.
- G8: extend keyboard/accessibility coverage to the main guided workflows that will be distributed.

## Residual risk statement

The four EF contexts have separate transaction boundaries even though they use one PostgreSQL database. Cross-context recovery relies on module-owned records, idempotent writes and registry resynchronization, not global atomicity. The shared deployment/multi-user model is unsupported by the fixed local identity boundary. Clean-PC, separate-account and full final-source backend acceptance remain unknown. These limits are compatible with a carefully bounded pilot, but not with an unqualified broad release.
