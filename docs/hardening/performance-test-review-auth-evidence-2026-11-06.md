# Performance Test Review — authentication evidence integrity (NEXT51, 2026-11-06)

Branch `008-traceability-first`. **Status: partially completed.** The authentication-readiness integrity issue from the brief's screenshot is fixed and tested. The compact Scenario UI redesign (§2), the versioning/stale-readiness UX (§10) and the tab simplification (§8) are not done in this pass. No load test, readiness check, network check or run was started; no traffic reached m2lbdev, TT02 or any external host.

## Audit — "default None · Bearer observed · Configuration Missing · 'No step requires authentication'"

- **One evaluator already exists** (NEXT37b): `PerformanceScenarioRuntime.AuthenticationRequirements`. It is used by step status, the scenario summary, the Readiness tab, Run and the backend start. A requirement comes from evidence:
  - authentication recorded on the step when it was created from discovery (`Source.ObservedAuthentication`), or
  - the **persisted Endpoint Discovery history**.
- Default None is never a confirmation; an explicit unauthenticated override is fingerprinted.
- **Root cause of the contradiction**: the history is loaded asynchronously by the page (`LoadObservationHistoryAsync`). When it had not loaded yet, or **failed to load** (the exception was swallowed to `_history = null`), steps whose authentication was observed only in the history had no requirement. The summary then stated **"No step requires authentication"** — a claim without evidence — while the server-side Readiness, which reads the persisted history, reported the requirement as missing. That is how the tabs disagreed.
- Readiness and Run are evaluated on the server, so execution was never allowed through: the defect was a misleading client summary, not a safety bypass.

## Fix (`PerformanceTestReview.razor`)

- Loading the history is tracked separately from failing to load it (`_historyFailed`).
- While the history loads, the summary shows the shared loading indicator ("Checking endpoint evidence for authentication requirements…") instead of a verdict.
- If the history cannot be read, the summary says **"Authentication requirements not fully known"**. An alert explains that authentication observed only in the history is not checked here, that Readiness and the run still check it on the server, and offers **Retry**.
- "No step requires authentication" now appears only when the evidence is complete.

## Tests

- New page tests (2): no auth verdict while the history loads (then the right one); a failed history gives "not fully known", Retry recovers.
- The test `FakeApi` can now hold the history pending or make it fail.
- Performance Test Review / scenario tests 101/101. Frontend full suite **8233 passed, 0 failed**.

## Browser check (M2LB copy, environment d1c77ffc…, "Baseline check" v13) — view only

1. The summary reads "1 step uses attended sign-in — not executable by the load generator · Scenario default: None · 1 step requires authentication · 1 step override" — the observed requirement is never summarised as "no step requires authentication".
2. Counts name the requirement and the default.
3. No state-changing request. The only POST was `runtime-authentication/resolve`, a pure resolver call that stores nothing.
4. No external host was contacted.
5. No page errors.

## Not done (remaining brief scope)

- §2 compact operation list and focused 5-part step editor.
- §4 shared scenario variables UX for `{brukerId}` (the existing runtime-parameter rows already block missing values; not redesigned).
- §5 discovery/acceptance state redesign.
- §6/§8 Defined / Reviewed / Configured / Executable status chips across tabs.
- §10 explicit draft → v1 save sequence before readiness.
