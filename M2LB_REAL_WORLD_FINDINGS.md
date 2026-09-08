# M2LB Real-World Testing: Findings & Corrections

## Executive Summary

Testing against real M2LB revealed that the auth detection implementation works **theoretically** but not **practically** because:

1. Server-side HTTP preflight detection cannot see the client-side MSAL auth redirect
2. Blazor WASM loads and executes client-side, triggering auth flow that server never observes
3. Public configuration must be discovered from Blazor/MSAL assets, not from auth redirect chain

Additionally, the UI has a **semantic error**: manual verification status is displayed under the "Detected Authentication" label, conflating two different signals.

---

## Problem 1: Redirect Chain Detection Doesn't Work for Blazor WASM

### Theory (What Was Implemented)
```
Server GET /
  → HTTP 200 (follows redirects)
  → Detects redirect to login.microsoftonline.com
  → Extracts client_id, redirect_uri from query params
  → Returns detected auth metadata
```

### Reality (What Actually Happens)
```
Server GET /
  → HTTP 200 with Blazor shell HTML (no redirect)
  → [Server stops here; detection complete]

[In Browser]
  → Blazor WASM loads
  → JavaScript executes
  → MSAL initializes
  → MSAL redirects to login.microsoftonline.com
  → User signs in
```

**Why It Fails:**
- The server HTTP request returns 200 with shell content
- No server-side redirect to Entra (Blazor hasn't loaded yet)
- MSAL redirect only happens after JavaScript executes
- Server-side detector returns before any auth redirect occurs

**Current M2LB Result:**
- Reachability: Reachable (200 OK)
- Detected Authentication Type: None (no redirect observed)
- Detected Authority: null
- Detected Tenant: null
- Detected Client ID: null
- Detected Redirect URLs: empty

---

## Problem 2: UI Semantic Error

### Current Display
```
Detected Authentication: Manual verification passed
```

### Problem
- "Manual verification passed" is NOT a detected authentication type
- It's a **verification status**, not a **detection result**
- These are two completely different signals

### Correct Separation
```
Detected Authentication: [Should show auth type if detected]
  - Type: None (not detected)
  - Authority: null
  - Client ID: null
  - Redirect URLs: []

Manual Authentication Verification: Passed
  - Method: Manual managed Microsoft Edge
  - Result: Passed
  - Verified At: [timestamp]
```

### Why This Matters
Conflating these signals hides the actual problem: auth detection didn't work, and we're only seeing the manual verification result.

---

## What's Actually Needed

### Current Implementation
- Observes server-side HTTP redirect chain
- Extracts query parameters (client_id, redirect_uri)
- Works when server can see the redirect

### For Blazor WASM / M2LB
Need to inspect **public SPA configuration**, not auth redirects:

```
Public Sources:
  1. appsettings.json (if publicly exposed)
  2. Blazor boot configuration (blazor.boot.json)
  3. JavaScript bundles containing MSAL config
  4. Well-known OIDC endpoints
  5. HTML script tags with embedded config

Extract:
  - ClientId (GUID in MSAL config)
  - Authority (login.microsoftonline.com or custom)
  - TenantId (from config or default)
  - RedirectUri (callback path, often /auth/callback or similar)
```

### Example MSAL Config
```json
{
  "auth": {
    "clientId": "87654321-4321-4321-4321-210987654321",
    "authority": "https://login.microsoftonline.com/12345678-1234-1234-1234-123456789012",
    "redirectUri": "https://m2lbdev.bufetat.no/auth/callback"
  }
}
```

---

## Corrected Status

| Component | Status | Notes |
|-----------|--------|-------|
| **Manual Edge Verification** | ✅ WORKING | User can sign in, result recorded as Passed |
| **Auth Detection (Theory)** | ✅ WORKS | Redirect extraction logic is sound |
| **Auth Detection (M2LB)** | ❌ FAILS | Cannot detect - server doesn't see client-side redirect |
| **UI Semantic** | ❌ ERROR | Manual verification labeled as "Detected Authentication" |
| **Public Config Discovery** | ❌ NOT IMPLEMENTED | Need Blazor/MSAL config parser |

---

## Next Phase Requirements

### Phase 1: Fix UI Semantic Error (Immediate)
- Separate "Detected Authentication" from "Manual Authentication Verification"
- Show each in distinct UI section
- Prevent confusion between detection result and verification result

### Phase 2: Implement Blazor/MSAL Config Discovery
1. Investigate what M2LB actually exposes publicly
   - Run: `.\m2lb-diagnostic.ps1`
   - Inspect: appsettings.json, blazor.boot.json, JS bundles
2. Implement config file parser
3. Extract auth metadata from public config
4. Verify against real M2LB
5. Add tests for Blazor config discovery

### Phase 3: Unify Detection Strategies
- Keep redirect-chain extraction (works for OAuth/OIDC)
- Add Blazor/MSAL config discovery (works for WASM apps)
- Use both when applicable, fall back to config when redirect fails

---

## Key Learning

**Do Not Assume Server Sees Auth Redirects for Blazor WASM:**

The fundamental issue is architectural:
- Traditional web apps: Server-side redirect during initial load
- Blazor WASM: Shell loads, then JavaScript (MSAL) runs, then redirect

The auth redirect is a **browser event**, not a server event. Server-side detection cannot see it.

**For Blazor apps, configuration discovery is the right approach.**

---

## Files

- `m2lb-diagnostic.ps1` - Script to discover M2LB public config sources
- This document - Real-world findings and corrections

---

## Action Items

- [ ] Fix UI: Separate "Detected Authentication" and "Manual Verification" sections
- [ ] Run m2lb-diagnostic.ps1 to capture actual M2LB public assets
- [ ] Review diagnostic output to identify config structure
- [ ] Implement Blazor/MSAL config parser
- [ ] Add tests for config-based detection
- [ ] Verify detection works against real M2LB
