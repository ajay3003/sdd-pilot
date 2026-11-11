using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.BuildInfo;
using BirkNext.WorkspaceTransfer;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>
/// The one PostgreSQL tool readiness resolution of Backup &amp; Transfer. Ready requires the real tools to answer and the versions to fit the
/// real server; a missing directory, an unreachable database or an unknown container is never Ready. Expected outcomes are written here
/// independently of the implementation (versions from the test server itself, statuses from the rules in the task).
/// </summary>
public sealed class PostgresToolReadinessTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public async Task Configured_directory_without_tools_is_unavailable_and_says_where_it_looked()
    {
        var empty = Directory.CreateTempSubdirectory("birknext-no-pg-").FullName;
        try
        {
            var tools = new PostgresTools(new PostgresToolOptions { Mode = "Host", BinDirectory = empty });
            var r = await PostgresToolReadiness.EvaluateAsync(tools, TestPostgres.Admin().ConnectionString, RuntimeProfile.TesterNative, Clock, default);
            r.Backup.Should().Be(BackupReadinessStatus.Unavailable, "a configured directory without pg_dump can never back up");
            r.Restore.Should().Be(BackupReadinessStatus.Unavailable);
            r.ToolProvider.Should().Be("Tester Package (bundled)");
            r.SearchLocations.Should().Equal(empty);
            r.BackupBlocker.Should().Contain("pg_dump could not be started").And.Contain(empty);
            r.Items.Single(i => i.Key == "compatibility").Status.Should().Be(BackupReadinessStatus.NotChecked, "nothing to compare without a tool");
            r.DumpToolVersion.Should().BeNull();
        }
        finally { Directory.Delete(empty); }
    }

    [Fact]
    public async Task An_unknown_container_is_never_trusted()
    {
        var tools = new PostgresTools(new PostgresToolOptions { Mode = "ContainerExec", Runtime = "podman", Container = "birknext-no-such-container-" + Guid.NewGuid().ToString("N")[..8] });
        var r = await PostgresToolReadiness.EvaluateAsync(tools, TestPostgres.Admin().ConnectionString, RuntimeProfile.DeveloperContainer, Clock, default);
        r.Backup.Should().NotBe(BackupReadinessStatus.Ready);
        r.ToolProvider.Should().Be("Container exec");
    }

    [Fact]
    public async Task An_unreachable_database_blocks_backup_even_with_tools()
    {
        var tools = new PostgresTools(new PostgresToolOptions { Mode = "Host", BinDirectory = Directory.CreateTempSubdirectory("birknext-no-pg-").FullName });
        var r = await PostgresToolReadiness.EvaluateAsync(tools, "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2", RuntimeProfile.DeveloperContainer, Clock, default);
        r.Items.Single(i => i.Key == "database").Status.Should().Be(BackupReadinessStatus.Unavailable);
        r.Backup.Should().Be(BackupReadinessStatus.Unavailable);
        r.Items.Single(i => i.Key == "restore-destination").Status.Should().Be(BackupReadinessStatus.NotChecked);
    }

    [Fact]
    public async Task Missing_configuration_is_blocked_not_unavailable()
    {
        var tools = new PostgresTools(new PostgresToolOptions { Mode = "ContainerExec" });
        var r = await PostgresToolReadiness.EvaluateAsync(tools, TestPostgres.Admin().ConnectionString, RuntimeProfile.DeveloperContainer, Clock, default);
        r.Backup.Should().Be(BackupReadinessStatus.Blocked);
        r.BackupBlocker.Should().Contain("Container is required");
    }

    [PostgresToolsFact]
    public async Task Real_tools_against_the_real_server_are_ready_with_versions_compared()
    {
        var r = await PostgresToolReadiness.EvaluateAsync(PostgresToolsTestGate.Tools(), TestPostgres.Admin().ConnectionString, RuntimeProfile.DeveloperContainer, Clock, default);
        r.Backup.Should().Be(BackupReadinessStatus.Ready, string.Join("; ", r.Items.Select(i => $"{i.Key}={i.Status}: {i.Detail}")));
        r.DumpToolVersion.Should().StartWith("pg_dump (PostgreSQL)");
        r.ServerVersion.Should().NotBeNullOrEmpty();
        PostgresToolReadiness.Major(r.DumpToolVersion).Should().BeGreaterThanOrEqualTo(int.Parse(r.ServerVersion!.Split('.')[0]));
        r.Items.Single(i => i.Key == "compatibility").Status.Should().Be(BackupReadinessStatus.Ready);
        if (PostgresToolsTestGate.Tools().ContainerMode)
            r.Items.Single(i => i.Key == "identity").Status.Should().Be(BackupReadinessStatus.Ready, "the container is the server the connection reaches");
        r.Items.Should().NotContain(i => i.Detail.Contains("Password", StringComparison.OrdinalIgnoreCase), "no secret in readiness");
    }

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 16.13", 16)]
    [InlineData("pg_restore (PostgreSQL) 17.2 (Debian 17.2-1.pgdg120+1)", 17)]
    [InlineData("pg_dump (PostgreSQL) 9.6.24", 9)]
    [InlineData("not a version", null)]
    [InlineData(null, null)]
    public void Tool_major_versions_are_parsed_from_the_real_output(string? text, int? major) => PostgresToolReadiness.Major(text).Should().Be(major);

    [Theory]
    [InlineData("pg_restore (PostgreSQL) 15.4", "pg_dump (PostgreSQL) 16.13", true)]
    [InlineData("pg_restore (PostgreSQL) 16.13", "pg_dump (PostgreSQL) 16.13", false)]
    [InlineData("pg_restore (PostgreSQL) 17.1", "pg_dump (PostgreSQL) 16.13", false)]
    public void An_older_pg_restore_refuses_a_backup_from_a_newer_pg_dump(string restore, string dump, bool refused) =>
        (PostgresToolReadiness.RestoreToolProblem(restore, dump) is not null).Should().Be(refused);
}
