# System Settings Subsystem Refactoring Plan

## PHASE 1: CONTRACT & FOUNDATION ✅ COMPLETE

### Established Contracts

**Shared Models** (SystemSettingsCommonModels.cs)
- `SystemSettingsStatus`: Pass, Warning, Fail, Unavailable
- `SettingsItem`: Name, Value, Status, Description, Recommendation, IsRequired
- `SettingsSection`: Title, Description, Status, Items (list of SettingsItem)
- `DiagnosticItem`: Name, Status, Summary, Details, Recommendation
- `StatusSummary`: PassCount, WarningCount, FailCount, UnavailableCount, OverallStatus

**Shared Status Engine** (SystemSettingsStatusEngine.cs)
- Consistent status hierarchy: FAIL > WARNING > PASS (UNAVAILABLE ≈ WARNING)
- Status calculation from items, sections, or status arrays
- Item creation helpers (CreatePassItem, CreateWarningItem, CreateFailItem, CreateUnavailableItem)
- Summary calculation

**Tests Passing**
- SystemSettingsStatusEngineTests: 22/22 passing
- SystemSettingsPageContractTests: 16/16 passing

---

## PHASE 2: PAGE UPDATES (IN PROGRESS)

### Priority 1: Data Model Pages
These pages primarily display configuration data with validation.

#### General Page
**Status**: Update pending
**Required sections**:
- Application (Name, Version, Build, Package, Environment)
- Runtime (Hosting Model, .NET Runtime, OS, Architecture)
- Configuration (Database, Logging, Migration Status)
- Endpoints (Backend, GraphQL, Frontend)

**Contract**: Each item must show Value, Status, Description, Recommendation

**Implementation**:
1. Create `GeneralPageService` using `ISystemSettingsStatusEngine`
2. Return `List<SettingsSection>` with structured items
3. Update frontend to consume structured data

#### Configuration Health
**Status**: ✅ Partially complete (basic structure exists)
**Enhancements needed**:
- Use shared StatusSummary instead of custom counts
- Apply shared status calculation for overall status
- Update UI to use shared layout patterns

#### Feature Visibility
**Status**: Update pending
**Sections needed**:
- Platform Features (always enabled)
- Core Features (by category: Review, Library, Analysis, Quality, AI)
- Advanced Features
- Summary (enabled/disabled counts per category)

**Implementation**:
1. Create `FeatureVisibilityPageService`
2. Validate feature flags vs menu items
3. Report duplicates and missing configurations

#### Platform (Azure DevOps)
**Status**: Update pending
**Sections needed**:
- Configuration (Enabled, Connection Status, PAT Status, URLs)
- Connection Test Results

**Implementation**:
1. Create `PlatformPageService`
2. Include ADO connection test results
3. Show readiness for Implementation Traceability

### Priority 2: Diagnostic Pages
These pages show health and status of various subsystems.

#### Environment Diagnostics
**Status**: Exists but needs restructuring
**Sections needed** (keep current grouping):
- Database Checks
- Backend API Checks
- Workspace Checks
- ReviewContext Checks
- Export Checks

**Implementation**:
1. Convert EnvironmentDiagnosticCheck to use SystemSettingsStatus
2. Add recommendation field
3. Use shared summary calculation

#### System Diagnostics (currently "System Diagnostics")
**Status**: Update pending
**Sections needed**:
- Application Info
- Backend Configuration
- Frontend Configuration
- Database Status
- Logging Configuration
- Runtime Info

#### Runtime Diagnostics
**Status**: Exists but needs restructuring
**Sections needed** (keep current grouping):
- Runtime Status Summary
- Workspace State
- Markdown Engine
- Services Status
- Analysis Sessions
- Application Info

#### ReviewContext Validation
**Status**: Update pending
**Sections needed**:
- Loaded Artifacts (availability check)
- Canonical Metrics
- Source Comparisons
- Validation Findings

#### Documentation Health
**Status**: Exists but needs to use shared models

### Priority 3: Configuration Pages
These pages manage configuration and settings.

#### Target Environments
**Status**: Update pending
**Sections needed**:
- Configured Environments
- Current Default
- Deployment Readiness

#### AI
**Status**: Update pending
**Sections needed**:
- AI Provider Status
- Configured Models
- Availability
- Feature Integration Status

#### Maintenance
**Status**: Update pending
**Sections needed**:
- Reset Database Action
- Danger Zone Warnings
- Current State

---

## PHASE 3: FRONTEND UPDATES

### Shared Frontend Components

All frontend pages should:
1. Display structured data from backend
2. Use consistent layout for sections
3. Show status badges consistently
4. Display recommendations where available
5. Show summary at top

### Frontend Models

Create in `AdminApiService.cs`:
- `SettingsItemDto` (mirrors backend SettingsItem)
- `SettingsSectionDto` (mirrors backend SettingsSection)
- `DiagnosticItemDto` (mirrors backend DiagnosticItem)
- `StatusSummaryDto` (mirrors backend StatusSummary)

### Frontend Pages to Update

1. **General.razor**: Show sections with items
2. **Configuration Health**: Already updated, verify layout consistency
3. **Feature Visibility**: Show groups with counts
4. **Platform**: Show configuration and test results
5. **Target Environments**: Show environments with readiness
6. **AI**: Show provider status
7. **Environment Diagnostics**: Show grouped diagnostics
8. **System Diagnostics**: Show application diagnostics
9. **Documentation Health**: Show document health
10. **ReviewContext Validation**: Show validation results
11. **Runtime Diagnostics**: Show service and session status
12. **Maintenance**: Show available actions

---

## PHASE 4: BUILD & VERIFICATION

### Backend Tests
- [ ] All existing tests passing
- [ ] New page service tests passing
- [ ] Status engine tests passing (22/22 ✅)
- [ ] Page contract tests passing (16/16 ✅)

### Backend Build
- [ ] No compilation errors
- [ ] No warnings (or acceptable warnings only)

### Frontend Tests
- [ ] All existing tests passing
- [ ] New integration tests for structured data

### Frontend Build
- [ ] No compilation errors
- [ ] No Razor syntax errors

### Manual Verification
- [ ] Each page loads without errors
- [ ] Sections display properly
- [ ] Status badges show correctly
- [ ] Recommendations display
- [ ] Summary counts match items
- [ ] Navigation works
- [ ] Data refreshes on action

---

## STATUS SUMMARY

### Completed ✅
- Shared models defined
- Status engine implemented
- 38 tests passing (22 + 16)
- Backend compiles

### In Progress
- (To be started)

### Not Started
- Page service implementations
- Frontend updates
- Manual verification

### Next Steps
1. Create GeneralPageService using shared contracts
2. Update General.razor to display structured data
3. Repeat for remaining pages systematically

---

## KEY PRINCIPLES

1. **One shared status model**: SystemSettingsStatus used everywhere
2. **One shared calculation engine**: ISystemSettingsStatusEngine used everywhere
3. **One shared data model**: SettingsItem, SettingsSection for all data
4. **Consistent terminology**: "Status" always means SystemSettingsStatus
5. **No duplication**: Services calculate once, reused by all pages
6. **Test-driven**: Contract tests first, then implementation
7. **No redesign**: Keep existing layouts, improve consistency only

---

## Architecture

```
System Settings Subsystem
├── Backend Services
│   ├── AdminService (existing)
│   ├── GeneralPageService (new)
│   ├── ConfigurationHealthService (existing, to be updated)
│   ├── FeatureVisibilityPageService (new)
│   ├── PlatformPageService (new)
│   ├── [12 more page services]
│   └── SystemSettingsStatusEngine (shared - 1 instance)
├── Shared Models
│   ├── SystemSettingsStatus (enum)
│   ├── SettingsItem
│   ├── SettingsSection
│   ├── DiagnosticItem
│   └── StatusSummary
└── Frontend Pages
    ├── General.razor (to update)
    ├── Configuration Health (to verify)
    ├── Feature Visibility (to update)
    └── [12 more pages]
```

---

## Validation Rules

### PASS (Correctly Configured)
- Required configuration present and valid
- System healthy
- Everything expected is working

### WARNING (Needs Attention)
- Optional item not configured (use default)
- Database migration pending
- Workspace not yet created
- AI provider not configured (if optional)
- Optional integrations disabled

### FAIL (Broken)
- Required configuration missing
- Backend unreachable
- Database unavailable
- Migration failure
- Runtime failure
- Required dependency unavailable
- Required integration missing/broken

### UNAVAILABLE (Cannot Check)
- Check cannot run in current environment
- Service not applicable to current deployment
- Never counts as FAIL

---

## Success Criteria

- [x] Shared models defined
- [x] Status engine implemented and tested
- [ ] All 12 pages updated to use shared models
- [ ] All frontend pages display structured data
- [ ] All tests passing
- [ ] Both builds successful
- [ ] Manual verification complete
- [ ] No duplicated logic in page services
- [ ] Consistent terminology throughout
- [ ] Consistent layout on all pages
