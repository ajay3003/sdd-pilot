# Tester distribution — audit summary

**Baseline:** `451402d3` (branch `008-traceability-first`). Pipeline `qa-review-studio-ci` (`azure-pipelines.yml`). Build 415003 was not accessed (no Azure DevOps traffic).

- **Artifacts:** four publications, two packages. Details: `birknext-tester-artifact-inventory.md`.
- **Startup:** the self-contained launcher already had bounded waits, identity-checked database reuse, a single instance and clean shutdown. Root causes found: the connection code missing on relaunch; a 5 s code window; a wrong help path; the console QuickEdit freeze; a silent 5-minute wait; and testers using the developer package. Details: `birknext-windows-startup-reliability-audit.md`.
- **Local access:** the "key" is the local launch code. It is now handed to the browser automatically through a one-time token, without weakening authentication. Details: `birknext-tester-package-security-audit.md`.
- **Implementation:** `docs/hardening/birknext-tester-distribution-implementation.md`.
- **Acceptance and remaining work:** `birknext-tester-package-acceptance.md`, `birknext-tester-distribution-gap-register.md`. The package build, clean-machine/AVD runs and full regression were **not run**.
