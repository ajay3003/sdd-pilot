# Authenticated Review Deployment Decision

- **Status:** APPROVED — LOCAL TESTER PACKAGE
- **Implementation gate:** Option A approved; implementation is limited to the local-workstation contract
- **Scope:** Entra ID + Microsoft Defender for Cloud Apps (MCAS) authenticated Frontend Quality Review

## Decision question

Will authenticated-review users be guaranteed to run BirkNext.Api in their own logged-in interactive desktop session, or must authenticated review support a centrally hosted/shared BirkNext backend?

This is a deployment decision. Whether Playwright Chromium is headed is a consequence, not the decision itself.

## Current repository evidence

The distributable Tester Package runs BirkNext.Web and BirkNext.Api locally. Browser Runtime and Playwright run on the backend host; Lighthouse runs in the same backend environment. PostgreSQL may be local or shared. The repository does not establish a centrally hosted Web/API deployment, secure remote interactive-browser infrastructure, or a local companion.

## Option A — Local Tester Package is the authenticated-review deployment contract

### Required guarantees

- BirkNext.Api runs on the user's workstation as the same logged-in OS user.
- The backend runs in the interactive desktop session, not Windows Service Session 0 or a headless container.
- One authenticated review belongs to one workstation/user.
- Enterprise policy permits headed Playwright Chromium.
- The browser window is visible to the correct user.
- Every review has isolated browser and session ownership.
- RDP/Citrix is supported only where the browser remains visible in the user's session.

### MVP architecture

```text
BirkNext Web
  → local BirkNext.Api
  → headed Chromium
  → Entra
  → MFA
  → visible MCAS notice
  → user explicitly continues
  → validate application delivery
  → Browser Runtime
  → axe
  → sanitized report
  → dispose context and browser
```

Advantages:

- Smallest code change and alignment with the existing Tester Package.
- Reuses the current Browser Runtime location.
- No authentication-state transport or cookie, token, profile, or storage-state export.
- Entra, MFA, Conditional Access, and MCAS interaction remain under user control.
- Browser Runtime and axe can share the authenticated page/context.

Constraints:

- Local-only; unsuitable for a shared remote backend.
- Unsuitable for Windows Service Session 0 or a headless container.
- Requires per-user process and session ownership.

## Option B — Centrally hosted BirkNext must support authenticated review

### Required architecture

```text
BirkNext server
  → short-lived pairing
  → signed local companion
  → user-owned local Chromium
  → Entra
  → MFA
  → visible MCAS notice
  → user explicitly continues
  → Browser Runtime and axe locally
  → sanitize findings locally
  → return sanitized findings to server
```

### Required companion controls

- Signed binary and signed updates.
- Random, one-time pairing token bound to user, review, target, and expiry.
- Outbound-only communication preferred; no general-purpose localhost HTTP API.
- No stored browser profile and no token, cookie, or storage-state export.
- One isolated browser session per review; cross-user/session reuse prohibited.
- Reliable cancellation, timeout, process cleanup, and version/health checks.

Advantages:

- Works with a central server while leaving interactive authentication on the user's workstation.
- Supports account selection, MFA, Conditional Access, and visible MCAS interaction.

Costs:

- A significantly larger codebase and security boundary.
- Installer, signed-update, compatibility, and support lifecycle.
- Pairing protocol, endpoint security, and additional operations.
- Greater multi-user isolation and threat-model burden.

## Rejected or deferred alternatives

- **Browser-side popup/session handoff — REJECT.** Browser origin isolation prevents backend Playwright from safely taking over the page without profile/session extraction or privileged integration.
- **Remote streamed server browser — NOT MVP.** It introduces a large browser-streaming, authentication, session-isolation, privacy, and operations boundary.
- **Manual storageState/cookie/token transfer — REJECT.** It violates the secret-handling model.
- **Normal browser-profile reuse — REJECT.** It risks credential, token, history, and cross-target session exposure.

## Comparison

| Criterion | Local Tester Package | Central + Companion |
|---|---|---|
| Current repository alignment | Strong | None yet |
| Implementation complexity | Lower; adapt existing runtime | High; new agent, protocol, packaging, and lifecycle |
| Authentication security | State remains in a local ephemeral Playwright context | State remains local, but pairing and agent boundaries must be secured |
| MFA compatibility | Yes | Yes |
| MCAS compatibility | Yes, with visible manual Continue | Yes, with visible manual Continue |
| Multi-user isolation | One workstation/user plus per-review isolation | Per-user companion and strict server-side pairing/isolation |
| Deployment burden | Headed-browser prerequisites on Tester Package workstation | Signed companion installation on every supported endpoint |
| Operational burden | Browser health, policy, cleanup, and local diagnostics | All local burdens plus pairing, distribution, upgrades, and fleet support |
| Upgrade burden | Tester Package, Playwright, and Chromium compatibility | Server, protocol, companion, Playwright, and Chromium compatibility |
| Citrix/RDP implications | Valid only when backend and browser share the visible user session | Companion must run inside the correct user session |
| Time to MVP | Shorter | Materially longer |
| Future scalability | Limited to the local deployment contract | Supports central hosting, with substantially higher operational cost |

## Decision

**Approved: Option A — Local Tester Package plus backend-owned headed browser.**

Authenticated Frontend Quality Review is supported only when BirkNext.Api runs locally in the tester's logged-in interactive Windows session. It follows current repository evidence, adds the smallest security boundary, avoids browser-session transport, and lets Browser Runtime and axe reuse one context. Central/shared hosting is outside the MVP and would require a separate architecture decision and local-companion security design.

## Implementation backlog if Option A is approved

### A1 — Authenticated browser-session infrastructure

- Replace the placeholder session service.
- Create one ephemeral headed-browser session per review.
- Keep browser, context, and page only in memory behind an opaque session ID.
- Bind ownership to target, review, and user.
- Implement cancellation, timeout, cleanup, and process-tree disposal.

### A2 — Entra + MCAS state machine

Support `AuthenticationRequired`, `AuthenticationInProgress`, `ConditionalAccessIntermediary`, `AwaitingUserContinuation`, `Authenticated`, `Expired`, `Cancelled`, `UnexpectedOrigin`, and `Failed`, using existing naming conventions where applicable.

### A3 — Origin validation

- Require the configured application origin as the final target.
- Permit the approved Entra authority only during authentication.
- Runtime-classify and pin the observed MCAS intermediary narrowly.
- Validate target-correlated proxied application delivery.
- Reject unexpected origins and prevent engine execution.

### A4 — Browser Runtime reuse

- Consume an authenticated page lease instead of creating an independent context.
- Enforce the final-target navigation invariant before evidence collection.
- Stop immediately if authentication expires or an interstitial reappears.

### A5 — axe reuse

- Inject and execute axe in the same authenticated page/context.
- Do not create a second browser or context.
- Sanitize findings before they leave the session owner.

### A6 — User interface

- Sign in for review.
- Authentication progress and “Waiting for security access confirmation.”
- Cancel, Authenticated, and Run supported authenticated checks states.
- Clearly identify unsupported engines as unassessed.

### A7 — Security sanitization

- No screenshots or DOM snippets by default.
- Structural selectors only.
- No tokens, cookies, storage state, account identity, query strings, or fragments in evidence/logs.

### A8 — Synthetic Entra/MCAS E2E

Test success, cancellation at Entra/MCAS, unexpected origin, non-returning MCAS, expiry, engine interstitial protection, ownership, isolation, cleanup, and absence of secret persistence without a real Microsoft account.

### A9 — Real M2LB manual acceptance

Verify manual Entra/MFA, visible MCAS notice, explicit user Continue, final-target validation, same-context Browser Runtime and axe execution, unsupported-engine truthfulness, sanitization, and complete session disposal.

## Option A acceptance criteria

1. Headed Chromium appears to the correct local user.
2. The user manually completes Entra and MFA.
3. The MCAS notice appears visibly.
4. The product does not auto-click Continue.
5. The user explicitly continues.
6. The final M2LB application state is validated.
7. Browser Runtime executes in the same page/context.
8. Axe executes in the same page/context.
9. No secondary authenticated browser is created.
10. Static Security, Passive Performance, Lighthouse, and ZAP remain truthfully unsupported/unassessed.
11. Authentication expiry stops engines.
12. Cancellation destroys the browser/context.
13. A session cannot be reused across target, review, or user.
14. No password, token, cookie, or storage state persists.
15. Reports contain no account identity.
16. The synthetic Entra + MCAS suite passes.
17. Real M2LB manual acceptance passes.

## Non-goals for the authenticated MVP

- Authenticated Static Security execution.
- Authenticated Passive Performance execution.
- Authenticated Lighthouse execution.
- Authenticated ZAP execution.
- Credential or MFA automation.
- Automatic MCAS continuation.
- Authenticated-session persistence across restarts.
- Normal browser or browser-profile reuse.

## Approval record

The product/deployment decision approved the BirkNext Tester Package as the authenticated-review deployment contract. BirkNext.Api must run locally as the tester's logged-in Windows user in the interactive desktop session. A future central/shared deployment must not reuse this local-browser design without a new decision record.
