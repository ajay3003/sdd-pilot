# Authentication Detection Implementation Plan
## Phases 2–4: Discovery → Implementation → Testing

---

## Current State

✅ **Phase 1 Complete**: UI semantic fix
- `DetectedAuthenticationLabel` shows ONLY detected auth type
- Manual verification shown in separate row
- These signals never conflate

🔄 **Phase 2**: M2LB Discovery (IN PROGRESS)
- Diagnostic script exists: `m2lb-config-discovery.ps1`
- Needs to be enhanced and run against real M2LB
- Must identify actual authentication configuration sources

⏳ **Phase 3**: Implementation (Pending Phase 2 results)
⏳ **Phase 4**: Testing (Pending Phase 3)

---

## Phase 2: Real M2LB Configuration Discovery

### Objectives
1. Execute `m2lb-config-discovery.ps1` against real M2LB
2. Identify all publicly exposed authentication configuration
3. Document exact evidence sources with provenance
4. Determine what can/cannot be automatically discovered

### Discovery Scope
The script must systematically check:

```
1. Initial HTML shell (index.html, /)
2. Script references from <script src="...">
3. JavaScript bundles and their content
4. Public JSON configuration files
5. Configuration file locations:
   - /appsettings.json
   - /appsettings.development.json
   - /appsettings.local.json
   - /config*.json
   - /runtime-config*.json
   - /environment*.json
6. Blazor _framework resources
7. Bootstrap/runtime configuration
8. OIDC/.well-known endpoints
9. Entra ID/Azure AD indicators
10. MSAL configuration patterns
```

### Evidence Search Patterns
Look for public exposure of:
- `clientId` / `client_id` (GUID format)
- `authority` (URL to identity provider)
- `tenantId` / `tenant_id` (GUID or tenant name)
- `redirectUri` / `redirect_uri` / `redirectUris` (application callback URL)
- MSAL library references
- Azure AD / Entra ID markers
- login.microsoftonline.com references

### Expected Outcomes

**Best case**: M2LB exposes clear auth config
```json
{
  "auth": {
    "clientId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "authority": "https://login.microsoftonline.com/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "redirectUri": "https://m2lbdev.bufetat.no/auth/callback"
  }
}
```
Evidence source: `/runtime-config.json` or similar

**Moderate case**: Auth config embedded in JavaScript
```javascript
const msalConfig = {
  auth: {
    clientId: "...",
    authority: "...",
    redirectUri: "..."
  }
};
```
Evidence source: `/app.js` or similar

**Worst case**: No publicly exposed auth configuration
Result: `Not determined` with clear documentation

---

## Phase 3: Implementation

### Based on Phase 2 findings

If auth config is found:

1. **Create/Update Parser**
   - Parse source identified in Phase 2
   - Extract clientId, authority, tenantId, redirectUri
   - Handle partial config gracefully

2. **Integrate with TargetEnvironmentDetectionService**
   - Add method: `ExtractAuthenticationConfigAsync()`
   - Call after framework detection
   - Populate DetectedAuthenticationType, Authority, TenantId, ClientId, RedirectUrls

3. **Add EvidenceSource Tracking**
   - Track where each field came from
   - Preserve for UI/debugging
   - Example: `{ Field: "ClientId", Source: "/runtime-config.json", Value: "xxxx" }`

4. **Update Models** (if needed)
   - Add EvidenceSource to TargetEnvironmentDetectionResponse
   - Ensure DetectedAuthenticationType remains independent of manual verification
   - Ensure Authority/TenantId/ClientId/RedirectUrls are independent

5. **UI Integration**
   - Show evidence source in detected authentication section
   - Keep manual verification completely separate
   - No auto-population of configured values

### Architecture

```
TargetEnvironmentDetectionService
├── CheckTargetWithRedirect()         [existing]
├── DetectClientFramework()            [existing]
├── ExtractAuthenticationMetadata()   [existing - redirect-based]
├── ExtractAuthenticationConfig()     [NEW - config-based]
│   ├── FetchConfigFile()             [NEW]
│   ├── ParseMsalConfig()             [NEW]
│   ├── ParseOidcConfig()             [NEW]
│   └── ExtractAuthFields()           [NEW]
└── ApplyTypedOutcome()               [existing]
```

### Example: Microsoft Entra Detection

```csharp
// If /runtime-config.json contains MSAL config:
DetectedAuthenticationType = MicrosoftEntraId
Authority = "https://login.microsoftonline.com/{tenant}"
TenantId = "{tenant}"
ClientId = "{extracted-client-id}"
RedirectUrls = ["{extracted-redirect-uri}"]
EvidenceSource = "/runtime-config.json"
Confidence = High
```

---

## Phase 4: Testing

### Test Scenarios (12 minimum)

```
1. Entra ID/MSAL config discovered from JSON
2. OIDC config discovered from JSON
3. Multiple redirect URIs
4. Config discovered from JavaScript (if applicable)
5. Multiple possible config sources
6. No auth configuration found
7. Invalid/malformed JSON
8. Unreachable config file (404)
9. Missing optional fields (partial config)
10. Manual verification = Passed + Detected auth = Not determined [INDEPENDENT]
11. Manual verification = Failed + Detected auth successfully discovered [INDEPENDENT]
12. Manual verification never influences detected fields [ISOLATION]
```

### Test Organization

```
Backend Tests:
  ✓ AuthenticationDetectionTests.cs (existing - 7 tests)
  + ConfigBasedAuthenticationDiscoveryTests.cs (new - 12 tests)
  + M2LBRealWorldAuthenticationTests.cs (new - integration with real M2LB)

Frontend Tests:
  ✓ AuthenticationDetectionApplyTests.cs (existing - 8 tests)
  + (no changes expected if detection works correctly)

Full Suite:
  - Run: dotnet test AIAssisted/backend/BirkNext.Api.Tests
  - Run: dotnet test AIAssisted/frontend/BirkNext.Web.Tests
  - Verify: 0 regressions
```

### Acceptance Criteria

```
✓ All existing tests pass
✓ New detection tests pass
✓ Real M2LB produces non-empty detected auth (or clear "Not determined" reason)
✓ Manual verification remains completely independent
✓ Evidence source preserved and shown in UI
✓ No secrets/tokens logged or exposed
```

---

## Phase 2 → 3 Handoff

After Phase 2, the discovery report will contain:

```markdown
## M2LB Authentication Configuration Discovery

Target: https://m2lbdev.bufetat.no
Status: [Reachable / Unreachable]
Framework: [Blazor WASM / React / Unknown]

### Authentication Evidence Found

| Field | Value | Source | Confidence |
|-------|-------|--------|------------|
| Type | Microsoft Entra ID | login.microsoftonline.com redirect | High |
| Authority | https://login.microsoftonline.com/... | /runtime-config.json | High |
| Tenant ID | xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx | /runtime-config.json | High |
| Client ID | xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx | /runtime-config.json | High |
| Redirect URI | https://m2lbdev.bufetat.no/auth/callback | /runtime-config.json | High |

### Configuration Source
- **Primary**: /runtime-config.json (JSON file)
- **Secondary**: [if multiple sources found]
- **Format**: JSON with structure: { auth: { clientId, authority, redirectUri } }

### Limitations
- [List any auth info NOT publicly exposed]
- [List any assumptions made]
```

Phase 3 implementation will use this report to implement the parser.

---

## Reuse of Existing Infrastructure

✓ **Target Environment Detection** - Reuse existing framework
  - TargetEnvironmentDetectionService
  - Extend, don't duplicate
  - Add auth discovery alongside framework detection

? **Integration Quality Review** - To be determined in Phase 3
  - Keep separate concerns distinct
  - Use same detection service, not duplicated logic
  - IQR can consume auth detection results

---

## Key Principles (Non-negotiable)

1. **No Guessing**: Only report what's publicly exposed
2. **No Conflation**: Manual verification ≠ detected auth, ever
3. **No Hard-Coding**: Discover structure, don't assume M2LB paths
4. **No Duplication**: Reuse existing services where possible
5. **Preserving Behavior**: No breaking changes to existing detection
6. **Evidence Tracking**: Always know where evidence came from

---

## Success Criteria

Phase 2 success:
- Script runs against real M2LB
- Clear report of what was found/not found
- Exact source documentation

Phase 3 success:
- Detection service updated
- Auth config parsed correctly
- Models updated cleanly
- UI shows evidence source

Phase 4 success:
- All 12 test scenarios pass
- 0 regressions in full suite
- Real M2LB validation complete
- Manual verification stays independent
