# Managed Edge browser context

The local workstation backend attaches to an Edge instance that the user signs into manually. The bridge uses the existing Microsoft.Playwright dependency (`ConnectOverCDPAsync`). Disposing the transport leaves the user's browser and tabs open.

The API requires a loopback peer, loopback backend Host, and the configured local `FRONTEND_ORIGIN` header. `AuthenticatedReview:Enabled` must be true and `AuthenticatedReview:Runtime` must be `LocalWorkstation`; that configuration is the deployment statement that BirkNext.Api runs in the tester's own interactive Windows session (Local Tester Package, see `docs/architecture/authenticated-review-deployment-decision.md`). In any other deployment mode every managed Edge operation reports "local browser integration unavailable" instead of probing or launching a browser on the backend host. A remote backend would need a local agent architecture; no loopback shortcut is attempted.

`ManagedEdge:Endpoint` defaults to `http://127.0.0.1:9222`. Only `localhost`, `127.0.0.1`, and `::1` are accepted. HTTP discovery disables redirects, proxies, and cookie handling. The advertised browser WebSocket must use the same loopback host and port. No remote CDP hosts are exposed in the UI.

## Compatibility preflight (`POST api/managed-edge/preflight`)

Transient, read-only, never persisted with a profile:

- Edge installation: Windows App Paths registration and the two standard `Program Files` locations only; no disk scan. Version from the executable's file version.
- `RemoteDebuggingAllowed` policy from `HKLM`/`HKCU\SOFTWARE\Policies\Microsoft\Edge`: `Allowed`, `Blocked`, `NotConfigured` (absent, permitted) or `Unknown` (unreadable). Machine policy wins. `Blocked` disables connect and launch; BirkNext never bypasses policy.
- Remote debugging runtime: TCP connect to the loopback port, then `GET /json/version`. Valid only with a JSON object carrying `Browser` and a `ws://` `webSocketDebuggerUrl` on the same loopback host and port. Redirects are not followed. A port that answers anything else is reported as occupied by a non-CDP service and is never freed by BirkNext.
- Target tab: `GET /json/list` page targets matching the exact target origin.
- `CanConnect` = runtime active and policy not blocked. `CanLaunchTestEdge` = Edge installed, policy not blocked and the port free.

## Safe launch (`POST api/managed-edge/launch`)

Starts a **separate** `msedge.exe` process with `--remote-debugging-port=<loopback port>`, `--user-data-dir=%LOCALAPPDATA%\BirkNext\ManagedEdgeProfile` (override with `ManagedEdge:ProfileDirectory`), `--no-first-run`, `--no-default-browser-check` and the validated target URL. Any directory under `Microsoft\Edge*\User Data` is rejected, so the normal profile is never reused, copied or unlocked. No process is enumerated, signalled or killed. If a valid CDP endpoint already exists the launch is skipped and the existing instance is offered. After launch the backend polls `/json/version` for `ManagedEdge:LaunchTimeoutSeconds` (default 20) and reports readiness separately from authentication: starting Edge proves nothing about sign-in.

## Positive proof and approved probes

No site gets an inferred authentication proof. An administrator must establish an application-specific contract before configuring a rule. A page title, successful attachment, HTTP 200 from a public page, and historical manual attestation do not establish authenticated access.

Example configuration for a hypothetical application (these paths/selectors are **not** M2LB assumptions):

```json
{
  "ManagedEdge": {
    "Endpoint": "http://127.0.0.1:9222",
    "LaunchTimeoutSeconds": 20,
    "Targets": [{
      "Origin": "https://app.example.test",
      "AuthenticatedOnlySelector": "#authenticated-session-controls",
      "ProtectedGetPath": "/api/current-user-status",
      "ProtectedContentType": "application/json",
      "SafeGetPaths": ["/api/current-user-status"],
      "GraphQlPath": "/graphql",
      "AllowedGraphQlQueries": ["query { viewer { id } }"]
    }]
  }
}
```

Only configure an authenticated-only selector after establishing that it is visible exclusively in the authenticated application. Only configure `ProtectedGetPath` after establishing that an unauthenticated request is denied and authenticated success returns HTTP 200 with the configured media type. Check that every approved GET is harmless; HTTP method alone does not guarantee that a badly designed endpoint is read-only. Unknown endpoints, query strings, encoded paths, traversal, cross-origin requests, and redirects are rejected.

The first probe executes `fetch` in the existing page context. It sets no authentication headers and returns only status code, allowlisted media type, and elapsed time. It does not read response bodies. Browser-provided credentials remain inside the browser. A failed API probe does not invalidate independent authenticated-shell proof. Application-specific JavaScript token acquisition is not reproduced.

`IManagedEdgeCdpService.ExecuteSameOriginFetchAsync` provides a reusable review execution path. GraphQL input is parsed with the existing HotChocolate parser: exactly one QUERY, no variables, exact administrator-approved query text. Mutations/subscriptions are rejected. GraphQL HTTP success is not semantic success, so authenticated GraphQL coverage remains unavailable in this phase. Public review capabilities are unchanged. Authenticated security coverage means only the approved browser-context checks; no full authenticated security scan is claimed.

## Tabs the browser refuses to expose

Connect compares two views: the page targets advertised by `/json/list` and the pages Playwright could attach to. When the target origin is advertised but no page is attachable, the state is `TargetTabNotInspectable`, not `TargetTabNotFound`. This is the observed behaviour for M2LB Dev in a signed-in Edge for Business work profile: Microsoft Defender for Cloud Apps in-browser protection keeps the real origin in the address bar and turns developer tools off, and the browser answers `Target.attachToTarget` with `Not allowed` for that tab only (fresh tabs, including the `*.access.mcas.ms` sign-in interstitial, remain attachable). BirkNext reports this precisely and does not bypass browser protection; authenticated access cannot be proven through CDP for such a tab. Reference: https://learn.microsoft.com/en-us/defender-cloud-apps/in-browser-protection

## State and lifecycle

Session handles, proof, counts, capabilities and compatibility results are transient. No field is added to saved environment profiles. A profile/context digest binds requests to the selected configuration; a compatibility result is shown only for the target origin it was checked for. URL/auth/security changes and environment selection immediately revoke frontend availability and disconnect the old session. Backend status revokes proof for closed tabs, navigation, disconnected browsers, and expiry. Verification expires after two minutes and sessions after thirty minutes. UI polls every three seconds; the backend also cleans up expired connections. No cookie, token, storage, response body, or transport exception is logged or returned.

Machine-proven browser access can satisfy the authentication activation gate when configuration, detection freshness, framework, warning and unsaved-change gates also pass. Historical manual attestation retains its separate semantics and never grants authenticated automation coverage. Neither path activates automatically. Disconnecting CDP does not invalidate public detection. Compatibility check, launch, connect, verify and disconnect never enter edit mode or mark the environment dirty.

The application never enters credentials or automates MFA/PIN/passkey/Conditional Access.

Playwright reference: https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-connect-over-cdp
