using BirkNext.Api.Data;
using BirkNext.Api.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BirkNext.Api.Tests.Data;

public class MigrationIntegrityTests
{
    [Fact]
    public async Task ValidateMigrations_AllMigrationsHaveDesignerFiles()
    {
        // Arrange
        var logger = new XunitLogger();
        var validator = new MigrationIntegrityValidator(logger);

        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=birknext_test;Username=birknext;Password=birknext")
                .Options);

        // Act
        var report = await validator.ValidateAsync(context);

        // Assert
        Assert.True(
            report.MigrationFilesComplete,
            $"Migration files incomplete. Issues: {string.Join(", ", report.Issues.Where(i => i.Severity == MigrationIssueSeverity.Critical).Select(i => i.Issue))}");
    }

    [Fact]
    public async Task ValidateMigrations_AllDesignerFilesHaveMatchingMigrations()
    {
        // Arrange
        var logger = new XunitLogger();
        var validator = new MigrationIntegrityValidator(logger);

        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=birknext_test;Username=birknext;Password=birknext")
                .Options);

        // Act
        var report = await validator.ValidateAsync(context);

        // Assert
        Assert.True(
            report.DesignerFilesPresent,
            $"Designer files orphaned. Issues: {string.Join(", ", report.Issues.Where(i => i.Issue.Contains("orphaned")).Select(i => i.Issue))}");
    }

    [Fact]
    public async Task ValidateMigrations_AllMigrationsRecognizedByEFCore()
    {
        // Arrange
        var logger = new XunitLogger();
        var validator = new MigrationIntegrityValidator(logger);

        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=birknext_test;Username=birknext;Password=birknext")
                .Options);

        // Act
        var report = await validator.ValidateAsync(context);

        // Assert
        Assert.True(
            report.MigrationsRecognized,
            $"Untracked migrations found. Issues: {string.Join(", ", report.Issues.Where(i => i.Issue.Contains("not tracked")).Select(i => i.Issue))}");
    }

    [Fact]
    public async Task ValidateMigrations_OverallIntegrity()
    {
        // Arrange
        var logger = new XunitLogger();
        var validator = new MigrationIntegrityValidator(logger);

        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=birknext_test;Username=birknext;Password=birknext")
                .Options);

        // Act
        var report = await validator.ValidateAsync(context);

        // Assert
        Assert.True(
            report.IsValid,
            $"Migration integrity check failed:\n{string.Join("\n", report.Issues.Select(i => $"  [{i.Severity}] {i.Issue}"))}");

        Assert.Empty(report.Issues.Where(i => i.Severity == MigrationIssueSeverity.Critical));
    }

    private class XunitLogger : ILogger<MigrationIntegrityValidator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Xunit handles output automatically
        }
    }
}
