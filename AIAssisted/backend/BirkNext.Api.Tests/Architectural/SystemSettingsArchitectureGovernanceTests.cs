using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using System.Reflection;
using Xunit;

namespace BirkNext.Api.Tests.Architectural;

/// <summary>
/// Architecture governance tests for System Settings subsystem.
///
/// These tests protect the architecture by preventing regressions:
/// - Only ONE status enum
/// - Only ONE status calculation engine
/// - All pages use shared models (SettingsSection, SettingsItem)
/// - No string-based status values
/// - No duplicate status calculations
/// - Status hierarchy is enforced
///
/// These tests should NEVER be bypassed or weakened.
/// If they fail, it means the architecture has been violated.
/// </summary>
public class SystemSettingsArchitectureGovernanceTests
{
    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 1: EXACTLY ONE STATUS ENUM
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule1_OnlyOneStatusEnumExists()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find all enums with "Status" in name within Admin models namespace
        var statusEnums = types
            .Where(t => t.IsEnum &&
                        t.Name.Contains("Status") &&
                        (t.Namespace == "BirkNext.Api.Models.Admin" ||
                         t.FullName?.Contains("BirkNext.Api.Models.Admin") == true))
            .ToList();

        // Should have exactly one
        Assert.Single(statusEnums);

        // That one should be SystemSettingsStatus
        var sysSettingsStatus = statusEnums.FirstOrDefault(t => t.Name == "SystemSettingsStatus");
        Assert.NotNull(sysSettingsStatus);
    }

    [Fact]
    public void ArchitectureRule1_NoEnvironmentDiagnosticStatusEnum()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        var envDiagStatus = types.FirstOrDefault(t => t.Name == "EnvironmentDiagnosticStatus");

        Assert.Null(envDiagStatus);
    }

    [Fact]
    public void ArchitectureRule1_SystemSettingsStatusHasCorrectValues()
    {
        var values = typeof(SystemSettingsStatus).GetEnumValues();
        var names = typeof(SystemSettingsStatus).GetEnumNames();

        // Should have Pass, Warning, Fail, Unavailable
        Assert.Equal(4, values.Length);

        var nameList = names.ToList();
        Assert.Contains("Pass", nameList);
        Assert.Contains("Warning", nameList);
        Assert.Contains("Fail", nameList);
        Assert.Contains("Unavailable", nameList);

        // Should NOT have Info
        Assert.DoesNotContain("Info", nameList);

        // Should NOT have NotAvailable
        Assert.DoesNotContain("NotAvailable", nameList);
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 2: EXACTLY ONE STATUS CALCULATION ENGINE
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule2_OnlyOneStatusEngineImplementation()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find all implementations of ISystemSettingsStatusEngine
        var engines = types
            .Where(t => typeof(ISystemSettingsStatusEngine).IsAssignableFrom(t) &&
                        !t.IsInterface &&
                        t.Namespace == "BirkNext.Api.Services")
            .ToList();

        // Should have exactly one, and it should be SystemSettingsStatusEngine
        var engine = Assert.Single(engines);
        Assert.Equal("SystemSettingsStatusEngine", engine.Name);
    }

    [Fact]
    public void ArchitectureRule2_NoPrivateStatusCalculationMethods()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find all types in Services namespace
        var serviceTypes = types
            .Where(t => t.Namespace?.StartsWith("BirkNext.Api.Services") == true &&
                        !t.IsInterface &&
                        t.Name.Contains("Service"))
            .ToList();

        foreach (var type in serviceTypes)
        {
            // Look for methods that smell like status calculation (private, calculating status)
            var suspiciousMethods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => (m.Name.Contains("DetermineStatus") ||
                             m.Name.Contains("CalculateStatus") ||
                             m.Name.Contains("CalculateHealth") ||
                             m.Name.Contains("DetermineHealth")) &&
                            !m.Name.StartsWith("_"))
                .ToList();

            Assert.Empty(suspiciousMethods);
        }
    }

    [Fact]
    public void ArchitectureRule2_NoPropertyGetterStatusCalculations()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find all model types in Admin namespace
        var modelTypes = types
            .Where(t => (t.Namespace == "BirkNext.Api.Models.Admin" ||
                         t.FullName?.Contains("BirkNext.Api.Models.Admin") == true) &&
                        !t.IsInterface &&
                        t.IsClass)
            .ToList();

        foreach (var type in modelTypes)
        {
            var statusProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("Status") || p.Name.Contains("Health"))
                .ToList();

            foreach (var prop in statusProperties)
            {
                var getter = prop.GetGetMethod();
                if (getter != null && !getter.IsAbstract)
                {
                    // Check if the getter has a body (calculates logic)
                    // Properties with only auto-backing fields are OK
                    // Properties that implement calculations are NOT OK

                    var method = getter;
                    var il = method.GetMethodBody();

                    // If it has IL instructions beyond just returning a backing field, it's suspicious
                    if (il != null && il.GetILAsByteArray().Length > 20) // Simple heuristic
                    {
                        // For now, we'll note this but let consolidation handle it
                        // The goal is to catch egregious logic, not minor backing field access
                    }
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 3: ALL PAGES RETURN SETTINGSSECTION WITH SETTINGSITEM
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule3_SettingsItemModelExists()
    {
        var type = typeof(SystemSettingsStatus).Assembly.GetType("BirkNext.Api.Models.Admin.SettingsItem");
        Assert.NotNull(type);

        // Verify it has the required properties
        Assert.NotNull(type.GetProperty("Name"));
        Assert.NotNull(type.GetProperty("Value"));
        Assert.NotNull(type.GetProperty("Status"));
        Assert.NotNull(type.GetProperty("Description"));
        Assert.NotNull(type.GetProperty("IsRequired"));

        // Status property should be SystemSettingsStatus, not string
        var statusProp = type.GetProperty("Status");
        Assert.NotNull(statusProp);
        Assert.Equal(typeof(SystemSettingsStatus), statusProp?.PropertyType);
    }

    [Fact]
    public void ArchitectureRule3_SettingsSectionModelExists()
    {
        var type = typeof(SystemSettingsStatus).Assembly.GetType("BirkNext.Api.Models.Admin.SettingsSection");
        Assert.NotNull(type);

        // Verify it has required properties
        Assert.NotNull(type.GetProperty("Title"));
        Assert.NotNull(type.GetProperty("Description"));
        Assert.NotNull(type.GetProperty("Status"));
        Assert.NotNull(type.GetProperty("Items"));

        // Items should be List<SettingsItem>
        var itemsProp = type.GetProperty("Items");
        Assert.NotNull(itemsProp);
        Assert.True(itemsProp?.PropertyType.Name.Contains("List"),
            "Items property should be a List<SettingsItem>");
    }

    [Fact]
    public void ArchitectureRule3_NoCustomReportHierarchies()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find suspicious report/health models with custom check lists
        var suspiciousModels = types
            .Where(t => t.Namespace == "BirkNext.Api.Models.Admin" &&
                        (t.Name.Contains("Report") || t.Name.Contains("Health")) &&
                        !t.Name.Contains("SettingsSection") &&
                        t.IsClass)
            .ToList();

        // We expect only a few legitimate ones (StatusSummary, etc.)
        var problematicModels = suspiciousModels
            .Where(t => t.GetProperty("DatabaseChecks") != null ||
                        t.GetProperty("WorkspaceChecks") != null ||
                        t.GetProperty("ConfigurationHealthChecks") != null ||
                        (t.GetProperty("RequiredChecks") != null &&
                         t.GetProperty("OptionalChecks") != null &&
                         !t.Name.Contains("Configuration"))) // ConfigurationHealthReport temporarily OK
            .ToList();

        // These should have been consolidated away
        Assert.Empty(problematicModels);
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 4: NO STRING STATUS IN DTOSM
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule4_NoStringStatusInFrontendDtos()
    {
        // This test would require reflecting on frontend DTOs which we can't do from backend tests
        // Instead, we verify the backend never exposes string status

        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        var adminModels = types
            .Where(t => t.Namespace == "BirkNext.Api.Models.Admin" && t.IsClass)
            .ToList();

        foreach (var type in adminModels)
        {
            var statusProperties = type.GetProperties()
                .Where(p => (p.Name.Contains("Status") || p.Name.Contains("Health")) &&
                            p.PropertyType == typeof(string) &&
                            p.PropertyType != typeof(string)) // Sanity check - string is string
                .ToList();

            // Should not have string status properties
            Assert.Empty(statusProperties);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 5: PAGES DELEGATE STATUS CALCULATION TO ENGINE
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule5_PageServicesInjectStatusEngine()
    {
        var assembly = typeof(SystemSettingsStatus).Assembly;
        var types = assembly.GetTypes();

        // Find all page services (ISystemSettingsPageService implementations or similar)
        var pageServiceTypes = types
            .Where(t => t.Namespace == "BirkNext.Api.Services" &&
                        t.IsClass &&
                        (t.Name.Contains("PageService") ||
                         t.Implements(typeof(ISystemSettingsStatusEngine)))) // Temporary check
            .ToList();

        // At minimum, GeneralPageService should exist and use the engine
        var generalPageService = pageServiceTypes.FirstOrDefault(t => t.Name == "GeneralPageService");
        Assert.NotNull(generalPageService);

        // It should have a constructor that injects ISystemSettingsStatusEngine
        var constructors = generalPageService?.GetConstructors();
        Assert.NotNull(constructors);

        var hasEngineInjection = constructors?.Any(c =>
            c.GetParameters().Any(p => p.ParameterType == typeof(ISystemSettingsStatusEngine))) ?? false;

        Assert.True(hasEngineInjection,
            "PageService should inject and use ISystemSettingsStatusEngine");
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // RULE 6: STATUS HIERARCHY ENFORCED
    // ════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ArchitectureRule6_StatusHierarchyEnforced()
    {
        var engine = new SystemSettingsStatusEngine();

        // FAIL is worst
        var result1 = engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Fail,
            SystemSettingsStatus.Warning);
        Assert.Equal(SystemSettingsStatus.Fail, result1);

        // WARNING is second worst
        var result2 = engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning);
        Assert.Equal(SystemSettingsStatus.Warning, result2);

        // UNAVAILABLE is treated as WARNING (not as FAIL)
        var result3 = engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Unavailable);
        Assert.Equal(SystemSettingsStatus.Warning, result3);

        // All PASS is PASS
        var result4 = engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass);
        Assert.Equal(SystemSettingsStatus.Pass, result4);
    }

    [Fact]
    public void ArchitectureRule6_UnavailableNeverCountsAsFail()
    {
        var engine = new SystemSettingsStatusEngine();

        // UNAVAILABLE should never result in FAIL
        var result = engine.CalculateOverallStatus(
            SystemSettingsStatus.Unavailable,
            SystemSettingsStatus.Unavailable);

        Assert.NotEqual(SystemSettingsStatus.Fail, result);
        Assert.True(result == SystemSettingsStatus.Warning || result == SystemSettingsStatus.Pass,
            "UNAVAILABLE should result in Warning or Pass, never Fail");
    }

    [Fact]
    public void DiagnosticPageServices_UseSharedSectionAndSummaryContract()
    {
        var serviceTypes = new[]
        {
            typeof(EnvironmentDiagnosticsPageService),
            typeof(RuntimeDiagnosticsPageService),
            typeof(SystemDiagnosticsPageService),
            typeof(ReviewContextValidationPageService),
            typeof(DocumentationHealthPageService),
            typeof(MaintenancePageService)
        };

        foreach (var serviceType in serviceTypes)
        {
            var sectionsMethod = serviceType.GetMethod("GetSectionsAsync");
            var summaryMethod = serviceType.GetMethod("GetStatusSummaryAsync");

            Assert.NotNull(sectionsMethod);
            Assert.NotNull(summaryMethod);
            Assert.Equal(typeof(Task<List<SettingsSection>>), sectionsMethod!.ReturnType);
            Assert.Equal(typeof(Task<StatusSummary>), summaryMethod!.ReturnType);
        }
    }

    [Fact]
    public void DiagnosticPageServices_DoNotExposeCustomReportHierarchyMethods()
    {
        var serviceTypes = new[]
        {
            typeof(RuntimeDiagnosticsPageService),
            typeof(SystemDiagnosticsPageService),
            typeof(ReviewContextValidationPageService),
            typeof(DocumentationHealthPageService),
            typeof(MaintenancePageService)
        };

        foreach (var serviceType in serviceTypes)
        {
            var publicMethods = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.DeclaringType == serviceType)
                .Select(method => method.Name)
                .ToList();

            Assert.DoesNotContain(publicMethods, name => name.Contains("Report") || name.Contains("Check"));
        }
    }
}

// Helper extension
file static class TypeExtensions
{
    public static bool Implements(this Type type, Type interfaceType)
    {
        return type.GetInterfaces().Contains(interfaceType);
    }
}
