using System.Runtime.CompilerServices;
using FluentAssertions;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Tests never touch the person's real BirkNext database. Real PostgreSQL connections go through <see cref="TestPostgres"/>, which opens only the
/// maintenance database or a temporary one; no test reads the configured connection string itself or names the real database.
/// </summary>
public sealed class TestPostgresGuardTests
{
    [Theory]
    [InlineData("birknext_migration_lock_0123456789ab", true)]
    [InlineData("birknext_delivery_reporting_abcdef012345", true)]
    [InlineData("birknext", false)]
    [InlineData("birknext_ci2", false)]
    [InlineData("postgres", false)]
    [InlineData("birknext_test", false)]
    [InlineData("other_db_0123456789ab", false)]
    [InlineData(null, false)]
    public void Only_temporary_databases_can_be_opened(string? database, bool allowed)
    {
        TestPostgres.IsTemporary(database).Should().Be(allowed);
        if (!allowed && database is not null)
            FluentActions.Invoking(() => TestPostgres.Temporary(database)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Temporary_names_are_unique_and_pass_the_guard()
    {
        var a = TestPostgres.TemporaryName("birknext_guard");
        var b = TestPostgres.TemporaryName("birknext_guard");
        a.Should().NotBe(b);
        TestPostgres.IsTemporary(a).Should().BeTrue();
        TestPostgres.Admin().Database.Should().Be("postgres");
    }

    [Fact]
    public void No_test_reads_the_configured_connection_or_names_the_real_database()
    {
        var root = Path.GetDirectoryName(Here())!;
        var tests = Directory.GetParent(root)!.FullName;
        var offenders = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) is not ("TestPostgres.cs" or "TestPostgresGuardTests.cs" or "DatabaseConnectionTests.cs"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("GetEnvironmentVariable(\"ConnectionStrings__Default\")") || text.Contains("Database=birknext;");
            })
            .Select(f => Path.GetRelativePath(tests, f)).ToList();
        offenders.Should().BeEmpty("real PostgreSQL connections go through TestPostgres (DatabaseConnectionTests only formats strings, it never connects)");
    }

    private static string Here([CallerFilePath] string path = "") => path;
}
