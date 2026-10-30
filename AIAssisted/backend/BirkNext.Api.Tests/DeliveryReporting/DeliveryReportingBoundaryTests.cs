using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Data.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Architecture guards for the Delivery Reporting boundary: its code never uses the BirkNext operational context or the stores of current
/// project state, and its model lives entirely in the delivery_reporting schema with no relationship to any AppDbContext table.
/// </summary>
public sealed class DeliveryReportingBoundaryTests
{
    private static readonly string Api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));

    private static IEnumerable<(string Rel, string Text)> ReportingSources() =>
        new[] { "Services/DeliveryReporting", "Data/DeliveryReporting" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(Api, d), "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(Api, "Controllers/DeliveryReportingController.cs"))
            .Select(f => (Path.GetRelativePath(Api, f).Replace('\\', '/'), File.ReadAllText(f)));

    [Fact]
    public void ReportingCode_NeverReadsCurrentBirkNextState()
    {
        // Operational state Delivery Reporting must not depend on: the main context and the stores/services of current project, target, test
        // data, connected systems, Source Analysis snapshots, imports, review results and Recommended Workflow.
        var forbidden = new[]
        {
            @"\bAppDbContext\b", @"\bIqrSourceStore\b", @"\bIqrSourceSnapshot", @"\bProjectImportService\b", @"\bProjectImportStagingStore\b",
            @"\bTargetEnvironment", @"\bEnvironmentProject\b", @"\bProjectEvidence", @"\bTestData\w*Service\b", @"\bITestData", @"\bExternalIntegration",
            @"\bIntegrationEvidenceBinding", @"\bConnectedSystem", @"\bRecommendedWorkflow", @"\bIReviewSourceEvidenceProvider\b", @"\bSavedWorkspace",
            @"\bIntegrationReview", @"\bQualityReview",
        };
        var offenders = ReportingSources()
            .SelectMany(f => forbidden.Where(p => Regex.IsMatch(f.Text, p)).Select(p => $"{f.Rel}: {p}"))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void ReportingModel_IsEntirelyInTheDeliveryReportingSchema_WithNoLinkToBirkNextTables()
    {
        var options = new DbContextOptionsBuilder<DeliveryReportingDbContext>();
        DeliveryReportingDbContext.Configure(options, "Host=localhost;Database=unused");
        using var db = new DeliveryReportingDbContext(options.Options);
        using var app = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        var appTypes = app.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        db.Model.GetEntityTypes().Should().OnlyContain(e => e.GetSchema() == DeliveryReportingDbContext.Schema);
        db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Should().OnlyContain(fk => fk.PrincipalEntityType.GetSchema() == DeliveryReportingDbContext.Schema);
        db.Model.GetEntityTypes().Select(e => e.ClrType).Should().NotIntersectWith(appTypes);
        app.Model.GetEntityTypes().Should().NotContain(e => e.GetSchema() == DeliveryReportingDbContext.Schema, "the main context never maps reporting tables");
    }

    [Fact]
    public void ReportingMigrations_LiveOutsideTheMainMigrationsFolder_AndOnlyTouchTheReportingSchema()
    {
        var folder = Path.Combine(Api, "Data/DeliveryReporting/Migrations");
        var migrations = Directory.GetFiles(folder, "*.cs").Where(f => !f.EndsWith(".Designer.cs") && !f.Contains("ModelSnapshot")).ToList();
        migrations.Should().NotBeEmpty();
        foreach (var file in migrations)
        {
            var text = File.ReadAllText(file);
            Regex.Matches(text, @"schema:\s*""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().Should().Equal(DeliveryReportingDbContext.Schema);
            text.Should().NotContain("Sql(", "no raw SQL that could reach other schemas");
        }
        Directory.GetFiles(Path.Combine(Api, "Data/Migrations"), "*.cs").Should().NotContain(f => File.ReadAllText(f).Contains(DeliveryReportingDbContext.Schema));
    }
}
