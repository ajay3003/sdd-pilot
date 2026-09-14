# Local HTTPS proxy (DEV-only authenticated API fallback)

A second, explicit authenticated testing method next to the Managed Edge/CDP bridge. It exists for targets where enterprise browser
protection (Microsoft Defender for Cloud Apps in-browser protection) refuses `Target.attachToTarget` for the signed-in tab, so the
authenticated browser context cannot be inspected through CDP. The proxy observes the application's own authenticated API traffic
instead. It never replaces CDP, never weakens the CDP trust model and is never selected automatically.

```
Edge (manual sign-in)  →  BirkNext proxy 127.0.0.1:<port>  →  approved DEV host(s)
                                   │
                                   └─ observes "Authorization: Bearer …" on a successful approved request
                                      → memory-only authenticated API context (never displayed / persisted / logged)
```

## Saved configuration

`authentication.authenticatedTestingMethod` on the Target Environment: `ManagedEdgeCdp` (default, legacy), `LocalHttpsProxy`,
`ManualOnly`. Edit → dirty → Save changes; Cancel restores; Duplicate preserves. Validation rejects `LocalHttpsProxy` for Production
and Custom environments and for non-HTTPS Frontend URLs. Everything else about the proxy is transient runtime state.

## Gates (all fail closed)

| Gate | Rule |
| --- | --- |
| Deployment | `AuthenticatedReview:Enabled=true` and `Runtime=LocalWorkstation` (same gate as Managed Edge). Otherwise: *Local HTTPS proxy unavailable in this deployment mode.* |
| Environment | `Local`, `Development`, `QA`, `Test`, `RC` only (`LocalHttpsProxyEnvironmentPolicy`, shared contract). Production and Custom are refused by frontend validation and by the backend. |
| Binding | `TcpListener(IPAddress.Loopback, port)`; non-loopback peers are dropped. Preferred port `LocalHttpsProxy:Port` (8888), then up to `PortSearchLimit` following ports; an occupied port is never freed. |
| Interception | Exact `host:port` allowlist (`ApprovedHostSet`) built from the environment's configured target origin, REST base, GraphQL endpoint, health/swagger URLs and allowlisted REST/GraphQL hosts. Identity providers, `*.access.mcas.ms`, loopback names, IP literals and wildcards can never be approved. Everything else is tunnelled byte-for-byte without TLS termination. |
| Caller | `ManagedEdgeLocalCallerFilter`: loopback peer, loopback Host, configured `FRONTEND_ORIGIN`. |

## Certificate authority

`ProxyCertificateAuthority` generates a per-workstation **BirkNext DEV HTTPS Inspection CA** (RSA 2048, one year) and stores it in the
current user's Personal store with a non-exportable, DPAPI-protected key (`WindowsUserCertificateStore`); on other platforms it is
process-memory only. Leaf certificates (24 h, `serverAuth`, SAN = host) are issued only for approved hosts and live in memory. Trust is
never installed silently: the UI requires an explicit confirmation, then Windows shows its own dialog. *Remove test certificate* deletes
the trust anchor and the stored root. No private key is logged, exported to disk by BirkNext or shipped with the product.

## Credential handling

`LocalHttpsProxyServer` relays HTTP/1.1 verbatim and inspects only the request line, method and the `Authorization` scheme. A Bearer
value is promoted to the `TransientAuthenticatedApiContextStore` only when the request went to an approved host over the intercepted
TLS connection, the response was 2xx, the token (if a JWT) is not expired and its tenant matches the environment's expected tenant when
both are known. The store keeps the secret in a private byte array of a private class, bound to `profileId + contextFingerprint +
approved authorities`, with expiry `min(token exp, LocalHttpsProxy:CredentialLifetimeMinutes)`. It is zeroed on stop, replacement by
another environment or fingerprint, expiry, session lifetime and shutdown. The public contract (`ITransientAuthenticatedApiContextStore`)
offers only `IsAuthenticatedApiContextAvailable`/`Describe`; execution goes through `IAuthenticatedApiExecutionService`
(REST `GET`/`HEAD`/`OPTIONS`, GraphQL single `query` without variables, HTTPS, approved hosts, 15 s timeout, sanitized result: status,
media type, length, elapsed, GraphQL error count / data presence). Cookies are transported but never read, stored or reused.

`SensitiveDataRedactor` masks `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, API-key headers, Bearer values, JWT
fragments and token-like query parameters before any text may reach logs or evidence. Proxy code logs host names, counts and exception
type names only.

## Runtime states

`NotStarted → Starting → WaitingForCertificateTrust | Listening → WaitingForAuthenticatedTraffic → AuthenticatedTrafficDetected → Ready`,
plus `Failed`, `Stopped`, `Stale`. `Ready` means an authenticated API context is available (memory only); it never means DOM access.
The status document (`LocalHttpsProxyStatus`) has no credential-shaped members by contract test.

## Browser configuration

BirkNext never changes the Windows or default-profile proxy settings. Either *Start Edge with proxy* (separate `msedge.exe` with
`--proxy-server=127.0.0.1:<port> --proxy-bypass-list=<-loopback> --user-data-dir=%LOCALAPPDATA%\BirkNext\LocalHttpsProxyEdgeProfile`,
never the normal profile) or configure the proxy manually in Windows Settings → Network & Internet → Proxy. If organization policy
forces proxy settings, only the manual route applies.

## Configuration

```json
{
  "LocalHttpsProxy": {
    "Port": 8888,
    "PortSearchLimit": 10,
    "UpstreamProxy": null,
    "CredentialLifetimeMinutes": 30,
    "SessionLifetimeMinutes": 120
  }
}
```

`UpstreamProxy` (`host:port`, no credentials) routes outbound connections through a corporate proxy via CONNECT when direct egress is
blocked; the authenticated API execution client uses the same setting and never routes through the loopback inspection proxy itself.

## Tests

`BirkNext.Api.Tests/Services/LocalHttpsProxy/*`: redaction, allowlist and environment gate, certificate lifecycle, credential store,
HTTP framing, authenticated execution safety, and an end-to-end loopback proof (`LocalHttpsProxyServiceTests`) with a fake TLS origin:
loopback-only binding, pass-through for unrelated hosts, bearer promotion on a successful approved request, no credential in status JSON
or logs, wipe on stop / environment switch / target change / expiry / lifetime / dispose. Frontend:
`BirkNext.Web.Tests/Components/AuthenticatedTestingMethodTests.cs` and `LocalHttpsProxyRuntimeTests.cs`.
