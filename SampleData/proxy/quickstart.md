# Quickstart: M2LB.Proxy — Local Development

**Target audience**: Developer setting up M2LB.Proxy for the first time.
**Time to first request**: ~5 minutes.

---

## Prerequisites

- .NET 8 SDK (`dotnet --version` should show `8.x.x`)
- An EntraID app registration for local development (see below for minimum required values)
- Git (repo already cloned)

---

## Step 1: Configure Local Settings

Copy the example configuration and fill in your EntraID values:

```bash
cp src/M2LB.Proxy.Api/appsettings.Development.json.example \
   src/M2LB.Proxy.Api/appsettings.Development.json
```

Edit `appsettings.Development.json` and fill in:

```json
{
  "AzureAd": {
    "TenantId": "<your-entra-tenant-id>",
    "ClientId": "<your-entra-client-id>",
    "Audience": "api://<your-entra-client-id>"
  }
}
```

> `appsettings.Development.json` is git-ignored. Never commit EntraID credentials.

The stub backend route is pre-configured to target `https://localhost:7200` — you can start
WireMock.Net on that port (see Step 4) or point it to any local HTTP server.

---

## Step 2: Build

```bash
dotnet build
```

All projects should compile without warnings. If there are warnings, fix them before proceeding —
warnings are treated as errors in CI.

---

## Step 3: Start the Proxy

```bash
cd src/M2LB.Proxy.Api
dotnet run
```

The proxy listens on `https://localhost:5001` by default.

---

## Step 4: Verify Health Check

```bash
curl -k https://localhost:5001/healthz
```

Expected response (HTTP 200):

```json
{
  "status": "Degraded",
  "entries": {
    "yarp-clusters": {
      "status": "Degraded",
      "description": "0 of 1 destination(s) reachable"
    }
  }
}
```

`Degraded` is expected when the stub backend is not running. `Unhealthy` means the proxy itself
failed to start correctly.

---

## Step 5: Verify Authentication Enforcement

Send a request without a token — expect HTTP 401:

```bash
curl -k -o /dev/null -w "%{http_code}" https://localhost:5001/api/stub/test
# Expected: 401
```

Send a request with a valid EntraID token — expect the request to be forwarded:

```bash
# Obtain a token for your dev app registration (e.g. via az cli or Postman)
TOKEN="<your-bearer-token>"
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5001/api/stub/test
# Expected: forwarded to stub backend (502 if stub is not running)
```

---

## Step 6: Verify Correlation ID Propagation

Send a request with an explicit correlation ID header:

```bash
curl -k -H "Authorization: Bearer $TOKEN" \
        -H "X-Correlation-Id: 00000000-0000-0000-0000-000000000001" \
        https://localhost:5001/api/stub/test
```

Check the console log output — the `correlationId` field in the structured log entry for this
request MUST match `00000000-0000-0000-0000-000000000001`.

---

## Step 7: Run Tests

```bash
dotnet test
```

All unit and integration tests MUST pass. Integration tests spin up the proxy in-process via
`WebApplicationFactory` and use WireMock.Net stubs for backend simulation — no external services
needed.

---

## Common Issues

| Problem | Cause | Fix |
|---------|-------|-----|
| `401` on all requests | EntraID config missing or wrong | Check `AzureAd` section in `appsettings.Development.json` |
| `dotnet run` fails with Key Vault error | Key Vault not configured locally | Key Vault is optional locally; check that dev config fallback is in place |
| Health check shows `Unhealthy` | Proxy failed to start or YARP config invalid | Check console for startup errors |
| Tests fail with `Unable to connect` | WireMock.Net port conflict | Ensure no other process uses the test stub port |
