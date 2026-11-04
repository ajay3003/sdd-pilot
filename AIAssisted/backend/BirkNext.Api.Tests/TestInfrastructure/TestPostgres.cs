using System.Text.RegularExpressions;
using Npgsql;

namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// The only way a test opens a real PostgreSQL connection. The server and credentials come from ConnectionStrings__Default (else the local
/// development server), but a test may open only the <c>postgres</c> maintenance database (to create / drop) or a temporary database whose name
/// ends with a random suffix. The database named in the connection string — the person's real BirkNext data, e.g. <c>birknext</c> — is never
/// opened, whatever it is called.
/// </summary>
public static partial class TestPostgres
{
    private const string LocalDevelopment = "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    private static string Base => Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? LocalDevelopment;

    /// <summary>The maintenance database, for CREATE / DROP DATABASE only.</summary>
    public static NpgsqlConnectionStringBuilder Admin() => new(Base) { Database = "postgres", Pooling = false };

    /// <summary>A temporary database name: the prefix plus a random 12-hex suffix.</summary>
    public static string TemporaryName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    /// <summary>A connection to a temporary database; refuses any other database (see <see cref="IsTemporary"/>).</summary>
    public static NpgsqlConnectionStringBuilder Temporary(string database)
    {
        if (!IsTemporary(database))
            throw new InvalidOperationException($"Tests may open only temporary databases (prefix + random suffix), never \"{database}\".");
        return new(Base) { Database = database, Pooling = false };
    }

    /// <summary>A temporary name, and never the configured (real) database.</summary>
    public static bool IsTemporary(string? database) =>
        database is not null && TemporaryRe().IsMatch(database)
        && !string.Equals(database, new NpgsqlConnectionStringBuilder(Base).Database, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^birknext_[a-z_]+_[0-9a-f]{12}$")]
    private static partial Regex TemporaryRe();
}
