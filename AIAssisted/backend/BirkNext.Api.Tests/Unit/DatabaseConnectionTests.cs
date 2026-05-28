using BirkNext.Api.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.Unit;

public class DatabaseConnectionTests
{
    [Fact]
    public void GetConnectionString_UsesConfiguredConnectionStringFirst()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=db;Port=5433;Database=custom;Username=user;Password=secret"
            })
            .Build();

        DatabaseConnection.GetConnectionString(configuration)
            .Should().Contain("Database=custom");
    }

    [Fact]
    public void GetConnectionString_BuildsLocalDefaultFromPostgresEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["POSTGRES_DB"] = "birknext",
                ["POSTGRES_USER"] = "birknext",
                ["POSTGRES_PASSWORD"] = "birknext",
                ["POSTGRES_PORT"] = "5432"
            })
            .Build();

        var connectionString = DatabaseConnection.GetConnectionString(configuration);

        connectionString.Should().Contain("Database=birknext");
        connectionString.Should().Contain("Username=birknext");
        connectionString.Should().Contain("Password=birknext");
    }

    [Fact]
    public void AuthFailureMessage_SanitizesPassword()
    {
        var message = DatabaseConnection.AuthFailureMessage(
            "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext");

        message.Should().Contain("Password=***");
        message.Should().NotContain("Password=birknext");
        message.Should().Contain(DatabaseConnection.LocalEnvFileHint);
    }
}
