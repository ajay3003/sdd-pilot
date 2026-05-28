using Npgsql;

namespace BirkNext.Api.Configuration;

public static class DatabaseConnection
{
    public const string LocalEnvFileHint = "AIAssisted/.env";

    public static string GetConnectionString(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return BuildConnectionString(
            host: configuration["POSTGRES_HOST"] ?? "localhost",
            port: configuration["POSTGRES_PORT"] ?? "5432",
            database: configuration["POSTGRES_DB"] ?? "birknext",
            username: configuration["POSTGRES_USER"] ?? "birknext",
            password: configuration["POSTGRES_PASSWORD"] ?? "birknext");
    }

    public static string BuildConnectionString(
        string host,
        string port,
        string database,
        string username,
        string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = database,
            Username = username,
            Password = password
        };

        if (int.TryParse(port, out var portNumber))
            builder.Port = portNumber;

        return builder.ConnectionString;
    }

    public static string Sanitize(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.Password))
            builder.Password = "***";

        return builder.ConnectionString;
    }

    public static string AuthFailureMessage(string connectionString) =>
        "PostgreSQL authentication failed during startup migration. " +
        $"Runtime connection: {Sanitize(connectionString)}. " +
        $"Local compose credentials come from {LocalEnvFileHint}. " +
        "If POSTGRES_USER or POSTGRES_PASSWORD changed after the database container was first created, " +
        "the existing postgres_data volume still has the old credentials. " +
        "Either update ConnectionStrings__Default to match the existing database, or recreate the local database volume.";
}
