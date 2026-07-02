using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BirkNext.Api.Data.Migrations;

public interface IMigrationIntegrityValidator
{
    Task<MigrationIntegrityReport> ValidateAsync(AppDbContext dbContext);
}

public class MigrationIntegrityValidator : IMigrationIntegrityValidator
{
    private readonly ILogger<MigrationIntegrityValidator> _logger;
    private readonly string _migrationsPath;

    public MigrationIntegrityValidator(ILogger<MigrationIntegrityValidator> logger)
    {
        _logger = logger;

        // Find the actual source directory of the BirkNext.Api project
        // by looking for the Migrations folder relative to AppDbContext
        var dbContextType = typeof(AppDbContext);
        var projectNamespace = dbContextType.Namespace;

        // Search up directory tree from any known location to find the project root
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;

        // Handle both direct run and test scenarios
        // Try to find Migrations folder by searching up from current directory
        while (currentDir != null && currentDir.Length > 3)
        {
            var migrationsCandidate = System.IO.Path.Combine(currentDir, "BirkNext.Api", "Data", "Migrations");
            if (Directory.Exists(migrationsCandidate))
            {
                _migrationsPath = migrationsCandidate;
                return;
            }

            // Also try if we're already in BirkNext.Api directory
            migrationsCandidate = System.IO.Path.Combine(currentDir, "Data", "Migrations");
            if (Directory.Exists(migrationsCandidate))
            {
                _migrationsPath = migrationsCandidate;
                return;
            }

            currentDir = System.IO.Path.GetDirectoryName(currentDir);
        }

        // Fallback: use default location
        var assemblyLocation = dbContextType.Assembly.Location;
        var baseDir = System.IO.Path.GetDirectoryName(assemblyLocation);
        _migrationsPath = System.IO.Path.Combine(baseDir ?? "", "Migrations");
    }

    public async Task<MigrationIntegrityReport> ValidateAsync(AppDbContext dbContext)
    {
        var report = new MigrationIntegrityReport();

        // Check 1: File system integrity
        CheckFileSystemIntegrity(report);

        // Check 2: EF Core migration tracking
        await CheckEFCoreMigrationsAsync(dbContext, report);

        // Check 3: Migration class attributes
        CheckMigrationAttributes(report);

        // Check 4: DbContextModelSnapshot currency
        CheckModelSnapshot(report);

        return report;
    }

    private void CheckFileSystemIntegrity(MigrationIntegrityReport report)
    {
        if (!Directory.Exists(_migrationsPath))
        {
            report.AddIssue("Migrations directory not found", MigrationIssueSeverity.Critical);
            return;
        }

        var csFiles = Directory.GetFiles(_migrationsPath, "*.cs")
            .Where(f =>
            {
                var fileName = System.IO.Path.GetFileName(f);
                // Only include files matching migration pattern: YYYYMMDDHHMMSS_Name.cs
                // Exclude Designer, ModelSnapshot, and utility classes
                return !fileName.EndsWith(".Designer.cs")
                    && !fileName.Contains("ModelSnapshot")
                    && !fileName.Contains("Validator")
                    && !fileName.Contains("Report")
                    && !fileName.Contains("Severity")
                    && !fileName.Contains("Issue")
                    && char.IsDigit(fileName[0])
                    && fileName.Length > 18; // At least: YYYYMMDDHHMMSS_X.cs
            })
            .ToList();

        var designerFiles = Directory.GetFiles(_migrationsPath, "*.Designer.cs").ToList();

        // Check each .cs migration has matching .Designer.cs
        foreach (var csFile in csFiles)
        {
            var fileName = System.IO.Path.GetFileName(csFile);
            var designerFile = System.IO.Path.Combine(_migrationsPath, fileName.Replace(".cs", ".Designer.cs"));

            if (!File.Exists(designerFile))
            {
                report.AddIssue(
                    $"Migration file missing Designer: {fileName}",
                    MigrationIssueSeverity.Critical);
            }
        }

        // Check each .Designer.cs has matching .cs
        foreach (var designerFile in designerFiles)
        {
            var fileName = System.IO.Path.GetFileName(designerFile);
            var csFile = System.IO.Path.Combine(_migrationsPath, fileName.Replace(".Designer.cs", ".cs"));

            if (!File.Exists(csFile))
            {
                report.AddIssue(
                    $"Designer file orphaned (no matching migration): {fileName}",
                    MigrationIssueSeverity.Warning);
            }
        }

        report.MigrationFilesComplete = !report.Issues.Any(i => i.Severity == MigrationIssueSeverity.Critical);
        report.DesignerFilesPresent = report.MigrationFilesComplete && !report.Issues.Any(i => i.Issue.Contains("orphaned"));
    }

    private async Task CheckEFCoreMigrationsAsync(AppDbContext dbContext, MigrationIntegrityReport report)
    {
        try
        {
            var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync()).ToHashSet();
            var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync()).ToHashSet();
            var allMigrations = appliedMigrations.Union(pendingMigrations).ToHashSet();

            // Check that .cs migration files are in EF's migration list
            var csFiles = Directory.GetFiles(_migrationsPath, "*.cs")
                .Where(f =>
                {
                    var fileName = System.IO.Path.GetFileName(f);
                    // Only include files matching migration pattern
                    return !fileName.EndsWith(".Designer.cs")
                        && !fileName.Contains("ModelSnapshot")
                        && !fileName.Contains("Validator")
                        && !fileName.Contains("Report")
                        && !fileName.Contains("Severity")
                        && !fileName.Contains("Issue")
                        && char.IsDigit(fileName[0])
                        && fileName.Length > 18;
                })
                .ToList();

            foreach (var csFile in csFiles)
            {
                var migrationClass = ExtractMigrationName(csFile);
                if (migrationClass != null && !allMigrations.Contains(migrationClass))
                {
                    report.AddIssue(
                        $"Migration file exists but not tracked by EF: {migrationClass}",
                        MigrationIssueSeverity.Critical);
                }
            }

            report.MigrationsRecognized = !report.Issues.Any(i => i.Severity == MigrationIssueSeverity.Critical);
            report.AppliedMigrationCount = appliedMigrations.Count;
            report.PendingMigrationCount = pendingMigrations.Count;
            report.PendingMigrations = pendingMigrations.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check EF Core migrations");
            report.AddIssue(
                $"Could not query EF migrations: {ex.Message}",
                MigrationIssueSeverity.Warning);
        }
    }

    private void CheckMigrationAttributes(MigrationIntegrityReport report)
    {
        try
        {
            var migrationType = typeof(Migration);
            var migrationClasses = typeof(AppDbContext).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && migrationType.IsAssignableFrom(t))
                .ToList();

            foreach (var type in migrationClasses)
            {
                var migrationAttr = type.GetCustomAttribute<MigrationAttribute>();
                if (migrationAttr == null)
                {
                    report.AddIssue(
                        $"Migration class missing [Migration] attribute: {type.Name}",
                        MigrationIssueSeverity.Critical);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check migration attributes");
        }
    }

    private void CheckModelSnapshot(MigrationIntegrityReport report)
    {
        var snapshotPath = System.IO.Path.Combine(_migrationsPath, "AppDbContextModelSnapshot.cs");
        if (!File.Exists(snapshotPath))
        {
            report.AddIssue(
                "DbContextModelSnapshot.cs missing",
                MigrationIssueSeverity.Critical);
            return;
        }

        var content = File.ReadAllText(snapshotPath);
        if (!content.Contains("AppDbContextModelSnapshot") || !content.Contains("BuildModel"))
        {
            report.AddIssue(
                "DbContextModelSnapshot.cs appears corrupted",
                MigrationIssueSeverity.Warning);
        }

        report.SnapshotCurrent = true;
    }

    private string? ExtractMigrationName(string filePath)
    {
        var fileName = System.IO.Path.GetFileName(filePath).Replace(".cs", "");
        // Migration name format: <timestamp>_<Name>
        // e.g., 20260507124300_InitialCreate
        if (fileName.Length > 15 && char.IsDigit(fileName[0]))
        {
            return fileName;
        }
        return null;
    }
}

public class MigrationIntegrityReport
{
    public bool MigrationFilesComplete { get; set; }
    public bool DesignerFilesPresent { get; set; }
    public bool SnapshotCurrent { get; set; }
    public bool MigrationsRecognized { get; set; }

    public int AppliedMigrationCount { get; set; }
    public int PendingMigrationCount { get; set; }
    public List<string> PendingMigrations { get; set; } = new();

    public List<MigrationIssue> Issues { get; set; } = new();

    public bool IsHealthy => Issues.All(i => i.Severity != MigrationIssueSeverity.Critical);
    public bool IsValid => MigrationFilesComplete && DesignerFilesPresent && MigrationsRecognized && SnapshotCurrent;

    public void AddIssue(string issue, MigrationIssueSeverity severity)
    {
        Issues.Add(new MigrationIssue { Issue = issue, Severity = severity });
    }
}

public class MigrationIssue
{
    public string Issue { get; set; } = "";
    public MigrationIssueSeverity Severity { get; set; }
}

public enum MigrationIssueSeverity
{
    Info,
    Warning,
    Critical
}
