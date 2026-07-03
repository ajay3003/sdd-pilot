# System Settings Architecture Consolidation Plan

## Objective

Eliminate ALL duplicated status enums, models, and calculation logic.

After consolidation:
- ✅ ONE status enum (SystemSettingsStatus)
- ✅ ONE status engine (ISystemSettingsStatusEngine)
- ✅ ONE item model (SettingsItem)
- ✅ ONE section model (SettingsSection)
- ✅ ONE summary model (StatusSummary)
- ✅ ZERO duplicate calculations

---

## PHASE 1: REPLACE STATUS ENUM

### Step 1.1: Map EnvironmentDiagnosticStatus values to SystemSettingsStatus

| EnvironmentDiagnosticStatus | SystemSettingsStatus | Notes |
|-------|-------|-------|
| Pass | Pass | Direct match |
| Info | Pass | "Info" is not a failure condition |
| Warning | Warning | Direct match |
| Fail | Fail | Direct match |
| NotAvailable | Unavailable | Rename (semantic equivalent) |

### Step 1.2: Update EnvironmentDiagnosticsService

**File:** `BirkNext.Api/Services/EnvironmentDiagnosticsService.cs`

**Change:**
```csharp
// OLD: Uses EnvironmentDiagnosticStatus
private EnvironmentDiagnosticStatus EvaluateSavedWorkspaceReviewContext()
{
    return EnvironmentDiagnosticStatus.Warning;
}

// NEW: Uses SystemSettingsStatus
private SystemSettingsStatus EvaluateSavedWorkspaceReviewContext()
{
    return SystemSettingsStatus.Warning;
}
```

**All methods that return status should return SystemSettingsStatus**

### Step 1.3: Update EnvironmentDiagnosticsModels

**File:** `BirkNext.Api/Models/Admin/EnvironmentDiagnosticsModels.cs`

**Changes:**
1. Replace all `EnvironmentDiagnosticStatus` with `SystemSettingsStatus`
2. Update EnvironmentDiagnosticCheck.Status from `EnvironmentDiagnosticStatus` to `SystemSettingsStatus`
3. Update EnvironmentDiagnosticsReport.OverallStatus from `EnvironmentDiagnosticStatus` to `SystemSettingsStatus`
4. Remove EnvironmentDiagnosticStatus enum definition
5. Add `using BirkNext.Api.Models.Admin;` to access SystemSettingsStatus

### Step 1.4: Update EnvironmentDiagnosticsTests

**File:** `BirkNext.Api.Tests/Services/EnvironmentDiagnosticsClassificationTests.cs`

**Changes:**
1. Replace all `EnvironmentDiagnosticStatus.Pass` → `SystemSettingsStatus.Pass`
2. Replace all `EnvironmentDiagnosticStatus.Warning` → `SystemSettingsStatus.Warning`
3. Replace all `EnvironmentDiagnosticStatus.Fail` → `SystemSettingsStatus.Fail`
4. Replace all `EnvironmentDiagnosticStatus.NotAvailable` → `SystemSettingsStatus.Unavailable`

---

## PHASE 2: CONSOLIDATE CALCULATION LOGIC

### Step 2.1: Inject Status Engine into ConfigurationHealthService

**File:** `BirkNext.Api/Services/ConfigurationHealthService.cs`

**Current:**
```csharp
public class ConfigurationHealthService : IConfigurationHealthService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ConfigurationHealthService> _logger;

    public ConfigurationHealthService(
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ConfigurationHealthService> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
    }
    
    // ... DetermineOverallStatus() method
}
```

**Updated:**
```csharp
public class ConfigurationHealthService : IConfigurationHealthService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ConfigurationHealthService> _logger;
    private readonly ISystemSettingsStatusEngine _statusEngine;

    public ConfigurationHealthService(
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ConfigurationHealthService> logger,
        ISystemSettingsStatusEngine statusEngine)  // ← ADD
    {
        _config = config;
        _env = env;
        _logger = logger;
        _statusEngine = statusEngine;  // ← ADD
    }
    
    // REMOVE: private string DetermineOverallStatus(ConfigurationHealthReport report) method
}
```

### Step 2.2: Replace DetermineOverallStatus() call

**File:** `BirkNext.Api/Services/ConfigurationHealthService.cs`

**Current (line 49):**
```csharp
report.OverallStatus = DetermineOverallStatus(report);
```

**Updated:**
```csharp
// OLD METHOD REMOVED
// Use engine instead:
var allStatuses = report.RequiredChecks
    .Concat(report.OptionalChecks)
    .Select(c => ConvertStringStatusToEnum(c.Status))
    .ToList();

report.OverallStatus = _statusEngine.CalculateOverallStatus(allStatuses).ToString();
// or if ConfigurationHealthReport.OverallStatus becomes an enum:
report.OverallStatus = _statusEngine.CalculateOverallStatus(allStatuses);
```

### Step 2.3: Remove Duplicate Calculation

**File:** `BirkNext.Api/Models/Admin/EnvironmentDiagnosticsModels.cs`

**Current (lines 72-91):**
```csharp
public EnvironmentDiagnosticStatus OverallStatus
{
    get
    {
        var allChecks = GetAllChecks();
        if (allChecks.Any(c => c.Status == EnvironmentDiagnosticStatus.Fail))
            return EnvironmentDiagnosticStatus.Fail;
        if (allChecks.Any(c => 
            c.Status == EnvironmentDiagnosticStatus.Warning ||
            c.Status == EnvironmentDiagnosticStatus.NotAvailable))
            return EnvironmentDiagnosticStatus.Warning;
        return EnvironmentDiagnosticStatus.Pass;
    }
}
```

**Remove entirely.** OverallStatus will be calculated by service using engine.

### Step 2.4: Update EnvironmentDiagnosticsService

**File:** `BirkNext.Api/Services/EnvironmentDiagnosticsService.cs`

**Current:** No injection of status engine

**Updated:** Inject ISystemSettingsStatusEngine and use it to calculate overall status in GetDiagnosticsAsync()

---

## PHASE 3: UPDATE MODELS FOR CONSISTENCY

### Step 3.1: Update ConfigurationHealthReport

**File:** `BirkNext.Api/Models/Admin/ConfigurationHealthModels.cs`

**Current:**
```csharp
public class ConfigurationHealthReport
{
    [JsonPropertyName("overallStatus")]
    public string OverallStatus { get; set; } = "Pass";  // ← STRING
    
    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }
    
    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }
    
    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }
    
    [JsonPropertyName("unavailableCount")]
    public int UnavailableCount { get; set; }
    
    [JsonPropertyName("requiredChecks")]
    public List<ConfigurationHealthCheck> RequiredChecks { get; set; } = new();
    
    [JsonPropertyName("optionalChecks")]
    public List<ConfigurationHealthCheck> OptionalChecks { get; set; } = new();
}
```

**Updated:**
```csharp
public class ConfigurationHealthReport
{
    [JsonPropertyName("overallStatus")]
    public SystemSettingsStatus OverallStatus { get; set; } = SystemSettingsStatus.Pass;  // ← ENUM
    
    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }
    
    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }
    
    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }
    
    [JsonPropertyName("unavailableCount")]
    public int UnavailableCount { get; set; }
    
    [JsonPropertyName("requiredChecks")]
    public List<ConfigurationHealthCheck> RequiredChecks { get; set; } = new();
    
    [JsonPropertyName("optionalChecks")]
    public List<ConfigurationHealthCheck> OptionalChecks { get; set; } = new();
}
```

### Step 3.2: Keep ConfigurationHealthCheck for now (Phase 3 only)

**Note:** ConfigurationHealthCheck should eventually be replaced with SettingsItem, but for Phase 2 consolidation, keep it working. When ConfigurationHealthService is rewritten to use SettingsSection + SettingsItem pattern (Phase 2), it can be removed.

### Step 3.3: Update EnvironmentDiagnosticsReport OverallStatus type

**File:** `BirkNext.Api/Models/Admin/EnvironmentDiagnosticsModels.cs`

**Current:**
```csharp
public EnvironmentDiagnosticStatus OverallStatus { get; set; }
```

**Updated:**
```csharp
public SystemSettingsStatus OverallStatus { get; set; } = SystemSettingsStatus.Pass;
```

---

## PHASE 4: UPDATE FRONTEND MODELS

### Step 4.1: Update AdminApiService DTOs

**File:** `BirkNext.Web/Services/AdminApiService.cs`

**Current:**
```csharp
public class ConfigurationHealthReport
{
    [JsonPropertyName("overallStatus")] 
    public string OverallStatus { get; set; } = "Pass";  // ← STRING
    
    // ...
}
```

**Updated:**
```csharp
// First, add enum definition:
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemSettingsStatus
{
    Pass,
    Warning,
    Fail,
    Unavailable
}

public class ConfigurationHealthReport
{
    [JsonPropertyName("overallStatus")] 
    public SystemSettingsStatus OverallStatus { get; set; } = SystemSettingsStatus.Pass;  // ← ENUM
    
    // ...
}
```

---

## PHASE 5: UPDATE TESTS

### Step 5.1: Update ConfigurationHealthCheckTests

**File:** `BirkNext.Api.Tests/Services/ConfigurationHealthCheckTests.cs`

**Changes:**
- All assertions comparing Status to "Pass" → SystemSettingsStatus.Pass
- All assertions comparing OverallStatus to "Pass" → SystemSettingsStatus.Pass
- All string status comparisons → enum comparisons

### Step 5.2: Update EnvironmentDiagnosticsTests

**File:** `BirkNext.Api.Tests/Services/EnvironmentDiagnosticsClassificationTests.cs`

**Changes:**
- Replace all `EnvironmentDiagnosticStatus.*` → `SystemSettingsStatus.*`
- Update test data to use new enum values

### Step 5.3: Add Architectural Tests

**New File:** `BirkNext.Api.Tests/Architectural/SystemSettingsConsolidationTests.cs`

```csharp
public class SystemSettingsConsolidationTests
{
    [Fact]
    public void VerifyOnlyOneStatusEnum()
    {
        // Get all types in BirkNext.Api
        var types = typeof(SystemSettingsStatus).Assembly.GetTypes();
        
        // Find all enums with "Status" in the name
        var statusEnums = types
            .Where(t => t.IsEnum && t.Name.Contains("Status") && t.Namespace == "BirkNext.Api.Models.Admin")
            .ToList();
        
        // Should only have SystemSettingsStatus
        Assert.Single(statusEnums);
        Assert.Contains(statusEnums, t => t.Name == "SystemSettingsStatus");
    }
    
    [Fact]
    public void VerifyOnlyOneStatusEngine()
    {
        // Should be exactly one implementation of ISystemSettingsStatusEngine
        var types = typeof(SystemSettingsStatusEngine).Assembly.GetTypes();
        
        var engines = types
            .Where(t => typeof(ISystemSettingsStatusEngine).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();
        
        Assert.Single(engines);
        Assert.Contains(engines, t => t.Name == "SystemSettingsStatusEngine");
    }
    
    [Fact]
    public void VerifyNoStringStatusProperties()
    {
        // Find all public properties named "Status" or "OverallStatus" in Admin models
        var types = typeof(SystemSettingsStatus).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("BirkNext.Api.Models.Admin") ?? false)
            .ToList();
        
        foreach (var type in types)
        {
            var statusProperties = type.GetProperties()
                .Where(p => p.Name.Contains("Status") && p.PropertyType == typeof(string))
                .ToList();
            
            Assert.Empty(statusProperties, 
                $"Type {type.Name} has string Status property: {string.Join(", ", statusProperties.Select(p => p.Name))}");
        }
    }
}
```

---

## CONSOLIDATION CHECKLIST

### Backend Changes
- [ ] Update EnvironmentDiagnosticsService to use SystemSettingsStatus
- [ ] Update EnvironmentDiagnosticsModels: Replace EnvironmentDiagnosticStatus with SystemSettingsStatus
- [ ] Remove EnvironmentDiagnosticStatus enum definition
- [ ] Update EnvironmentDiagnosticsReport: Use SystemSettingsStatus for OverallStatus
- [ ] Remove OverallStatus property getter calculation from EnvironmentDiagnosticsReport
- [ ] Inject ISystemSettingsStatusEngine into ConfigurationHealthService
- [ ] Remove DetermineOverallStatus() method from ConfigurationHealthService
- [ ] Update ConfigurationHealthReport.OverallStatus from string to SystemSettingsStatus
- [ ] Update EnvironmentDiagnosticsService to calculate OverallStatus using engine
- [ ] Update all test files to use SystemSettingsStatus enum
- [ ] Add architectural consolidation tests

### Frontend Changes
- [ ] Add SystemSettingsStatus enum to AdminApiService
- [ ] Update ConfigurationHealthReport DTO: OverallStatus string → SystemSettingsStatus
- [ ] Update any frontend code that compares status to strings

### Tests
- [ ] Update ConfigurationHealthCheckTests (13 tests)
- [ ] Update EnvironmentDiagnosticsClassificationTests (19 tests)
- [ ] Add SystemSettingsConsolidationTests (3+ tests)

### Verification
- [ ] Backend builds: 0 errors
- [ ] All tests passing
- [ ] Frontend builds: 0 errors
- [ ] No string status values found in Admin models
- [ ] Only one SystemSettingsStatus enum exists
- [ ] Only one ISystemSettingsStatusEngine implementation exists

---

## Expected Effort

- Backend changes: 2-3 hours
- Frontend changes: 1 hour
- Test updates: 1-2 hours
- Verification: 30 minutes
- **Total: 4-6.5 hours**

---

## Success Criteria

✅ Zero status enums in System Settings (only SystemSettingsStatus)
✅ Zero duplicate status calculation code (only ISystemSettingsStatusEngine)
✅ Zero string status properties (all are SystemSettingsStatus enum)
✅ All tests passing
✅ All builds clean
✅ Architectural tests verify consolidation
